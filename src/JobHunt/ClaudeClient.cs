using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace JobHunt;

/// <summary>
/// Calls Claude through the Claude Code CLI in print mode, so usage counts against a Claude
/// subscription (CLAUDE_CODE_OAUTH_TOKEN from `claude setup-token`) instead of API billing.
/// </summary>
public partial class ClaudeClient
{
    const string ModelId = "claude-sonnet-5";
    static readonly TimeSpan Timeout = TimeSpan.FromMinutes(10);

    // Running totals for the run summary. CostUsd is the list-price equivalent Claude Code
    // reports; on a subscription it isn't billed, but it's the best proxy for usage consumed.
    public int Calls;
    public long InputTokens, OutputTokens;
    public decimal CostUsd;

    public string UsageSummary() =>
        $"Claude usage: {Calls} calls, {InputTokens:N0} input tokens (incl. cache), {OutputTokens:N0} output tokens, " +
        $"~${CostUsd:0.00} at API list price";

    public async Task<string> AskAsync(string system, string user, bool webSearch)
    {
        // Tools: none for scoring/drafting; search-only for resolving and research.
        // LinkedIn pages are blocked outright: its terms prohibit scraping.
        string[] args =
        [
            "-p", "--output-format", "json", "--model", ModelId,
            "--system-prompt", system,
            "--tools", webSearch ? "WebSearch,WebFetch" : "",
            .. webSearch
                ? new[] { "--allowedTools", "WebSearch,WebFetch",
                          "--disallowedTools", "WebFetch(domain:linkedin.com)", "WebFetch(domain:www.linkedin.com)" }
                : [],
        ];
        var (exit, output, stderr) = await RunAsync(args, user);
        if (exit != 0 && output.Length == 0)
            throw new InvalidOperationException($"claude CLI exited {exit}: {Trim(stderr)}");

        using var doc = JsonDocument.Parse(output);
        var root = doc.RootElement;
        Track(root);
        var result = root.TryGetProperty("result", out var r) ? r.GetString() ?? "" : "";
        if (root.TryGetProperty("is_error", out var e) && e.GetBoolean())
            throw new InvalidOperationException("claude CLI error: " + Trim(result));
        return result;
    }

    public record LimitSnapshot(int? SessionPct, int? WeekPct);

    [GeneratedRegex(@"Current session:\s*(\d+)%")] private static partial Regex SessionRx();
    [GeneratedRegex(@"Current week \(all models\):\s*(\d+)%")] private static partial Regex WeekRx();

    /// <summary>
    /// Reads the subscription's limit percentages from `claude -p /usage` (no model call).
    /// Whole percents only, and they include any other Claude use on the account at the same time.
    /// </summary>
    public static async Task<LimitSnapshot> LimitsAsync()
    {
        try
        {
            var (_, output, _) = await RunAsync(["-p", "/usage"], "");
            int? Pct(Regex rx) => rx.Match(output) is { Success: true } m ? int.Parse(m.Groups[1].Value) : null;
            return new(Pct(SessionRx()), Pct(WeekRx()));
        }
        catch { return new(null, null); } // reporting only; never fail the run over it
    }

    public static string LimitsSummary(LimitSnapshot before, LimitSnapshot after)
    {
        // A `claude setup-token` token can run Claude but not read usage, so on GitHub these are missing.
        if (before.WeekPct is null || after.WeekPct is null)
            return "Max plan limits: not readable with a setup-token (check claude.ai > Settings > Usage)";
        static string Delta(int? a, int? b) => a is null || b is null ? "n/a" : $"{a}% -> {b}% (+{b - a} pts)";
        return $"Max plan limits: 5-hour session {Delta(before.SessionPct, after.SessionPct)}, " +
               $"week {Delta(before.WeekPct, after.WeekPct)}";
    }

    static async Task<(int Exit, string Stdout, string Stderr)> RunAsync(string[] args, string stdin)
    {
        var psi = new ProcessStartInfo("claude")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            // Empty working dir: no repo files or CLAUDE.md for the session to pick up.
            WorkingDirectory = Path.GetTempPath(),
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        // Only project settings (none exist) so local plugins, hooks, and MCP servers stay out.
        foreach (var a in new[] { "--setting-sources", "project", "--strict-mcp-config", "--no-session-persistence" })
            psi.ArgumentList.Add(a);

        // ponytail: `claude` must be on PATH (npm i -g @anthropic-ai/claude-code); on Windows that's claude.exe.
        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Could not start the claude CLI");
        await proc.StandardInput.WriteAsync(stdin); // prompt via stdin: resumes blow past command-line limits
        proc.StandardInput.Close();

        var stdout = proc.StandardOutput.ReadToEndAsync();
        var stderr = proc.StandardError.ReadToEndAsync();
        using var cts = new CancellationTokenSource(Timeout);
        try { await proc.WaitForExitAsync(cts.Token); }
        catch (OperationCanceledException) { proc.Kill(true); throw new TimeoutException("claude CLI timed out"); }
        return (proc.ExitCode, await stdout, await stderr);
    }

    void Track(JsonElement root)
    {
        Calls++;
        if (root.TryGetProperty("total_cost_usd", out var c) && c.ValueKind == JsonValueKind.Number) CostUsd += c.GetDecimal();
        if (!root.TryGetProperty("usage", out var u)) return;
        long N(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : 0;
        InputTokens += N(u, "input_tokens") + N(u, "cache_creation_input_tokens") + N(u, "cache_read_input_tokens");
        OutputTokens += N(u, "output_tokens");
    }

    static string Trim(string s) => s.Length > 300 ? s[..300] + "..." : s;
}
