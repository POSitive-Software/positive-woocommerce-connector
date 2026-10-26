using System.Reflection;

namespace PA.Connectors.Host.Ui;

/// <summary>
/// The connector pages (index.html, app.js, app.css), embedded in the host assembly so they work
/// wherever the service runs from (its content root is the exe folder) and can never be swapped on
/// disk. Served at <c>/connectors/</c>; users reach them through PA.Server's forward, so the page is
/// same-origin with the ops console and its session.
/// </summary>
public static class ConnectorUiAssets
{
    private const string ResourcePrefix = "ConnectorUi.";

    private static readonly Dictionary<string, string> Files = new(StringComparer.Ordinal)
    {
        ["index.html"] = "text/html; charset=utf-8",
        ["app.js"] = "text/javascript; charset=utf-8",
        ["app.css"] = "text/css; charset=utf-8",
    };

    /// <summary>
    /// No inline script anywhere in the pages (every handler is wired from app.js), so the policy can
    /// forbid it; inline style attributes are allowed. Never framed.
    /// </summary>
    public const string ContentSecurityPolicy =
        "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; " +
        "connect-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'none'; object-src 'none'";

    public static void MapConnectorUiAssets(this IEndpointRouteBuilder app)
    {
        // One route for "/connectors" and "/connectors/" (routing ignores the trailing slash):
        // the page needs the slash so its relative links resolve under /connectors/.
        app.MapGet(ConnectorUiGate.PathPrefix, (HttpContext http) =>
            http.Request.Path.Value?.EndsWith('/') == true
                ? Serve(http, "index.html")
                : Results.Redirect(ConnectorUiGate.PathPrefix + "/"));
        app.MapGet(ConnectorUiGate.PathPrefix + "/{file}", (HttpContext http, string file) =>
            file == "index.html" || Files.ContainsKey(file) ? Serve(http, file) : Results.NotFound());
    }

    internal static byte[]? Read(string file)
    {
        if (!Files.ContainsKey(file))
            return null;
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourcePrefix + file);
        if (stream is null)
            return null;
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    private static IResult Serve(HttpContext http, string file)
    {
        var bytes = Read(file);
        if (bytes is null)
            return Results.NotFound();
        var headers = http.Response.Headers;
        // Revalidate every time, like the server's own pages: a host update must not leave a stale page.
        headers.CacheControl = "no-cache, must-revalidate";
        if (file.EndsWith(".html", StringComparison.Ordinal))
        {
            headers.ContentSecurityPolicy = ContentSecurityPolicy;
            headers.XFrameOptions = "DENY";
        }
        return Results.Bytes(bytes, Files[file]);
    }
}
