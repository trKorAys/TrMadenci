using System.Threading.Channels;
using TrMadenci.NativeBridge;

namespace TrMadenci.Service.Mining;

internal sealed record CudaShare(ulong Nonce, byte[] MixHash, byte[] FinalHash);

internal interface IKawPowWorkerEpoch : IDisposable
{
    CudaEpochBuildInfo BuildInfo { get; }
    CudaSearchResult Search(int blockHeight, byte[] headerHash, byte[] target, ulong startNonce, uint nonceCount);
    void WarmVerifier(int blockHeight, byte[] headerHash, ulong nonce);
}

internal sealed class NativeKawPowWorkerEpoch(int blockHeight, int deviceIndex) : IKawPowWorkerEpoch
{
    private readonly CudaEpochContext _context = NativeDiagnostics.CreateCudaEpoch(blockHeight, deviceIndex);

    public CudaEpochBuildInfo BuildInfo => _context.BuildInfo;

    public CudaSearchResult Search(
        int blockHeight, byte[] headerHash, byte[] target, ulong startNonce, uint nonceCount) =>
        _context.Search(blockHeight, headerHash, target, startNonce, nonceCount);

    public void WarmVerifier(int blockHeight, byte[] headerHash, ulong nonce) =>
        _ = NativeDiagnostics.ComputeReferenceHash(blockHeight, headerHash, nonce);

    public void Dispose() => _context.Dispose();
}

