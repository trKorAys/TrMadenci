using System.Diagnostics;
using TrMadenci.Core.Configuration;
using TrMadenci.Core.Fees;
using TrMadenci.NativeBridge;

namespace TrMadenci.Service.Mining;

internal sealed class OctopusMiningSession(
    MinerOptions options,
    Action<string> log,
    Action<MiningStatusSnapshot>? updateStatus = null,
    int? stopAfterAcceptedShares = null,
    TimeSpan? stopAfterDuration = null,
    MiningPauseController? pauseController = null)
{
    private readonly CoinProfile _coin = CoinProfileCatalog.GetRequired(options.Coin);
    private readonly DeveloperFeeScheduler _scheduler = new(
        ProductPolicy.CreateDeveloperFeePolicy(),
        state: DeveloperFeeStateStore.Load(log));
    private readonly List<CudaOctopusWorker> _workers = [];
    private OctopusWorkRouter? _workRouter;
    private long _accepted;
    private long _acceptedUser;
    private long _acceptedDeveloper;
    private long _rejected;
    private long _invalid;
    private CancellationTokenSource? _sessionLifetime;
    private int _qualificationCompleted;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        using var sessionLifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _sessionLifetime = sessionLifetime;
        var sessionToken = sessionLifetime.Token;
        var configuredDevices = options.GetCudaDeviceIndexes();
        var devices = configuredDevices.Length == 0
            ? NativeDiagnostics.GetCudaDevices().Select(device => device.Index).ToArray()
            : configuredDevices;
        if (devices.Length == 0)
            throw new InvalidOperationException("No CUDA device was selected.");

        await using var userPool = new OctopusPoolClient(
            options.Pool, MiningBeneficiary.User, log, options.FailoverPool);
        await using var developerPool = new OctopusPoolClient(
            ProductPolicy.CreateDeveloperPool(_coin), MiningBeneficiary.Developer, log);
        userPool.WorkReceived += OnWorkReceived;
        developerPool.WorkReceived += OnWorkReceived;
        userPool.ConnectionLost += OnConnectionLost;
        developerPool.ConnectionLost += OnConnectionLost;

        foreach (var device in devices)
        {
            var worker = new CudaOctopusWorker(device, SubmitAsync, log);
            worker.Start();
            _workers.Add(worker);
        }
        _workRouter = new OctopusWorkRouter(_workers);
        if (pauseController is not null)
        {
            pauseController.StateChanged += OnPauseStateChanged;
            if (pauseController.IsPaused)
                _workRouter.SetManuallyPaused(true);
        }

        log($"Connecting user pool {options.Pool.Host}:{options.Pool.Port} and same-coin " +
            $"developer pool; coin={_coin.Ticker}, algorithm={_coin.Algorithm.ToUpperInvariant()}, " +
            $"fee={ProductPolicy.DeveloperFeeRate:P2}.");
        await Task.WhenAll(
            userPool.ConnectAsync(sessionToken),
            developerPool.ConnectAsync(sessionToken));

        var clock = Stopwatch.StartNew();
        var soakPausedBaseline = pauseController?.TotalPausedDuration ?? TimeSpan.Zero;
        var lastTick = clock.Elapsed;
        var lastStatus = TimeSpan.Zero;
        var lastPersist = TimeSpan.Zero;
        var activeMiningTime = TimeSpan.Zero;
        ulong lastHashes = 0;
        var lastWorkerHashes = _workers.ToDictionary(worker => worker.DeviceIndex, worker => worker.Hashes);
        double sessionEnergyWattHours = 0;
        double telemetryMeasuredSeconds = 0;

        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
            while (await timer.WaitForNextTickAsync(sessionToken))
            {
                ComputeWorkerWatchdog.ThrowIfUnhealthy(_workers.Select(worker => worker.Health));
                var now = clock.Elapsed;
                var elapsed = now - lastTick;
                lastTick = now;
                if (pauseController?.IsPaused != true && _workers.Any(worker => worker.IsHashing))
                {
                    activeMiningTime += elapsed;
                    var previous = _scheduler.Beneficiary;
                    var current = _scheduler.Record(elapsed);
                    if (current != previous)
                    {
                        log(current == MiningBeneficiary.Developer
                            ? $"Developer fee mining started ({ProductPolicy.DeveloperFeeRate:P2}, randomized window)."
                            : "Developer fee mining completed; returning to user destination.");
                        if (!_workRouter.Select(current))
                            log($"No current {current} Octopus job is available; hashing pauses until one arrives.");
                    }
                }

                if (now - lastStatus >= TimeSpan.FromSeconds(10))
                {
                    var totalHashes = _workers.Aggregate(0UL, (sum, worker) => sum + worker.Hashes);
                    var deltaSeconds = (now - lastStatus).TotalSeconds;
                    var rate = (totalHashes - lastHashes) / deltaSeconds;
                    var gpuStatuses = new List<GpuMiningStatus>(_workers.Count);
                    foreach (var worker in _workers)
                    {
                        var workerHashes = worker.Hashes;
                        var workerRate = (workerHashes - lastWorkerHashes[worker.DeviceIndex]) / deltaSeconds;
                        var health = worker.Health;
                        NativeDiagnostics.TryGetGpuTelemetry(worker.DeviceIndex, out var telemetry);
                        gpuStatuses.Add(new GpuMiningStatus(
                            worker.DeviceIndex,
                            worker.IsPreparing,
                            workerRate,
                            telemetry,
                            $"cuda:{worker.DeviceIndex}",
                            health.Phase,
                            health.TotalRecoveries,
                            health.LastError));
                        lastWorkerHashes[worker.DeviceIndex] = workerHashes;
                    }

                    var measuredPower = gpuStatuses
                        .Where(gpu => gpu.Telemetry?.PowerWatts is not null)
                        .Sum(gpu => gpu.Telemetry!.PowerWatts!.Value);
                    if (gpuStatuses.Any(gpu => gpu.Telemetry?.PowerWatts is not null))
                    {
                        sessionEnergyWattHours += measuredPower * deltaSeconds / 3600;
                        telemetryMeasuredSeconds += deltaSeconds;
                    }

                    var accepted = Interlocked.Read(ref _accepted);
                    var rejected = Interlocked.Read(ref _rejected);
                    var invalid = Interlocked.Read(ref _invalid);
                    var averageRate = activeMiningTime > TimeSpan.Zero
                        ? totalHashes / activeMiningTime.TotalSeconds
                        : 0;
                    double? averagePower = telemetryMeasuredSeconds > 0
                        ? sessionEnergyWattHours * 3600 / telemetryMeasuredSeconds
                        : null;
                    var snapshot = new MiningStatusSnapshot(
                        DateTimeOffset.Now,
                        clock.Elapsed,
                        activeMiningTime,
                        _coin.Name,
                        _coin.Ticker,
                        _coin.Network,
                        _coin.Algorithm.ToUpperInvariant(),
                        $"{options.Pool.Host}:{options.Pool.Port}",
                        _scheduler.Beneficiary,
                        rate,
                        averageRate,
                        totalHashes,
                        accepted,
                        rejected,
                        invalid,
                        sessionEnergyWattHours / 1000,
                        averagePower,
                        gpuStatuses,
                        pauseController?.IsPaused == true,
                        Interlocked.Read(ref _acceptedUser),
                        Interlocked.Read(ref _acceptedDeveloper));
                    if (updateStatus is null)
                    {
                        log($"Hashrate {rate:F2} H/s | shares {accepted}/{rejected} | beneficiary {_scheduler.Beneficiary}");
                        foreach (var gpu in gpuStatuses)
                            log(GpuTelemetryFormatter.Format(gpu.DeviceIndex, gpu.HashesPerSecond, gpu.Telemetry));
                    }
                    else
                    {
                        updateStatus(snapshot);
                    }
                    lastHashes = totalHashes;
                    lastStatus = now;
                }

                if (now - lastPersist >= TimeSpan.FromSeconds(30))
                {
                    await DeveloperFeeStateStore.SaveAsync(_scheduler.Snapshot(), sessionToken);
                    lastPersist = now;
                }

                if (stopAfterDuration is { } duration &&
                    GetSoakElapsed(now, soakPausedBaseline) >= duration)
                {
                    log($"Octopus soak duration completed: {stopAfterDuration} active test time.");
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (
            !cancellationToken.IsCancellationRequested && Volatile.Read(ref _qualificationCompleted) != 0)
        {
            log($"Octopus qualification target reached: {stopAfterAcceptedShares} accepted share(s).");
        }
        finally
        {
            if (pauseController is not null)
                pauseController.StateChanged -= OnPauseStateChanged;
            _sessionLifetime = null;
            await DeveloperFeeStateStore.SaveAsync(_scheduler.Snapshot(), CancellationToken.None);
            foreach (var worker in _workers)
                await worker.DisposeAsync();
            log($"Mining stopped. Accepted={Interlocked.Read(ref _accepted)}, " +
                $"rejected={Interlocked.Read(ref _rejected)}, local-invalid={Interlocked.Read(ref _invalid)}.");
        }
    }

    private void OnPauseStateChanged(MiningPauseSnapshot state)
    {
        var paused = pauseController!.IsPaused;
        var hasCurrentWork = _workRouter!.SetManuallyPaused(paused);
        if (paused)
        {
            log($"Mining manually paused; pool connections remain active (pause #{state.PauseCount}).");
            return;
        }

        log($"Mining resumed after {state.TotalPausedDuration:c} total manual pause time.");
        if (!hasCurrentWork)
            log($"No current {_scheduler.Beneficiary} Octopus job is available; hashing pauses until one arrives.");
    }

    private TimeSpan GetSoakElapsed(TimeSpan wallClockElapsed, TimeSpan pausedDurationBaseline) =>
        pauseController?.GetUnpausedElapsed(wallClockElapsed, pausedDurationBaseline) ??
        wallClockElapsed;

    private void OnWorkReceived(OctopusPoolWork work)
    {
        _workRouter!.Receive(work);
        log($"[{work.Beneficiary}] Octopus job {work.Job.JobId}, block {work.Job.BlockHeight}, " +
            $"DAG epoch {work.EpochNumber}.");
    }

    private void OnConnectionLost(OctopusPoolClient pool)
    {
        if (_workRouter!.Disconnect(pool.Beneficiary))
            log($"[{pool.Beneficiary}] Octopus hashing paused until reconnect supplies fresh work.");
    }

    private async Task SubmitAsync(
        OctopusPoolWork work,
        CudaShare share,
        CancellationToken cancellationToken)
    {
        if (!OctopusShareVerifier.Verify(work.Job, share, work.Target))
        {
            Interlocked.Increment(ref _invalid);
            log($"Octopus GPU share failed local CPU verification: nonce=0x{share.Nonce:x16}. Not submitted.");
            return;
        }

        var accepted = await work.Pool.SubmitAsync(work.Job, share, cancellationToken);
        if (accepted)
        {
            var count = Interlocked.Increment(ref _accepted);
            if (work.Beneficiary == MiningBeneficiary.User)
                Interlocked.Increment(ref _acceptedUser);
            else
                Interlocked.Increment(ref _acceptedDeveloper);
            log($"Octopus share #{count} ACCEPTED [{work.Beneficiary}] nonce=0x{share.Nonce:x16}.");
            if (stopAfterAcceptedShares is > 0 && count >= stopAfterAcceptedShares)
            {
                Volatile.Write(ref _qualificationCompleted, 1);
                _sessionLifetime?.Cancel();
            }
        }
        else
        {
            Interlocked.Increment(ref _rejected);
        }
    }

}
