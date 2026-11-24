using System.Threading.Channels;
using Microsoft.Extensions.Options;

namespace FlashforgeMonitor;

/// <summary>
/// The printer's camera (mjpg-streamer on :8080) only serves one client at a time.
/// This hub keeps a single upstream connection while anyone is watching, splits it into
/// JPEG frames, and fans them out to every viewer. It disconnects when the last viewer leaves.
/// </summary>
public sealed class CameraHub(IHttpClientFactory http, IOptionsMonitor<PrinterOptions> options, ILogger<CameraHub> log)
{
    private readonly Lock _lock = new();
    private readonly List<Channel<byte[]>> _viewers = [];
    private CancellationTokenSource? _pumpCts;
    private Task? _pump;

    public byte[]? LastFrame { get; private set; }
    public DateTimeOffset LastFrameAt { get; private set; }
    public string? LastError { get; private set; }
    public int Viewers { get { lock (_lock) return _viewers.Count; } }

    public (ChannelReader<byte[]> Reader, Action Unsubscribe) Subscribe()
    {
        var ch = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(2)
        {
            FullMode = BoundedChannelFullMode.DropOldest, // slow viewers skip frames instead of lagging
            SingleReader = true,
        });
        lock (_lock)
        {
            _viewers.Add(ch);
            if (_pump is null || _pump.IsCompleted)
            {
                _pumpCts = new CancellationTokenSource();
                var token = _pumpCts.Token;
                _pump = Task.Run(() => PumpAsync(token));
            }
        }
        return (ch.Reader, () =>
        {
            lock (_lock)
            {
                _viewers.Remove(ch);
                ch.Writer.TryComplete();
                if (_viewers.Count == 0) _pumpCts?.Cancel(); // free the printer's single camera slot
            }
        });
    }

    /// <summary>Latest frame (fresh if someone is watching), otherwise grabs one.</summary>
    public async Task<byte[]?> SnapshotAsync(CancellationToken ct)
    {
        if (LastFrame is { } f && DateTimeOffset.Now - LastFrameAt < TimeSpan.FromSeconds(3)) return f;
        var (reader, unsubscribe) = Subscribe();
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(8));
            return await reader.ReadAsync(cts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return null; }
        catch (ChannelClosedException) { return null; }
        finally { unsubscribe(); }
    }

    private async Task PumpAsync(CancellationToken ct)
    {
        var soi = new byte[] { 0xFF, 0xD8 };
        var eoi = new byte[] { 0xFF, 0xD9 };
        while (!ct.IsCancellationRequested)
        {
            var opt = options.CurrentValue;
            try
            {
                using var resp = await http.CreateClient("camera").GetAsync(
                    $"http://{opt.Host}:{opt.CameraPort}/?action=stream", HttpCompletionOption.ResponseHeadersRead, ct);
                resp.EnsureSuccessStatusCode();
                await using var stream = await resp.Content.ReadAsStreamAsync(ct);
                LastError = null;
                log.LogInformation("Camera stream connected");

                var buf = new byte[64 * 1024];
                var acc = new List<byte>(256 * 1024);
                int n;
                while ((n = await stream.ReadAsync(buf, ct)) > 0)
                {
                    acc.AddRange(buf.AsSpan(0, n));
                    while (true)
                    {
                        var span = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(acc);
                        var start = span.IndexOf(soi);
                        if (start < 0) { if (acc.Count > 1) acc.RemoveRange(0, acc.Count - 1); break; }
                        var endRel = span[(start + 2)..].IndexOf(eoi);
                        if (endRel < 0)
                        {
                            if (start > 0) acc.RemoveRange(0, start);
                            if (acc.Count > 8 * 1024 * 1024) acc.Clear(); // garbage guard
                            break;
                        }
                        var end = start + 2 + endRel + 2;
                        var frame = span[start..end].ToArray();
                        acc.RemoveRange(0, end);
                        Publish(frame);
                    }
                }
                throw new IOException("Camera stream ended");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                LastError = ex.Message;
                log.LogDebug("Camera stream error: {Message}", ex.Message);
                try { await Task.Delay(TimeSpan.FromSeconds(2), ct); } catch (OperationCanceledException) { break; }
            }
        }
        log.LogInformation("Camera stream closed (no viewers)");
    }

    private void Publish(byte[] frame)
    {
        LastFrame = frame;
        LastFrameAt = DateTimeOffset.Now;
        lock (_lock)
            foreach (var v in _viewers) v.Writer.TryWrite(frame);
    }
}
