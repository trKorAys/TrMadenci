using TrMadenci.Core.Configuration;
using TrMadenci.Core.Fees;
using TrMadenci.NativeBridge;
using TrMadenci.Protocols.Stratum;
using TrMadenci.Service.Mining;

namespace TrMadenci.Core.Tests;

public sealed class CudaEtcHashWorkerTests
{
    [Fact]
    public async Task Transient_compute_failure_rebuilds_epoch_and_resumes_search()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var logs = new List<string>();
        var submitted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstEpoch = new FailingEpoch();
        var secondEpoch = new ImmediateEpoch();
        var creations = 0;
        var poolOptions = new PoolOptions
        {
            Host = "127.0.0.1",
            Port = 1,
            Username = "test.worker",
            Password = "x"
        };
        await using var pool = new EtcHashPoolClient(
            poolOptions, MiningBeneficiary.User, _ => { }, resolveSeedEpoch: _ => 844);
        EtcHashWorker? worker = null;
        worker = new EtcHashWorker(
            new ComputeDeviceId(ComputeBackendKind.Cuda, 0, 0),
            (_, _, _) =>
            {
                worker!.Pause();
                submitted.TrySetResult();
                return Task.CompletedTask;
            },
            logs.Add,
            (_, _) => Interlocked.Increment(ref creations) == 1 ? firstEpoch : secondEpoch,
            batchSize: 128,
            recoveryOptions: new ComputeWorkerRecoveryOptions(
                3,
                TimeSpan.Zero,
                TimeSpan.FromSeconds(30),
                TimeSpan.FromMinutes(1),
                TimeSpan.FromSeconds(30),
                TimeSpan.FromSeconds(1)),
            waitBeforeRecovery: (_, _) => Task.CompletedTask);

        await using (worker)
        {
            worker.Start();
            worker.Assign(Work(pool, "recoverable-job", 0x44));
            await submitted.Task.WaitAsync(timeout.Token);
        }

