namespace JobHunt;

/// <summary>
/// Cheap title-only screen for large batches (full LinkedIn searches return hundreds of roles).
/// Drops only clear misses; generic titles are kept for the closer look later.
/// </summary>
public class Triage(ClaudeClient claude, string promptTemplate, string preferencesText, string model)
{
    const string System = "You screen job titles conservatively. When unsure, keep. You answer with JSON only.";
    const int BatchSize = 50;

    record DropList(List<Drop> Drop);
    record Drop(string Id, string? Why);

    /// <summary>Map of LinkedIn job ID to drop reason. Batches that fail are treated as "keep all".</summary>
    public async Task<Dictionary<string, string>> ScreenAsync(IReadOnlyList<AlertJob> jobs, int parallel)
    {
        var drops = new Dictionary<string, string>();
        var batches = jobs.Chunk(BatchSize).ToList();
        using var gate = new SemaphoreSlim(parallel);
        await Task.WhenAll(batches.Select(async batch =>
        {
            await gate.WaitAsync();
            try
            {
                var roles = string.Join("\n", batch.Select(j => $"{j.LinkedInId} | {j.Title} | {j.Company}"));
                var prompt = Prompt.Fill(promptTemplate, ("preferences", preferencesText), ("roles", roles));
                var result = Json.Parse<DropList>(await claude.AskAsync(System, prompt, webSearch: false, model: model));
                var ids = batch.Select(j => j.LinkedInId).ToHashSet();
                lock (drops)
                    foreach (var d in result.Drop.Where(d => ids.Contains(d.Id)))
                        drops[d.Id] = d.Why ?? "title looks off-target";
            }
            catch (Exception ex) { Console.WriteLine($"  title check batch failed, keeping all: {ex.Message}"); }
            finally { gate.Release(); }
        }));
        return drops;
    }
}
