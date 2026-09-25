using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace NT.Storix.Core.Ipc;

public sealed class PipeRequest
{
    /// <summary><c>status</c>, <c>run</c>, <c>cancel</c> or <c>drill</c>.</summary>
    public string Command { get; set; } = "status";

    public Guid? JobId { get; set; }
}

public sealed record RunningJob(Guid JobId, string JobName, DateTimeOffset StartedAt, string Kind);

public sealed class PipeResponse
{
    public bool Ok { get; set; }

    public string? Error { get; set; }

    public string? Version { get; set; }

    public List<RunningJob> Running { get; set; } = [];
}

/// <summary>
/// Local API of the service: one JSON request and one JSON response per connection, over a named pipe (a Unix
/// domain socket on Linux). Only administrators and SYSTEM (Windows) or the same user (Linux) can connect.
/// Requests are also written to the database, so the pipe only makes them immediate; nothing is lost without it.
/// </summary>
public static class StorixPipe
{
    public const string DefaultName = "Storix";
    private const int MaxMessageBytes = 64 * 1024;

    /// <summary>Sends a request to the service. Returns null when the service is not reachable.</summary>
    public static async Task<PipeResponse?> SendAsync(PipeRequest request, TimeSpan timeout, string pipeName = DefaultName, CancellationToken cancellationToken = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);
        try
        {
            await using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await client.ConnectAsync(cts.Token).ConfigureAwait(false);
            await WriteAsync(client, request, cts.Token).ConfigureAwait(false);
            return await ReadAsync<PipeResponse>(client, cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>Serves requests until <paramref name="stoppingToken"/> is cancelled.</summary>
    public static async Task ServeAsync(Func<PipeRequest, CancellationToken, Task<PipeResponse>> handler, ILogger logger, CancellationToken stoppingToken, string pipeName = DefaultName)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            NamedPipeServerStream server;
            try
            {
                server = CreateServer(pipeName);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning("The local API pipe could not be created: {Error}. Retrying in 30 seconds.", ex.Message);
                await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
                continue;
            }

            try
            {
                await server.WaitForConnectionAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                await server.DisposeAsync();
                break;
            }

            _ = HandleAsync(server, handler, logger, stoppingToken);
        }
    }

    private static async Task HandleAsync(NamedPipeServerStream server, Func<PipeRequest, CancellationToken, Task<PipeResponse>> handler, ILogger logger, CancellationToken stoppingToken)
    {
        await using (server)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            cts.CancelAfter(TimeSpan.FromSeconds(10));
            try
            {
                var request = await ReadAsync<PipeRequest>(server, cts.Token);
                PipeResponse response;
                try
                {
                    response = request is null ? new PipeResponse { Error = "Empty request." } : await handler(request, cts.Token);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    response = new PipeResponse { Error = ex.Message };
                }

                response.Version ??= StorixInfo.Version;
                await WriteAsync(server, response, cts.Token);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or JsonException or InvalidDataException)
            {
                logger.LogDebug("Local API request failed: {Error}", ex.Message);
            }
        }
    }

    private static NamedPipeServerStream CreateServer(string pipeName)
    {
        if (OperatingSystem.IsWindows())
        {
            // Administrators and SYSTEM only: the manager runs elevated, the service as SYSTEM or a service account.
            var security = new PipeSecurity();
            security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.ReadWrite, AccessControlType.Allow));
            security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
            security.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User!, PipeAccessRights.FullControl, AccessControlType.Allow));
            return NamedPipeServerStreamAcl.Create(
                pipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, security);
        }

        return new NamedPipeServerStream(pipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
    }

    // Messages are one line of UTF-8 JSON.
    private static async Task WriteAsync<T>(Stream stream, T message, CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message, StorixJson.Options) + "\n");
        await stream.WriteAsync(bytes, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    private static async Task<T?> ReadAsync<T>(Stream stream, CancellationToken cancellationToken)
    {
        var buffer = new MemoryStream();
        var one = new byte[1];
        while (await stream.ReadAsync(one, cancellationToken) == 1 && one[0] != (byte)'\n')
        {
            buffer.WriteByte(one[0]);
            if (buffer.Length > MaxMessageBytes)
            {
                throw new InvalidDataException("Message too large.");
            }
        }

        return buffer.Length == 0 ? default : JsonSerializer.Deserialize<T>(buffer.ToArray(), StorixJson.Options);
    }
}
