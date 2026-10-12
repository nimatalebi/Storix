using System.Text;
using NT.KeepstoneBackup.Cli;

Console.OutputEncoding = Encoding.UTF8;

try
{
    return await Commands.RunAsync(args, Console.Out);
}
catch (CliException ex)
{
    Console.Error.WriteLine($"error: {ex.Message}");
    Console.Error.WriteLine("Run 'keepstonebackup help' for usage.");
    return 2;
}
catch (Exception ex) when (ex is not OutOfMemoryException)
{
    Console.Error.WriteLine($"error: {ex.Message}");
    return 1;
}
