using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace D6Downloader.Platform.Linux;

/// <param name="ExitCode">Null: läuft nach Ablauf der Wartezeit noch.</param>
public sealed record CommandResult(int? ExitCode, string Output);

/// <summary>Ein Kindprozess, der läuft, solange seine Standardeingabe offen ist. <see cref="IDisposable.Dispose"/> beendet ihn.</summary>
public interface IHeldProcess : IDisposable
{
    bool HasExited { get; }
}

/// <summary>
/// Startet Systemprogramme, ohne Shell und nur mit Argumentliste. Hinter einer Schnittstelle, damit Tests nie etwas am System
/// auslösen. Alle Methoden außer <see cref="Find"/> werfen IOException, wenn der Start nicht geht.
/// </summary>
public interface ICommandRunner
{
    /// <summary>Voller Pfad eines Programms aus dem PATH, sonst null.</summary>
    string? Find(string name);

    /// <summary>Startet und wartet höchstens <paramref name="timeout"/>. Beendet das Programm nie.</summary>
    CommandResult Run(string exe, IReadOnlyList<string> args, TimeSpan timeout);

    /// <summary>Startet, ohne zu warten.</summary>
    void Start(string exe, IReadOnlyList<string> args);

    /// <summary>Startet mit offener Standardeingabe.</summary>
    IHeldProcess Hold(string exe, IReadOnlyList<string> args);
}

public sealed class CommandRunner : ICommandRunner
{
    public static readonly CommandRunner Instance = new();

    /// <summary>
    /// Nur absolute Einträge des PATH. Ein bloßer Name an <see cref="Process"/> würde zuerst im Programmordner und im
    /// Arbeitsordner gesucht.
    /// </summary>
    public string? Find(string name)
    {
        foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(':', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!dir.StartsWith('/')) continue;
            string path = Path.Combine(dir, name);
            if (File.Exists(path)) return path;
        }
        return null;
    }

    public CommandResult Run(string exe, IReadOnlyList<string> args, TimeSpan timeout)
    {
        var info = Info(exe, args);
        info.RedirectStandardOutput = true;
        info.RedirectStandardError = true;
        var process = Launch(info);
        process.StandardInput.Close();
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        _ = process.StandardError.ReadToEndAsync(); // leeren, sonst kann das Programm blockieren
        if (!process.WaitForExit(timeout)) return new(null, "");
        using (process)
            return new(process.ExitCode, output.Wait(TimeSpan.FromSeconds(2)) ? output.Result : "");
    }

    public void Start(string exe, IReadOnlyList<string> args)
    {
        using var process = Launch(Info(exe, args));
        process.StandardInput.Close();
    }

    public IHeldProcess Hold(string exe, IReadOnlyList<string> args) => new Held(Launch(Info(exe, args)));

    private static ProcessStartInfo Info(string exe, IReadOnlyList<string> args)
    {
        var info = new ProcessStartInfo(exe) { UseShellExecute = false, RedirectStandardInput = true, WorkingDirectory = "/" };
        foreach (string a in args) info.ArgumentList.Add(a);
        return info;
    }

    private static Process Launch(ProcessStartInfo info)
    {
        try
        {
            return Process.Start(info) ?? throw new IOException(info.FileName);
        }
        catch (Win32Exception ex)
        {
            throw new IOException(ex.Message, ex);
        }
    }

    private sealed class Held : IHeldProcess
    {
        private Process? _process;

        public Held(Process process) => _process = process;

        public bool HasExited => _process?.HasExited ?? true;

        public void Dispose()
        {
            var process = _process;
            _process = null;
            if (process is null) return;
            using (process)
            {
                try
                {
                    process.StandardInput.Close();
                    if (!process.WaitForExit(1000)) process.Kill(true);
                }
                catch (Exception ex) when (ex is IOException or InvalidOperationException or Win32Exception)
                {
                }
            }
        }
    }
}
