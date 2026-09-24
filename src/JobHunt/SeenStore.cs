using System.Text.Json;
using System.Text.RegularExpressions;

namespace JobHunt;

public class SeenStore(string path)
{
    class Data { public HashSet<string> Ids { get; set; } = new(); public HashSet<string> Keys { get; set; } = new(); }

    readonly Data _data = File.Exists(path)
        ? JsonSerializer.Deserialize<Data>(File.ReadAllText(path), Json.Options) ?? new()
        : new();

    public bool IsSeen(AlertJob j) => _data.Ids.Contains(j.LinkedInId) || _data.Keys.Contains(Key(j));

    public void Add(AlertJob j) { _data.Ids.Add(j.LinkedInId); _data.Keys.Add(Key(j)); }

    public void Save() => File.WriteAllText(path, JsonSerializer.Serialize(_data, Json.Options));

    /// <summary>Normalized company|title so the same role from different alerts collapses.</summary>
    public static string Key(AlertJob j) => Norm(Regex.Replace(j.Company, @"\b(inc|llc|ltd|corp|corporation|co)\b\.?", "", RegexOptions.IgnoreCase)) + "|" + Norm(j.Title);

    static string Norm(string s) => Regex.Replace(Regex.Replace(s.ToLowerInvariant(), @"[^a-z0-9 ]", " "), @"\s+", " ").Trim();
}
