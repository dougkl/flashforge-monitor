using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;

namespace FlashforgeMonitor;

/// <summary>
/// Optional client for the Adventurer 5M / 5M Pro local HTTP API (port 8898).
/// Needs LAN-only mode on the printer and the check code it shows. Adds extra
/// detail (chamber temp, fans, door, filament, disk) and a few extra controls.
/// Endpoint shapes follow the MIT-licensed flashforge-python-api by GhostTypes.
/// </summary>
public sealed class FlashforgeHttp(HttpClient http, IOptionsMonitor<PrinterOptions> options, PrinterMonitor monitor)
{
    private PrinterOptions Opt => options.CurrentValue;
    private string Url(string path) => $"http://{Opt.Host}:{Opt.HttpPort}{path}";
    private string Serial => !string.IsNullOrWhiteSpace(Opt.SerialNumber) ? Opt.SerialNumber : monitor.Latest.Serial ?? "";

    public bool Enabled => !string.IsNullOrWhiteSpace(Opt.CheckCode) && !string.IsNullOrWhiteSpace(Opt.Host);

    private static JsonObject Error(string message) => new() { ["code"] = -1, ["message"] = message };

    public async Task<JsonObject> PostAsync(string path, JsonObject? extra, CancellationToken ct)
    {
        if (!Enabled) return Error("LAN mode not configured (set Printer:CheckCode).");
        if (string.IsNullOrEmpty(Serial)) return Error("Serial number not known yet.");
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

    public Task<JsonObject> DetailAsync(CancellationToken ct) => PostAsync("/detail", null, ct);

    public Task<JsonObject> ControlAsync(string cmd, object args, CancellationToken ct) =>
        PostAsync("/control", new JsonObject
        {
            ["payload"] = new JsonObject { ["cmd"] = cmd, ["args"] = JsonSerializer.SerializeToNode(args) },
        }, ct);
}
