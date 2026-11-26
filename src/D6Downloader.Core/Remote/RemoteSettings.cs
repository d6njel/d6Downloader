using System;
using System.Collections.Generic;

namespace D6Downloader.Remote;

/// <summary>Fernempfang vom Handy in <c>settings.json</c>. Ab Werk aus: dann gibt es keine Verbindung nach außen.</summary>
public sealed class RemoteSettings
{
    public const string DefaultServer = "https://ntfy.sh";
    public const int MaxDevices = 10;

    public bool Enabled { get; set; }

    public string ServerUrl
    {
        get => _serverUrl;
        set => _serverUrl = string.IsNullOrWhiteSpace(value) ? DefaultServer : value.Trim();
    }

    private string _serverUrl = DefaultServer;

    /// <summary>Links vom Handy ohne Dialog starten. Ab Werk aus.</summary>
    public bool AutoStart { get; set; }

    public List<RemoteDevice> Devices
    {
        get => _devices;
        set => _devices = value ?? new();
    }

    private List<RemoteDevice> _devices = new();

    /// <summary>
    /// Nur <c>https://</c> mit Host, ohne Anmeldedaten, Anfrage oder Anker. Null = ungültig, sonst die Adresse mit <c>/</c> am Ende.
    /// </summary>
    public static Uri? ParseServer(string? url)
    {
        if (!Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme != Uri.UriSchemeHttps || uri.Host.Length == 0 || uri.UserInfo.Length > 0
            || uri.Query.Length > 0 || uri.Fragment.Length > 0) return null;
        return uri.AbsoluteUri.EndsWith('/') ? uri : new Uri(uri.AbsoluteUri + "/");
    }

    public static bool IsDefaultServer(Uri server) =>
        string.Equals(server.AbsoluteUri.TrimEnd('/'), DefaultServer, StringComparison.OrdinalIgnoreCase);
}

/// <summary>Ein gekoppeltes Handy. Der Seed steht nur verschlüsselt darin (<c>v1:</c>, <see cref="Debrid.SecretProtector"/>).</summary>
public sealed class RemoteDevice
{
    public string DeviceId { get; set; } = "";
    public string Name { get; set; } = "";
    public string? SeedProtected { get; set; }
    public DateTime PairedUtc { get; set; }
    public DateTime? LastSeenUtc { get; set; }
}
