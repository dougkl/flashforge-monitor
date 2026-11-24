using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using FlashforgeMonitor;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Options;

// Published builds ship wwwroot next to the executable; use that folder so it runs from any working directory.
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = Directory.Exists(Path.Combine(AppContext.BaseDirectory, "wwwroot"))
        ? AppContext.BaseDirectory
        : Directory.GetCurrentDirectory(),
});
builder.Configuration.AddJsonFile("appsettings.local.json", optional: true, reloadOnChange: true);
builder.Configuration.AddEnvironmentVariables("FFM_");

// Settings saved from the web UI win over everything else (incl. env vars), so the UI is always the truth.
var dataDir = Path.GetFullPath(builder.Configuration["Monitor:DataDirectory"] ?? "data", builder.Environment.ContentRootPath);
Directory.CreateDirectory(dataDir);
var settingsPath = Path.Combine(dataDir, "settings.json");
builder.Configuration.AddJsonFile(settingsPath, optional: true, reloadOnChange: true);
builder.Services.AddSingleton(new SettingsStore(settingsPath));

builder.Services.Configure<PrinterOptions>(builder.Configuration.GetSection("Printer"));
builder.Services.Configure<MonitorOptions>(builder.Configuration.GetSection("Monitor"));
builder.Services.Configure<NotificationOptions>(builder.Configuration.GetSection("Notifications"));

builder.Services.AddSingleton<PrinterTcp>();
builder.Services.AddSingleton<CameraHub>();
builder.Services.AddSingleton<PrinterIdentity>();
builder.Services.AddSingleton<HistoryStore>();
builder.Services.AddSingleton<Notifier>();
builder.Services.AddSingleton<PrinterMonitor>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<PrinterMonitor>());
builder.Services.AddHttpClient<FlashforgeHttp>(c => c.Timeout = TimeSpan.FromSeconds(15))
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(4) });
builder.Services.AddHttpClient("camera", c => c.Timeout = Timeout.InfiniteTimeSpan)
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(3) });
builder.Services.AddHttpClient("notify", c => c.Timeout = TimeSpan.FromSeconds(10));

const long MaxUpload = 1L << 30; // 1 GB of G-code is plenty
builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = MaxUpload);
builder.Services.Configure<FormOptions>(o => o.MultipartBodyLengthLimit = MaxUpload);

var app = builder.Build();

if (string.IsNullOrWhiteSpace(app.Services.GetRequiredService<IOptionsMonitor<PrinterOptions>>().CurrentValue.Host))
    app.Logger.LogWarning("Printer:Host is not set. Set it in appsettings.local.json or with FFM_Printer__Host=<ip>.");

