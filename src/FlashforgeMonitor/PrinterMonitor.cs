using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace FlashforgeMonitor;

public sealed record TempPoint(DateTimeOffset At, double Nozzle, double Bed);

public sealed record PrinterStatus
{
    public bool Online { get; init; }
    public string? Error { get; init; }
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.Now;
    public string? Model { get; init; }
    public string? Firmware { get; init; }
    public string? Serial { get; init; }
    /// <summary>idle, printing, paused, completed, busy, offline</summary>
    public string State { get; init; } = "offline";
    public string? MachineStatus { get; init; }
    public string? CurrentFile { get; init; }
    public bool? LedOn { get; init; }
    public double? NozzleTemp { get; init; }
    public double? NozzleTarget { get; init; }
    public double? BedTemp { get; init; }
    public double? BedTarget { get; init; }
    public int? Progress { get; init; }
    public int? Layer { get; init; }
    public int? LayerTotal { get; init; }
    public double? X { get; init; }
    public double? Y { get; init; }
    public double? Z { get; init; }
    public DateTimeOffset? PrintStartedAt { get; init; }
    public DateTimeOffset? EstimatedFinish { get; init; }
    public string? Activity { get; init; }
    public int? ActivityPercent { get; init; }
    public string? Host { get; init; }
    public string? MoveMode { get; init; }
    /// <summary>"http" (access code) or "tcp".</summary>
    public string Source { get; init; } = "tcp";
}

