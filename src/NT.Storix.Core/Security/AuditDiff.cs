using System.Text.Json;
using System.Text.Json.Nodes;

namespace NT.Storix.Core.Security;

/// <summary>Human-readable list of changed settings between two versions of an object. Secrets are never shown.</summary>
public static class AuditDiff
{
    public static string Describe<T>(T? before, T? after)
        where T : class
    {
        var changes = new List<string>();
        Compare(string.Empty, Flatten(before), Flatten(after), changes);
        return changes.Count == 0 ? "no changes" : string.Join("; ", changes.Take(50)) + (changes.Count > 50 ? $"; ... {changes.Count - 50} more" : string.Empty);
    }

    private static JsonNode? Flatten<T>(T? value)
        where T : class
    {
        if (value is null)
        {
            return null;
        }

        var copy = StorixJson.Clone(value);
        SecretWalker.Transform(copy, secret => string.IsNullOrEmpty(secret) ? secret : "*** (" + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(secret)))[..8] + ")");
        return JsonNode.Parse(JsonSerializer.Serialize(copy, StorixJson.Options));
    }

    private static void Compare(string path, JsonNode? before, JsonNode? after, List<string> changes)
    {
        if (before is JsonObject a && after is JsonObject b)
        {
            foreach (var key in a.Select(p => p.Key).Union(b.Select(p => p.Key)).Distinct())
            {
                Compare(path.Length == 0 ? key : $"{path}.{key}", a[key], b[key], changes);
            }

            return;
        }

        var left = before?.ToJsonString() ?? "(none)";
        var right = after?.ToJsonString() ?? "(none)";
        if (left != right)
        {
            // Secrets appear as "*** (hash)": the hash only tells that the value changed.
            changes.Add($"{(path.Length == 0 ? "value" : path)}: {Shorten(left)} -> {Shorten(right)}");
        }
    }

    private static string Shorten(string value) => value.Length > 120 ? value[..117] + "..." : value;
}
