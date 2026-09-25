using System.Text;
using NT.Storix.Cli;

Console.OutputEncoding = Encoding.UTF8;

try
{
    return await Commands.RunAsync(args, Console.Out);
}
catch (CliException ex)
{
    Console.Error.WriteLine($"error: {ex.Message}");
    Console.Error.WriteLine("Run 'storix help' for usage.");
    return 2;
}
catch (Exception ex) when (ex is not OutOfMemoryException)
{
    Console.Error.WriteLine($"error: {ex.Message}");
    return 1;
}
