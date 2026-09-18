using System.Threading.Channels;
using TrMadenci.Core.Algorithms;
using TrMadenci.NativeBridge;

namespace TrMadenci.Service.Mining;

internal interface IOctopusWorkSink
{
    void Assign(OctopusPoolWork work);
    void Pause();
}

internal interface IOctopusWorkerEpoch : IDisposable
{
    CudaEpochBuildInfo BuildInfo { get; }
    CudaSearchResult Search(ulong blockNumber, byte[] headerHash, byte[] target, ulong startNonce, uint nonceCount);
    void WarmVerifier(ulong blockNumber, byte[] headerHash, ulong nonce);
}

internal sealed class NativeOctopusWorkerEpoch : IOctopusWorkerEpoch
{
    private readonly OctopusCudaEpochContext _context;

    public NativeOctopusWorkerEpoch(ulong blockNumber, int deviceIndex) =>
        _context = NativeDiagnostics.CreateOctopusCudaEpoch(blockNumber, deviceIndex);

    public CudaEpochBuildInfo BuildInfo => _context.BuildInfo;

    public CudaSearchResult Search(
        ulong blockNumber, byte[] headerHash, byte[] target, ulong startNonce, uint nonceCount) =>
        _context.Search(blockNumber, headerHash, target, startNonce, nonceCount);

    public void WarmVerifier(ulong blockNumber, byte[] headerHash, ulong nonce)
    {
        var multiPoint = OctopusMultiPoint.Evaluate(headerHash, nonce);
        _ = NativeDiagnostics.ComputeOctopusReferenceHash(
            blockNumber, headerHash, nonce, multiPoint.Compressed, multiPoint.Points.ToArray());
    }

    public void Dispose() => _context.Dispose();
}

internal sealed class CudaOctopusWorker(
    int deviceIndex,
    Func<OctopusPoolWork, CudaShare, CancellationToken, Task> submit,
    Action<string> log,
    Func<ulong, int, IOctopusWorkerEpoch>? createEpoch = null,
    ComputeWorkerRecoveryOptions? recoveryOptions = null,
    TimeProvider? timeProvider = null,
    Func<TimeSpan, CancellationToken, Task>? waitBeforeRecovery = null) : IAsyncDisposable, IOctopusWorkSink
{
    // Intentionally conservative until the kernel is qualified on real hardware.
    private const uint BatchSize = 256;
    private readonly Channel<OctopusPoolWork> _work = Channel.CreateBounded<OctopusPoolWork>(
        new BoundedChannelOptions(1)
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
    private int _isHashing;
    private int _isPreparing;
    private int _paused = 1;

    public int DeviceIndex => deviceIndex;
    public bool IsHashing => Volatile.Read(ref _isHashing) != 0;
    public bool IsPreparing => Volatile.Read(ref _isPreparing) != 0;
    public ulong Hashes => unchecked((ulong)Interlocked.Read(ref _hashes));
    public ComputeWorkerHealthSnapshot Health => _health.Snapshot;

    public void Start() => _task = Task.Run(() => RunAsync(_lifetime.Token), CancellationToken.None);

    public void Assign(OctopusPoolWork work)
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
        IOctopusWorkerEpoch? epoch = null;
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

                        if (epoch?.BuildInfo.EpochNumber != current.EpochNumber)
                        {
                            DisposeEpoch(ref epoch, log, deviceIndex);
                            Volatile.Write(ref _isHashing, 0);
                            Volatile.Write(ref _isPreparing, 1);
                            _health.MarkPreparing();
                            log($"GPU{deviceIndex}: building Octopus epoch {current.EpochNumber} DAG...");
                            epoch = (createEpoch ?? ((block, device) => new NativeOctopusWorkerEpoch(block, device)))(
                                current.Job.BlockHeight, deviceIndex);
                            log($"GPU{deviceIndex}: Octopus DAG ready in {epoch.BuildInfo.BuildTime.TotalSeconds:F2}s.");

                            while (_work.Reader.TryRead(out var afterBuild))
                                current = afterBuild;
                            if (epoch.BuildInfo.EpochNumber != current.EpochNumber)
                                continue;

                            epoch.WarmVerifier(
                                current.Job.BlockHeight, current.Job.HeaderHash, current.Nonces.Reserve(1));
                            Volatile.Write(ref _isPreparing, 0);
                            log($"GPU{deviceIndex}: Octopus CPU share verifier ready for epoch {current.EpochNumber}.");
                        }

                        if (Volatile.Read(ref _paused) != 0)
                        {
                            Volatile.Write(ref _isHashing, 0);
                            _health.MarkPaused();
                            break;
                        }
                        if (_work.Reader.TryRead(out var replacement))
                        {
                            current = replacement;
                            continue;
                        }

                        Volatile.Write(ref _isHashing, 1);
                        _health.MarkHashing();
                        var startNonce = current.Nonces.Reserve(BatchSize);
                        var result = epoch!.Search(
                            current.Job.BlockHeight, current.Job.HeaderHash, current.Target, startNonce, BatchSize);
                        ValidateSearchResult(result, BatchSize, deviceIndex);
                        _health.MarkProgress();
                        Interlocked.Add(ref _hashes, checked((long)result.HashesSearched));

                        if (_work.Reader.TryRead(out var next))
                        {
                            current = next;
                            continue;
                        }
                        if (Volatile.Read(ref _paused) != 0)
                        {
                            Volatile.Write(ref _isHashing, 0);
                            _health.MarkPaused();
                            break;
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
                $"GPU{deviceIndex} returned an invalid Octopus search result: " +
                $"hashes={result.HashesSearched}/{expectedHashes}, time={result.SearchTime}.");
    }

    private static void DisposeEpoch(
        ref IOctopusWorkerEpoch? epoch,
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
