using System.Threading.Channels;
using TrMadenci.NativeBridge;

namespace TrMadenci.Service.Mining;

internal interface IEtcHashWorkerEpoch : IDisposable
{
    CudaEpochBuildInfo BuildInfo { get; }
    CudaSearchResult Search(int blockNumber, byte[] headerHash, byte[] target, ulong startNonce, uint nonceCount);
    void WarmVerifier(int blockNumber, byte[] headerHash, ulong nonce);
}

internal sealed class NativeEtcHashWorkerEpoch : IEtcHashWorkerEpoch
{
    private readonly EtcHashCudaEpochContext _context;

    public NativeEtcHashWorkerEpoch(int blockNumber, int deviceIndex) =>
        _context = NativeDiagnostics.CreateEtcHashCudaEpoch(blockNumber, deviceIndex);

    public CudaEpochBuildInfo BuildInfo => _context.BuildInfo;

    public CudaSearchResult Search(
        int blockNumber, byte[] headerHash, byte[] target, ulong startNonce, uint nonceCount) =>
        _context.Search(blockNumber, headerHash, target, startNonce, nonceCount);

    public void WarmVerifier(int blockNumber, byte[] headerHash, ulong nonce) =>
        _ = NativeDiagnostics.ComputeEtcHashReferenceHash(blockNumber, headerHash, nonce);

    public void Dispose() => _context.Dispose();
}

internal sealed class NativeOpenClEtcHashWorkerEpoch : IEtcHashWorkerEpoch
{
    private readonly EtcHashOpenClEpochContext _context;

    public NativeOpenClEtcHashWorkerEpoch(int blockNumber, ComputeDeviceId deviceId)
    {
        if (deviceId.Backend != ComputeBackendKind.OpenCl)
            throw new ArgumentException("An OpenCL device id is required.", nameof(deviceId));
        _context = NativeDiagnostics.CreateEtcHashOpenClEpoch(
            blockNumber, deviceId.PlatformIndex, deviceId.DeviceIndex);
    }

    public CudaEpochBuildInfo BuildInfo => new(
        _context.BuildInfo.EpochNumber,
        _context.BuildInfo.DeviceIndex,
        _context.BuildInfo.DatasetBytes,
        _context.BuildInfo.BuildTime);

    public CudaSearchResult Search(
        int blockNumber, byte[] headerHash, byte[] target, ulong startNonce, uint nonceCount) =>
        _context.Search(blockNumber, headerHash, target, startNonce, nonceCount);

    public void WarmVerifier(int blockNumber, byte[] headerHash, ulong nonce) =>
        _ = NativeDiagnostics.ComputeEtcHashReferenceHash(blockNumber, headerHash, nonce);

    public void Dispose() => _context.Dispose();
}