// ---- optional HTTP Basic auth for the whole app ----
app.Use(async (ctx, next) =>
{
    var mon = ctx.RequestServices.GetRequiredService<IOptionsMonitor<MonitorOptions>>().CurrentValue;
    if (string.IsNullOrEmpty(mon.Password) || ctx.Request.Path.StartsWithSegments("/healthz"))
    {
        await next();
        return;
    }
    var header = ctx.Request.Headers.Authorization.ToString();
    if (header.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
    {
        try
        {
            var raw = Encoding.UTF8.GetString(Convert.FromBase64String(header[6..].Trim()));
            var sep = raw.IndexOf(':');
            if (sep > 0 && FixedEq(raw[..sep], mon.Username) && FixedEq(raw[(sep + 1)..], mon.Password))
            {
                await next();
                return;
            }
        }
        catch (FormatException) { }
    }
    ctx.Response.Headers.WWWAuthenticate = "Basic realm=\"FlashForge Monitor\"";
    ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
});

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/healthz", () => Results.Ok("ok"));

// ---- status ----
app.MapGet("/api/status", (PrinterMonitor m) => m.Latest);
app.MapGet("/api/temps", (PrinterMonitor m) => m.Temps);
app.MapGet("/api/config", (IOptionsMonitor<PrinterOptions> o, FlashforgeHttp ff) => new
{
    o.CurrentValue.Host,
    Configured = !string.IsNullOrWhiteSpace(o.CurrentValue.Host),
    LanMode = ff.Enabled,
    Version = typeof(Program).Assembly.GetName().Version?.ToString(3),
});
app.MapGet("/api/detail", async (FlashforgeHttp ff, CancellationToken ct) => await ff.DetailCachedAsync(ct));

// ---- history ----
app.MapGet("/api/history", async (HistoryStore h, CancellationToken ct) => await h.GetAsync(ct));
app.MapDelete("/api/history", async (HistoryStore h, CancellationToken ct) =>
{
    await h.ClearAsync(ct);
    return Results.NoContent();
});

// ---- files ----
// Prefer the HTTP API (no TCP control slot needed); fall back to TCP M661/M662.
app.MapGet("/api/files", async (PrinterTcp p, FlashforgeHttp ff, CancellationToken ct) =>
{
    if (ff.Ready)
    {
        var r = await ff.PostAsync("/gcodeList", null, ct);
        if (r["code"]?.GetValue<int>() == 0)
        {
            var list = (r["gcodeListDetail"] ?? r["gcodeList"]) as JsonArray ?? [];
            return Results.Ok(list.Select(x => x is JsonObject o ? o["gcodeFileName"]?.ToString() : x?.ToString())
                .Where(n => !string.IsNullOrEmpty(n)).ToList());
        }
    }
    return await Run(() => p.ListFilesAsync(ct));
});
app.MapGet("/api/thumb", async (string name, PrinterTcp p, FlashforgeHttp ff, CancellationToken ct) =>
{
    try
    {
        if (ff.Ready)
        {
            var r = await ff.PostAsync("/gcodeThumb", new JsonObject { ["fileName"] = name }, ct);
            if (r["code"]?.GetValue<int>() == 0 && r["imageData"]?.ToString() is { Length: > 0 } b64)
                return Results.File(Convert.FromBase64String(b64), "image/png");
            if (r["code"]?.GetValue<int>() == 0) return Results.NotFound();
        }
        var png = await p.ThumbnailAsync(name, ct);
        return png is null ? Results.NotFound() : Results.File(png, "image/png");
    }
    catch (Exception) { return Results.NotFound(); }
});
app.MapPost("/api/files/print", (PrintRequest req, PrinterTcp p, CancellationToken ct) =>
    Run(() => p.StartFileAsync(req.FileName, ct)));
app.MapPost("/api/files/upload", async (HttpRequest request, PrinterTcp p, CancellationToken ct) =>
{
    var form = await request.ReadFormAsync(ct);
    var file = form.Files["file"];
    if (file is null || file.Length == 0) return Results.BadRequest(new { ok = false, message = "No file" });
    var printNow = form["printNow"] == "true";
    await using var stream = file.OpenReadStream();
    return await Run(() => p.UploadAsync(file.FileName, stream, file.Length, printNow, ct));
}).DisableAntiforgery();

// ---- job & machine control (TCP, no LAN mode needed) ----
app.MapPost("/api/job/{action}", (string action, PrinterTcp p, CancellationToken ct) => action switch
{
    "pause" => Run(() => p.PauseAsync(ct)),
    "resume" => Run(() => p.ResumeAsync(ct)),
    "cancel" => Run(() => p.CancelAsync(ct)),
    _ => Task.FromResult(Results.BadRequest(new { ok = false, message = $"Unknown action '{action}'" })),
});
app.MapPost("/api/control/home", (PrinterTcp p, CancellationToken ct) => Run(() => p.HomeAsync(ct)));
app.MapPost("/api/control/motors-off", (PrinterTcp p, CancellationToken ct) => Run(() => p.MotorsOffAsync(ct)));
app.MapPost("/api/control/estop", async (PrinterTcp p, CancellationToken ct) =>
{
    try { await p.EmergencyStopAsync(ct); }
    catch (TimeoutException) { /* the printer often halts without replying */ }
    catch (IOException) { }
    return Results.Ok(new { ok = true, message = "Emergency stop sent. Power-cycle the printer to recover." });
});
app.MapPost("/api/control/light/{state}", (string state, PrinterTcp p, CancellationToken ct) =>
    Run(() => p.LightAsync(state == "on", ct)));
app.MapPost("/api/control/runout/{state}", (string state, PrinterTcp p, CancellationToken ct) =>
    Run(() => p.RunoutSensorAsync(state == "on", ct)));
app.MapPost("/api/control/temp", (TempRequest req, PrinterTcp p, CancellationToken ct) => Run(async () =>
{
    if (req.Nozzle is { } n) await p.NozzleTempAsync(n, ct);
    if (req.Bed is { } b) await p.BedTempAsync(b, ct);
    return "Temperature set";
}));
app.MapPost("/api/control/fan", (PercentRequest req, PrinterTcp p, CancellationToken ct) => Run(() => p.FanAsync(req.Percent, ct)));
app.MapPost("/api/control/speed", (PercentRequest req, PrinterTcp p, CancellationToken ct) => Run(() => p.SpeedAsync(req.Percent, ct)));
app.MapPost("/api/control/flow", (PercentRequest req, PrinterTcp p, CancellationToken ct) => Run(() => p.FlowAsync(req.Percent, ct)));
app.MapPost("/api/control/jog", (JogRequest req, PrinterMonitor m, PrinterTcp p, CancellationToken ct) =>
    m.Latest.State is "printing" or "paused" && req.Axis.ToUpperInvariant() != "Z"
        ? Task.FromResult(Results.BadRequest(new { ok = false, message = "Can't jog while a print is active." }))
        : Run(() => p.JogAsync(req.Axis, req.Distance, ct)));
app.MapPost("/api/console", (ConsoleRequest req, PrinterTcp p, CancellationToken ct) =>
    Run(() => p.RawCommandAsync(req.Command, ct)));

// ---- LAN-mode extras (HTTP API) ----
app.MapPost("/api/lan/{action}", async (string action, FlashforgeHttp ff, CancellationToken ct) =>
{
    var (cmd, args) = action switch
    {
        "camera-on" => ("streamCtrl_cmd", (object)new { action = "open" }),
        "camera-off" => ("streamCtrl_cmd", new { action = "close" }),
        "clear-platform" => ("stateCtrl_cmd", new { action = "setClearPlatform" }),
        "filter-internal" => ("circulateCtl_cmd", new { @internal = "open", external = "close" }),
        "filter-external" => ("circulateCtl_cmd", new { @internal = "close", external = "open" }),
        "filter-off" => ("circulateCtl_cmd", new { @internal = "close", external = "close" }),
        _ => ((string?)null, (object?)null)!,
    };
    if (cmd is null) return Results.BadRequest(new { ok = false, message = $"Unknown action '{action}'" });
    var r = await ff.ControlAsync(cmd, args!, ct);
    var ok = r["code"]?.GetValue<int>() == 0;
    return Results.Ok(new { ok, message = ok ? "Done" : r["message"]?.ToString() ?? "Failed" });
});

// ---- settings (web UI) ----
app.MapGet("/api/settings", (IOptionsMonitor<PrinterOptions> p, IOptionsMonitor<MonitorOptions> m,
    IOptionsMonitor<NotificationOptions> n, SettingsStore store, PrinterIdentity id) => new
{
    Host = p.CurrentValue.Host,
    SerialNumber = !string.IsNullOrWhiteSpace(p.CurrentValue.SerialNumber) ? p.CurrentValue.SerialNumber : id.Serial,
    p.CurrentValue.TcpPort,
    p.CurrentValue.HttpPort,
    p.CurrentValue.CameraPort,
    p.CurrentValue.PollSeconds,
    HasCheckCode = !string.IsNullOrEmpty(p.CurrentValue.CheckCode),
    m.CurrentValue.Username,
    HasPassword = !string.IsNullOrEmpty(m.CurrentValue.Password),
    Webhooks = n.CurrentValue.Webhooks.Select(w => new { w.Type, w.Url, w.Events }),
    SettingsFile = store.FilePath,
});

app.MapPut("/api/settings", async (SettingsUpdate u, SettingsStore store, CancellationToken ct) =>
{
    if (u.Host is not null && !string.IsNullOrWhiteSpace(u.Host) &&
        Uri.CheckHostName(u.Host.Trim()) == UriHostNameType.Unknown)
        return Results.BadRequest(new { ok = false, message = "That doesn't look like an IP address or hostname." });
    await store.UpdateAsync(root =>
    {
        var printer = SettingsStore.Section(root, "Printer");
        if (u.Host is not null) printer["Host"] = u.Host.Trim();
        if (u.CheckCode is not null) printer["CheckCode"] = u.CheckCode.Trim();      // "" clears it
        if (u.SerialNumber is not null) printer["SerialNumber"] = u.SerialNumber.Trim(); // "" = auto-detect
        if (u.PollSeconds is { } ps) printer["PollSeconds"] = Math.Clamp(ps, 1, 60);
        var monitor = SettingsStore.Section(root, "Monitor");
        if (u.Username is not null) monitor["Username"] = u.Username.Trim();
        if (u.Password is not null) monitor["Password"] = u.Password;                // "" removes the password
        if (u.Webhooks is not null)
        {
            var arr = new JsonArray();
            foreach (var w in u.Webhooks.Where(w => !string.IsNullOrWhiteSpace(w.Url)))
                arr.Add(new JsonObject
                {
                    ["Type"] = string.IsNullOrWhiteSpace(w.Type) ? "json" : w.Type,
                    ["Url"] = w.Url.Trim(),
                    ["Events"] = new JsonArray(w.Events.Select(e => (JsonNode)JsonValue.Create(e)!).ToArray()),
                });
            SettingsStore.Section(root, "Notifications")["Webhooks"] = arr;
        }
    }, ct);
    await Task.Delay(600, ct); // let the file watcher reload configuration
    return Results.Ok(new { ok = true, message = "Settings saved" });
});

app.MapPost("/api/settings/test", (TestRequest req, IOptionsMonitor<PrinterOptions> o, CancellationToken ct) =>
    Discovery.TestAsync(req.Host.Trim(), o.CurrentValue.TcpPort, o.CurrentValue.HttpPort, o.CurrentValue.CameraPort, ct));

app.MapPost("/api/discover", async (CancellationToken ct) =>
    await Discovery.FindAsync(TimeSpan.FromSeconds(3), ct));

// ---- notifications ----
app.MapPost("/api/notify/test", async (Notifier n, CancellationToken ct) =>
{
    await n.SendAsync("test", "Test notification", "FlashForge Monitor can reach your webhook.", ct);
    return Results.Ok(new { ok = true, message = "Test sent to all configured webhooks" });
});

// ---- camera: one shared upstream connection (the printer allows a single viewer), fanned out to everyone ----
app.MapGet("/camera", async (HttpContext ctx, CameraHub hub) =>
{
    var (reader, unsubscribe) = hub.Subscribe();
    try
    {
        // Wait for the first frame before committing to a 200, so <img onerror> works when the camera is off.
        using var first = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted);
        first.CancelAfter(TimeSpan.FromSeconds(10));
        byte[] frame;
        try { frame = await reader.ReadAsync(first.Token); }
        catch (OperationCanceledException) when (!ctx.RequestAborted.IsCancellationRequested)
        {
            ctx.Response.StatusCode = StatusCodes.Status502BadGateway;
            await ctx.Response.WriteAsync($"Camera unavailable: {hub.LastError ?? "no frames"}");
            return;
        }

        const string boundary = "ffmframe";
        ctx.Response.ContentType = $"multipart/x-mixed-replace; boundary={boundary}";
        ctx.Response.Headers.CacheControl = "no-cache, no-store";
        var body = ctx.Response.Body;
        while (true)
        {
            var header = System.Text.Encoding.ASCII.GetBytes(
                $"--{boundary}\r\nContent-Type: image/jpeg\r\nContent-Length: {frame.Length}\r\n\r\n");
            await body.WriteAsync(header, ctx.RequestAborted);
            await body.WriteAsync(frame, ctx.RequestAborted);
            await body.WriteAsync("\r\n"u8.ToArray(), ctx.RequestAborted);
            await body.FlushAsync(ctx.RequestAborted);
            frame = await reader.ReadAsync(ctx.RequestAborted);
        }
    }
    catch (OperationCanceledException) { }
    catch (System.Threading.Channels.ChannelClosedException) { }
    catch (IOException) { }
    finally { unsubscribe(); }
});

