using System.Diagnostics;
using TrMadenci.Core.Configuration;
using TrMadenci.Core.Fees;
using TrMadenci.NativeBridge;

namespace TrMadenci.Service.Mining;

internal sealed class EtcHashMiningSession(
    MinerOptions options,
    Action<string> log,
    Action<MiningStatusSnapshot>? updateStatus = null,
    int? stopAfterAcceptedShares = null,
    TimeSpan? stopAfterDuration = null,
    IReadOnlyList<ComputeDeviceId>? selectedComputeDevices = null,
    MiningPauseController? pauseController = null)
{
    private readonly CoinProfile _coin = CoinProfileCatalog.GetRequired(options.Coin);
    private readonly object _workGate = new();
    private readonly DeveloperFeeScheduler _scheduler = new(
        ProductPolicy.CreateDeveloperFeePolicy(),
        state: DeveloperFeeStateStore.Load(log));
    private readonly List<EtcHashWorker> _workers = [];
    private EtcHashPoolWork? _latestUserWork;
    private EtcHashPoolWork? _latestDeveloperWork;
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
        var devices = selectedComputeDevices ?? (configuredDevices.Length == 0
            ? NativeDiagnostics.GetCudaDevices()
                .Select(device => new ComputeDeviceId(ComputeBackendKind.Cuda, 0, device.Index))
                .ToArray()
            : configuredDevices
                .Select(device => new ComputeDeviceId(ComputeBackendKind.Cuda, 0, device))
                .ToArray());
        if (devices.Count == 0)
            throw new InvalidOperationException("No compute device was selected.");

        await using var userPool = new EtcHashPoolClient(
            options.Pool, MiningBeneficiary.User, log, options.FailoverPool);
        await using var developerPool = new EtcHashPoolClient(
            ProductPolicy.CreateDeveloperPool(_coin), MiningBeneficiary.Developer, log);
        userPool.WorkReceived += OnWorkReceived;
        developerPool.WorkReceived += OnWorkReceived;
        userPool.ConnectionLost += OnConnectionLost;
        developerPool.ConnectionLost += OnConnectionLost;

        foreach (var device in devices)
        {
            var worker = new EtcHashWorker(device, SubmitAsync, log);
            worker.Start();
            _workers.Add(worker);
        }
        if (pauseController is not null)
        {
            pauseController.StateChanged += OnPauseStateChanged;
            if (pauseController.IsPaused)
                PauseWorkers();
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
        var lastWorkerHashes = _workers.ToDictionary(worker => worker.DeviceId, worker => worker.Hashes);
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
                        SelectCurrentWork(current);
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
                        var workerRate = (workerHashes - lastWorkerHashes[worker.DeviceId]) / deltaSeconds;
                        var health = worker.Health;
                        GpuTelemetry? telemetry = null;
                        if (worker.DeviceId.Backend == ComputeBackendKind.Cuda)
                            NativeDiagnostics.TryGetGpuTelemetry(worker.DeviceIndex, out telemetry);
                        gpuStatuses.Add(new GpuMiningStatus(
                            worker.DeviceIndex,
                            worker.IsPreparing,
                            workerRate,
                            telemetry,
                            worker.DeviceId.ToString(),
                            health.Phase,
                            health.TotalRecoveries,
                            health.LastError));
                        lastWorkerHashes[worker.DeviceId] = workerHashes;
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
                        log($"Hashrate {rate / 1_000_000:F2} MH/s | shares {accepted}/{rejected} | beneficiary {_scheduler.Beneficiary}");
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
                    log($"ETCHash soak duration completed: {stopAfterDuration} active test time.");
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (
            !cancellationToken.IsCancellationRequested &&
            Volatile.Read(ref _qualificationCompleted) != 0)
        {
            log($"ETCHash qualification target reached: {stopAfterAcceptedShares} accepted share(s).");
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

    private void OnWorkReceived(EtcHashPoolWork work)
    {
        lock (_workGate)
        {
            if (work.Beneficiary == MiningBeneficiary.User)
                _latestUserWork = work;
            else
                _latestDeveloperWork = work;

            if (work.Beneficiary != _scheduler.Beneficiary || pauseController?.IsPaused == true)
                return;
            foreach (var worker in _workers)
                worker.Assign(work);
        }
        log($"[{work.Beneficiary}] ETCHash job {work.Job.JobId}, DAG epoch {work.DatasetEpoch}, " +
            $"seed epoch {work.SeedEpoch}, clean={work.Job.CleanJobs}.");
    }

    private void SelectCurrentWork(MiningBeneficiary beneficiary)
    {
        lock (_workGate)
        {
            if (pauseController?.IsPaused == true)
            {
                PauseWorkers();
                return;
            }
            var work = beneficiary == MiningBeneficiary.User ? _latestUserWork : _latestDeveloperWork;
            if (work is null)
            {
                foreach (var worker in _workers)
                    worker.Pause();
                log($"No current {beneficiary} ETCHash job is available; hashing pauses until one arrives.");
                return;
            }
            foreach (var worker in _workers)
                worker.Assign(work);
        }
    }

    private void OnPauseStateChanged(MiningPauseSnapshot state)
    {
        if (pauseController!.IsPaused)
        {
            lock (_workGate)
                PauseWorkers();
            log($"Mining manually paused; pool connections remain active (pause #{state.PauseCount}).");
            return;
        }

        log($"Mining resumed after {state.TotalPausedDuration:c} total manual pause time.");
        SelectCurrentWork(_scheduler.Beneficiary);
    }

    private void PauseWorkers()
    {
        foreach (var worker in _workers)
            worker.Pause();
    }

    private TimeSpan GetSoakElapsed(TimeSpan wallClockElapsed, TimeSpan pausedDurationBaseline) =>
        pauseController?.GetUnpausedElapsed(wallClockElapsed, pausedDurationBaseline) ??
        wallClockElapsed;

    private void OnConnectionLost(EtcHashPoolClient pool)
    {
        lock (_workGate)
        {
            if (pool.Beneficiary == MiningBeneficiary.User)
                _latestUserWork = null;
            else
                _latestDeveloperWork = null;
            if (pool.Beneficiary != _scheduler.Beneficiary)
                return;
            foreach (var worker in _workers)
                worker.Pause();
        }
        log($"[{pool.Beneficiary}] ETCHash hashing paused until reconnect supplies fresh work.");
    }

    private async Task SubmitAsync(
        EtcHashPoolWork work,
        CudaShare share,
        CancellationToken cancellationToken)
    {
        var reference = NativeDiagnostics.ComputeEtcHashReferenceHash(
            work.RepresentativeBlock, work.Job.HeaderHash, share.Nonce);
        if (!reference.MixHash.AsSpan().SequenceEqual(share.MixHash) ||
            !reference.FinalHash.AsSpan().SequenceEqual(share.FinalHash))
        {
            Interlocked.Increment(ref _invalid);
            log($"ETCHash GPU share failed local CPU verification: nonce=0x{share.Nonce:x16}. Not submitted.");
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
            log($"ETCHash share #{count} ACCEPTED [{work.Beneficiary}] nonce=0x{share.Nonce:x16}.");
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
