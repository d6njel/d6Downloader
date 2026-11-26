using System;
using System.Collections.Generic;
using System.IO;
using D6Downloader.Power;

namespace D6Downloader.Platform.Linux;

/// <summary>
/// Herunterfahren zuerst über KDE (Programme dürfen speichern), sonst <c>systemctl</c>. Energie sparen und Ruhezustand über
/// <c>systemctl</c>, Ruhezustand nur, wenn logind <c>CanHibernate</c> mit <c>yes</c> beantwortet.
/// </summary>
public sealed class LinuxPowerActions : IPowerActions
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan QueryWait = TimeSpan.FromSeconds(3);

    private readonly ICommandRunner _commands;

    public LinuxPowerActions(ICommandRunner? commands = null) => _commands = commands ?? CommandRunner.Instance;

    public bool IsAvailable(PowerAction action) => action switch
    {
        PowerAction.Shutdown => _commands.Find("systemctl") is not null || _commands.Find("busctl") is not null,
        PowerAction.Hibernate => _commands.Find("systemctl") is not null && Ask("CanHibernate") == "yes",
        // Ohne Antwort (kein busctl) darf es systemctl versuchen.
        PowerAction.Sleep => _commands.Find("systemctl") is not null && Ask("CanSuspend") is not ("no" or "na"),
        _ => false,
    };

    public string? Execute(PowerAction action)
    {
        try
        {
            switch (action)
            {
                case PowerAction.Shutdown:
                    return TryKdeShutdown() ? null : Systemctl("poweroff");
                case PowerAction.Sleep:
                    return Systemctl("suspend");
                case PowerAction.Hibernate:
                    return IsAvailable(action) ? Systemctl("hibernate") : Loc.T("After.NoHibernate");
                default:
                    return null;
            }
        }
        catch (IOException ex)
        {
            return ex.Message;
        }
    }

    private bool TryKdeShutdown()
    {
        if (_commands.Find("busctl") is not { } busctl) return false;
        try
        {
            var result = _commands.Run(busctl,
                new[] { "--user", "call", "org.kde.Shutdown", "/Shutdown", "org.kde.Shutdown", "logoutAndShutdown" }, Wait);
            return result.ExitCode is null or 0; // läuft noch: im Gange
        }
        catch (IOException)
        {
            return false;
        }
    }

    private string? Systemctl(string verb)
    {
        if (_commands.Find("systemctl") is not { } systemctl) return Loc.T("Power.StartFailed");
        var result = _commands.Run(systemctl, new[] { "--no-ask-password", verb }, Wait);
        return result.ExitCode is null or 0 ? null : Loc.T("Power.Refused", result.ExitCode);
    }

    /// <summary>Antwort von logind (<c>yes</c>, <c>no</c>, <c>challenge</c>, <c>na</c>), null wenn nicht zu erfragen.</summary>
    private string? Ask(string method)
    {
        if (_commands.Find("busctl") is not { } busctl) return null;
        try
        {
            var result = _commands.Run(busctl, new List<string>
            {
                "--system", "call", "org.freedesktop.login1", "/org/freedesktop/login1", "org.freedesktop.login1.Manager", method,
            }, QueryWait);
            if (result.ExitCode != 0) return null;
            string[] parts = result.Output.Split('"');
            return parts.Length >= 3 ? parts[1] : null; // Ausgabe: s "yes"
        }
        catch (IOException)
        {
            return null;
        }
    }
}