app.MapGet("/camera/snapshot", async (CameraHub hub, CancellationToken ct) =>
{
    var jpg = await hub.SnapshotAsync(ct);
    return jpg is null
        ? Results.Problem($"Camera unavailable: {hub.LastError ?? "no frames"}", statusCode: 502)
        : Results.File(jpg, "image/jpeg");
});

app.Run();

static async Task<IResult> Run<T>(Func<Task<T>> action)
{
    try
    {
        var result = await action();
        return result is string s ? Results.Ok(new { ok = true, message = s }) : Results.Ok(result);
    }
    catch (Exception ex) when (ex is not OperationCanceledException)
    {
        return Results.Ok(new { ok = false, message = ex.Message });
    }
}

static bool FixedEq(string a, string b) =>
    CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));

public sealed record PrintRequest(string FileName);
public sealed record TempRequest(int? Nozzle, int? Bed);
public sealed record PercentRequest(int Percent);
public sealed record JogRequest(string Axis, double Distance);
public sealed record ConsoleRequest(string Command);
public sealed record TestRequest(string Host);
public sealed record WebhookUpdate(string Type, string Url, List<string> Events);
public sealed record SettingsUpdate(
    string? Host, string? CheckCode, int? PollSeconds, string? Username, string? Password, List<WebhookUpdate>? Webhooks,
    string? SerialNumber = null);
public partial class Program;