/// <summary>Polls the printer, keeps temperature history, and raises print events.</summary>
public sealed partial class PrinterMonitor(
    PrinterTcp tcp,
    FlashforgeHttp http,
    PrinterIdentity identity,
    SettingsStore settings,
    HistoryStore history,
    Notifier notifier,
    IOptionsMonitor<PrinterOptions> options,
    ILogger<PrinterMonitor> log) : BackgroundService
{
    private readonly LinkedList<TempPoint> _temps = new();
    private readonly Lock _lock = new();
    private (string? Model, string? Fw, string? Sn)? _info;
    private int _failures;
    private bool _announcedOffline;
    private PrinterStatus? _lastOnline;
    private string? _infoHost;
    private bool _httpWarned;

    // current job tracking
    private string? _jobFile;
    private DateTimeOffset? _jobStart;
    private int _jobStartProgress;
    private bool _jobSeenFromStart;

    public PrinterStatus Latest { get; private set; } = new() { Error = "Starting..." };

    public IReadOnlyList<TempPoint> Temps
    {
        get { lock (_lock) return _temps.ToList(); }
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                // Preferred: the HTTP API (needs access code + serial). Richer, and it doesn't use the
                // printer's single TCP control slot, so slicers and commands never get locked out.
                if (http.Ready && await PollHttpAsync(ct) is { } httpStatus)
                {
                    await OnStatusAsync(httpStatus, ct);
                    await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, options.CurrentValue.PollSeconds)), ct);
                    continue;
                }

                var (ran, status) = await tcp.SessionAsync(s => PollAsync(s, ct), ct, wait: false);
                if (!ran)
                {
                    // Another operation (e.g. an upload) holds the connection: show its progress.
                    Latest = Latest with { Activity = tcp.Activity, ActivityPercent = tcp.ActivityPercent, UpdatedAt = DateTimeOffset.Now };
                }
                else if (status is not null)
                {
                    await OnStatusAsync(status, ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (PrinterBusyException)
            {
                // Reachable, just refusing control for a moment (e.g. homing at print start, or a stale session).
                Latest = Latest with { Activity = "Reconnecting…", UpdatedAt = DateTimeOffset.Now };
            }
            catch (Exception ex) when (++_failures < 3 && Latest.Online)
            {
                // One or two slow/failed polls while printing is normal; keep showing the last good status.
                Latest = Latest with { Activity = "Printer slow to respond…", UpdatedAt = DateTimeOffset.Now };
                log.LogDebug("Transient poll failure: {Message}", ex.Message);
            }
            catch (Exception ex)
            {
                Latest = Latest with { Online = false, State = "offline", Error = ex.Message, UpdatedAt = DateTimeOffset.Now, Host = options.CurrentValue.Host };
                if (_failures == 1) log.LogWarning("Printer unreachable: {Message}", ex.Message);
                if (_failures >= 5 && !_announcedOffline)
                {
                    _announcedOffline = true;
                    await notifier.SendAsync("offline", "Printer offline", $"Can't reach the printer: {ex.Message}", ct);
                }
            }
            await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, options.CurrentValue.PollSeconds)), ct);
        }
    }

    private async Task OnStatusAsync(PrinterStatus s, CancellationToken ct)
    {
        if (_failures > 0) log.LogInformation("Printer reachable again");
        _failures = 0;
        if (_announcedOffline)
        {
            _announcedOffline = false;
            await notifier.SendAsync("online", "Printer back online", s.Model ?? "Printer", ct);
        }

        // Compare against the last *online* reading so short blips don't break job tracking.
        var prev = _lastOnline ?? Latest;
        var now = s.UpdatedAt;
        var wasActive = prev.State is "printing" or "paused";
        var isActive = s.State is "printing" or "paused";

        // --- job lifecycle ---
        if (isActive && (_jobFile is null || s.CurrentFile != _jobFile))
        {
            _jobFile = s.CurrentFile;
            _jobStart = s.PrintStartedAt ?? now; // HTTP gives the real start time
            _jobStartProgress = s.Progress ?? 0;
            var startedWhileWatching = prev.Online && !wasActive; // false when the app starts mid-print
            _jobSeenFromStart = s.PrintStartedAt is not null || startedWhileWatching;
            if (startedWhileWatching)
                await notifier.SendAsync("started", "Print started", s.CurrentFile ?? "Print job", ct);
        }
        if (prev.State == "printing" && s.State == "paused")
            await notifier.SendAsync("paused", "Print paused", $"{s.CurrentFile} at {s.Progress}%", ct);
        if (prev.State == "paused" && s.State == "printing")
            await notifier.SendAsync("resumed", "Print resumed", s.CurrentFile ?? "", ct);

        if (wasActive && !isActive && prev.Online)
        {
            var finished = s.State == "completed" || prev.Progress >= 99;
            var duration = _jobStart is { } st ? now - st : (TimeSpan?)null;
            var entry = new HistoryEntry(
                File: _jobFile ?? prev.CurrentFile ?? "unknown",
                StartedAt: _jobStart,
                EndedAt: now,
                DurationSeconds: duration is { } d ? (long)d.TotalSeconds : null,
                Result: finished ? "finished" : "cancelled",
                LastProgress: prev.Progress,
                StartTimeExact: _jobSeenFromStart);
            await history.AddAsync(entry, ct);
            var took = duration is { } dd ? $" in {Fmt(dd)}" : "";
            if (finished)
                await notifier.SendAsync("finished", "Print finished", $"{entry.File} finished{took}", ct);
            else
                await notifier.SendAsync("cancelled", "Print stopped", $"{entry.File} stopped at {prev.Progress}%{took}", ct);
            _jobFile = null;
            _jobStart = null;
        }

        // --- ETA from observed progress rate ---
        DateTimeOffset? eta = null;
        if (s.State == "printing" && _jobStart is { } js && s.Progress is { } p && p > _jobStartProgress && p < 100)
        {
            var elapsed = now - js;
            if (elapsed > TimeSpan.FromSeconds(60)) eta = now + elapsed / (p - _jobStartProgress) * (100 - p);
        }

        Latest = s.Source == "http"
            ? s // printer-reported start/finish times
            : s with
            {
                PrintStartedAt = isActive && _jobSeenFromStart ? _jobStart : null,
                EstimatedFinish = eta,
            };
        _lastOnline = Latest;

        if (s is { NozzleTemp: { } n, BedTemp: { } b })
        {
            lock (_lock)
            {
                _temps.AddLast(new TempPoint(now, n, b));
                while (_temps.Count > 1200) _temps.RemoveFirst(); // ~1 h at 3 s
            }
        }
    }

    private async Task<PrinterStatus> PollAsync(TcpSession s, CancellationToken ct)
    {
        if (_infoHost != options.CurrentValue.Host) { _info = null; _infoHost = options.CurrentValue.Host; }
        if (_info is null)
        {
            var m115 = await s.CommandAsync("M115", ct);
            _info = (Field(m115, "Machine Type"), Field(m115, "Firmware"), Field(m115, "SN"));
            await RememberSerialAsync(_info.Value.Sn, ct);
        }
        var m105 = await s.CommandAsync("M105", ct);
        var m119 = await s.CommandAsync("M119", ct);
        var m27 = await s.CommandAsync("M27", ct);
        var m114 = await s.CommandAsync("M114", ct);

        var t = TempRx().Match(m105);
        var b = BedRx().Match(m105);
        var prog = ProgressRx().Match(m27);
        var layer = LayerRx().Match(m27);
        var pos = PosRx().Match(m114);
        var machine = Field(m119, "MachineStatus") ?? "";
        var file = Field(m119, "CurrentFile");
        int? progress = prog.Success && int.Parse(prog.Groups[2].Value) > 0
            ? (int)Math.Round(100.0 * int.Parse(prog.Groups[1].Value) / int.Parse(prog.Groups[2].Value))
            : null;
        var state = machine switch
        {
            _ when machine.Contains("PAUSED", StringComparison.OrdinalIgnoreCase) => "paused",
            _ when machine.Contains("COMPLETED", StringComparison.OrdinalIgnoreCase) => "completed",
            _ when machine.Contains("BUILDING", StringComparison.OrdinalIgnoreCase) => "printing",
            _ when machine.Contains("READY", StringComparison.OrdinalIgnoreCase) => "idle",
            "" => "unknown",
            _ => "busy",
        };

        return new PrinterStatus
        {
            Online = true,
            UpdatedAt = DateTimeOffset.Now,
            Model = _info?.Model,
            Firmware = _info?.Fw,
            Serial = _info?.Sn,
            State = state,
            MachineStatus = machine,
            MoveMode = Field(m119, "MoveMode"),
            Host = options.CurrentValue.Host,
            CurrentFile = string.IsNullOrWhiteSpace(file) ? null : file,
            LedOn = Field(m119, "LED") switch { "1" => true, "0" => false, _ => null },
            NozzleTemp = t.Success ? D(t.Groups[1].Value) : null,
            NozzleTarget = t.Success ? D(t.Groups[2].Value) : null,
            BedTemp = b.Success ? D(b.Groups[1].Value) : null,
            BedTarget = b.Success ? D(b.Groups[2].Value) : null,
            Progress = progress,
            Layer = layer.Success ? int.Parse(layer.Groups[1].Value) : null,
            LayerTotal = layer.Success ? int.Parse(layer.Groups[2].Value) : null,
            X = pos.Success ? D(pos.Groups[1].Value) : null,
            Y = pos.Success ? D(pos.Groups[2].Value) : null,
            Z = pos.Success ? D(pos.Groups[3].Value) : null,
        };
    }

    /// <summary>The HTTP API needs the serial; save it so HTTP works right after a restart without TCP.</summary>
    private async Task RememberSerialAsync(string? sn, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(sn)) return;
        identity.Serial = sn;
        if (string.IsNullOrWhiteSpace(options.CurrentValue.SerialNumber))
        {
            try { await settings.UpdateAsync(r => SettingsStore.Section(r, "Printer")["SerialNumber"] = sn, ct); }
            catch (Exception ex) { log.LogDebug("Could not save serial: {Message}", ex.Message); }
        }
    }

    private async Task<PrinterStatus?> PollHttpAsync(CancellationToken ct)
    {
        var r = await http.DetailAsync(ct);
        if (r["code"]?.GetValue<int>() != 0 || r["detail"] is not JsonObject d)
        {
            if (!_httpWarned) log.LogWarning("HTTP API: {Message} (falling back to TCP)", r["message"]?.ToString());
            _httpWarned = true;
            return null;
        }
        _httpWarned = false;

        static double? Num(JsonObject d, string k) =>
            d[k] is JsonValue v && v.TryGetValue<double>(out var x) ? x : null;
        static string? Str(JsonObject d, string k) => d[k]?.ToString();

        var raw = (Str(d, "status") ?? "").ToLowerInvariant();
        var state = raw switch
        {
            "printing" => "printing",
            "pausing" or "paused" or "pause" => "paused",
            "completed" or "complete" => "completed",
            "ready" or "idle" => "idle",
            "error" => "error",
            "" => "unknown",
            _ => "busy", // heating, calibrating, cancelling, busy…
        };
        var pid = (int?)Num(d, "pid");
        var model = pid switch
        {
            0x23 => "Flashforge Adventurer 5M",
            0x24 => "Flashforge Adventurer 5M Pro",
            0x26 => "Flashforge AD5X",
            _ => _info?.Model ?? "Flashforge printer",
        };
        var now = DateTimeOffset.Now;
        var file = Str(d, "printFileName");
        var active = state is "printing" or "paused";
        var progress = Num(d, "printProgress") is { } p ? (int?)Math.Round(p * 100) : null;
        var left = Num(d, "estimatedTime");
        var elapsed = Num(d, "printDuration");

        return new PrinterStatus
        {
            Online = true,
            UpdatedAt = now,
            Model = model,
            Firmware = Str(d, "firmwareVersion") is { } fw ? "v" + fw.TrimStart('v') : _info?.Fw,
            Serial = http.Serial,
            Host = options.CurrentValue.Host,
            State = state,
            MachineStatus = raw.ToUpperInvariant(),
            CurrentFile = active || state == "completed" ? file : null,
            LedOn = Str(d, "lightStatus") is { } l ? l == "open" : null,
            NozzleTemp = Num(d, "rightTemp"),
            NozzleTarget = Num(d, "rightTargetTemp"),
            BedTemp = Num(d, "platTemp"),
            BedTarget = Num(d, "platTargetTemp"),
            Progress = progress,
            Layer = (int?)Num(d, "printLayer"),
            LayerTotal = (int?)Num(d, "targetPrintLayer"),
            // The printer's own numbers beat our estimate.
            PrintStartedAt = active && elapsed is { } e ? now.AddSeconds(-e) : null,
            EstimatedFinish = active && left is { } t && t > 0 ? now.AddSeconds(t) : null,
            Source = "http",
        };
    }

    private static string Fmt(TimeSpan d) =>
        d.TotalHours >= 1 ? $"{(int)d.TotalHours}h {d.Minutes}m" : $"{d.Minutes}m {d.Seconds}s";

    // [ \t]* (not \s*) so an empty field doesn't swallow the next line.
    private static string? Field(string text, string name)
    {
        var m = Regex.Match(text, $@"^{Regex.Escape(name)}:[ \t]*([^\r\n]*?)[ \t]*\r?$", RegexOptions.Multiline);
        return m.Success ? m.Groups[1].Value : null;
    }

    private static double D(string v) => double.Parse(v, CultureInfo.InvariantCulture);

    [GeneratedRegex(@"T0:\s*(-?[\d.]+)\s*/\s*(-?[\d.]+)")] private static partial Regex TempRx();
    [GeneratedRegex(@"B:\s*(-?[\d.]+)\s*/\s*(-?[\d.]+)")] private static partial Regex BedRx();
    [GeneratedRegex(@"byte\s+(\d+)\s*/\s*(\d+)")] private static partial Regex ProgressRx();
    [GeneratedRegex(@"Layer:\s*(\d+)\s*/\s*(\d+)")] private static partial Regex LayerRx();
    [GeneratedRegex(@"X:\s*(-?[\d.]+)\s+Y:\s*(-?[\d.]+)\s+Z:\s*(-?[\d.]+)")] private static partial Regex PosRx();
}
