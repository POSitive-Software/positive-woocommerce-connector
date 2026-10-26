using System.Net;

namespace PA.Connectors.Host.Ui;

/// <summary>
/// Who is using the connector pages. Users never reach this host directly: PA.Server forwards
/// <c>/connectors/**</c> here after its portal login and permission check, and states the signed-in
/// user and whether they may change settings in these headers (it strips any copy a client sent).
/// </summary>
public static class PortalHeaders
{
    /// <summary>The portal user's name, as PA.Server signed them in (percent-encoded UTF-8).</summary>
    public const string User = "X-PA-Portal-User";

    /// <summary>"1" when the user holds Operations Settings (MA 106) and may change anything here.</summary>
    public const string CanWrite = "X-PA-Portal-Can-Write";
}

/// <summary>The caller of a /connectors/api request, from the trusted headers.</summary>
public sealed record PortalCaller(string User, bool CanWrite)
{
    internal const string ItemKey = "pa.connectors.caller";

    public static PortalCaller From(HttpContext context) =>
        context.Items[ItemKey] as PortalCaller ?? throw new InvalidOperationException("No portal caller on this request.");
}

/// <summary>
/// Guards everything under <c>/connectors</c> on the host:
/// <list type="bullet">
/// <item>the peer must be loopback (the host binds loopback only; this holds even if that is ever misconfigured);</item>
/// <item>the Host header must name this machine (localhost / 127.0.0.1 / [::1]), so a web page cannot
/// reach the host through DNS rebinding and send its own copy of the trusted headers;</item>
/// <item>the portal headers count only next to PA.Server's shared secret
/// (<see cref="ConnectorForwardToken.HeaderName"/>, constant-time compare): without it they are
/// dropped and the request is anonymous;</item>
/// <item>a forwarded page request whose path no longer starts with /connectors is refused (400);</item>
/// <item><c>/connectors/api/**</c> needs <see cref="PortalHeaders.User"/> (401 otherwise — open the pages
/// through POSitive Anywhere), and anything but GET/HEAD also needs <see cref="PortalHeaders.CanWrite"/> = 1 (403).</item>
/// </list>
/// The pages and <c>/connectors/health</c> need no user (the ops console card and the page shell).
/// </summary>
public sealed class ConnectorUiGate
{
    public const string PathPrefix = "/connectors";
    public const string ApiPrefix = "/connectors/api";

    private readonly RequestDelegate _next;

    public ConnectorUiGate(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext context, ConnectorForwardToken forwardToken)
    {
        var path = context.Request.Path;
        var headers = context.Request.Headers;

        // Only PA.Server's forwards carry the shared token; without it the portal headers are not
        // believed (any other local process can reach :5220) and the request is anonymous.
        var forwarded = forwardToken.Matches(headers[ConnectorForwardToken.HeaderName].ToString());
        if (!forwarded)
        {
            headers.Remove(PortalHeaders.User);
            headers.Remove(PortalHeaders.CanWrite);
        }

        if (!path.StartsWithSegments(PathPrefix, StringComparison.OrdinalIgnoreCase))
        {
            // A connector page request (it names a portal user) that normalised to somewhere else.
            if (forwarded && headers.ContainsKey(PortalHeaders.User))
            {
                await RefuseAsync(context, StatusCodes.Status400BadRequest, "path_not_allowed",
                    "Connector page requests must stay under /connectors.");
                return;
            }
            await _next(context);
            return;
        }

        // In-process (TestServer) requests have no peer address; Kestrel always sets one.
        var peer = context.Connection.RemoteIpAddress;
        if (peer is not null && !IPAddress.IsLoopback(peer))
        {
            await RefuseAsync(context, StatusCodes.Status403Forbidden, "loopback_only",
                "The connector service only answers on this machine. Open /connectors on the POSitive Anywhere server.");
            return;
        }
        if (!IsLocalHostHeader(context.Request.Host))
        {
            await RefuseAsync(context, StatusCodes.Status403Forbidden, "host_not_allowed",
                "This request did not come through the POSitive Anywhere server. Open /connectors there.");
            return;
        }

        context.Response.OnStarting(() =>
        {
            var headers = context.Response.Headers;
            headers.XContentTypeOptions = "nosniff";
            headers["Referrer-Policy"] = "same-origin";
            if (!headers.ContainsKey("Cache-Control"))
                headers.CacheControl = "no-store";
            return Task.CompletedTask;
        });

        if (path.StartsWithSegments(ApiPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var user = Clean(Decode(context.Request.Headers[PortalHeaders.User].ToString()));
            if (user is null)
            {
                await RefuseAsync(context, StatusCodes.Status401Unauthorized, "portal_user_required",
                    "Sign in to POSitive Anywhere and open /connectors there; the connector service is not used directly.");
                return;
            }
            var canWrite = context.Request.Headers[PortalHeaders.CanWrite].ToString().Trim() is "1" or "true";
            if (!HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method) && !canWrite)
            {
                await RefuseAsync(context, StatusCodes.Status403Forbidden, "settings_permission_required",
                    "Changing connectors needs the POSitive permission \"Able To Access Operations Settings\" (MA 106).");
                return;
            }
            context.Items[PortalCaller.ItemKey] = new PortalCaller(user, canWrite);
        }

        await _next(context);
    }

    internal static bool IsLocalHostHeader(HostString host)
    {
        var name = host.Host;
        return string.Equals(name, "localhost", StringComparison.OrdinalIgnoreCase)
               || name is "127.0.0.1" or "[::1]" or "::1";
    }

    /// <summary>The user header is percent-encoded UTF-8 (header values are ASCII on the wire; names need not be).</summary>
    internal static string Decode(string value)
    {
        try
        {
            return Uri.UnescapeDataString(value);
        }
        catch (UriFormatException)
        {
            return value;
        }
    }

    /// <summary>Trimmed, no control characters, at most 100 characters; null when nothing is left.</summary>
    internal static string? Clean(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var cleaned = new string(value.Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (cleaned.Length == 0)
            return null;
        return cleaned.Length <= 100 ? cleaned : cleaned[..100];
    }

    private static Task RefuseAsync(HttpContext context, int status, string errorCode, string detail)
    {
        context.Response.StatusCode = status;
        context.Response.Headers.CacheControl = "no-store";
        return context.Response.WriteAsJsonAsync(
            new { status, title = "Connector request refused", detail, errorCode },
            options: (System.Text.Json.JsonSerializerOptions?)null,
            contentType: "application/problem+json");
    }
}
