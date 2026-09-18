namespace TrMadenci.Service.Mining;

internal sealed record MiningPauseSnapshot(
    bool IsPaused,
    int PauseCount,
    TimeSpan TotalPausedDuration);

internal sealed class MiningPauseController(TimeProvider? timeProvider = null)
{
    private readonly object _gate = new();
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private TimeSpan _completedPausedDuration;
    private long _pauseStartedTimestamp;
    private bool _isPaused;
    private int _pauseCount;

    public event Action<MiningPauseSnapshot>? StateChanged;

    public bool IsPaused
    {
        get
        {
            lock (_gate)
                return _isPaused;
        }
    }

    public int PauseCount
    {
        get
        {
            lock (_gate)
                return _pauseCount;
        }
    }

    public TimeSpan TotalPausedDuration => Snapshot.TotalPausedDuration;

    public MiningPauseSnapshot Snapshot
    {
        get
        {
            lock (_gate)
                return SnapshotUnsafe();
        }
    }

    public bool Pause() => SetPaused(true);

    public bool Resume() => SetPaused(false);

    public TimeSpan GetUnpausedElapsed(TimeSpan wallClockElapsed, TimeSpan pausedDurationBaseline)
    {
        var pausedSinceBaseline = TotalPausedDuration - pausedDurationBaseline;
        if (pausedSinceBaseline < TimeSpan.Zero)
            pausedSinceBaseline = TimeSpan.Zero;
        var elapsed = wallClockElapsed - pausedSinceBaseline;
        return elapsed < TimeSpan.Zero ? TimeSpan.Zero : elapsed;
    }

    private bool SetPaused(bool paused)
    {
        MiningPauseSnapshot snapshot;
        lock (_gate)
        {
            if (_isPaused == paused)
                return false;

            var now = _timeProvider.GetTimestamp();
            if (paused)
            {
                _isPaused = true;
                _pauseStartedTimestamp = now;
                _pauseCount++;
            }
            else
            {
                _completedPausedDuration +=
                    _timeProvider.GetElapsedTime(_pauseStartedTimestamp, now);
                _isPaused = false;
            }
            snapshot = SnapshotUnsafe(now);
        }

        StateChanged?.Invoke(snapshot);
        return true;
    }

    private MiningPauseSnapshot SnapshotUnsafe(long? now = null)
    {
        var duration = _completedPausedDuration;
        if (_isPaused)
            duration += _timeProvider.GetElapsedTime(
                _pauseStartedTimestamp,
                now ?? _timeProvider.GetTimestamp());
        return new MiningPauseSnapshot(_isPaused, _pauseCount, duration);
    }
}
