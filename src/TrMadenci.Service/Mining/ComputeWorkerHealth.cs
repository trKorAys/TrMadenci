namespace TrMadenci.Service.Mining;

internal enum ComputeWorkerPhase
{
    Paused,
    Preparing,
    Hashing,
    Submitting,
    Recovering,
    Faulted,
    Stopped
}

internal sealed record ComputeWorkerRecoveryOptions(
    int MaximumRecoveryAttempts,
    TimeSpan FirstRecoveryDelay,
    TimeSpan SearchTimeout,
    TimeSpan PreparationTimeout,
    TimeSpan SubmissionTimeout,
    TimeSpan StopTimeout)
{
    public static ComputeWorkerRecoveryOptions Production { get; } = new(
        MaximumRecoveryAttempts: 3,
        FirstRecoveryDelay: TimeSpan.FromSeconds(1),
        SearchTimeout: TimeSpan.FromSeconds(30),
        PreparationTimeout: TimeSpan.FromMinutes(5),
        SubmissionTimeout: TimeSpan.FromSeconds(45),
        StopTimeout: TimeSpan.FromSeconds(5));

    public TimeSpan DelayForAttempt(int attempt)
    {
        if (attempt <= 0)
            throw new ArgumentOutOfRangeException(nameof(attempt));
        var multiplier = 1L << Math.Min(attempt - 1, 10);
        return TimeSpan.FromTicks(checked(FirstRecoveryDelay.Ticks * multiplier));
    }

    public void Validate()
    {
        if (MaximumRecoveryAttempts < 0)
            throw new ArgumentOutOfRangeException(nameof(MaximumRecoveryAttempts));
        if (FirstRecoveryDelay < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(FirstRecoveryDelay));
        if (SearchTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(SearchTimeout));
        if (PreparationTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(PreparationTimeout));
        if (SubmissionTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(SubmissionTimeout));
        if (StopTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(StopTimeout));
    }
}

internal sealed record ComputeWorkerHealthSnapshot(
    string DeviceLabel,
    ComputeWorkerPhase Phase,
    TimeSpan PhaseDuration,
    int ConsecutiveFailures,
    long TotalRecoveries,
    string? LastError,
    TimeSpan SearchTimeout,
    TimeSpan PreparationTimeout,
    TimeSpan SubmissionTimeout)
{
    public string? UnhealthyReason => Phase switch
    {
        ComputeWorkerPhase.Faulted =>
            $"{DeviceLabel} entered a permanent fault after {ConsecutiveFailures} consecutive failure(s): " +
            (LastError ?? "unknown error"),
        ComputeWorkerPhase.Hashing when PhaseDuration > SearchTimeout =>
            $"{DeviceLabel} search exceeded the {SearchTimeout.TotalSeconds:F0}s watchdog timeout " +
            $"({PhaseDuration.TotalSeconds:F1}s). A process restart is required.",
        ComputeWorkerPhase.Preparing when PhaseDuration > PreparationTimeout =>
            $"{DeviceLabel} DAG preparation exceeded the {PreparationTimeout.TotalMinutes:F1} minute watchdog " +
            $"timeout ({PhaseDuration.TotalMinutes:F1} minutes). A process restart is required.",
        ComputeWorkerPhase.Submitting when PhaseDuration > SubmissionTimeout =>
            $"{DeviceLabel} share submission exceeded the {SubmissionTimeout.TotalSeconds:F0}s watchdog timeout " +
            $"({PhaseDuration.TotalSeconds:F1}s).",
        _ => null
    };
}

internal sealed class ComputeWorkerHealthTracker
{
    private readonly object _gate = new();
    private readonly string _deviceLabel;
    private readonly ComputeWorkerRecoveryOptions _options;
    private readonly TimeProvider _timeProvider;
    private ComputeWorkerPhase _phase = ComputeWorkerPhase.Paused;
    private long _phaseStarted;
    private int _consecutiveFailures;
    private long _totalRecoveries;
    private string? _lastError;

