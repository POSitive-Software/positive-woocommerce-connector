using System.Net;
using System.Net.Sockets;

namespace PA.Connectors.WooCommerce;

/// <summary>The store address resolves to an address the connector must not call (private, loopback, link-local…).</summary>
public sealed class StoreAddressBlockedException : Exception
{
    public StoreAddressBlockedException(string host)
        : base($"The store address '{host}' resolves to a private, loopback or link-local address, which the connector does not call.")
    {
        Host = host;
    }

    public string Host { get; }
}

/// <summary>
/// Which store addresses the connector may call (SSRF guard). A store is on the internet: an address
/// that is loopback, link-local (169.254/16, fe80::/10 — cloud metadata lives there), private
/// (10/8, 172.16/12, 192.168/16, fc00::/7), unspecified, multicast or carrying user info is refused —
/// checked when the address is saved or tested (<see cref="CheckAsync"/>) and again on every new
/// connection (<see cref="CreateHandler"/>'s ConnectCallback), so a DNS answer that changes after the
/// check (DNS rebinding) is still refused. <see cref="AllowPrivate"/> (connectorhost.json
/// <c>Connectors:AllowPrivateStoreHosts</c>, default false) lifts the address rule for a dev store on
/// this machine or the LAN (e.g. a Docker test store on localhost:8090); user info is refused regardless.
/// </summary>
public sealed class WooStoreAddress
{
    public const string AllowPrivateSettingKey = "Connectors:AllowPrivateStoreHosts";

    private readonly Func<string, CancellationToken, Task<IPAddress[]>> _resolve;

    /// <param name="resolve">DNS seam for tests; default <see cref="Dns.GetHostAddressesAsync(string, CancellationToken)"/>.</param>
    public WooStoreAddress(bool allowPrivate, Func<string, CancellationToken, Task<IPAddress[]>>? resolve = null)
    {
        AllowPrivate = allowPrivate;
        _resolve = resolve ?? ((host, ct) => Dns.GetHostAddressesAsync(host, ct));
    }

    public bool AllowPrivate { get; }

    /// <summary>
    /// Null when <paramref name="storeUrl"/> may be called, else why not (a sentence for the
    /// page). Resolves the host's DNS; an IP literal is checked as it is.
    /// </summary>
    public async Task<string?> CheckAsync(Uri storeUrl, CancellationToken cancellationToken)
    {
        if (storeUrl.Scheme != Uri.UriSchemeHttps && storeUrl.Scheme != Uri.UriSchemeHttp)
            return "Enter the store's address, e.g. https://shop.example.com.";
        if (!string.IsNullOrEmpty(storeUrl.UserInfo))
            return "The store address must not contain a user name or password.";
        if (AllowPrivate)
            return null;

        IPAddress[] addresses;
        try
        {
            addresses = await ResolveAsync(storeUrl.IdnHost, cancellationToken);
        }
        catch (SocketException)
        {
            return "The store address could not be found (DNS). Check it is spelled correctly.";
        }
        if (addresses.Length == 0)
            return "The store address could not be found (DNS). Check it is spelled correctly.";
        if (addresses.Any(IsBlocked))
            return "The store address points at this machine or a private network. Enter the store's public internet address.";
        return null;
    }

    /// <summary>
    /// The primary handler for the Woo HttpClient: never follows a redirect (a store cannot send the
    /// connector's Basic-auth request somewhere else), no proxy (the address check must see the real
    /// peer), and every new connection re-resolves the host and refuses a blocked address.
    /// </summary>
    public SocketsHttpHandler CreateHandler() => new()
    {
        AllowAutoRedirect = false,
        UseProxy = false,
        UseCookies = false,
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
        ConnectTimeout = TimeSpan.FromSeconds(15),
        ConnectCallback = ConnectAsync,
    };

    internal async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        var host = context.DnsEndPoint.Host;
        var addresses = await ResolveAsync(host, cancellationToken);
        if (addresses.Length == 0)
            throw new HttpRequestException($"The store address '{host}' could not be resolved.");
        if (!AllowPrivate && addresses.Any(IsBlocked))
            throw new HttpRequestException(
                "The store address resolves to a private, loopback or link-local address.", new StoreAddressBlockedException(host));

        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(addresses, context.DnsEndPoint.Port, cancellationToken);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken)
    {
        var bare = host.StartsWith('[') && host.EndsWith(']') ? host[1..^1] : host;
        return IPAddress.TryParse(bare, out var literal) ? Task.FromResult(new[] { literal }) : _resolve(bare, cancellationToken);
    }

    /// <summary>True for every address a store on the internet never has.</summary>
    public static bool IsBlocked(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();
            return b[0] == 0                                   // 0.0.0.0/8 (unspecified, "this network")
                   || b[0] == 10                               // 10/8
                   || b[0] == 127                              // loopback
                   || (b[0] == 169 && b[1] == 254)             // link-local (cloud metadata)
                   || (b[0] == 172 && b[1] is >= 16 and <= 31) // 172.16/12
                   || (b[0] == 192 && b[1] == 168)             // 192.168/16
                   || (b[0] == 100 && b[1] is >= 64 and <= 127) // 100.64/10 carrier-grade NAT
                   || b[0] >= 224;                             // multicast, reserved, broadcast
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (address.Equals(IPAddress.IPv6Any) || address.Equals(IPAddress.IPv6Loopback)
                || address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast)
                return true;
            var b = address.GetAddressBytes();
            return (b[0] & 0xFE) == 0xFC; // fc00::/7 unique local
        }

        return true;
    }
}
