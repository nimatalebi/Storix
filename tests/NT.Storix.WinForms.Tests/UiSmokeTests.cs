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
    }

    private static void Capture(Control control, string name)
    {
        Directory.CreateDirectory(Screenshots);
        Application.DoEvents();
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
    public void Main_window_tabs_render(bool persian)
    {
        RunOnSta(() =>
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
        });
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
}
