using System.Text.RegularExpressions;
using NT.Storix.Core.Security;

namespace NT.Storix.Core.Configuration;

/// <summary>
/// Config as code: secret values in job files can be written as <c>${env:NAME}</c> and are resolved from
/// environment variables when the file is applied, so no secret has to be committed to version control.
/// </summary>
public static partial class SecretPlaceholders
{
    /// <summary>Replaces placeholders in every secret property; returns the names of variables that were not set.</summary>
    public static IReadOnlyList<string> Resolve(object root, Func<string, string?>? lookup = null)
    {
        lookup ??= Environment.GetEnvironmentVariable;
        var missing = new List<string>();
        SecretWalker.Transform(root, value =>
        {
            if (value is null)
            {
                return null;
            }

            return Placeholder().Replace(value, match =>
            {
                var name = match.Groups["name"].Value;
                var resolved = lookup(name);
                if (resolved is null)
                {
                    missing.Add(name);
                    return match.Value;
                }

                return resolved;
            });
        });
        return missing.Distinct().ToList();
    }

    [GeneratedRegex(@"\$\{env:(?<name>[A-Za-z_][A-Za-z0-9_]*)\}")]
    private static partial Regex Placeholder();
}