    public ComputeWorkerHealthTracker(
        string deviceLabel,
        ComputeWorkerRecoveryOptions? options = null,
        TimeProvider? timeProvider = null)
    {
        _deviceLabel = deviceLabel;
        _options = options ?? ComputeWorkerRecoveryOptions.Production;
        _options.Validate();
        _timeProvider = timeProvider ?? TimeProvider.System;
        _phaseStarted = _timeProvider.GetTimestamp();
    }

    public ComputeWorkerRecoveryOptions Options => _options;

    public ComputeWorkerHealthSnapshot Snapshot
    {
        get
        {
            lock (_gate)
            {
                return new ComputeWorkerHealthSnapshot(
                    _deviceLabel,
                    _phase,
                    _timeProvider.GetElapsedTime(_phaseStarted, _timeProvider.GetTimestamp()),
                    _consecutiveFailures,
                    _totalRecoveries,
                    _lastError,
                    _options.SearchTimeout,
                    _options.PreparationTimeout,
                    _options.SubmissionTimeout);
            }
        }
    }

    public void MarkPaused() => SetPhase(ComputeWorkerPhase.Paused);
    public void MarkPreparing() => SetPhase(ComputeWorkerPhase.Preparing);
    public void MarkHashing() => SetPhase(ComputeWorkerPhase.Hashing);
    public void MarkSubmitting() => SetPhase(ComputeWorkerPhase.Submitting);
    public void MarkStopped() => SetPhase(ComputeWorkerPhase.Stopped);

    public void MarkProgress()
    {
        lock (_gate)
        {
            _consecutiveFailures = 0;
            _lastError = null;
            SetPhaseUnsafe(ComputeWorkerPhase.Hashing);
        }
    }

    public bool TryBeginRecovery(Exception exception, out int attempt, out TimeSpan delay)
    {
        ArgumentNullException.ThrowIfNull(exception);
        lock (_gate)
        {
            _consecutiveFailures++;
            _lastError = $"{exception.GetType().Name}: {exception.Message}";
            attempt = _consecutiveFailures;
            if (!IsRecoverable(exception) || attempt > _options.MaximumRecoveryAttempts)
            {
                delay = TimeSpan.Zero;
                SetPhaseUnsafe(ComputeWorkerPhase.Faulted);
                return false;
            }

            _totalRecoveries++;
            delay = _options.DelayForAttempt(attempt);
            SetPhaseUnsafe(ComputeWorkerPhase.Recovering);
            return true;
        }
    }

    public void MarkFaulted(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        lock (_gate)
        {
            _consecutiveFailures = Math.Max(1, _consecutiveFailures);
            _lastError = $"{exception.GetType().Name}: {exception.Message}";
            SetPhaseUnsafe(ComputeWorkerPhase.Faulted);
        }
    }

    private static bool IsRecoverable(Exception exception) => exception is not (
        OperationCanceledException or
        ArgumentException or
        NotSupportedException or
        DllNotFoundException or
        EntryPointNotFoundException or
        BadImageFormatException or
        OutOfMemoryException);

    private void SetPhase(ComputeWorkerPhase phase)
    {
        lock (_gate)
            SetPhaseUnsafe(phase);
    }

    private void SetPhaseUnsafe(ComputeWorkerPhase phase)
    {
        _phase = phase;
        _phaseStarted = _timeProvider.GetTimestamp();
    }
}

internal static class ComputeWorkerWatchdog
{
    public static void ThrowIfUnhealthy(IEnumerable<ComputeWorkerHealthSnapshot> workers)
    {
        foreach (var worker in workers)
        {
            if (worker.UnhealthyReason is { } reason)
                throw new ComputeWorkerWatchdogException(reason);
        }
    }
}

internal sealed class ComputeWorkerWatchdogException(string message) : Exception(message);
