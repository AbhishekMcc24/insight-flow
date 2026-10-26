using System.Net;
using System.Net.Sockets;

namespace InsightFlow.Connectors.Knowledge;

/// <summary>
/// Stops a tenant administrator from pointing the server at addresses inside our own network (SSRF). In production only
/// public hosts are allowed; local development may use localhost and private addresses.
/// </summary>
public static class KnowledgeEndpointPolicy
{
    /// <summary>Throws <see cref="KnowledgeGraphException"/> when the host resolves to a loopback, private or link-local address.</summary>
    public static async Task EnsureAllowedAsync(string uri, bool allowPrivateAddresses, CancellationToken cancellationToken)
    {
        if (allowPrivateAddresses)
        {
            return;
        }

        if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed) || string.IsNullOrEmpty(parsed.Host))
        {
            throw new KnowledgeGraphException("The address is not valid.");
        }

        IPAddress[] addresses;
        try
        {
            addresses = IPAddress.TryParse(parsed.Host, out var literal)
                ? [literal]
                : await Dns.GetHostAddressesAsync(parsed.Host, cancellationToken);
        }
        catch (SocketException)
        {
            throw new KnowledgeGraphException("The host name could not be resolved.");
        }

        if (addresses.Length == 0 || addresses.Any(IsInternal))
        {
            throw new KnowledgeGraphException("The host must be a public address. Private and local addresses are not allowed here.");
        }
    }

    internal static bool IsInternal(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any))
        {
            return true;
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var bytes = address.GetAddressBytes();
            return address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || (bytes[0] & 0xFE) == 0xFC; // fc00::/7 unique local
        }

        var b = address.GetAddressBytes();
        return b[0] == 10
            || (b[0] == 172 && b[1] is >= 16 and <= 31)
            || (b[0] == 192 && b[1] == 168)
            || (b[0] == 169 && b[1] == 254)
            || (b[0] == 100 && b[1] is >= 64 and <= 127)
            || b[0] == 0;
    }
}
