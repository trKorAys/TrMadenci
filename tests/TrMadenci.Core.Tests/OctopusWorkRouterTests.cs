using TrMadenci.Core.Configuration;
using TrMadenci.Core.Fees;
using TrMadenci.Protocols.Stratum;
using TrMadenci.Service.Mining;

namespace TrMadenci.Core.Tests;

public sealed class OctopusWorkRouterTests
{
    [Fact]
    public async Task Fee_beneficiary_switch_assigns_only_the_cached_destination_job()
    {
        await using var pool = Pool();
        var worker = new RecordingWorker();
        var router = new OctopusWorkRouter([worker]);
        var scheduler = new DeveloperFeeScheduler(
            new DeveloperFeePolicy
            {
                Rate = 0.01m,
                MinimumWindow = TimeSpan.FromSeconds(1),
                MaximumWindow = TimeSpan.FromSeconds(1)
            },
            new FixedWindowSource());

        Assert.True(router.Receive(Work(pool, "user-job", MiningBeneficiary.User)));
        Assert.False(router.Receive(Work(pool, "developer-job", MiningBeneficiary.Developer)));
        Assert.Equal(["user-job"], worker.Assignments);

        scheduler.Record(TimeSpan.FromSeconds(99));
        Assert.Equal(MiningBeneficiary.Developer, scheduler.Beneficiary);
        Assert.True(router.Select(scheduler.Beneficiary));
        Assert.Equal(["user-job", "developer-job"], worker.Assignments);

        scheduler.Record(TimeSpan.FromSeconds(1));
        Assert.Equal(MiningBeneficiary.User, scheduler.Beneficiary);
        Assert.True(router.Select(scheduler.Beneficiary));
        Assert.Equal(["user-job", "developer-job", "user-job"], worker.Assignments);
    }

    [Fact]
    public async Task Active_pool_disconnect_pauses_without_using_other_beneficiary_job()
    {
        await using var pool = Pool();
        var worker = new RecordingWorker();
        var router = new OctopusWorkRouter([worker]);
        router.Receive(Work(pool, "developer-job", MiningBeneficiary.Developer));

        Assert.True(router.Select(MiningBeneficiary.Developer));
        Assert.True(router.Disconnect(MiningBeneficiary.Developer));
        Assert.Equal(1, worker.PauseCount);
        Assert.Equal(["developer-job"], worker.Assignments);
    }

    [Fact]
    public async Task Manual_pause_caches_fresh_work_and_resume_assigns_only_the_latest_active_job()
    {
        await using var pool = Pool();
        var worker = new RecordingWorker();
        var router = new OctopusWorkRouter([worker]);

        Assert.True(router.Receive(Work(pool, "user-job-1", MiningBeneficiary.User)));
        Assert.True(router.SetManuallyPaused(true));
        Assert.False(router.Receive(Work(pool, "user-job-2", MiningBeneficiary.User)));
        Assert.False(router.Receive(Work(pool, "developer-job", MiningBeneficiary.Developer)));
        Assert.Equal(["user-job-1"], worker.Assignments);

        Assert.True(router.SetManuallyPaused(false));
        Assert.Equal(["user-job-1", "user-job-2"], worker.Assignments);
        Assert.Equal(1, worker.PauseCount);
    }

    private static OctopusPoolWork Work(
        OctopusPoolClient pool,
        string jobId,
        MiningBeneficiary beneficiary) => new(
        pool,
        new OctopusJob(jobId, 2, new byte[32], Enumerable.Repeat((byte)0xff, 32).ToArray()),
        0,
        Enumerable.Repeat((byte)0xff, 32).ToArray(),
        new NonceAllocator(0),
        beneficiary);

    private static OctopusPoolClient Pool() => new(
        new PoolOptions { Host = "127.0.0.1", Port = 1, Username = "test.worker" },
        MiningBeneficiary.User,
        _ => { });

    private sealed class RecordingWorker : IOctopusWorkSink
    {
        public List<string> Assignments { get; } = [];
        public int PauseCount { get; private set; }

        public void Assign(OctopusPoolWork work) => Assignments.Add(work.Job.JobId);
        public void Pause() => PauseCount++;
    }

    private sealed class FixedWindowSource : IFeeWindowSource
    {
        public TimeSpan Next(TimeSpan minimum, TimeSpan maximum) => TimeSpan.FromSeconds(1);
    }
}
