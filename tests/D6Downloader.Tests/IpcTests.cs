using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using D6Downloader.Browser;
using D6Downloader.Platform.Linux;
using Xunit;

namespace D6Downloader.Tests;

/// <summary>Socket und Sperre in einem eigenen Ordner, nie im echten Laufzeitordner.</summary>
public class IpcTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly RuntimePaths _paths;

    public IpcTests() => _paths = new RuntimePaths(_dir.File("d6downloader"));

    public void Dispose() => _dir.Dispose();

    private sealed class App : IDisposable
    {
        public readonly BlockingCollection<string> Received = new();
        private readonly SingleInstanceLock _lock;
        private readonly IpcServer _server;

        public App(RuntimePaths paths)
        {
            _lock = SingleInstanceLock.TryAcquire(paths)!;
            _server = IpcServer.Create(_lock);
            _server.ShowRequested += () => Received.Add("show");
            _server.LinksReceived += t => Received.Add("links:" + t);
            _server.Listen();
        }

        public string Next() => Received.TryTake(out var message, 5000) ? message : "(nichts)";

        public void Dispose()
        {
            _server.Dispose();
            _lock.Dispose();
        }
    }

    [Fact]
    public void Only_one_instance_gets_the_lock()
    {
        using (var first = SingleInstanceLock.TryAcquire(_paths))
        {
            Assert.NotNull(first);
            Assert.Null(SingleInstanceLock.TryAcquire(_paths));
        }
        using var again = SingleInstanceLock.TryAcquire(_paths);
        Assert.NotNull(again);
    }

    [Fact]
    public void Show_and_links_arrive_and_an_empty_text_is_only_a_ping()
    {
        using var app = new App(_paths);

        Assert.True(IpcClient.TryShow(_paths));
        Assert.Equal("show", app.Next());
        Assert.True(IpcClient.TrySendLinks(_paths, ""));
        string text = "https://example.org/ä\n" + new string('x', 200_000);
        Assert.True(IpcClient.TrySendLinks(_paths, text));
        Assert.Equal("links:" + text, app.Next());
        Assert.Empty(app.Received);
    }

    [Fact]
    public void Socket_and_folder_are_private()
    {
        using var app = new App(_paths);

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(_paths.Socket));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
            File.GetUnixFileMode(_paths.Directory));
    }

    [Fact]
    public void Without_app_nothing_is_delivered_and_a_stale_socket_is_replaced()
    {
        Assert.False(IpcClient.TryShow(_paths));
        Directory.CreateDirectory(_paths.Directory);
        File.WriteAllText(_paths.Socket, "Rest eines Absturzes");
        Assert.False(IpcClient.TryShow(_paths));

        using (var app = new App(_paths)) Assert.True(IpcClient.TryShow(_paths));

        Assert.False(File.Exists(_paths.Socket));
        Assert.False(IpcClient.TryShow(_paths));
    }

    [Theory]
    [InlineData("format-c")]
    [InlineData("")]
    public void Unknown_messages_get_no_answer_and_the_server_keeps_running(string message)
    {
        using var app = new App(_paths);
        using (var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified))
        {
            socket.ReceiveTimeout = 5000;
            socket.Connect(new UnixDomainSocketEndPoint(_paths.Socket));
            using var stream = new NetworkStream(socket);
            NativeMessage.Write(stream, Encoding.UTF8.GetBytes(message));
            Assert.Null(NativeMessage.Read(stream));
        }
        using (var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified))
        {
            socket.Connect(new UnixDomainSocketEndPoint(_paths.Socket));
            socket.Send(new byte[] { 0xff, 0xff, 0xff, 0x7f }); // viel zu große Länge
        }

        Assert.True(IpcClient.TryShow(_paths));
        Assert.Equal("show", app.Next());
        Assert.Empty(app.Received);
    }

    [Fact]
    public void A_linked_runtime_folder_is_refused()
    {
        Directory.CreateDirectory(_dir.File("fremd"));
        Directory.CreateSymbolicLink(_paths.Directory, _dir.File("fremd"));

        Assert.Throws<IOException>(() => SingleInstanceLock.TryAcquire(_paths));
    }

    [Fact]
    public void Runtime_folder_follows_xdg_with_a_private_fallback()
    {
        var with = new XdgPaths(n => n == "XDG_RUNTIME_DIR" ? "/run/user/7" : n == "HOME" ? "/h" : null);
        var without = new XdgPaths(n => n == "HOME" ? "/h" : null);

        Assert.Equal("/run/user/7/d6downloader/ipc.sock", RuntimePaths.Resolve(with).Socket);
        Assert.Equal("/h/.config/d6downloader/run", RuntimePaths.Resolve(without).Directory);
    }

    /// <summary>Der echte Prozess, so wie ihn ein Browser startet: Nachrichten über stdin und stdout.</summary>
    [Fact]
    public void Native_host_process_answers_a_ping_and_hands_links_to_the_running_app()
    {
        var info = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true,
        };
        info.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "d6downloader.dll"));
        info.ArgumentList.Add(NativeHost.Argument);
        info.Environment["XDG_RUNTIME_DIR"] = _dir.Path;
        using var host = Process.Start(info)!;
        try
        {
            JsonElement Ask(string json)
            {
                NativeMessage.Write(host.StandardInput.BaseStream, Encoding.UTF8.GetBytes(json));
                var read = System.Threading.Tasks.Task.Run(() => NativeMessage.Read(host.StandardOutput.BaseStream));
                Assert.True(read.Wait(TimeSpan.FromSeconds(30)), "keine Antwort");
                return JsonDocument.Parse(read.Result!).RootElement;
            }

            var alone = Ask("{\"type\":\"ping\"}");
            Assert.True(alone.GetProperty("ok").GetBoolean());
            Assert.False(alone.GetProperty("running").GetBoolean());

            using var app = new App(_paths);
            Assert.True(Ask("{\"type\":\"ping\"}").GetProperty("running").GetBoolean());
            Assert.True(Ask("{\"type\":\"add\",\"text\":\"https://example.org/a.zip\"}").GetProperty("ok").GetBoolean());
            Assert.Equal("links:https://example.org/a.zip", app.Next());

            host.StandardInput.Close();
            Assert.True(host.WaitForExit(10_000));
            Assert.Equal(0, host.ExitCode);
        }
        finally
        {
            if (!host.HasExited) host.Kill();
        }
    }
}
