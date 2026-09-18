using TrMadenci.Service.Mining;

namespace TrMadenci.Core.Tests;

public sealed class MiningPauseControllerTests
{
    [Fact]
    public void Pause_and_resume_are_idempotent_and_measure_only_paused_time()
    {
        var time = new ManualTimeProvider();
        var controller = new MiningPauseController(time);
        var transitions = new List<MiningPauseSnapshot>();
        controller.StateChanged += transitions.Add;

        Assert.True(controller.Pause());
        Assert.False(controller.Pause());
        time.Advance(TimeSpan.FromMinutes(3));

        var paused = controller.Snapshot;
        Assert.True(paused.IsPaused);
        Assert.Equal(1, paused.PauseCount);
        Assert.Equal(TimeSpan.FromMinutes(3), paused.TotalPausedDuration);

        Assert.True(controller.Resume());
        Assert.False(controller.Resume());
        time.Advance(TimeSpan.FromMinutes(2));

        var running = controller.Snapshot;
        Assert.False(running.IsPaused);
        Assert.Equal(TimeSpan.FromMinutes(3), running.TotalPausedDuration);
        Assert.Equal(2, transitions.Count);
    }

    [Fact]
    public void Unpaused_elapsed_excludes_an_active_pause_after_the_baseline()
    {
        var time = new ManualTimeProvider();
        var controller = new MiningPauseController(time);
        var baseline = controller.TotalPausedDuration;

        time.Advance(TimeSpan.FromMinutes(2));
        controller.Pause();
        time.Advance(TimeSpan.FromMinutes(4));

        Assert.Equal(
            TimeSpan.FromMinutes(2),
            controller.GetUnpausedElapsed(TimeSpan.FromMinutes(6), baseline));
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private long _timestamp;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref _timestamp);

        public void Advance(TimeSpan duration) =>
            Interlocked.Add(ref _timestamp, duration.Ticks);
    }
}
