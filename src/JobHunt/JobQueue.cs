using System.Text.Json;

namespace JobHunt;

/// <summary>
/// Roles a run couldn't get to (time budget, per-run cap, LinkedIn rate limit). They aren't marked
/// seen; the next run processes them first, so nothing found is ever silently dropped.
/// </summary>
public class JobQueue(string path)
{
    public List<AlertJob> Items { get; } = File.Exists(path)
        ? JsonSerializer.Deserialize<List<AlertJob>>(File.ReadAllText(path), Json.Options) ?? new()
        : new();

    public void Save(IEnumerable<AlertJob> pending) =>
        File.WriteAllText(path, JsonSerializer.Serialize(pending.ToList(), Json.Options));
}
