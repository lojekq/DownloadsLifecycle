using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Windows;
using DownloadsLifecycle.Shell;

namespace DownloadsLifecycle.Features;

/// <summary>
/// The --ttl verb and its pipe. Explorer starts one process per selected file ("Lifetime → 7 days"); each hands
/// "7d\npath" to the tray over a per-user pipe and exits. No tray running: one of them starts it (--background) and waits for the pipe.
/// The tray collects what arrives within 500 ms into one batch and one balloon. Shell/SingleInstance is untouched (00-shared §2.4).
/// </summary>
public static class TtlPipe
{
    static readonly string Name = $"lojekq.{AppInfo.InstanceName}.ttl"; // a dev instance's verbs never reach the installed tray
    static readonly List<(TtlCommand Cmd, string Path)> Batch = [];
    static readonly CancellationTokenSource Stopping = new();

    /// <summary>--ttl &lt;1d|7d|30d|keep|clear|yyyy-MM-dd&gt; &lt;path&gt;… → 0 delivered, 1 no tray, 2 bad arguments. Writes nothing to the console.</summary>
    public static int Verb(string[] args)
    {
        if (args.Length < 2 || TtlCommand.Parse(args[0]) is null) return 2;
        var message = string.Join('\n', args.Select((a, i) => i == 0 ? a : Path.GetFullPath(a)));
        if (Send(message, 1000)) return 0;
        using var starting = new Mutex(false, @$"Local\lojekq.{AppInfo.InstanceName}.ttl-start"); // 100 processes, one tray start
        try { starting.WaitOne(TimeSpan.FromSeconds(30)); } catch (AbandonedMutexException) { }
        try
        {
            if (Send(message, 300)) return 0; // another verb process started it meanwhile
            Process.Start(new ProcessStartInfo(AppInfo.ExePath, "--background") { UseShellExecute = false });
            for (var sw = Stopwatch.StartNew(); sw.Elapsed < TimeSpan.FromSeconds(20);)
                if (Send(message, 500)) return 0;
            Log.Warn("the tray did not open the pipe");
            return 1;
        }
        finally { starting.ReleaseMutex(); }
    }

    static bool Send(string message, int timeoutMs)
    {
        try
        {
            using var c = new NamedPipeClientStream(".", Name, PipeDirection.InOut, PipeOptions.CurrentUserOnly);
            c.Connect(timeoutMs);
            var bytes = Encoding.UTF8.GetBytes(message);
            c.Write(BitConverter.GetBytes(bytes.Length));
            c.Write(bytes);
            c.Flush();
            return c.ReadByte() == 1;
        }
        catch (Exception e) when (e is TimeoutException or IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>In the tray: four listeners, so a burst of 100 Explorer processes doesn't queue on one.</summary>
    public static void Serve()
    {
        for (var n = 0; n < 4; n++) _ = Task.Run(Listen);
    }

    public static void Stop() => Stopping.Cancel();

    static async Task Listen()
    {
        while (!Stopping.IsCancellationRequested)
        {
            try
            {
                await using var s = new NamedPipeServerStream(Name, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await s.WaitForConnectionAsync(Stopping.Token);
                var len = new byte[4];
                await s.ReadExactlyAsync(len, Stopping.Token);
                var n = BitConverter.ToInt32(len);
                if (n is <= 0 or > 1 << 20) continue;
                var body = new byte[n];
                await s.ReadExactlyAsync(body, Stopping.Token);
                s.WriteByte(1);
                Receive(Encoding.UTF8.GetString(body));
            }
            catch (OperationCanceledException) { return; }
            catch (Exception e) when (e is IOException or EndOfStreamException) { Log.Warn("ttl pipe", e); }
        }
    }

    static void Receive(string message)
    {
        var lines = message.Split('\n');
        if (TtlCommand.Parse(lines[0]) is not { } cmd) return;
        lock (Batch)
        {
            var first = Batch.Count == 0;
            Batch.AddRange(lines.Skip(1).Where(l => l.Length > 0).Select(p => (cmd, p)));
            if (first) _ = Task.Delay(500).ContinueWith(_ => Flush());
        }
    }

    static async Task Flush()
    {
        List<(TtlCommand Cmd, string Path)> batch;
        lock (Batch) { batch = [.. Batch]; Batch.Clear(); }
        foreach (var g in batch.GroupBy(b => b.Cmd))
        {
            var (done, outside) = await Engine.ApplyPaths([.. g.Select(b => b.Path).Distinct(StringComparer.OrdinalIgnoreCase)], g.Key);
            var text = (done > 0 ? string.Format(Loc.T("Notify.Ttl"), Fmt.Command(g.Key), Fmt.Files(done)) : "")
                + (outside > 0 ? (done > 0 ? "\n" : "") + string.Format(Loc.T("Notify.TtlOutside"), Fmt.Files(outside)) : "");
            Application.Current?.Dispatcher.BeginInvoke(() => Tray.Notify(AppInfo.DisplayName, text, "files"));
        }
    }
}
