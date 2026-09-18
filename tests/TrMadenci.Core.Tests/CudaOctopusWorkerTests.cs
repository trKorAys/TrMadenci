using TrMadenci.Core.Configuration;
using TrMadenci.Core.Fees;
using TrMadenci.NativeBridge;
using TrMadenci.Protocols.Stratum;
using TrMadenci.Service.Mining;

namespace TrMadenci.Core.Tests;

public sealed class CudaOctopusWorkerTests
{
    [Fact]
    public async Task Replacement_during_gpu_batch_discards_old_solution_and_submits_new_job()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var releaseFirstSearch = new ManualResetEventSlim();
        var backend = new ControlledEpoch(releaseFirstSearch);
        var submitted = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var pool = new OctopusPoolClient(
            new PoolOptions
            {
                Host = "127.0.0.1",
                Port = 1,
                Username = "test.worker",
                Password = "x"
            },
            MiningBeneficiary.User,
            _ => { });
        CudaOctopusWorker? worker = null;
        worker = new CudaOctopusWorker(
            0,
            (work, _, _) =>
            {
                submitted.TrySetResult(work.Job.JobId);
                worker!.Pause();
                return Task.CompletedTask;
            },
            _ => { },
            (_, _) => backend);

        await using (worker)
        {
            worker.Start();
            worker.Assign(Work(pool, "old-job", 0x11));
            await backend.FirstSearchStarted.Task.WaitAsync(timeout.Token);

            worker.Assign(Work(pool, "new-job", 0x22));
            releaseFirstSearch.Set();

            Assert.Equal("new-job", await submitted.Task.WaitAsync(timeout.Token));
            Assert.Equal(2, backend.SearchCount);
        }
    }

    private static OctopusPoolWork Work(OctopusPoolClient pool, string jobId, byte headerByte) => new(
        pool,
        new OctopusJob(
            jobId,
            2,
            Enumerable.Repeat(headerByte, 32).ToArray(),
            Enumerable.Repeat((byte)0xff, 32).ToArray()),
        0,
        Enumerable.Repeat((byte)0xff, 32).ToArray(),
        new NonceAllocator(0),
        MiningBeneficiary.User);

    private sealed class ControlledEpoch(ManualResetEventSlim releaseFirstSearch) : IOctopusWorkerEpoch
    {
        private int _searchCount;

        public TaskCompletionSource FirstSearchStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int SearchCount => Volatile.Read(ref _searchCount);
        public CudaEpochBuildInfo BuildInfo { get; } =
            new(0, 0, 4UL * 1024 * 1024 * 1024, TimeSpan.FromSeconds(1));

        public CudaSearchResult Search(
            ulong blockNumber, byte[] headerHash, byte[] target, ulong startNonce, uint nonceCount)
        {
            var call = Interlocked.Increment(ref _searchCount);
            if (call == 1)
            {
                FirstSearchStarted.TrySetResult();
                if (!releaseFirstSearch.Wait(TimeSpan.FromSeconds(5)))
                    throw new TimeoutException("Test did not release the first simulated GPU batch.");
            }

            return new CudaSearchResult(
                true,
                startNonce,
                Enumerable.Repeat((byte)call, 32).ToArray(),
                Enumerable.Repeat((byte)call, 32).ToArray(),
                nonceCount,
                TimeSpan.FromMilliseconds(5));
        }

        public void WarmVerifier(ulong blockNumber, byte[] headerHash, ulong nonce) { }
        public void Dispose() { }
    }
}