        Assert.Equal(2, creations);
        Assert.True(firstEpoch.Disposed);
        Assert.Equal(1, worker.Health.TotalRecoveries);
        Assert.Contains(logs, message => message.Contains("recovery 1/3", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Submission_failure_faults_worker_without_rebuilding_GPU_epoch()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var submissionAttempted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var logs = new List<string>();
        var epoch = new ImmediateEpoch();
        var creations = 0;
        await using var pool = new EtcHashPoolClient(
            new PoolOptions
            {
                Host = "127.0.0.1",
                Port = 1,
                Username = "test.worker",
                Password = "x"
            },
            MiningBeneficiary.User,
            _ => { },
            resolveSeedEpoch: _ => 844);
        var worker = new EtcHashWorker(
            new ComputeDeviceId(ComputeBackendKind.Cuda, 0, 0),
            (_, _, _) =>
            {
                submissionAttempted.TrySetResult();
                throw new IOException("simulated pool write failure");
            },
            logs.Add,
            (_, _) =>
            {
                Interlocked.Increment(ref creations);
                return epoch;
            },
            batchSize: 128,
            recoveryOptions: new ComputeWorkerRecoveryOptions(
                3,
                TimeSpan.Zero,
                TimeSpan.FromSeconds(30),
                TimeSpan.FromMinutes(1),
                TimeSpan.FromSeconds(30),
                TimeSpan.FromSeconds(1)),
            waitBeforeRecovery: (_, _) => Task.CompletedTask);

        try
        {
            worker.Start();
            worker.Assign(Work(pool, "submission-failure", 0x55));
            await submissionAttempted.Task.WaitAsync(timeout.Token);
            while (worker.Health.Phase != ComputeWorkerPhase.Faulted)
                await Task.Delay(10, timeout.Token);

            Assert.Equal(1, creations);
            Assert.Equal(0, worker.Health.TotalRecoveries);
            Assert.Contains(logs, message =>
                message.Contains("share submission stopped the worker", StringComparison.Ordinal));
        }
        finally
        {
            await worker.DisposeAsync();
        }
    }

    [Fact]
    public async Task Opencl_device_uses_stable_identity_and_conservative_qualification_batch()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var selectedDevice = new ComputeDeviceId(ComputeBackendKind.OpenCl, 2, 3);
        var backend = new ImmediateEpoch();
        var submitted = new TaskCompletionSource<CudaShare>(TaskCreationOptions.RunContinuationsAsynchronously);
        var logs = new List<string>();
        var poolOptions = new PoolOptions
        {
            Host = "127.0.0.1",
            Port = 1,
            Username = "test.worker",
            Password = "x"
        };
        await using var pool = new EtcHashPoolClient(
            poolOptions, MiningBeneficiary.User, _ => { }, resolveSeedEpoch: _ => 844);
        ComputeDeviceId? factoryDevice = null;
        EtcHashWorker? worker = null;
        worker = new EtcHashWorker(
            selectedDevice,
            (_, share, _) =>
            {
                submitted.TrySetResult(share);
                worker!.Pause();
                return Task.CompletedTask;
            },
            logs.Add,
            (_, device) =>
            {
                factoryDevice = device;
                return backend;
            });

        await using (worker)
        {
            worker.Start();
            worker.Assign(Work(pool, "opencl-job", 0x33));
            _ = await submitted.Task.WaitAsync(timeout.Token);
        }

        Assert.Equal(selectedDevice, factoryDevice);
        Assert.Equal(65_536U, backend.LastNonceCount);
        Assert.Contains(logs, message => message.Contains("opencl:2:3", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Replacement_during_gpu_batch_discards_old_solution_and_submits_new_job()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var releaseFirstSearch = new ManualResetEventSlim();
        var backend = new ControlledEpoch(releaseFirstSearch);
        var submitted = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var poolOptions = new PoolOptions
        {
            Host = "127.0.0.1",
            Port = 1,
            Username = "test.worker",
            Password = "x"
        };
        await using var pool = new EtcHashPoolClient(
            poolOptions, MiningBeneficiary.User, _ => { }, resolveSeedEpoch: _ => 844);
        EtcHashWorker? worker = null;
        worker = new EtcHashWorker(
            new ComputeDeviceId(ComputeBackendKind.Cuda, 0, 0),
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

            // The replacement arrives while the old batch is still executing.
            worker.Assign(Work(pool, "new-job", 0x22));
            releaseFirstSearch.Set();

            Assert.Equal("new-job", await submitted.Task.WaitAsync(timeout.Token));
            Assert.Equal(2, backend.SearchCount);
        }
    }

    private static EtcHashPoolWork Work(EtcHashPoolClient pool, string jobId, byte headerByte) => new(
        pool,
        new EtcHashJob(
            jobId,
            Enumerable.Repeat(headerByte, 32).ToArray(),
            new byte[32],
            Enumerable.Repeat((byte)0xff, 32).ToArray(),
            true),
        844,
        422,
        25_320_000,
        Enumerable.Repeat((byte)0xff, 32).ToArray(),
        new NonceAllocator(0),
        MiningBeneficiary.User);

    private sealed class ControlledEpoch(ManualResetEventSlim releaseFirstSearch) : IEtcHashWorkerEpoch
    {
        private int _searchCount;

        public TaskCompletionSource FirstSearchStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int SearchCount => Volatile.Read(ref _searchCount);
        public CudaEpochBuildInfo BuildInfo { get; } =
            new(422, 0, 4UL * 1024 * 1024 * 1024, TimeSpan.FromSeconds(1));

        public CudaSearchResult Search(
            int blockNumber, byte[] headerHash, byte[] target, ulong startNonce, uint nonceCount)
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

        public void WarmVerifier(int blockNumber, byte[] headerHash, ulong nonce) { }
        public void Dispose() { }
    }

    private sealed class ImmediateEpoch : IEtcHashWorkerEpoch
    {
        public uint LastNonceCount { get; private set; }
        public CudaEpochBuildInfo BuildInfo { get; } =
            new(422, 3, 4UL * 1024 * 1024 * 1024, TimeSpan.FromSeconds(1));

        public CudaSearchResult Search(
            int blockNumber, byte[] headerHash, byte[] target, ulong startNonce, uint nonceCount)
        {
            LastNonceCount = nonceCount;
            return new CudaSearchResult(
                true,
                startNonce,
                new byte[32],
                new byte[32],
                nonceCount,
                TimeSpan.FromMilliseconds(5));
        }

        public void WarmVerifier(int blockNumber, byte[] headerHash, ulong nonce) { }
        public void Dispose() { }
    }

    private sealed class FailingEpoch : IEtcHashWorkerEpoch
    {
        public bool Disposed { get; private set; }
        public CudaEpochBuildInfo BuildInfo { get; } =
            new(422, 0, 4UL * 1024 * 1024 * 1024, TimeSpan.FromSeconds(1));

        public CudaSearchResult Search(
            int blockNumber, byte[] headerHash, byte[] target, ulong startNonce, uint nonceCount) =>
            throw new InvalidOperationException("simulated device loss");

        public void WarmVerifier(int blockNumber, byte[] headerHash, ulong nonce) { }
        public void Dispose() => Disposed = true;
    }
}
