namespace TrMadenci.NativeBridge;

public interface IMiningEngine : IAsyncDisposable
{
    bool IsAvailable { get; }
    Task StartAsync(CancellationToken cancellationToken);
    Task StopAsync(CancellationToken cancellationToken);
}

public sealed class UnavailableMiningEngine : IMiningEngine
{
    public bool IsAvailable => false;

    public Task StartAsync(CancellationToken cancellationToken) =>
        throw new InvalidOperationException("The CUDA native engine has not been built yet.");

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
