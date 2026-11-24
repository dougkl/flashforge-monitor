using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;

namespace FlashforgeMonitor;

/// <summary>Serial number learned from the printer (TCP M115), shared with the HTTP client.</summary>
public sealed class PrinterIdentity
{
    public string? Serial { get; set; }

    // Cache of the last /detail payload (singleton; the typed HttpClient itself is transient).
    public JsonObject? LastDetail { get; set; }
    public DateTimeOffset LastDetailAt { get; set; }
}

/// <summary>
/// Client for the Adventurer 5M / 5M Pro HTTP API (port 8898). Needs the printer's
/// access code ("Printer ID" / check code on the Network screen) and its serial number.
/// On firmware 5.x it works without LAN-only mode; on 3.x LAN-only mode must be on.
/// Endpoint shapes follow the MIT-licensed flashforge-python-api by GhostTypes.
/// </summary>
public sealed class FlashforgeHttp(HttpClient http, IOptionsMonitor<PrinterOptions> options, PrinterIdentity identity)
{
    private PrinterOptions Opt => options.CurrentValue;
    private string Url(string path) => $"http://{Opt.Host}:{Opt.HttpPort}{path}";

    public string Serial => !string.IsNullOrWhiteSpace(Opt.SerialNumber) ? Opt.SerialNumber : identity.Serial ?? "";
    public bool Enabled => !string.IsNullOrWhiteSpace(Opt.CheckCode) && !string.IsNullOrWhiteSpace(Opt.Host);
    public bool Ready => Enabled && Serial.Length > 0;

    private static JsonObject Error(string message) => new() { ["code"] = -1, ["message"] = message };

    public async Task<JsonObject> PostAsync(string path, JsonObject? extra, CancellationToken ct)
    {
        if (!Enabled) return Error("No access code set (⚙ Settings → Access code).");
        if (Serial.Length == 0) return Error("Printer serial number not known yet.");
        var body = new JsonObject { ["serialNumber"] = Serial, ["checkCode"] = Opt.CheckCode };
        if (extra is not null)
            foreach (var (k, v) in extra) body[k] = v?.DeepClone();
        try
        {
            using var content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
            using var resp = await http.PostAsync(Url(path), content, ct);
            var text = await resp.Content.ReadAsStringAsync(ct);
            return JsonNode.Parse(text) as JsonObject ?? Error($"Unexpected reply: {text}");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return Error($"Printer HTTP API unreachable: {ex.Message}");
        }
    }

    public async Task<JsonObject> DetailAsync(CancellationToken ct)
    {
        var r = await PostAsync("/detail", null, ct);
        if (r["code"]?.GetValue<int>() == 0 && r["detail"] is JsonObject d)
        {
            identity.LastDetail = d;
            identity.LastDetailAt = DateTimeOffset.Now;
        }
        return r;
    }

    /// <summary>Recent cached detail if fresh, otherwise asks the printer.</summary>
    public async Task<JsonObject> DetailCachedAsync(CancellationToken ct) =>
        identity.LastDetail is { } d && DateTimeOffset.Now - identity.LastDetailAt < TimeSpan.FromSeconds(4)
            ? new JsonObject { ["code"] = 0, ["detail"] = d.DeepClone() }
            : await DetailAsync(ct);

    public Task<JsonObject> ControlAsync(string cmd, object args, CancellationToken ct) =>
        PostAsync("/control", new JsonObject
        {
            ["payload"] = new JsonObject { ["cmd"] = cmd, ["args"] = JsonSerializer.SerializeToNode(args) },
        }, ct);
}
