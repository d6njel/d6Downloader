using System;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace D6Downloader.Remote;

public enum TargetVerdict
{
    Allowed,
    /// <summary>Lokales oder privates Ziel.</summary>
    Blocked,
    /// <summary>Der Name ließ sich nicht auflösen: nicht ohne Dialog starten.</summary>
    Unresolved,
}

/// <summary>
/// Schutz für „sofort starten“: keine Ziele auf dem eigenen Rechner oder im Heimnetz. Restrisiko: Der Download löst den Namen
/// erneut auf (DNS-Rebinding) und folgt Weiterleitungen selbst.
/// </summary>
public sealed class TargetGuard
{
    private static readonly string[] LocalSuffixes = { "localhost", "local", "lan", "home", "internal", "fritz.box", "home.arpa" };

    private readonly Func<string, CancellationToken, Task<IPAddress[]>> _resolve;

    public TargetGuard(Func<string, CancellationToken, Task<IPAddress[]>>? resolve = null) =>
        _resolve = resolve ?? ((host, ct) => Dns.GetHostAddressesAsync(host, ct));

    /// <summary>Nur für Tests: Adressen, die trotz Regel erlaubt sind (der lokale Testserver).</summary>
    internal Func<Uri, bool>? Exempt { get; set; }

    public async Task<TargetVerdict> CheckAsync(string url, CancellationToken ct = default)
    {
        if (!System.Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) return TargetVerdict.Blocked;
        if (Exempt?.Invoke(uri) == true) return TargetVerdict.Allowed;
        string host;
        try
        {
            host = uri.IdnHost.Trim('[', ']').TrimEnd('.').ToLowerInvariant();
        }
        catch (UriFormatException)
        {
            return TargetVerdict.Blocked;
        }
        if (IPAddress.TryParse(host, out var literal) && (host.Contains(':') || host.Contains('.')))
            return IsBlockedAddress(literal) ? TargetVerdict.Blocked : TargetVerdict.Allowed;
        if (IsLocalName(host)) return TargetVerdict.Blocked;

        IPAddress[] addresses;
        try
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(TimeSpan.FromSeconds(10));
            addresses = await _resolve(host, limit.Token).WaitAsync(limit.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SocketException or ArgumentException || (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            return TargetVerdict.Unresolved;
        }
        if (addresses.Length == 0) return TargetVerdict.Unresolved;
        return addresses.Any(IsBlockedAddress) ? TargetVerdict.Blocked : TargetVerdict.Allowed;
    }

    internal static bool IsLocalName(string host) =>
        !host.Contains('.') || LocalSuffixes.Any(s => host == s || host.EndsWith("." + s, StringComparison.Ordinal));

    internal static bool IsBlockedAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) return IsBlockedAddress(address.MapToIPv4());
        byte[] b = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
            return b[0] is 0 or 10 or 127
                || (b[0] == 172 && b[1] is >= 16 and <= 31)
                || (b[0] == 192 && b[1] == 168)
                || (b[0] == 169 && b[1] == 254)
                || (b[0] == 100 && b[1] is >= 64 and <= 127)
                || b[0] >= 224; // Multicast und reserviert, samt 255.255.255.255
        // NAT64 (64:ff9b::/96): Die letzten vier Byte sind die IPv4-Adresse.
        if (b[0] == 0 && b[1] == 0x64 && b[2] == 0xFF && b[3] == 0x9B && b.Skip(4).Take(8).All(x => x == 0))
            return IsBlockedAddress(new IPAddress(b.Skip(12).ToArray()));
        return b.Take(15).All(x => x == 0) && b[15] <= 1 // :: und ::1
            || (b[0] & 0xFE) == 0xFC                     // fc00::/7
            || (b[0] == 0xFE && (b[1] & 0x80) == 0x80)   // fe80::/10 und fec0::/10
            || b[0] == 0xFF;                             // Multicast
    }
}
