namespace NT.Storix.Cli;

/// <summary>Minimal argument parser: positional values, --flags and --option value pairs.</summary>
internal sealed class Arguments
{
    private readonly Dictionary<string, List<string>> _options = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _flags = new(StringComparer.OrdinalIgnoreCase);

    public Arguments(IEnumerable<string> args, IReadOnlySet<string> flagNames)
    {
        var list = args.ToList();
        for (var i = 0; i < list.Count; i++)
        {
            var arg = list[i];
            if (arg.StartsWith("--", StringComparison.Ordinal))
            {
                var name = arg[2..];
                var eq = name.IndexOf('=');
                if (eq > 0)
                {
                    Add(name[..eq], name[(eq + 1)..]);
                }
                else if (flagNames.Contains(name))
                {
                    _flags.Add(name);
                }
                else if (i + 1 < list.Count)
                {
                    Add(name, list[++i]);
                }
                else
                {
                    throw new CliException($"Option --{name} needs a value.");
                }
            }
            else
            {
                Positional.Add(arg);
            }
        }
    }

    public List<string> Positional { get; } = [];

    public bool Flag(string name) => _flags.Contains(name);

    public string? Option(string name) => _options.TryGetValue(name, out var values) ? values[^1] : null;

    public IReadOnlyList<string> Options(string name) => _options.TryGetValue(name, out var values) ? values : [];

    public string Required(int position, string what) =>
        Positional.Count > position ? Positional[position] : throw new CliException($"Missing {what}.");

    private void Add(string name, string value)
    {
        if (!_options.TryGetValue(name, out var values))
        {
            _options[name] = values = [];
        }

        values.Add(value);
    }
}

internal sealed class CliException(string message) : Exception(message);
