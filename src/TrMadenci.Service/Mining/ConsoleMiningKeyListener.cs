namespace TrMadenci.Service.Mining;

internal sealed class ConsoleMiningKeyListener(
    Func<MiningControlCommand, CancellationToken, Task<string>> execute,
    Action<string> log) : IAsyncDisposable
{
    private readonly CancellationTokenSource _lifetime = new();
    private Task? _listenerTask;

    public static ConsoleMiningKeyListener CreateLocal(
        MiningPauseController pauseController,
        Action<string> log) => new(
        (command, _) => Task.FromResult(ExecuteLocal(pauseController, command)),
        log);

    public static ConsoleMiningKeyListener CreateRemote(
        Action<string> log,
        Action<MiningControlResponse>? updateControl = null) => new(
        async (command, cancellationToken) =>
        {
            try
            {
                var response = await MiningControlClient.SendAsync(
                    command, cancellationToken: cancellationToken);
                updateControl?.Invoke(response);
                return Format(response);
            }
            catch (Exception exception) when (
                exception is IOException or TimeoutException or OperationCanceledException)
            {
                return $"Mining control is not available: {exception.Message}";
            }
        },
        log);

    public void Start()
    {
        if (_listenerTask is not null)
            throw new InvalidOperationException("The console mining key listener is already running.");
        if (Console.IsInputRedirected || Console.IsOutputRedirected)
            return;
        _listenerTask = Task.Run(() => RunAsync(_lifetime.Token), CancellationToken.None);
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (!Console.KeyAvailable)
                {
                    await Task.Delay(100, cancellationToken);
                    continue;
                }

                var key = Console.ReadKey(intercept: true).Key;
                var command = MapKey(key);
                if (command is not null)
                    log(await execute(command.Value, cancellationToken));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception) when (exception is IOException or InvalidOperationException)
            {
                log($"Console mining control stopped: {exception.Message}");
                return;
            }
        }
    }

    internal static MiningControlCommand? MapKey(ConsoleKey key) => key switch
    {
        ConsoleKey.P => MiningControlCommand.Pause,
        ConsoleKey.S or ConsoleKey.R => MiningControlCommand.Resume,
        ConsoleKey.D => MiningControlCommand.Status,
        _ => null
    };

    private static string ExecuteLocal(
        MiningPauseController pauseController,
        MiningControlCommand command)
    {
        var changed = command switch
        {
            MiningControlCommand.Pause => pauseController.Pause(),
            MiningControlCommand.Resume => pauseController.Resume(),
            _ => false
        };
        var snapshot = pauseController.Snapshot;
        var action = command switch
        {
            MiningControlCommand.Pause when !changed => "Mining was already paused.",
            MiningControlCommand.Pause => "Pause command accepted.",
            MiningControlCommand.Resume when !changed => "Mining was already running.",
            MiningControlCommand.Resume => "Resume command accepted.",
            _ => snapshot.IsPaused ? "Mining is paused." : "Mining is running."
        };
        return $"{action} Pauses={snapshot.PauseCount}, paused time={snapshot.TotalPausedDuration:c}.";
    }

    private static string Format(MiningControlResponse response) =>
        $"{response.Message} State={response.State}, PID={response.ProcessId}, " +
        $"pauses={response.PauseCount}, paused time={response.TotalPausedDuration:c}.";

    public async ValueTask DisposeAsync()
    {
        _lifetime.Cancel();
        if (_listenerTask is not null)
        {
            try
            {
                await _listenerTask;
            }
            catch (OperationCanceledException)
            {
            }
        }
        _lifetime.Dispose();
    }
}
