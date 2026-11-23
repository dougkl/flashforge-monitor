using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace FlashforgeMonitor;

/// <summary>
/// Talks to the printer's TCP control port (8899). This works without LAN-only mode.
/// Protocol: ASCII G-code prefixed with "~", replies end with "ok". The printer only
/// handles one control session well, so every operation goes through one gate.
/// Command set based on the MIT-licensed flashforge-python-api by GhostTypes.
/// </summary>
public sealed partial class PrinterTcp(IOptionsMonitor<PrinterOptions> options)
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>What a long-running operation is doing right now (e.g. "Uploading x.gcode").</summary>
    public string? Activity { get; private set; }
    public int? ActivityPercent { get; private set; }

    /// <summary>Runs work inside a logged-in control session. Returns default if busy and wait == false.</summary>
    public async Task<(bool Ran, T? Result)> SessionAsync<T>(
        Func<TcpSession, Task<T>> work, CancellationToken ct, bool wait = true)
    {
        if (wait) await _gate.WaitAsync(ct);
        else if (!await _gate.WaitAsync(0, ct)) return (false, default);
        try
        {
            var opt = options.CurrentValue;
            if (string.IsNullOrWhiteSpace(opt.Host))
                throw new InvalidOperationException("Printer:Host is not configured.");

            using var client = new TcpClient { NoDelay = true };
            using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                cts.CancelAfter(TimeSpan.FromSeconds(4));
                await client.ConnectAsync(opt.Host, opt.TcpPort, cts.Token);
            }
            await using var stream = client.GetStream();
            var session = new TcpSession(stream);
            var login = await session.CommandAsync("M601 S1", ct);
            if (login.Contains("failed", StringComparison.OrdinalIgnoreCase))
                throw new PrinterBusyException("Printer is busy (homing, leveling, or another app has control). Try again in a moment.");
            try
            {
                return (true, await work(session));
            }
            finally
            {
                try { await session.CommandAsync("M602", CancellationToken.None, TimeSpan.FromSeconds(2)); }
                catch { /* best effort */ }
            }
        }
        finally
        {
            Activity = null;
            ActivityPercent = null;
            _gate.Release();
        }
    }

    public async Task<string> RunAsync(string command, CancellationToken ct)
    {
        var (_, reply) = await SessionAsync(s => s.CommandAsync(command, ct), ct);
        if (reply is null || FailRx().IsMatch(reply))
            throw new InvalidOperationException($"Printer rejected {command}: {Clean(reply)}");
        return Clean(reply);
    }

    public Task<string> PauseAsync(CancellationToken ct) => RunAsync("M25", ct);
    public Task<string> ResumeAsync(CancellationToken ct) => RunAsync("M24", ct);
    public Task<string> CancelAsync(CancellationToken ct) => RunAsync("M26", ct);
    public Task<string> HomeAsync(CancellationToken ct) => RunAsync("G28", ct);
    public Task<string> LightAsync(bool on, CancellationToken ct) =>
        RunAsync(on ? "M146 r255 g255 b255 F0" : "M146 r0 g0 b0 F0", ct);
    public Task<string> NozzleTempAsync(int c, CancellationToken ct) => RunAsync($"M104 S{Math.Clamp(c, 0, 280)}", ct);
    public Task<string> BedTempAsync(int c, CancellationToken ct) => RunAsync($"M140 S{Math.Clamp(c, 0, 110)}", ct);
    /// <summary>Starts a file already on the printer. Files live in /data/; older firmware used 0:/user/.</summary>
    public async Task<string> StartFileAsync(string name, CancellationToken ct)
    {
        name = SafeName(name);
        var (_, reply) = await SessionAsync(async s =>
        {
            var r = await s.CommandAsync($"M23 /data/{name}", ct, TimeSpan.FromSeconds(10));
            if (FailRx().IsMatch(r)) r = await s.CommandAsync($"M23 0:/user/{name}", ct, TimeSpan.FromSeconds(10));
            return r;
        }, ct);
        if (reply is null || FailRx().IsMatch(reply))
            throw new InvalidOperationException($"Printer could not start {name}: {Clean(reply)}");
        return $"Started {name}";
    }
    public Task<string> EmergencyStopAsync(CancellationToken ct) => RunAsync("M112", ct);
    public Task<string> MotorsOffAsync(CancellationToken ct) => RunAsync("M18", ct);
    public Task<string> FanAsync(int percent, CancellationToken ct) =>
        RunAsync(percent <= 0 ? "M107" : $"M106 S{(int)Math.Round(Math.Clamp(percent, 0, 100) * 2.55)}", ct);
    public Task<string> SpeedAsync(int percent, CancellationToken ct) => RunAsync($"M220 S{Math.Clamp(percent, 10, 300)}", ct);
    public Task<string> FlowAsync(int percent, CancellationToken ct) => RunAsync($"M221 S{Math.Clamp(percent, 50, 200)}", ct);
    public Task<string> RunoutSensorAsync(bool on, CancellationToken ct) => RunAsync(on ? "M405" : "M406", ct);

    /// <summary>Relative move of one axis (mm). Uses G91/G90 so it never jumps to an absolute position.</summary>
    public async Task<string> JogAsync(string axis, double mm, CancellationToken ct)
    {
        axis = axis.ToUpperInvariant();
        if (axis is not ("X" or "Y" or "Z" or "E")) throw new ArgumentException("Axis must be X, Y, Z or E");
        mm = Math.Clamp(mm, -100, 100);
        var feed = axis switch { "Z" => 600, "E" => 300, _ => 3000 };
        var (_, reply) = await SessionAsync(async s =>
        {
            await s.CommandAsync("G91", ct);
            try { return await s.CommandAsync(FormattableString.Invariant($"G1 {axis}{mm:0.##} F{feed}"), ct, TimeSpan.FromSeconds(30)); }
            finally { await s.CommandAsync("G90", ct); }
        }, ct);
        return Clean(reply);
    }

    /// <summary>Sends any command (console). Returns the raw text reply.</summary>
    public async Task<string> RawCommandAsync(string command, CancellationToken ct)
    {
        command = command.Trim().TrimStart('~').Replace("\r", "").Replace("\n", " ");
        if (command.Length == 0) throw new ArgumentException("Empty command");
        var (_, reply) = await SessionAsync(s => s.RawAsync(command, ct, TimeSpan.FromSeconds(15), untilIdle: false), ct);
        var text = Encoding.Latin1.GetString(reply ?? []);
        return new string(text.Select(c => c is '\r' or '\n' or '\t' || (c >= ' ' && c < 127) ? c : '.').ToArray()).Trim();
    }

    public async Task<List<string>> ListFilesAsync(CancellationToken ct)
    {
        var (_, raw) = await SessionAsync(s => s.RawAsync("M661", ct, TimeSpan.FromSeconds(10), untilIdle: true), ct);
        var text = Encoding.Latin1.GetString(raw ?? []);
        var files = new List<string>();
        foreach (var segment in text.Split("::"))
        {
            var i = segment.IndexOf("/data/", StringComparison.Ordinal);
            if (i < 0) continue;
            var name = segment[(i + 6)..];
            var bad = BadCharRx().Match(name);
            if (bad.Success) name = name[..bad.Index];
            if (!string.IsNullOrWhiteSpace(name)) files.Add(name);
        }
        return files;
    }

    public async Task<byte[]?> ThumbnailAsync(string name, CancellationToken ct)
    {
        var (_, raw) = await SessionAsync(async s =>
        {
            var r = await s.RawAsync($"M662 /data/{SafeName(name)}", ct, TimeSpan.FromSeconds(10), untilIdle: true);
            if (ExtractPng(r) is null && Encoding.Latin1.GetString(r).Contains("not exist", StringComparison.OrdinalIgnoreCase))
                r = await s.RawAsync($"M662 {SafeName(name)}", ct, TimeSpan.FromSeconds(10), untilIdle: true);
            return r;
        }, ct);
        return ExtractPng(raw);
    }

    /// <summary>Uploads over TCP: M28 size 0:/user/name, raw bytes, M29.</summary>
    public async Task<string> UploadAsync(string name, Stream data, long size, bool printNow, CancellationToken ct)
    {
        name = SafeName(name);
        var (_, result) = await SessionAsync(async s =>
        {
            Activity = $"Uploading {name}";
            ActivityPercent = 0;
            var start = await s.CommandAsync($"M28 {size} 0:/user/{name}", ct, TimeSpan.FromSeconds(15));
            if (FailRx().IsMatch(start)) throw new InvalidOperationException($"Printer refused upload: {Clean(start)}");

            var buf = new byte[64 * 1024];
            long sent = 0;
            int n;
            while ((n = await data.ReadAsync(buf, ct)) > 0)
            {
                await s.Stream.WriteAsync(buf.AsMemory(0, n), ct);
                sent += n;
                ActivityPercent = (int)(100 * sent / Math.Max(1, size));
            }
            Activity = $"Finishing {name}";
            var end = await s.CommandAsync("M29", ct, TimeSpan.FromSeconds(120));
            if (FailRx().IsMatch(end)) throw new InvalidOperationException($"Upload failed: {Clean(end)}");

            if (printNow)
            {
                var go = await s.CommandAsync($"M23 /data/{name}", ct, TimeSpan.FromSeconds(10));
                if (FailRx().IsMatch(go)) go = await s.CommandAsync($"M23 0:/user/{name}", ct, TimeSpan.FromSeconds(10));
                if (FailRx().IsMatch(go)) throw new InvalidOperationException($"Uploaded, but start failed: {Clean(go)}");
                return $"Uploaded {name} and started printing";
            }
            return $"Uploaded {name}";
        }, ct);
        return result ?? "Upload did not run";
    }

    private static byte[]? ExtractPng(byte[]? raw)
    {
        if (raw is null || raw.Length < 16) return null;
        var span = raw.AsSpan();
        // Newer firmware: 4-byte marker (a2a22a2a or 2a2aa2a2 depending on version) + 4-byte big-endian length + PNG
        var magic = span.IndexOf([(byte)0xa2, (byte)0xa2, (byte)0x2a, (byte)0x2a]);
        if (magic < 0) magic = span.IndexOf([(byte)0x2a, (byte)0x2a, (byte)0xa2, (byte)0xa2]);
        if (magic >= 0 && raw.Length >= magic + 8 && raw[magic + 4] == 0 && raw[magic + 5] == 0 && raw[magic + 6] == 0 && raw[magic + 7] == 0)
            return null; // file has no thumbnail
        if (magic >= 0 && raw.Length >= magic + 8)
        {
            var len = (raw[magic + 4] << 24) | (raw[magic + 5] << 16) | (raw[magic + 6] << 8) | raw[magic + 7];
            if (len > 0 && raw.Length >= magic + 8 + len) return raw[(magic + 8)..(magic + 8 + len)];
        }
        var png = span.IndexOf([(byte)0x89, (byte)'P', (byte)'N', (byte)'G', (byte)0x0d, (byte)0x0a, (byte)0x1a, (byte)0x0a]);
        return png >= 0 ? raw[png..] : null;
    }

    private static string SafeName(string name)
    {
        name = name.Replace('\\', '/');
        name = Regex.Replace(name, "^(0:/user/|/data/)", "", RegexOptions.IgnoreCase);
        name = Path.GetFileName(name).Replace("\r", "").Replace("\n", "").Replace("~", "");
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Invalid file name");
        return name;
    }

    private static string Clean(string? reply) =>
        Regex.Replace(reply ?? "", @"CMD M\d+ Received\.|\bok\b", "").Trim();

    [GeneratedRegex(@"error|failed|not exist|not available|cannot|not enough space", RegexOptions.IgnoreCase)]
    private static partial Regex FailRx();

    [GeneratedRegex(@"[^\w\s\-\.\(\)\+%,@\[\]{}:;!#$^&*=<>?\/]")]
    private static partial Regex BadCharRx();
}

