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

internal sealed class CudaEtcHashWorker(
    int deviceIndex,
    Func<EtcHashPoolWork, CudaShare, CancellationToken, Task> submit,
    Action<string> log,
    Func<int, int, IEtcHashWorkerEpoch>? createEpoch = null) : IAsyncDisposable
{
    // About 17 ms on the qualification RTX 3060: near-sustained throughput while
    // keeping replacement-job latency far below one Stratum round trip.
    private const uint BatchSize = 262_144;
    private readonly Channel<EtcHashPoolWork> _work = Channel.CreateBounded<EtcHashPoolWork>(
        new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });
    private readonly CancellationTokenSource _lifetime = new();
    private Task? _task;
    private long _hashes;
    private int _isHashing;
    private int _isPreparing;
    private int _paused = 1;

    public int DeviceIndex => deviceIndex;
    public bool IsHashing => Volatile.Read(ref _isHashing) != 0;
    public bool IsPreparing => Volatile.Read(ref _isPreparing) != 0;
    public ulong Hashes => unchecked((ulong)Interlocked.Read(ref _hashes));

    public void Start() => _task = Task.Run(() => RunAsync(_lifetime.Token), CancellationToken.None);

    public void Assign(EtcHashPoolWork work)
    {
        Volatile.Write(ref _paused, 0);
        _work.Writer.TryWrite(work);
    }

    public void Pause() => Volatile.Write(ref _paused, 1);

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
                    while (_work.Reader.TryRead(out var newer))
                        current = newer;

                    if (epoch?.BuildInfo.EpochNumber != current.DatasetEpoch)
                    {
                        epoch?.Dispose();
                        epoch = null;
                        Volatile.Write(ref _isHashing, 0);
                        Volatile.Write(ref _isPreparing, 1);
                        log($"GPU{deviceIndex}: building ETCHash epoch {current.DatasetEpoch} DAG...");
                        epoch = (createEpoch ?? ((block, device) => new NativeEtcHashWorkerEpoch(block, device)))(
                            current.RepresentativeBlock, deviceIndex);
                        log($"GPU{deviceIndex}: ETCHash DAG ready in {epoch.BuildInfo.BuildTime.TotalSeconds:F2}s.");

                        while (_work.Reader.TryRead(out var afterBuild))
                            current = afterBuild;
                        if (epoch.BuildInfo.EpochNumber != current.DatasetEpoch)
                            continue;

                        epoch.WarmVerifier(
                            current.RepresentativeBlock,
                            current.Job.HeaderHash,
                            current.Nonces.Reserve(1));
                        Volatile.Write(ref _isPreparing, 0);
                        log($"GPU{deviceIndex}: ETCHash CPU share verifier ready for epoch {current.DatasetEpoch}.");
                    }

                    if (Volatile.Read(ref _paused) != 0)
                    {
                        Volatile.Write(ref _isHashing, 0);
                        break;
                    }

                    if (_work.Reader.TryRead(out var replacement))
                    {
                        current = replacement;
                        continue;
                    }

                    Volatile.Write(ref _isHashing, 1);
                    var startNonce = current.Nonces.Reserve(BatchSize);
                    var result = epoch.Search(
                        current.RepresentativeBlock,
                        current.Job.HeaderHash,
                        current.Target,
                        startNonce,
                        BatchSize);
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
                        break;
                    }

                    if (result.SolutionFound)
                        await submit(
                            current,
                            new CudaShare(result.Nonce, result.MixHash, result.FinalHash),
                            cancellationToken);
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