internal sealed class CudaKawPowWorker(
    int deviceIndex,
    Func<KawPowPoolWork, CudaShare, CancellationToken, Task> submit,
    Action<string> log,
    Func<int, int, IKawPowWorkerEpoch>? createEpoch = null,
    uint? batchSize = null,
    ComputeWorkerRecoveryOptions? recoveryOptions = null,
    TimeProvider? timeProvider = null,
    Func<TimeSpan, CancellationToken, Task>? waitBeforeRecovery = null) : IAsyncDisposable
{
    private const uint BatchSize = 65_536;
    private readonly Channel<KawPowPoolWork> _work = Channel.CreateBounded<KawPowPoolWork>(new BoundedChannelOptions(1)
    {
        FullMode = BoundedChannelFullMode.DropOldest,
        SingleReader = true,
        SingleWriter = false
    });
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ComputeWorkerHealthTracker _health = new(
        $"cuda:{deviceIndex}", recoveryOptions, timeProvider);
    private readonly Func<TimeSpan, CancellationToken, Task> _waitBeforeRecovery =
        waitBeforeRecovery ?? ((delay, token) => Task.Delay(delay, token));
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
    public ComputeWorkerHealthSnapshot Health => _health.Snapshot;

    public void Start() => _task = Task.Run(() => RunAsync(_lifetime.Token), CancellationToken.None);
    public void Assign(KawPowPoolWork work)
    {
        Volatile.Write(ref _paused, 0);
        _work.Writer.TryWrite(work);
    }

    public void Pause()
    {
        Volatile.Write(ref _paused, 1);
        if (!IsHashing && !IsPreparing)
            _health.MarkPaused();
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        IKawPowWorkerEpoch? epoch = null;
        try
        {
            while (await _work.Reader.WaitToReadAsync(cancellationToken))
            {
                if (!_work.Reader.TryRead(out var current))
                    continue;

                while (!cancellationToken.IsCancellationRequested)
                {
                    try
                    {
                        while (_work.Reader.TryRead(out var newer))
                            current = newer;

                        var blockHeight = checked((int)current.Job.BlockHeight);
                        var requiredEpoch = checked(blockHeight / 7500);
                        if (epoch?.BuildInfo.EpochNumber != requiredEpoch)
                        {
                            DisposeEpoch(ref epoch, log, deviceIndex);
                            Volatile.Write(ref _isHashing, 0);
                            Volatile.Write(ref _isPreparing, 1);
                            _health.MarkPreparing();
                            log($"GPU{deviceIndex}: building epoch {requiredEpoch} DAG...");
                            epoch = (createEpoch ?? ((block, device) =>
                                new NativeKawPowWorkerEpoch(block, device)))(blockHeight, deviceIndex);
                            log($"GPU{deviceIndex}: DAG ready in {epoch.BuildInfo.BuildTime.TotalSeconds:F2}s.");
                            while (_work.Reader.TryRead(out var afterBuild))
                                current = afterBuild;
                            blockHeight = checked((int)current.Job.BlockHeight);
                            requiredEpoch = checked(blockHeight / 7500);
                            if (epoch.BuildInfo.EpochNumber != requiredEpoch)
                                continue;

                            // Build the CPU light-cache before hashing starts so a found share
                            // can be cross-checked without pausing the live search pipeline.
                            epoch.WarmVerifier(
                                blockHeight,
                                current.Job.HeaderHash,
                                current.Nonces.Reserve(1));
                            Volatile.Write(ref _isPreparing, 0);
                            log($"GPU{deviceIndex}: CPU share verifier ready for epoch {requiredEpoch}.");
                        }

                        if (Volatile.Read(ref _paused) != 0)
                        {
                            Volatile.Write(ref _isHashing, 0);
                            _health.MarkPaused();
                            break;
                        }

                        Volatile.Write(ref _isHashing, 1);
                        _health.MarkHashing();
                        var currentBatchSize = batchSize ?? BatchSize;
                        var startNonce = current.Nonces.Reserve(currentBatchSize);
                        var result = epoch!.Search(
                            blockHeight,
                            current.Job.HeaderHash,
                            current.Target,
                            startNonce,
                            currentBatchSize);
                        ValidateSearchResult(result, currentBatchSize, deviceIndex);
                        _health.MarkProgress();
                        Interlocked.Add(ref _hashes, checked((long)result.HashesSearched));
                        Interlocked.Add(ref _searchTicks, result.SearchTime.Ticks);

                        if (Volatile.Read(ref _paused) != 0)
                        {
                            Volatile.Write(ref _isHashing, 0);
                            _health.MarkPaused();
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
                        {
                            _health.MarkSubmitting();
                            try
                            {
                                await submit(
                                    current,
                                    new CudaShare(result.Nonce, result.MixHash, result.FinalHash),
                                    cancellationToken);
                                if (Volatile.Read(ref _paused) == 0)
                                    _health.MarkProgress();
                            }
                            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                            {
                                throw;
                            }
                            catch (Exception exception)
                            {
                                _health.MarkFaulted(exception);
                                log($"GPU{deviceIndex}: share submission stopped the worker: " +
                                    $"{exception.GetType().Name}: {exception.Message}");
                                return;
                            }
                        }
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception exception)
                    {
                        Volatile.Write(ref _isHashing, 0);
                        Volatile.Write(ref _isPreparing, 0);
                        DisposeEpoch(ref epoch, log, deviceIndex);
                        if (!_health.TryBeginRecovery(exception, out var attempt, out var delay))
                        {
                            log($"GPU{deviceIndex}: compute worker stopped permanently: " +
                                $"{exception.GetType().Name}: {exception.Message}");
                            return;
                        }
                        log($"GPU{deviceIndex}: compute failure; recovery {attempt}/" +
                            $"{_health.Options.MaximumRecoveryAttempts} in {delay.TotalSeconds:F1}s: " +
                            $"{exception.GetType().Name}: {exception.Message}");
                        await _waitBeforeRecovery(delay, cancellationToken);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _health.MarkFaulted(exception);
            log($"GPU{deviceIndex}: compute worker terminated unexpectedly: " +
                $"{exception.GetType().Name}: {exception.Message}");
        }
        finally
        {
            Volatile.Write(ref _isHashing, 0);
            Volatile.Write(ref _isPreparing, 0);
            DisposeEpoch(ref epoch, log, deviceIndex);
            if (_health.Snapshot.Phase != ComputeWorkerPhase.Faulted)
                _health.MarkStopped();
        }
    }

    private static void ValidateSearchResult(CudaSearchResult result, uint expectedHashes, int deviceIndex)
    {
        if (result.HashesSearched != expectedHashes || result.SearchTime <= TimeSpan.Zero)
            throw new InvalidOperationException(
                $"GPU{deviceIndex} returned an invalid search result: " +
                $"hashes={result.HashesSearched}/{expectedHashes}, time={result.SearchTime}.");
    }

    private static void DisposeEpoch(
        ref IKawPowWorkerEpoch? epoch,
        Action<string> log,
        int deviceIndex)
    {
        if (epoch is null)
            return;
        try
        {
            epoch.Dispose();
        }
        catch (Exception exception)
        {
            log($"GPU{deviceIndex}: epoch cleanup reported " +
                $"{exception.GetType().Name}: {exception.Message}");
        }
        finally
        {
            epoch = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        _lifetime.Cancel();
        _work.Writer.TryComplete();
        if (_task is not null)
        {
            var completed = await Task.WhenAny(
                _task,
                Task.Delay(_health.Options.StopTimeout, CancellationToken.None));
            if (completed == _task)
            {
                try { await _task; }
                catch (OperationCanceledException) { }
            }
            else
            {
                log($"GPU{deviceIndex}: worker did not stop within " +
                    $"{_health.Options.StopTimeout.TotalSeconds:F0}s; process restart required.");
            }
        }
        if (_task?.IsCompleted != false)
            _lifetime.Dispose();
    }
}
