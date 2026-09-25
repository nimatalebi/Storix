using System.ComponentModel;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json.Serialization;
using NT.Storix.Core.Destinations;
using NT.Storix.Core.Models;
using NT.Storix.Core.Sources;

namespace NT.Storix.Core.Plugins;

/// <summary>A setting a plugin asks for; secret settings are encrypted in the database and never logged.</summary>
public sealed record PluginSetting(string Name, string Description, bool Secret = false, bool Required = false);

/// <summary>Common part of source and destination plugins. Implementations need a public parameterless constructor.</summary>
public interface IStorixPlugin
{
    /// <summary>Stable identifier stored in job definitions, e.g. <c>acme.nas</c>.</summary>
    string Id { get; }

    string DisplayName { get; }

    IReadOnlyList<PluginSetting> Settings { get; }
}

/// <summary>Adds a backup source. The source writes its files into the staging folder of <see cref="SourceContext"/>.</summary>
public interface ISourcePlugin : IStorixPlugin
{
    IBackupSource Create(PluginContext context);
}

/// <summary>Adds a destination (list, upload, download and delete of backup files).</summary>
public interface IDestinationPlugin : IStorixPlugin
{
    IBackupDestination Create(PluginContext context);
}

/// <summary>The configured settings (secrets included, already decrypted) of one source or destination.</summary>
public sealed class PluginContext(IReadOnlyDictionary<string, string?> settings, int maxUploadKBps = 0)
{
    public IReadOnlyDictionary<string, string?> Settings { get; } = settings;

    /// <summary>Upload limit of the destination in KB/s (0 = unlimited); see <c>ThrottledStream</c>.</summary>
    public int MaxUploadKBps { get; } = maxUploadKBps;

    public string? Get(string name) => Settings.TryGetValue(name, out var value) ? value : null;

    public string Require(string name) =>
        Get(name) is { Length: > 0 } value ? value : throw new InvalidOperationException($"The plugin setting '{name}' is required.");
}

/// <summary>Configuration of a plugin source or destination in a job.</summary>
public sealed class PluginOptions
{
    [Category("Plugin"), Description("Identifier of the plugin (see the plugins folder).")]
    public string? Plugin { get; set; }

