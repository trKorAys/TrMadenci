using System.Threading.Channels;
using TrMadenci.NativeBridge;

namespace TrMadenci.Service.Mining;

internal sealed record CudaShare(ulong Nonce, byte[] MixHash, byte[] FinalHash);

internal sealed class CudaKawPowWorker(
    int deviceIndex,
    Func<KawPowPoolWork, CudaShare, CancellationToken, Task> submit,
    Action<string> log) : IAsyncDisposable
{
    private const uint BatchSize = 65_536;
    private readonly Channel<KawPowPoolWork> _work = Channel.CreateBounded<KawPowPoolWork>(new BoundedChannelOptions(1)
    {
        FullMode = BoundedChannelFullMode.DropOldest,
        SingleReader = true,
        SingleWriter = false
    });
    private readonly CancellationTokenSource _lifetime = new();
    private Task? _task;
    private long _hashes;
    private long _searchTicks;
    private int _isHashing;
    private int _isPreparing;
    private int _paused = 1;

    public int DeviceIndex => deviceIndex;
    public bool IsHashing => Volatile.Read(ref _isHashing) != 0;
    public bool IsPreparing => Volatile.Read(ref _isPreparing) != 0;
    public ulong Hashes => unchecked((ulong)Interlocked.Read(ref _hashes));
    public TimeSpan SearchTime => TimeSpan.FromTicks(Interlocked.Read(ref _searchTicks));

    public void Start() => _task = Task.Run(() => RunAsync(_lifetime.Token), CancellationToken.None);
    public void Assign(KawPowPoolWork work)
    {
        Volatile.Write(ref _paused, 0);
        _work.Writer.TryWrite(work);
    }

    public void Pause() => Volatile.Write(ref _paused, 1);

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        CudaEpochContext? epoch = null;
        try
        {
            while (await _work.Reader.WaitToReadAsync(cancellationToken))
            {
                if (!_work.Reader.TryRead(out var current))
                    continue;
                while (_work.Reader.TryRead(out var newer))
                    current = newer;

                var requiredEpoch = checked((int)(current.Job.BlockHeight / 7500));
                if (epoch?.BuildInfo.EpochNumber != requiredEpoch)
                {
                    epoch?.Dispose();
                    Volatile.Write(ref _isHashing, 0);
                    Volatile.Write(ref _isPreparing, 1);
                    log($"GPU{deviceIndex}: building epoch {requiredEpoch} DAG...");
                    epoch = NativeDiagnostics.CreateCudaEpoch(checked((int)current.Job.BlockHeight), deviceIndex);
                    log($"GPU{deviceIndex}: DAG ready in {epoch.BuildInfo.BuildTime.TotalSeconds:F2}s.");
                    if (_work.Reader.TryRead(out var afterBuild))
                        current = afterBuild;
                    while (_work.Reader.TryRead(out var newerAfterBuild))
                        current = newerAfterBuild;
                    // Build the CPU light-cache before hashing starts so a found share
                    // can be cross-checked without pausing the live search pipeline.
                    _ = NativeDiagnostics.ComputeReferenceHash(
                        checked((int)current.Job.BlockHeight),
                        current.Job.HeaderHash,
                        current.Nonces.Reserve(1));
                    Volatile.Write(ref _isPreparing, 0);
                    log($"GPU{deviceIndex}: CPU share verifier ready for epoch {requiredEpoch}.");
                }

                while (!cancellationToken.IsCancellationRequested)
                {
                    if (_work.Reader.TryRead(out var replacement))
                    {
                        current = replacement;
                        while (_work.Reader.TryRead(out var newerReplacement))
                            current = newerReplacement;
                    }

                    Volatile.Write(ref _isHashing, 1);
                    var startNonce = current.Nonces.Reserve(BatchSize);
                    var result = epoch.Search(
                        checked((int)current.Job.BlockHeight),
                        current.Job.HeaderHash,
                        current.Target,
                        startNonce,
                        BatchSize);
                    Interlocked.Add(ref _hashes, checked((long)result.HashesSearched));
                    Interlocked.Add(ref _searchTicks, result.SearchTime.Ticks);

                    if (Volatile.Read(ref _paused) != 0)
                    {
                        Volatile.Write(ref _isHashing, 0);
                        break;
                    }

                    // A clean replacement makes a solution from the completed old batch stale.
                    if (_work.Reader.TryRead(out var next))
                    {
                        current = next;
                        while (_work.Reader.TryRead(out var newest))
                            current = newest;
                        continue;
                    }

                    if (result.SolutionFound)
                        await submit(current, new CudaShare(result.Nonce, result.MixHash, result.FinalHash), cancellationToken);
                }
            }
        }
        finally
        {
            Volatile.Write(ref _isHashing, 0);
            Volatile.Write(ref _isPreparing, 0);
            epoch?.Dispose();
        }
    }

    public async ValueTask DisposeAsync()
    {
        _lifetime.Cancel();
        _work.Writer.TryComplete();
        if (_task is not null)
        {
            try { await _task; }
            catch (OperationCanceledException) { }
        }
        _lifetime.Dispose();
    }
}
