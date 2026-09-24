namespace JobHunt;

/// <summary>
/// The one door to LinkedIn's public, logged-out pages (opt-in, see criteria.yaml). Every request,
/// from any thread, is spaced 3s apart. A 429 is honored (Retry-After, capped at 60s) and retried
/// once; a second 429 stops LinkedIn requests for the rest of the run so callers can queue work.
/// </summary>
public static class LinkedInHttp
{
    static readonly HttpClient Http = CreateClient();
    static readonly SemaphoreSlim Gate = new(1, 1);
    static readonly TimeSpan Spacing = TimeSpan.FromSeconds(3);
    static DateTime _last = DateTime.MinValue;

    public static bool Stopped { get; private set; }
    public static int Requests { get; private set; }

    static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("job-hunt-pipeline/1.0 (personal use)");
        return http;
    }

    /// <summary>Page HTML, or null when unavailable (error, non-200, or stopped).</summary>
    public static async Task<string?> GetAsync(string url)
    {
        await Gate.WaitAsync();
        try
        {
            for (var attempt = 0; attempt < 2; attempt++)
            {
                if (Stopped) return null;
                var wait = _last + Spacing - DateTime.UtcNow;
                if (wait > TimeSpan.Zero) await Task.Delay(wait);
                _last = DateTime.UtcNow;
                Requests++;
                try
                {
                    using var res = await Http.GetAsync(url);
                    if (res.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
                    {
                        var retry = res.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(30);
                        if (retry > TimeSpan.FromSeconds(60)) retry = TimeSpan.FromSeconds(60);
                        Console.WriteLine($"  LinkedIn: rate limited, waiting {retry.TotalSeconds:0}s");
                        await Task.Delay(retry);
                        continue;
                    }
                    if (!res.IsSuccessStatusCode)
                    {
                        Console.WriteLine($"  LinkedIn: HTTP {(int)res.StatusCode}, skipped");
                        return null;
                    }
                    return await res.Content.ReadAsStringAsync();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"  LinkedIn: {ex.Message}, skipped");
                    return null;
                }
            }
            Stopped = true;
            Console.WriteLine("  LinkedIn: still rate limited, no more LinkedIn requests this run (remaining roles are queued)");
            return null;
        }
        finally { Gate.Release(); }
    }
}
