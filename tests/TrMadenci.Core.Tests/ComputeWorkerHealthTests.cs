using TrMadenci.Service.Mining;

namespace TrMadenci.Core.Tests;

public sealed class ComputeWorkerHealthTests
{
    [Fact]
    public void Recovery_is_bounded_and_uses_exponential_backoff()
    {
        var options = Options(maximumAttempts: 3);
        var tracker = new ComputeWorkerHealthTracker("cuda:0", options);

        Assert.True(tracker.TryBeginRecovery(new InvalidOperationException("one"), out var first, out var firstDelay));
        Assert.True(tracker.TryBeginRecovery(new InvalidOperationException("two"), out var second, out var secondDelay));
        Assert.True(tracker.TryBeginRecovery(new InvalidOperationException("three"), out var third, out var thirdDelay));
        Assert.False(tracker.TryBeginRecovery(new InvalidOperationException("four"), out var fourth, out var finalDelay));

        Assert.Equal(1, first);
        Assert.Equal(2, second);
        Assert.Equal(3, third);
        Assert.Equal(4, fourth);
        Assert.Equal(TimeSpan.FromMilliseconds(250), firstDelay);
        Assert.Equal(TimeSpan.FromMilliseconds(500), secondDelay);
        Assert.Equal(TimeSpan.FromSeconds(1), thirdDelay);
        Assert.Equal(TimeSpan.Zero, finalDelay);
        Assert.Equal(ComputeWorkerPhase.Faulted, tracker.Snapshot.Phase);
        Assert.Equal(3, tracker.Snapshot.TotalRecoveries);
    }

    [Fact]
    public void Successful_progress_resets_only_the_consecutive_failure_budget()
    {
        var tracker = new ComputeWorkerHealthTracker("opencl:0:0", Options(maximumAttempts: 2));

        Assert.True(tracker.TryBeginRecovery(new InvalidOperationException("first"), out _, out _));
        tracker.MarkProgress();
        Assert.True(tracker.TryBeginRecovery(new InvalidOperationException("second"), out var attempt, out _));

        Assert.Equal(1, attempt);
        Assert.Equal(1, tracker.Snapshot.ConsecutiveFailures);
        Assert.Equal(2, tracker.Snapshot.TotalRecoveries);
    }

    [Fact]
    public void Deterministic_configuration_failure_is_not_retried()
    {
        var tracker = new ComputeWorkerHealthTracker("cuda:0", Options(maximumAttempts: 3));

        Assert.False(tracker.TryBeginRecovery(new ArgumentException("bad device"), out var attempt, out var delay));

        Assert.Equal(1, attempt);
        Assert.Equal(TimeSpan.Zero, delay);
        Assert.Equal(ComputeWorkerPhase.Faulted, tracker.Snapshot.Phase);
        Assert.Equal(0, tracker.Snapshot.TotalRecoveries);
    }

    [Fact]
    public void Watchdog_detects_a_search_that_stops_making_progress()
    {
        var time = new ManualTimeProvider();
        var tracker = new ComputeWorkerHealthTracker("cuda:2", Options(maximumAttempts: 3), time);
        tracker.MarkHashing();
        time.Advance(TimeSpan.FromSeconds(31));

        var exception = Assert.Throws<ComputeWorkerWatchdogException>(() =>
            ComputeWorkerWatchdog.ThrowIfUnhealthy([tracker.Snapshot]));

        Assert.Contains("cuda:2", exception.Message, StringComparison.Ordinal);
        Assert.Contains("process restart is required", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Paused_worker_does_not_trip_the_watchdog()
    {
        var time = new ManualTimeProvider();
        var tracker = new ComputeWorkerHealthTracker("cuda:0", Options(maximumAttempts: 3), time);
        tracker.MarkPaused();
        time.Advance(TimeSpan.FromDays(1));

        ComputeWorkerWatchdog.ThrowIfUnhealthy([tracker.Snapshot]);
    }

    [Fact]
    public void Watchdog_uses_phase_specific_preparation_and_submission_deadlines()
    {
        var preparationTime = new ManualTimeProvider();
        var preparation = new ComputeWorkerHealthTracker(
            "opencl:0:0", Options(maximumAttempts: 3), preparationTime);
        preparation.MarkPreparing();
        preparationTime.Advance(TimeSpan.FromMinutes(5) + TimeSpan.FromTicks(1));

        var preparationException = Assert.Throws<ComputeWorkerWatchdogException>(() =>
            ComputeWorkerWatchdog.ThrowIfUnhealthy([preparation.Snapshot]));
        Assert.Contains("DAG preparation", preparationException.Message, StringComparison.Ordinal);

        var submissionTime = new ManualTimeProvider();
        var submission = new ComputeWorkerHealthTracker(
            "cuda:0", Options(maximumAttempts: 3), submissionTime);
        submission.MarkSubmitting();
        submissionTime.Advance(TimeSpan.FromSeconds(45) + TimeSpan.FromTicks(1));

        var submissionException = Assert.Throws<ComputeWorkerWatchdogException>(() =>
            ComputeWorkerWatchdog.ThrowIfUnhealthy([submission.Snapshot]));
        Assert.Contains("share submission", submissionException.Message, StringComparison.Ordinal);
    }

    private static ComputeWorkerRecoveryOptions Options(int maximumAttempts) => new(
        maximumAttempts,
        TimeSpan.FromMilliseconds(250),
        TimeSpan.FromSeconds(30),
        TimeSpan.FromMinutes(5),
        TimeSpan.FromSeconds(45),
        TimeSpan.FromSeconds(1));

    private sealed class ManualTimeProvider : TimeProvider
    {
        private long _timestamp;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref _timestamp);

        public void Advance(TimeSpan duration) =>
            Interlocked.Add(ref _timestamp, duration.Ticks);
    }
}
