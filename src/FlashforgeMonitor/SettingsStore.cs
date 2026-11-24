using System.Text.Json;
using System.Text.Json.Nodes;

namespace FlashforgeMonitor;

/// <summary>
/// Settings changed from the web UI. Stored in {DataDirectory}/settings.json, which is
/// loaded last (highest priority) and watched, so changes apply without a restart.
/// </summary>
public sealed class SettingsStore(string path)
{
    private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true };
    private readonly SemaphoreSlim _gate = new(1, 1);

    public string FilePath => path;

    public async Task UpdateAsync(Action<JsonObject> change, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            JsonObject root;
            try
            {
                root = File.Exists(path)
                    ? JsonNode.Parse(await File.ReadAllTextAsync(path, ct)) as JsonObject ?? new JsonObject()
                    : new JsonObject();
            }
            catch (JsonException) { root = new JsonObject(); }

            change(root);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var tmp = path + ".tmp";
            await File.WriteAllTextAsync(tmp, root.ToJsonString(Pretty), ct);
            File.Move(tmp, path, overwrite: true);
        }
        finally { _gate.Release(); }
    }

    public static JsonObject Section(JsonObject root, string name)
    {
        if (root[name] is JsonObject o) return o;
        var created = new JsonObject();
        root[name] = created;
        return created;
    }
}