public sealed class PrinterBusyException(string message) : Exception(message);

public sealed class TcpSession(NetworkStream stream)
{
    public NetworkStream Stream => stream;

    public async Task<string> CommandAsync(string cmd, CancellationToken ct, TimeSpan? timeout = null) =>
        Encoding.Latin1.GetString(await RawAsync(cmd, ct, timeout ?? TimeSpan.FromSeconds(4), untilIdle: false));

    /// <summary>
    /// Sends a command and reads the reply. Text replies end with "ok". Binary replies
    /// (file list, thumbnails) continue after "ok", so with untilIdle we read until the line goes quiet.
    /// </summary>
    public async Task<byte[]> RawAsync(string cmd, CancellationToken ct, TimeSpan timeout, bool untilIdle)
    {
        var bytes = Encoding.ASCII.GetBytes($"~{cmd}\r\n");
        await stream.WriteAsync(bytes, ct);

        using var total = CancellationTokenSource.CreateLinkedTokenSource(ct);
        total.CancelAfter(timeout);
        using var ms = new MemoryStream();
        var buf = new byte[16 * 1024];
        var sawOk = false;
        while (true)
        {
            using var read = CancellationTokenSource.CreateLinkedTokenSource(total.Token);
            if (ms.Length > 0) read.CancelAfter(untilIdle && sawOk ? 700 : 1500);
            int n;
            try
            {
                n = await stream.ReadAsync(buf, read.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                if (ms.Length > 0) return ms.ToArray(); // reply went quiet: done
                throw new TimeoutException($"No reply to {cmd}");
            }
            if (n == 0) throw new IOException("Printer closed the connection");
            ms.Write(buf, 0, n);

            if (!untilIdle)
            {
                var text = Encoding.Latin1.GetString(ms.GetBuffer(), 0, (int)ms.Length);
                if (text.TrimEnd().EndsWith("ok", StringComparison.Ordinal)) return ms.ToArray();
            }
            else if (!sawOk)
            {
                var head = Encoding.Latin1.GetString(ms.GetBuffer(), 0, (int)Math.Min(ms.Length, 4096));
                sawOk = head.Contains("ok", StringComparison.Ordinal);
            }
        }
    }
}
