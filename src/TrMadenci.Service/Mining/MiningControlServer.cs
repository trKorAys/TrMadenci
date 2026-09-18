using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace TrMadenci.Service.Mining;

internal enum MiningControlCommand
{
    Pause,
    Resume,
    Status
}

internal sealed record MiningControlResponse(
    bool Success,
    string State,
    int ProcessId,
    int PauseCount,
    TimeSpan TotalPausedDuration,
    string Message,
    DateTimeOffset Timestamp);

internal sealed class MiningControlServer(
    MiningPauseController pauseController,
    Action<string>? log = null,
    string? pipeName = null) : IAsyncDisposable
{
    public const string DefaultPipeName = "TrMadenci.Mining.Control.v1";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly string _pipeName = pipeName ?? DefaultPipeName;
    private Task? _serverTask;

    public void Start()
    {
        if (_serverTask is not null)
            throw new InvalidOperationException("The mining control server is already running.");
        _serverTask = Task.Run(() => RunAsync(_lifetime.Token), CancellationToken.None);
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var options = PipeOptions.Asynchronous;
            if (OperatingSystem.IsWindows())
                options |= PipeOptions.CurrentUserOnly;

            await using var pipe = new NamedPipeServerStream(
                _pipeName,
                PipeDirection.InOut,
                1,
                PipeTransmissionMode.Byte,
                options);
            try
            {
                await pipe.WaitForConnectionAsync(cancellationToken);
                await ProcessRequestAsync(pipe, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception) when (
                exception is IOException or JsonException or InvalidOperationException)
            {
                log?.Invoke($"Mining control request failed: {exception.Message}");
            }
        }
    }

    private async Task ProcessRequestAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(
            stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
        await using var writer = new StreamWriter(
            stream, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        var request = await reader.ReadLineAsync(cancellationToken);
        var response = Handle(request);
        await writer.WriteLineAsync(JsonSerializer.Serialize(response, JsonOptions));
    }

    private MiningControlResponse Handle(string? request)
    {
        var normalized = request?.Trim().ToLowerInvariant();
        bool success;
        string message;
        switch (normalized)
        {
            case "pause":
                var paused = pauseController.Pause();
                success = true;
                message = paused ? "Mining paused." : "Mining was already paused.";
                break;
            case "resume":
                var resumed = pauseController.Resume();
                success = true;
                message = resumed ? "Mining resumed." : "Mining was already running.";
                break;
            case "status":
                success = true;
                message = pauseController.IsPaused ? "Mining is paused." : "Mining is running.";
                break;
            default:
                success = false;
                message = "Unknown mining control command.";
                break;
        }

        var snapshot = pauseController.Snapshot;
        return new MiningControlResponse(
            success,
            snapshot.IsPaused ? "paused" : "running",
            Environment.ProcessId,
            snapshot.PauseCount,
            snapshot.TotalPausedDuration,
            message,
            DateTimeOffset.Now);
    }

    public async ValueTask DisposeAsync()
    {
        _lifetime.Cancel();
        if (_serverTask is not null)
        {
            try
            {
                await _serverTask;
            }
            catch (OperationCanceledException)
            {
            }
        }
        _lifetime.Dispose();
    }
}

internal static class MiningControlClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static async Task<MiningControlResponse> SendAsync(
        MiningControlCommand command,
        string? pipeName = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout ?? TimeSpan.FromSeconds(3));
        var options = PipeOptions.Asynchronous;
        if (OperatingSystem.IsWindows())
            options |= PipeOptions.CurrentUserOnly;
        await using var pipe = new NamedPipeClientStream(
            ".",
            pipeName ?? MiningControlServer.DefaultPipeName,
            PipeDirection.InOut,
            options);
        await pipe.ConnectAsync(timeoutSource.Token);

        using var reader = new StreamReader(
            pipe, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
        await using var writer = new StreamWriter(
            pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
        await writer.WriteLineAsync(command.ToString().ToLowerInvariant());
        var responseJson = await reader.ReadLineAsync(timeoutSource.Token) ??
            throw new IOException("The mining control server closed without a response.");
        return JsonSerializer.Deserialize<MiningControlResponse>(responseJson, JsonOptions) ??
            throw new JsonException("The mining control response was empty.");
    }
}
