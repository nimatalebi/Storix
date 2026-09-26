using System.Globalization;
using System.Text.Json;

namespace NT.Storix.WinForms.Infrastructure;

internal enum UiLanguage
{
    Auto,
    English,
    Persian,
}

internal enum UiTheme
{
    System,
    Light,
    Dark,
}

/// <summary>Per-user appearance preferences, stored in %LocalAppData%\Storix\ui.json.</summary>
internal sealed class UiPreferences
{
    public UiLanguage Language { get; set; }

    public UiTheme Theme { get; set; }

    /// <summary>Main window position and size (x, y, width, height), restored when still on a screen.</summary>
    public int[]? WindowBounds { get; set; }

    public bool Maximized { get; set; }

    public string? Tab { get; set; }

    /// <summary>Column widths per list, keyed by list name.</summary>
    public Dictionary<string, int[]> Columns { get; set; } = [];

    private static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Storix", "ui.json");

    public static UiPreferences Load()
    {
        try
        {
            return File.Exists(FilePath) ? JsonSerializer.Deserialize<UiPreferences>(File.ReadAllText(FilePath)) ?? new() : new();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new();
        }
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this));
    }
}

/// <summary>
/// Translates the user interface (English or Persian, right-to-left) and applies the color theme. Forms call
/// <see cref="Attach"/>; controls added later (e.g. editor panels) are translated as they appear.
/// </summary>
internal static class Localizer
{
    public static bool IsPersian { get; private set; }

    public static void Initialize(UiPreferences preferences)
    {
        IsPersian = preferences.Language switch
        {
            UiLanguage.Persian => true,
            UiLanguage.English => false,
            _ => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "fa",
        };

#pragma warning disable WFO5001 // Dark mode is still marked experimental in Windows Forms.
        Application.SetColorMode(preferences.Theme switch
        {
            UiTheme.Dark => SystemColorMode.Dark,
            UiTheme.Light => SystemColorMode.Classic,
            _ => SystemColorMode.System,
        });
#pragma warning restore WFO5001
    }

    /// <summary>Switches the language without touching the theme (tests).</summary>
    internal static void SetLanguage(bool persian) => IsPersian = persian;

    /// <summary>The Persian text for <paramref name="english"/> when the UI is Persian, else the text itself.</summary>
    public static string T(string english) =>
        IsPersian && TranslationsFa.Strings.TryGetValue(english, out var persian) ? persian : english;

    /// <summary>Translates a format string, then fills it in: F("{0} job(s)", 3).</summary>
    public static string F(string format, params object?[] args) =>
        string.Format(CultureInfo.CurrentCulture, T(format), args);

    public static void Attach(Form form)
    {
        form.Icon = Branding.AppIcon;
        if (!IsPersian)
        {
            return;
        }

        form.RightToLeft = RightToLeft.Yes;
        form.RightToLeftLayout = true;
        form.Load += (_, _) => Translate(form);
    }

    private static void Translate(Control control)
    {
        switch (control)
        {
            case TextBoxBase textBox:
                if (textBox is TextBox { PlaceholderText.Length: > 0 } box)
                {
                    box.PlaceholderText = T(box.PlaceholderText);
                }

                return; // User content: never translated.
            case ComboBox or NumericUpDown or DateTimePicker or PropertyGrid or WebBrowser:
                return;
            case Form or Label or ButtonBase or GroupBox or TabPage:
                control.Text = T(control.Text);
                break;
            case ListView listView:
                foreach (ColumnHeader column in listView.Columns)
                {
                    column.Text = T(column.Text);
                }

                listView.RightToLeftLayout = true;
                break;
            case ToolStrip strip:
                foreach (ToolStripItem item in strip.Items)
                {
                    Translate(item);
                }

                break;
        }

        if (control.ContextMenuStrip is { } menu)
        {
            Translate(menu);
        }

        foreach (Control child in control.Controls)
        {
            Translate(child);
        }

        // Editor panels are swapped at run time (e.g. the source type): translate them when they appear.
        control.ControlAdded -= OnControlAdded;
        control.ControlAdded += OnControlAdded;
    }

    private static void Translate(ToolStripItem item)
    {
        item.Text = T(item.Text ?? string.Empty);
        if (item is ToolStripDropDownItem dropDown)
        {
            foreach (ToolStripItem child in dropDown.DropDownItems)
            {
                Translate(child);
            }
        }
    }

    private static void OnControlAdded(object? sender, ControlEventArgs e)
    {
        if (e.Control is { } control)
        {
            Translate(control);
        }
    }
}
