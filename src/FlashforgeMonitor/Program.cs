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

builder.Services.Configure<PrinterOptions>(builder.Configuration.GetSection("Printer"));
builder.Services.Configure<MonitorOptions>(builder.Configuration.GetSection("Monitor"));
builder.Services.Configure<NotificationOptions>(builder.Configuration.GetSection("Notifications"));

builder.Services.AddSingleton<PrinterTcp>();
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
app.MapGet("/api/detail", async (FlashforgeHttp ff, CancellationToken ct) => await ff.DetailAsync(ct));

// ---- history ----
app.MapGet("/api/history", async (HistoryStore h, CancellationToken ct) => await h.GetAsync(ct));
app.MapDelete("/api/history", async (HistoryStore h, CancellationToken ct) =>
{
    await h.ClearAsync(ct);
    return Results.NoContent();
});

// ---- files ----
app.MapGet("/api/files", (PrinterTcp p, CancellationToken ct) => Run(() => p.ListFilesAsync(ct)));
app.MapGet("/api/thumb", async (string name, PrinterTcp p, CancellationToken ct) =>
{
    try
    {
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

// ---- notifications ----
app.MapPost("/api/notify/test", async (Notifier n, CancellationToken ct) =>
{
    await n.SendAsync("test", "Test notification", "FlashForge Monitor can reach your webhook.", ct);
    return Results.Ok(new { ok = true, message = "Test sent to all configured webhooks" });
});

// ---- camera: proxy the MJPEG stream so any browser that can reach this app can see it ----
app.MapGet("/camera", async (HttpContext ctx, IHttpClientFactory f, IOptionsMonitor<PrinterOptions> o) =>
{
    var opt = o.CurrentValue;
    try
    {
        using var upstream = await f.CreateClient("camera").GetAsync(
            $"http://{opt.Host}:{opt.CameraPort}/?action=stream",
            HttpCompletionOption.ResponseHeadersRead, ctx.RequestAborted);
        ctx.Response.StatusCode = (int)upstream.StatusCode;
        ctx.Response.ContentType = upstream.Content.Headers.ContentType?.ToString() ?? "multipart/x-mixed-replace";
        ctx.Response.Headers.CacheControl = "no-cache";
        await using var s = await upstream.Content.ReadAsStreamAsync(ctx.RequestAborted);
        await s.CopyToAsync(ctx.Response.Body, ctx.RequestAborted);
    }
    catch (OperationCanceledException) { }
    catch (HttpRequestException)
    {
        if (!ctx.Response.HasStarted) ctx.Response.StatusCode = StatusCodes.Status502BadGateway;
    }
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
public partial class Program;