internal sealed class EtcHashWorker(
    ComputeDeviceId deviceId,
    Func<EtcHashPoolWork, CudaShare, CancellationToken, Task> submit,
    Action<string> log,
    Func<int, ComputeDeviceId, IEtcHashWorkerEpoch>? createEpoch = null,
    uint? batchSize = null,
    ComputeWorkerRecoveryOptions? recoveryOptions = null,
    TimeProvider? timeProvider = null,
    Func<TimeSpan, CancellationToken, Task>? waitBeforeRecovery = null) : IAsyncDisposable
{
    // About 17 ms on the qualification RTX 3060: near-sustained throughput while
    // keeping replacement-job latency far below one Stratum round trip.
    private const uint BatchSize = 262_144;
    private const uint OpenClQualificationBatchSize = 65_536;
    private readonly Channel<EtcHashPoolWork> _work = Channel.CreateBounded<EtcHashPoolWork>(
        new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ComputeWorkerHealthTracker _health = new(
        deviceId.ToString(), recoveryOptions, timeProvider);
    private readonly Func<TimeSpan, CancellationToken, Task> _waitBeforeRecovery =
        waitBeforeRecovery ?? ((delay, token) => Task.Delay(delay, token));
    private Task? _task;
    private long _hashes;
    private int _isHashing;
    private int _isPreparing;
    private int _paused = 1;

    public ComputeDeviceId DeviceId => deviceId;
    public int DeviceIndex => deviceId.DeviceIndex;
    public bool IsHashing => Volatile.Read(ref _isHashing) != 0;
    public bool IsPreparing => Volatile.Read(ref _isPreparing) != 0;
    public ulong Hashes => unchecked((ulong)Interlocked.Read(ref _hashes));
    public ComputeWorkerHealthSnapshot Health => _health.Snapshot;

    public void Start() => _task = Task.Run(() => RunAsync(_lifetime.Token), CancellationToken.None);

    public void Assign(EtcHashPoolWork work)
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
        IEtcHashWorkerEpoch? epoch = null;
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

                        if (epoch?.BuildInfo.EpochNumber != current.DatasetEpoch)
                        {
                            DisposeEpoch(ref epoch, log, deviceId.ToString());
                            Volatile.Write(ref _isHashing, 0);
                            Volatile.Write(ref _isPreparing, 1);
                            _health.MarkPreparing();
                            log($"{deviceId}: building ETCHash epoch {current.DatasetEpoch} DAG...");
                            epoch = (createEpoch ?? CreateNativeEpoch)(current.RepresentativeBlock, deviceId);
                            log($"{deviceId}: ETCHash DAG ready in {epoch.BuildInfo.BuildTime.TotalSeconds:F2}s.");

                            while (_work.Reader.TryRead(out var afterBuild))
                                current = afterBuild;
                            if (epoch.BuildInfo.EpochNumber != current.DatasetEpoch)
                                continue;

                            epoch.WarmVerifier(
                                current.RepresentativeBlock,
                                current.Job.HeaderHash,
                                current.Nonces.Reserve(1));
                            Volatile.Write(ref _isPreparing, 0);
                            log($"{deviceId}: ETCHash CPU share verifier ready for epoch {current.DatasetEpoch}.");
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
                        var currentBatchSize = batchSize ?? (deviceId.Backend == ComputeBackendKind.OpenCl
                            ? OpenClQualificationBatchSize
                            : BatchSize);
                        var startNonce = current.Nonces.Reserve(currentBatchSize);
                        var result = epoch!.Search(
                            current.RepresentativeBlock,
                            current.Job.HeaderHash,
                            current.Target,
                            startNonce,
                            currentBatchSize);
                        ValidateSearchResult(result, currentBatchSize, deviceId.ToString());
                        _health.MarkProgress();
                        Interlocked.Add(ref _hashes, checked((long)result.HashesSearched));

                        // Never submit work after a replacement arrived during the completed batch.
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
                                log($"{deviceId}: share submission stopped the worker: " +
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
                        DisposeEpoch(ref epoch, log, deviceId.ToString());
                        if (!_health.TryBeginRecovery(exception, out var attempt, out var delay))
                        {
                            log($"{deviceId}: compute worker stopped permanently: " +
                                $"{exception.GetType().Name}: {exception.Message}");
                            return;
                        }
                        log($"{deviceId}: compute failure; recovery {attempt}/" +
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
            log($"{deviceId}: compute worker terminated unexpectedly: " +
                $"{exception.GetType().Name}: {exception.Message}");
        }
        finally
        {
            Volatile.Write(ref _isHashing, 0);
            Volatile.Write(ref _isPreparing, 0);
            DisposeEpoch(ref epoch, log, deviceId.ToString());
            if (_health.Snapshot.Phase != ComputeWorkerPhase.Faulted)
                _health.MarkStopped();
        }
    }

    private static void ValidateSearchResult(CudaSearchResult result, uint expectedHashes, string deviceLabel)
    {
        if (result.HashesSearched != expectedHashes || result.SearchTime <= TimeSpan.Zero)
            throw new InvalidOperationException(
                $"{deviceLabel} returned an invalid search result: " +
                $"hashes={result.HashesSearched}/{expectedHashes}, time={result.SearchTime}.");
    }

    private static void DisposeEpoch(
        ref IEtcHashWorkerEpoch? epoch,
        Action<string> log,
        string deviceLabel)
    {
        if (epoch is null)
            return;
        try
        {
            epoch.Dispose();
        }
        catch (Exception exception)
        {
            log($"{deviceLabel}: epoch cleanup reported {exception.GetType().Name}: {exception.Message}");
        }
        finally
        {
            epoch = null;
        }
    }

    private static IEtcHashWorkerEpoch CreateNativeEpoch(int blockNumber, ComputeDeviceId selectedDevice) =>
        selectedDevice.Backend switch
        {
            ComputeBackendKind.Cuda => new NativeEtcHashWorkerEpoch(blockNumber, selectedDevice.DeviceIndex),
            ComputeBackendKind.OpenCl => new NativeOpenClEtcHashWorkerEpoch(blockNumber, selectedDevice),
            _ => throw new NotSupportedException($"Unsupported compute backend: {selectedDevice.Backend}.")
        };

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
                log($"{deviceId}: worker did not stop within " +
                    $"{_health.Options.StopTimeout.TotalSeconds:F0}s; process restart required.");
            }
        }
        if (_task?.IsCompleted != false)
            _lifetime.Dispose();
    }
}
