using System.Text.Json;
using Microsoft.Extensions.Options;

namespace FlashforgeMonitor;

public sealed record HistoryEntry(
    string File,
    DateTimeOffset? StartedAt,
    DateTimeOffset EndedAt,
    long? DurationSeconds,
    string Result,
    int? LastProgress,
    bool StartTimeExact);

/// <summary>Print history kept in a small JSON file in the data directory.</summary>
public sealed class HistoryStore(IOptions<MonitorOptions> options, IHostEnvironment env, ILogger<HistoryStore> log)
{
    private const int MaxEntries = 500;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly SemaphoreSlim _gate = new(1, 1);
    private List<HistoryEntry>? _cache;

    private string FilePath => Path.Combine(
        Path.GetFullPath(options.Value.DataDirectory, env.ContentRootPath), "history.json");

    public async Task<IReadOnlyList<HistoryEntry>> GetAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try { return (await LoadAsync(ct)).AsEnumerable().Reverse().ToList(); }
        finally { _gate.Release(); }
    }

    public async Task AddAsync(HistoryEntry entry, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var list = await LoadAsync(ct);
            list.Add(entry);
            if (list.Count > MaxEntries) list.RemoveRange(0, list.Count - MaxEntries);
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var tmp = FilePath + ".tmp";
            await File.WriteAllTextAsync(tmp, JsonSerializer.Serialize(list, Json), ct);
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.LogWarning("Could not save history: {Message}", ex.Message);
        }
        finally { _gate.Release(); }
    }

    public async Task ClearAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            _cache = [];
            if (File.Exists(FilePath)) File.Delete(FilePath);
        }
        finally { _gate.Release(); }
    }

    private async Task<List<HistoryEntry>> LoadAsync(CancellationToken ct)
    {
        if (_cache is not null) return _cache;
        try
        {
            _cache = File.Exists(FilePath)
                ? JsonSerializer.Deserialize<List<HistoryEntry>>(await File.ReadAllTextAsync(FilePath, ct), Json) ?? []
                : [];
        }
        catch (JsonException ex)
        {
            log.LogWarning("History file unreadable, starting fresh: {Message}", ex.Message);
            _cache = [];
        }
        return _cache;
    }
}
