using NT.Storix.WinForms.Forms;
using NT.Storix.WinForms.Infrastructure;

namespace NT.Storix.WinForms;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => Dialogs.Error(null, e.Exception.Message);

        AppServices services;
        try
        {
            services = AppServices.Create();
        }
        catch (Exception ex)
        {
            Dialogs.Error(null, $"Could not open the Storix database.\n\n{ex.Message}");
            return;
        }

        Application.Run(new MainForm(services));
    }
}
