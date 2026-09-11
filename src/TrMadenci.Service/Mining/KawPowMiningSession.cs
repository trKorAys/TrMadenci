using System.Diagnostics;
using TrMadenci.Core.Configuration;
using TrMadenci.Core.Fees;
using TrMadenci.NativeBridge;

namespace TrMadenci.Service.Mining;

internal sealed class KawPowMiningSession(
    MinerOptions options,
    Action<string> log,
    Action<MiningStatusSnapshot>? updateStatus = null)
{
    private readonly CoinProfile _coin = CoinProfileCatalog.GetRequired(options.Coin);
    private readonly object _workGate = new();
    private readonly DeveloperFeeScheduler _scheduler = new(
        ProductPolicy.CreateDeveloperFeePolicy(),
        state: DeveloperFeeStateStore.Load(log));
    private readonly List<CudaKawPowWorker> _workers = [];
    private KawPowPoolWork? _latestUserWork;
    private KawPowPoolWork? _latestDeveloperWork;
    private long _accepted;
    private long _rejected;
    private long _invalid;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var devices = options.GpuDevices.Length == 0
            ? NativeDiagnostics.GetCudaDevices().Select(device => device.Index).ToArray()
            : options.GpuDevices;
        if (devices.Length == 0)
            throw new InvalidOperationException("No CUDA device was selected.");

        var userPoolOptions = options.Pool;
        await using var userPool = new KawPowPoolClient(
            userPoolOptions, MiningBeneficiary.User, log, options.FailoverPool);
        await using var developerPool = new KawPowPoolClient(
            ProductPolicy.CreateDeveloperPool(_coin), MiningBeneficiary.Developer, log);
        userPool.WorkReceived += OnWorkReceived;
        developerPool.WorkReceived += OnWorkReceived;
        userPool.ConnectionLost += OnConnectionLost;
        developerPool.ConnectionLost += OnConnectionLost;

        foreach (var device in devices)
        {
            var worker = new CudaKawPowWorker(device, SubmitAsync, log);
            worker.Start();
            _workers.Add(worker);
        }

        log($"Connecting user pool {userPoolOptions.Host}:{userPoolOptions.Port} and same-coin " +
            $"developer pool; coin={_coin.Ticker}, algorithm={_coin.Algorithm.ToUpperInvariant()}, " +
            $"fee={ProductPolicy.DeveloperFeeRate:P2}.");
        await Task.WhenAll(
            userPool.ConnectAsync(cancellationToken),
            developerPool.ConnectAsync(cancellationToken));

        var feeClock = Stopwatch.StartNew();
        var lastFeeTick = feeClock.Elapsed;
        var statusClock = Stopwatch.StartNew();
        var lastStatus = TimeSpan.Zero;
        ulong lastHashes = 0;
        var lastWorkerHashes = _workers.ToDictionary(worker => worker.DeviceIndex, worker => worker.Hashes);
        var lastPersist = TimeSpan.Zero;
        var activeMiningTime = TimeSpan.Zero;
        double sessionEnergyWattHours = 0;
        double telemetryMeasuredSeconds = 0;

        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                var now = feeClock.Elapsed;
                var elapsed = now - lastFeeTick;
                lastFeeTick = now;
                if (_workers.Any(worker => worker.IsHashing))
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

                if (statusClock.Elapsed - lastStatus >= TimeSpan.FromSeconds(10))
                {
                    var totalHashes = _workers.Aggregate(0UL, (sum, worker) => sum + worker.Hashes);
                    var deltaSeconds = (statusClock.Elapsed - lastStatus).TotalSeconds;
                    var rate = (totalHashes - lastHashes) / deltaSeconds;
                    var gpuStatuses = new List<GpuMiningStatus>(_workers.Count);
                    foreach (var worker in _workers)
                    {
                        var workerHashes = worker.Hashes;
                        var workerRate = (workerHashes - lastWorkerHashes[worker.DeviceIndex]) / deltaSeconds;
                        NativeDiagnostics.TryGetGpuTelemetry(worker.DeviceIndex, out var telemetry);
                        gpuStatuses.Add(new GpuMiningStatus(
                            worker.DeviceIndex, worker.IsPreparing, workerRate, telemetry));
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
                        statusClock.Elapsed,
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
                        gpuStatuses);
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
                    lastStatus = statusClock.Elapsed;
                }

                if (now - lastPersist >= TimeSpan.FromSeconds(30))
                {
                    await DeveloperFeeStateStore.SaveAsync(_scheduler.Snapshot(), cancellationToken);
                    lastPersist = now;
                }
            }
        }
        finally
        {
            await DeveloperFeeStateStore.SaveAsync(_scheduler.Snapshot(), CancellationToken.None);
            foreach (var worker in _workers)
                await worker.DisposeAsync();
            log($"Mining stopped. Accepted={Interlocked.Read(ref _accepted)}, rejected={Interlocked.Read(ref _rejected)}, local-invalid={Interlocked.Read(ref _invalid)}.");
        }
    }

    private void OnWorkReceived(KawPowPoolWork work)
    {
        lock (_workGate)
        {
            if (work.Beneficiary == MiningBeneficiary.User)
                _latestUserWork = work;
            else
                _latestDeveloperWork = work;

            if (work.Beneficiary != _scheduler.Beneficiary)
                return;
            foreach (var worker in _workers)
                worker.Assign(work);
        }
        log($"[{work.Beneficiary}] job {work.Job.JobId}, block {work.Job.BlockHeight}, clean={work.Job.CleanJobs}.");
    }

    private void SelectCurrentWork(MiningBeneficiary beneficiary)
    {
        lock (_workGate)
        {
            var work = beneficiary == MiningBeneficiary.User
                ? _latestUserWork
                : _latestDeveloperWork;
            if (work is null)
            {
                foreach (var worker in _workers)
                    worker.Pause();
                log($"No current {beneficiary} job is available; hashing pauses until one arrives.");
                return;
            }
            foreach (var worker in _workers)
                worker.Assign(work);
        }
    }

    private void OnConnectionLost(KawPowPoolClient pool)
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
        log($"[{pool.Beneficiary}] hashing paused until reconnect supplies fresh work.");
    }

    private async Task SubmitAsync(
        KawPowPoolWork work,
        CudaShare share,
        CancellationToken cancellationToken)
    {
        var reference = NativeDiagnostics.ComputeReferenceHash(
            checked((int)work.Job.BlockHeight), work.Job.HeaderHash, share.Nonce);
        if (!reference.MixHash.AsSpan().SequenceEqual(share.MixHash) ||
            !reference.FinalHash.AsSpan().SequenceEqual(share.FinalHash))
        {
            Interlocked.Increment(ref _invalid);
            log($"GPU share failed local CPU verification: nonce=0x{share.Nonce:x16}. Not submitted.");
            return;
        }

        var accepted = await work.Pool.SubmitAsync(work.Job, share, cancellationToken);
        if (accepted)
        {
            var count = Interlocked.Increment(ref _accepted);
            log($"Share #{count} ACCEPTED [{work.Beneficiary}] nonce=0x{share.Nonce:x16}.");
        }
        else
        {
            Interlocked.Increment(ref _rejected);
        }
    }
}
