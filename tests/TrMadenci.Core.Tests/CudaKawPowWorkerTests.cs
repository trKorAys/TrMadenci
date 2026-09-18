using TrMadenci.Core.Configuration;
using TrMadenci.Core.Fees;
using TrMadenci.NativeBridge;
using TrMadenci.Protocols.Stratum;
using TrMadenci.Service.Mining;

namespace TrMadenci.Core.Tests;

public sealed class CudaKawPowWorkerTests
{
    [Fact]
    public async Task Replacement_across_epoch_boundary_rebuilds_DAG_and_discards_stale_solution()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var releaseFirstSearch = new ManualResetEventSlim();
        var firstEpoch = new ControlledEpoch(1, releaseFirstSearch);
        var secondEpoch = new ImmediateEpoch(2);
        var createdEpochs = new List<int>();
        var submitted = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var pool = new KawPowPoolClient(
            new PoolOptions
            {
                Host = "127.0.0.1",
                Port = 1,
                Username = "test.worker",
                Password = "x"
            },
            MiningBeneficiary.User,
            _ => { });
        CudaKawPowWorker? worker = null;
        worker = new CudaKawPowWorker(
            0,
            (work, _, _) =>
            {
                submitted.TrySetResult(work.Job.JobId);
                worker!.Pause();
                return Task.CompletedTask;
            },
            _ => { },
            (blockHeight, _) =>
            {
                var epoch = blockHeight / 7500;
                createdEpochs.Add(epoch);
                return epoch == 1 ? firstEpoch : secondEpoch;
            },
            batchSize: 128);

        await using (worker)
        {
            worker.Start();
            worker.Assign(Work(pool, "old-job", 7_500, 0x11));
            await firstEpoch.FirstSearchStarted.Task.WaitAsync(timeout.Token);

            worker.Assign(Work(pool, "new-job", 15_000, 0x22));
            releaseFirstSearch.Set();

            Assert.Equal("new-job", await submitted.Task.WaitAsync(timeout.Token));
        }

        Assert.Equal([1, 2], createdEpochs);
        Assert.True(firstEpoch.Disposed);
        Assert.Equal(1, secondEpoch.SearchCount);
    }

    private static KawPowPoolWork Work(
        KawPowPoolClient pool,
        string jobId,
        ulong blockHeight,
        byte headerByte) => new(
            pool,
            new KawPowJob(
                jobId,
                Enumerable.Repeat(headerByte, 32).ToArray(),
                new byte[32],
                Enumerable.Repeat((byte)0xff, 32).ToArray(),
                true,
                blockHeight,
                0x1d00ffff),
            Enumerable.Repeat((byte)0xff, 32).ToArray(),
            new NonceAllocator(0),
            MiningBeneficiary.User);

    private sealed class ControlledEpoch(
        int epochNumber,
        ManualResetEventSlim releaseFirstSearch) : IKawPowWorkerEpoch
    {
        public TaskCompletionSource FirstSearchStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Disposed { get; private set; }
        public CudaEpochBuildInfo BuildInfo { get; } =
            new(epochNumber, 0, 4UL * 1024 * 1024 * 1024, TimeSpan.FromSeconds(1));

        public CudaSearchResult Search(
            int blockHeight, byte[] headerHash, byte[] target, ulong startNonce, uint nonceCount)
        {
            FirstSearchStarted.TrySetResult();
            if (!releaseFirstSearch.Wait(TimeSpan.FromSeconds(5)))
                throw new TimeoutException("Test did not release the simulated GPU batch.");
            return Result(startNonce, nonceCount);
        }

        public void WarmVerifier(int blockHeight, byte[] headerHash, ulong nonce) { }
        public void Dispose() => Disposed = true;
    }

    private sealed class ImmediateEpoch(int epochNumber) : IKawPowWorkerEpoch
    {
        private int _searchCount;
        public int SearchCount => Volatile.Read(ref _searchCount);
        public CudaEpochBuildInfo BuildInfo { get; } =
            new(epochNumber, 0, 4UL * 1024 * 1024 * 1024, TimeSpan.FromSeconds(1));

        public CudaSearchResult Search(
            int blockHeight, byte[] headerHash, byte[] target, ulong startNonce, uint nonceCount)
        {
            Interlocked.Increment(ref _searchCount);
            return Result(startNonce, nonceCount);
        }

        public void WarmVerifier(int blockHeight, byte[] headerHash, ulong nonce) { }
        public void Dispose() { }
    }

    private static CudaSearchResult Result(ulong startNonce, uint nonceCount) => new(
        true,
        startNonce,
        new byte[32],
        new byte[32],
        nonceCount,
        TimeSpan.FromMilliseconds(5));
}
