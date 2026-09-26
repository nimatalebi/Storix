using NT.Storix.Core;
using NT.Storix.Core.Models;
using NT.Storix.WinForms.Forms;
using NT.Storix.WinForms.Infrastructure;

namespace NT.Storix.WinForms.Tests;

/// <summary>
/// Builds every main screen with realistic data, fails on any exception while creating, laying out or painting,
/// and saves screenshots (CI artifact "ui-screenshots") to review the layout.
/// </summary>
public class UiSmokeTests
{
    private static readonly string Screenshots = Path.Combine(AppContext.BaseDirectory, "screenshots");

    private static void RunOnSta(Action action)
    {
        lock (Problems)
        {
            Problems.Clear();
        }

        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
                action();
            }
            catch (Exception ex)
            {
                error = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error is not null)
        {
            throw new InvalidOperationException("UI failed: " + error, error);
        }

        lock (Problems)
        {
            Assert.True(Problems.Count == 0, "Layout problems:\n" + string.Join("\n", Problems.Distinct()));
        }
    }

    private static readonly List<string> Problems = [];

    /// <summary>Finds layout problems a screenshot would show: clipped text, controls outside their parent, toolbar overflow.</summary>
    private static void Audit(Control root, string screen)
    {
        void Visit(Control control)
        {
            if (!control.Visible)
            {
                return;
            }

            var name = $"{screen}: {control.GetType().Name} '{control.Text?.Split('\n')[0]}'";
            if (control is Button { AutoSize: false } button && button.Text.Length > 0)
            {
                var needed = TextRenderer.MeasureText(button.Text, button.Font).Width + 12;
                if (needed > button.Width)
                {
                    Problems.Add($"{name}: text needs {needed}px, button is {button.Width}px");
                }
            }

            if (control is Label { AutoSize: false } label && label.Text.Length > 0 && label is not LinkLabel)
            {
                var size = TextRenderer.MeasureText(label.Text, label.Font, new Size(label.Width, 0), TextFormatFlags.WordBreak);
                if (size.Height > label.Height + 2)
                {
                    Problems.Add($"{name}: text needs {size.Height}px height, label is {label.Height}px");
                }
            }

            if (control.Parent is { } parent && parent is not ScrollableControl { AutoScroll: true } && parent is not TabControl
                && control.Width > 0 && (control.Right > parent.ClientSize.Width + 2 || control.Bottom > parent.ClientSize.Height + 2) && parent.ClientSize.Width > 0)
            {
                Problems.Add($"{name}: outside its parent ({control.Bounds} in {parent.ClientSize})");
            }

            if (control is ToolStrip strip)
            {
                foreach (ToolStripItem item in strip.Items)
                {
                    if (item.Visible && item.Placement == ToolStripItemPlacement.Overflow)
                    {
                        Problems.Add($"{screen}: toolbar item '{item.Text}' does not fit and moved to the overflow menu");
                    }
                }
            }

            foreach (Control child in control.Controls)
            {
                Visit(child);
            }
        }

        Visit(root);
    }

    private static void Capture(Control control, string name)
    {
        Directory.CreateDirectory(Screenshots);
        Application.DoEvents();
        lock (Problems)
        {
            Audit(control, name);
        }
        using var bitmap = new Bitmap(control.Width, control.Height);
        control.DrawToBitmap(bitmap, new Rectangle(Point.Empty, control.Size));
        bitmap.Save(Path.Combine(Screenshots, name + ".png"));
    }

    private static AppServices SeededServices()
    {
        var folder = Path.Combine(Path.GetTempPath(), "storix-ui-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable(StorixPaths.DataDirectoryEnvironmentVariable, folder);
        var services = AppServices.Create();
        var ok = new BackupJob { Name = "Web site files", Schedule = { Kind = ScheduleKind.Daily }, Destinations = [new DestinationDefinition { Name = "NAS" }] };
        var failing = new BackupJob { Name = "SQL Server - accounting", Source = { Kind = SourceKind.SqlServer }, Destinations = [new DestinationDefinition { Name = "S3", Kind = DestinationKind.S3 }] };
        var disabled = new BackupJob { Name = "Old archive", Enabled = false };
        foreach (var job in new[] { ok, failing, disabled })
        {
            services.Jobs.Save(job);
        }

        void Run(BackupJob job, RunStatus status, int hoursAgo, long size)
        {
            var run = new BackupRun { JobId = job.Id, JobName = job.Name, StartedAt = DateTimeOffset.UtcNow.AddHours(-hoursAgo), Trigger = RunTrigger.Schedule };
            services.Runs.Insert(run);
            run.Status = status;
            run.FinishedAt = run.StartedAt.AddMinutes(4);
            run.SizeBytes = size;
            run.Message = status == RunStatus.Failed ? "Upload to 'S3' failed: access denied." : "Backup stored on 1 destination(s).";
            run.Log = "Backup started.";
            services.Runs.Complete(run);
        }

        Run(ok, RunStatus.Succeeded, 30, 120_000_000);
        Run(ok, RunStatus.Succeeded, 6, 125_000_000);
        Run(failing, RunStatus.Failed, 2, 0);
        return services;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Main_window_tabs_render(bool persian) => RunOnSta(() => Main_window_tabs_render_core(persian));

    private static void Main_window_tabs_render_core(bool persian)
    {
        {
            Localizer.SetLanguage(persian);
            try
            {
                var services = SeededServices();
                using var main = new MainForm(services) { ShowInTaskbar = false, Opacity = 0, Size = new Size(1280, 800), StartPosition = FormStartPosition.Manual, Location = new Point(0, 0) };
                main.Show();
                var tabs = main.Controls.OfType<TabControl>().Single();
                foreach (TabPage page in tabs.TabPages)
                {
                    tabs.SelectedTab = page;
                    Capture(main, $"main-{(persian ? "fa" : "en")}-{page.Name}");
                }

                main.Close();
            }
            finally
            {
                Localizer.SetLanguage(false);
            }
        }
    }

    [Fact]
    public void Main_window_empty_state_renders()
    {
        RunOnSta(() =>
        {
            var folder = Path.Combine(Path.GetTempPath(), "storix-ui-" + Guid.NewGuid().ToString("N"));
            Environment.SetEnvironmentVariable(StorixPaths.DataDirectoryEnvironmentVariable, folder);
            using var main = new MainForm(AppServices.Create(), startInTray: true) { ShowInTaskbar = false, Opacity = 0, Size = new Size(1100, 700) };
            main.Show();
            Capture(main, "main-empty");
            main.Close();
        });
    }

    [Fact]
    public void Job_editor_tabs_render()
    {
        RunOnSta(() =>
        {
            var services = SeededServices();
            using var editor = new JobEditorForm(new BackupJob { Name = "New job" }, services.Destinations, services.Jobs.GetAll()) { ShowInTaskbar = false, Opacity = 0 };
            editor.Show();
            var tabs = editor.Controls.OfType<TabControl>().Single();
            foreach (TabPage page in tabs.TabPages)
            {
                tabs.SelectedTab = page;
                Capture(editor, "job-editor-" + page.Name.Replace(' ', '-').Replace("&", "and"));
            }

            editor.Close();
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Destination_editor_renders_every_type_with_its_guide(bool persian)
    {
        RunOnSta(() =>
        {
            Localizer.SetLanguage(persian);
            try
            {
                foreach (var kind in Enum.GetValues<DestinationKind>())
                {
                    using var editor = new DestinationEditorForm(new DestinationDefinition { Name = kind.ToString(), Kind = kind }, new NT.Storix.Core.Destinations.DestinationFactory())
                    {
                        ShowInTaskbar = false,
                        Opacity = 0,
                    };
                    editor.Show();
                    Capture(editor, $"destination-{(persian ? "fa" : "en")}-{kind}");
                    editor.Close();
                }
            }
            finally
            {
                Localizer.SetLanguage(false);
            }
        });
    }

    [Fact]
    public void Channel_editor_renders_every_kind_with_its_guide()
    {
        RunOnSta(() =>
        {
            foreach (var kind in Enum.GetValues<NotificationChannelKind>())
            {
                using var editor = new ChannelEditorForm(new NotificationChannel { Name = kind.ToString(), Kind = kind }) { ShowInTaskbar = false, Opacity = 0 };
                editor.Show();
                Capture(editor, $"channel-{kind}");
                editor.Close();
            }
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Batch_setup_renders_for_every_kind(bool persian)
    {
        RunOnSta(() =>
        {
            Localizer.SetLanguage(persian);
            try
            {
                var services = SeededServices();
                using var wizard = new BatchSetupForm(services.Jobs.GetAll(), services.Destinations) { ShowInTaskbar = false, Opacity = 0 };
                wizard.Show();
                foreach (var radio in wizard.Controls.OfType<TableLayoutPanel>().SelectMany(Descendants).OfType<RadioButton>().ToList())
                {
                    radio.Checked = true;
                    Capture(wizard, $"batch-{(persian ? "fa" : "en")}-{radio.Text.Split(' ')[0]}");
                }

                wizard.Close();
            }
            finally
            {
                Localizer.SetLanguage(false);
            }
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Template_gallery_renders_every_category(bool persian)
    {
        RunOnSta(() =>
        {
            Localizer.SetLanguage(persian);
            try
            {
                using var gallery = new TemplateGalleryForm { ShowInTaskbar = false, Opacity = 0 };
                gallery.Show();
                var cards = Descendants(gallery).OfType<TemplateCard>().ToList();
                Assert.Equal(NT.Storix.Core.Configuration.JobTemplate.All.Count + 1, cards.Count);
                Assert.All(cards, card => Assert.False(string.IsNullOrWhiteSpace(card.Text)));
                var language = persian ? "fa" : "en";
                Capture(gallery, $"gallery-{language}-all");
                var categories = Descendants(gallery).OfType<RadioButton>().ToList();
                for (var i = 1; i < categories.Count; i++)
                {
                    categories[i].Checked = true;
                    Capture(gallery, $"gallery-{language}-{i}");
                }

                gallery.Close();

                using var welcome = new WelcomeForm(serviceInstalled: false) { ShowInTaskbar = false, Opacity = 0 };
                welcome.Show();
                Capture(welcome, $"welcome-{language}");
                welcome.Close();
            }
            finally
            {
                Localizer.SetLanguage(false);
            }
        });
    }

    private static IEnumerable<Control> Descendants(Control control) =>
        control.Controls.Cast<Control>().SelectMany(c => new[] { c }.Concat(Descendants(c)));
}
