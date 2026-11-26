using System;
using System.IO;

namespace D6Downloader.Platform.Linux;

/// <summary>
/// Sperrt Ruhezustand und Leerlauf bei logind über <c>systemd-inhibit</c>. Die Sperre gilt, solange dessen Kind <c>cat</c> auf
/// unsere Standardeingabe wartet: Endet die App, auch durch Absturz, ist die Sperre weg.
/// </summary>
public sealed class SystemdInhibitKeepAwake : IKeepAwake, IDisposable
{
    private static readonly TimeSpan RetryAfter = TimeSpan.FromMinutes(1);

    private readonly ICommandRunner _commands;
    private readonly Func<DateTime> _clock;
    private IHeldProcess? _held;
    private DateTime? _lastStart;

    public SystemdInhibitKeepAwake(ICommandRunner? commands = null, Func<DateTime>? clock = null)
    {
        _commands = commands ?? CommandRunner.Instance;
        _clock = clock ?? (() => DateTime.UtcNow);
    }

    public void Set(bool on)
    {
        if (!on)
        {
            Release();
            _lastStart = null;
            return;
        }
        if (_held is { HasExited: false }) return;
        // Abgelehnt oder nicht vorhanden: nicht bei jedem Takt neu versuchen.
        if (_lastStart is { } last && _clock() - last < RetryAfter) return;
        _lastStart = _clock();
        Release();
        if (_commands.Find("systemd-inhibit") is not { } inhibit || _commands.Find("cat") is not { } cat) return;
        try
        {
            _held = _commands.Hold(inhibit, new[]
            {
                "--what=sleep:idle", "--who=d6Downloader", "--why=Downloads", "--mode=block", "--no-ask-password", cat,
            });
        }
        catch (IOException)
        {
        }
    }

    public void Dispose() => Release();

    private void Release()
    {
        _held?.Dispose();
        _held = null;
    }
}