    [Browsable(false)]
    public Dictionary<string, string?> Settings { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Secret settings (passwords, tokens): encrypted in the database like every other secret.</summary>
    [Browsable(false), Secret]
    public Dictionary<string, string?> Secrets { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    [JsonIgnore, Category("Plugin"), DisplayName("Settings"), Description("One name=value per line.")]
    [Editor("System.ComponentModel.Design.MultilineStringEditor, System.Design", "System.Drawing.Design.UITypeEditor, System.Drawing")]
    public string SettingsText
    {
        get => string.Join(Environment.NewLine, Settings.Select(p => $"{p.Key}={p.Value}"));
        set => Settings = Parse(value, '\n');
    }

    [JsonIgnore, Category("Plugin"), DisplayName("Secret settings"), Description("name=value; name2=value2 (stored encrypted)."), PasswordPropertyText(true)]
    public string SecretsText
    {
        get => string.Join("; ", Secrets.Select(p => $"{p.Key}={p.Value}"));
        set => Secrets = Parse(value, ';');
    }

    /// <summary>Settings and secrets together, as the plugin sees them.</summary>
    public PluginContext ToContext(int maxUploadKBps = 0)
    {
        var all = new Dictionary<string, string?>(Settings, StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in Secrets)
        {
            all[key] = value;
        }

        return new PluginContext(all, maxUploadKBps);
    }

    private static Dictionary<string, string?> Parse(string? text, char separator)
    {
        var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in (text ?? string.Empty).Split(separator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var equals = part.IndexOf('=');
            if (equals > 0)
            {
                result[part[..equals].Trim()] = part[(equals + 1)..].Trim();
            }
        }

        return result;
    }
}

/// <summary>
/// Loads plugins from the <c>plugins</c> folder next to the program (writable by administrators only, since
/// plugins run inside the service). Each <c>*.dll</c> there, or in a sub-folder of the same name, is loaded
/// into its own load context.
/// </summary>
public static class PluginRegistry
{
    private static readonly Lock Sync = new();
    private static readonly Dictionary<string, ISourcePlugin> SourcePlugins = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, IDestinationPlugin> DestinationPlugins = new(StringComparer.OrdinalIgnoreCase);
    private static readonly List<string> LoadErrors = [];
    private static bool _loaded;

    public static string DefaultFolder => Path.Combine(AppContext.BaseDirectory, "plugins");

    public static IReadOnlyCollection<ISourcePlugin> Sources
    {
        get
        {
            EnsureLoaded();
            lock (Sync)
            {
                return [.. SourcePlugins.Values];
            }
        }
    }

    public static IReadOnlyCollection<IDestinationPlugin> Destinations
    {
        get
        {
            EnsureLoaded();
            lock (Sync)
            {
                return [.. DestinationPlugins.Values];
            }
        }
    }

    /// <summary>Plugins that could not be loaded, with the reason.</summary>
    public static IReadOnlyList<string> Errors
    {
        get
        {
            lock (Sync)
            {
                return [.. LoadErrors];
            }
        }
    }

    /// <summary>Registers a plugin instance (tests, or plugins built into a host).</summary>
    public static void Register(IStorixPlugin plugin)
    {
        lock (Sync)
        {
            if (plugin is ISourcePlugin source)
            {
                SourcePlugins[source.Id] = source;
            }

            if (plugin is IDestinationPlugin destination)
            {
                DestinationPlugins[destination.Id] = destination;
            }
        }
    }

    public static void EnsureLoaded()
    {
        lock (Sync)
        {
            if (_loaded)
            {
                return;
            }

            _loaded = true;
        }

        LoadFrom(DefaultFolder);
    }

    /// <summary>Loads every plugin assembly in <paramref name="folder"/>; returns the number of plugins found.</summary>
    public static int LoadFrom(string folder)
    {
        if (!Directory.Exists(folder))
        {
            return 0;
        }

        var count = 0;
        var files = Directory.GetFiles(folder, "*.dll")
            .Concat(Directory.GetDirectories(folder).Select(d => Path.Combine(d, Path.GetFileName(d) + ".dll")).Where(File.Exists));
        foreach (var file in files)
        {
            try
            {
                var context = new PluginLoadContext(file);
                var assembly = context.LoadFromAssemblyPath(Path.GetFullPath(file));
                foreach (var type in assembly.GetTypes().Where(t => t is { IsClass: true, IsAbstract: false } && typeof(IStorixPlugin).IsAssignableFrom(t) && t.GetConstructor(Type.EmptyTypes) is not null))
                {
                    Register((IStorixPlugin)Activator.CreateInstance(type)!);
                    count++;
                }
            }
            catch (Exception ex) when (ex is BadImageFormatException or FileLoadException or ReflectionTypeLoadException or TargetInvocationException or MissingMethodException)
            {
                lock (Sync)
                {
                    LoadErrors.Add($"{Path.GetFileName(file)}: {ex.Message}");
                }
            }
        }

        return count;
    }

    public static IBackupSource CreateSource(PluginOptions options) =>
        Find(SourcePlugins, options.Plugin, "source").Create(Validate(Find(SourcePlugins, options.Plugin, "source"), options.ToContext()));

    public static IBackupDestination CreateDestination(PluginOptions options, int maxUploadKBps) =>
        Find(DestinationPlugins, options.Plugin, "destination").Create(Validate(Find(DestinationPlugins, options.Plugin, "destination"), options.ToContext(maxUploadKBps)));

    /// <summary>Configuration problems of a plugin source or destination (empty when it can be used).</summary>
    public static IReadOnlyList<string> GetValidationErrors(PluginOptions options, bool destination)
    {
        EnsureLoaded();
        IStorixPlugin? plugin;
        lock (Sync)
        {
            plugin = string.IsNullOrWhiteSpace(options.Plugin) ? null
                : destination ? DestinationPlugins.GetValueOrDefault(options.Plugin) : SourcePlugins.GetValueOrDefault(options.Plugin);
        }

        if (plugin is null)
        {
            return [string.IsNullOrWhiteSpace(options.Plugin) ? "Choose a plugin." : $"The plugin '{options.Plugin}' is not installed."];
        }

        var context = options.ToContext();
        return plugin.Settings.Where(s => s.Required && string.IsNullOrEmpty(context.Get(s.Name))).Select(s => $"The plugin setting '{s.Name}' is required.").ToList();
    }

    private static T Find<T>(Dictionary<string, T> plugins, string? id, string kind)
        where T : class
    {
        EnsureLoaded();
        lock (Sync)
        {
            return (id is null ? null : plugins.GetValueOrDefault(id)) ?? throw new InvalidOperationException($"The {kind} plugin '{id}' is not installed.");
        }
    }

    private static PluginContext Validate(IStorixPlugin plugin, PluginContext context)
    {
        var missing = plugin.Settings.FirstOrDefault(s => s.Required && string.IsNullOrEmpty(context.Get(s.Name)));
        return missing is null ? context : throw new InvalidOperationException($"The plugin setting '{missing.Name}' is required.");
    }

    /// <summary>Resolves a plugin's own dependencies from its folder; Storix assemblies come from the host.</summary>
    private sealed class PluginLoadContext(string pluginPath) : AssemblyLoadContext(Path.GetFileNameWithoutExtension(pluginPath))
    {
        private readonly AssemblyDependencyResolver _resolver = new(pluginPath);

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            if (assemblyName.Name is { } name && (name.StartsWith("NT.Storix", StringComparison.Ordinal) || name.StartsWith("Microsoft.Extensions.", StringComparison.Ordinal)))
            {
                return null; // Shared with the host so the plugin interfaces match.
            }

            var path = _resolver.ResolveAssemblyToPath(assemblyName);
            return path is null ? null : LoadFromAssemblyPath(path);
        }
    }
}
