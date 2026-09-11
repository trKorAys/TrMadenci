using System.Globalization;
using System.Text.Json;
using TrMadenci.Core.Configuration;
using TrMadenci.Protocols.Stratum;
using TrMadenci.NativeBridge;
using TrMadenci.Service.Mining;

var etcHashLiveBenchmark = args.Contains("--etchash-live-benchmark", StringComparer.OrdinalIgnoreCase);
var probe = args.Contains("--probe", StringComparer.OrdinalIgnoreCase) || etcHashLiveBenchmark;
var verboseProtocol = args.Contains("--verbose-protocol", StringComparer.OrdinalIgnoreCase);
var nativeProbe = args.Contains("--native-probe", StringComparer.OrdinalIgnoreCase);
var buildDag = args.Contains("--build-dag", StringComparer.OrdinalIgnoreCase);
var nonceSelfTest = args.Contains("--nonce-self-test", StringComparer.OrdinalIgnoreCase);
var etcHashSelfTest = args.Contains("--etchash-self-test", StringComparer.OrdinalIgnoreCase);
var etcHashCudaSelfTest = args.Contains("--etchash-cuda-self-test", StringComparer.OrdinalIgnoreCase);
var etcHashBuildDag = args.Contains("--etchash-build-dag", StringComparer.OrdinalIgnoreCase);
var etcHashNonceSelfTest = args.Contains("--etchash-nonce-self-test", StringComparer.OrdinalIgnoreCase);
var octopusMultiPointSelfTest = args.Contains("--octopus-multipoint-self-test", StringComparer.OrdinalIgnoreCase);
var octopusCudaSelfTest = args.Contains("--octopus-cuda-self-test", StringComparer.OrdinalIgnoreCase);
var octopusBuildDag = args.Contains("--octopus-build-dag", StringComparer.OrdinalIgnoreCase);
var mine = args.Contains("--mine", StringComparer.OrdinalIgnoreCase);
var etcHashQualification = args.Contains("--etchash-qualification", StringComparer.OrdinalIgnoreCase);
var soakArguments = args.Where(argument =>
    argument.StartsWith("--etchash-soak-hours=", StringComparison.OrdinalIgnoreCase)).ToArray();
TimeSpan? etcHashSoakDuration = null;
string? soakArgumentError = soakArguments.Length > 1
    ? "--etchash-soak-hours may be specified only once."
    : null;
if (soakArguments.Length == 1)
{
    var soakArgument = soakArguments[0];
    var value = soakArgument[(soakArgument.IndexOf('=') + 1)..];
    if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var hours) ||
        hours <= 0 || hours > 168)
        soakArgumentError = "--etchash-soak-hours must be greater than 0 and no more than 168.";
    else
        etcHashSoakDuration = TimeSpan.FromHours(hours);
}
var plainConsole = args.Contains("--plain-console", StringComparer.OrdinalIgnoreCase);
var configPath = args.FirstOrDefault(argument => !argument.StartsWith("--", StringComparison.Ordinal)) ?? "trmadenci.json";

if (!File.Exists(configPath))
{
    Console.Error.WriteLine($"Configuration not found: {Path.GetFullPath(configPath)}");
    return 2;
}

try
{
    if (soakArgumentError is not null)
        throw new ArgumentException(soakArgumentError);
    var json = await File.ReadAllTextAsync(configPath);
    var options = JsonSerializer.Deserialize<MinerOptions>(json, new JsonSerializerOptions
    {
        PropertyNameCaseInsensitive = true
    }) ?? throw new InvalidOperationException("Configuration is empty.");

    options.Validate();
    var coin = CoinProfileCatalog.GetRequired(options.Coin);
    if (octopusBuildDag && (!probe ||
        !string.Equals(coin.Algorithm, "octopus", StringComparison.OrdinalIgnoreCase)))
        throw new ArgumentException(
            "--octopus-build-dag requires --probe and an Octopus coin profile.");

    Console.WriteLine("TrMadenci service bootstrap is ready.");
    Console.WriteLine($"Coin/network: {coin.Name} ({coin.Ticker}) / {coin.Network}");
    Console.WriteLine($"Algorithm: {coin.Algorithm.ToUpperInvariant()}");
    Console.WriteLine($"Pool: {options.Pool.Host}:{options.Pool.Port}");
    Console.WriteLine($"User worker: {options.Pool.Username} (explicit configuration; no automatic fallback)");
    Console.WriteLine($"Developer fee: {ProductPolicy.DeveloperFeeRate:P2} (embedded, transparent randomized windows)");
    if (ProductPolicy.TryCreateDeveloperPool(coin, out var developerPool))
        Console.WriteLine($"Developer destination: {developerPool!.Host}:{developerPool.Port} / {developerPool.Username}");
    else
        Console.WriteLine($"Developer destination: not configured for {coin.Ticker}; mining is disabled.");

    if (nativeProbe)
    {
        foreach (var device in NativeDiagnostics.GetCudaDevices())
        {
            Console.WriteLine($"CUDA{device.Index}: {device.Name}, {device.TotalMemoryBytes / 1024 / 1024} MiB, sm_{device.ComputeMajor}{device.ComputeMinor}");
            NativeDiagnostics.TryGetGpuTelemetry(device.Index, out var telemetry);
            Console.WriteLine(GpuTelemetryFormatter.Format(device.Index, 0, telemetry));
        }
    }

    if (nonceSelfTest)
    {
        Console.WriteLine("Running CUDA nonce self-test...");
        using var epoch = NativeDiagnostics.CreateCudaEpoch(0, 0);
        var result = epoch.Search(0, new byte[32], Enumerable.Repeat((byte)0xff, 32).ToArray(), 0, 1);
        const string expected = "E601A7257A70DC48FCCC97A7330D704D776047623B92883D77111FB36870F3D1";
        if (!result.SolutionFound || result.Nonce != 0 || Convert.ToHexString(result.FinalHash) != expected)
            throw new InvalidOperationException("CUDA nonce self-test did not match the official vector.");
        Console.WriteLine($"CUDA nonce self-test passed in {result.SearchTime.TotalMilliseconds:F2}ms.");
    }

    if (etcHashSelfTest)
    {
        const int activationBlock = 11_700_000;
        const string activationSeed = "E79F0F63030BF691445C2B9D0266B24A9619E355194067F2AD2C73A8E0A26C65";
        var managed = TrMadenci.Core.Algorithms.EtcHashParameters.GetEpoch(activationBlock);
        var native = NativeDiagnostics.GetEtcHashEpochInfo(activationBlock);
        if (native.DatasetEpoch != 195 || native.SeedEpoch != 390 ||
            native.DatasetEpoch != managed.DatasetEpoch || native.SeedEpoch != managed.SeedEpoch ||
            native.LightCacheBytes != managed.LightCacheBytes ||
            native.FullDatasetBytes != managed.FullDatasetBytes ||
            Convert.ToHexString(native.SeedHash) != activationSeed)
            throw new InvalidOperationException("ETCHash ECIP-1099 boundary self-test failed.");

        var proof = NativeDiagnostics.ComputeEtcHashReferenceHash(
            22,
            Convert.FromHexString("372ECA2454EAD349C3DF0AB5D00B0B706B23E49D469387DB91811CEE0358FC6D"),
            0x495732e0ed7a801cUL);
        if (Convert.ToHexString(proof.FinalHash) !=
            "00000B184F1FDD88BFD94C86C39E65DB0C36144D5E43F745F722196E730CB614")
            throw new InvalidOperationException("ETCHash Hashimoto reference vector failed.");
        Console.WriteLine(
            $"ETCHash ECIP-1099 self-test passed: block {activationBlock}, " +
            $"DAG epoch {native.DatasetEpoch}, seed epoch {native.SeedEpoch}, " +
            $"cache={native.LightCacheBytes / 1024 / 1024} MiB, " +
            $"DAG={native.FullDatasetBytes / 1024 / 1024} MiB, " +
            $"seed={Convert.ToHexString(native.SeedHash).ToLowerInvariant()}.");
    }

    if (etcHashCudaSelfTest)
    {
        const int activationBlock = 11_700_000;
        Console.WriteLine("Validating ETCHash CUDA DAG samples at the ECIP-1099 boundary...");
        NativeDiagnostics.ValidateEtcHashCudaDagItems(activationBlock, 8);
        Console.WriteLine("ETCHash CUDA DAG validation passed: 8 items match the 256-parent CPU reference.");
    }

    if (etcHashBuildDag)
    {
        const int activationBlock = 11_700_000;
        Console.WriteLine("Building the full ETCHash CUDA DAG at the ECIP-1099 boundary...");
        using var etcEpoch = NativeDiagnostics.CreateEtcHashCudaEpoch(activationBlock, 0);
        Console.WriteLine(
            $"ETCHash CUDA DAG ready: epoch {etcEpoch.BuildInfo.EpochNumber}, " +
            $"{etcEpoch.BuildInfo.DatasetBytes / 1024 / 1024} MiB in " +
            $"{etcEpoch.BuildInfo.BuildTime.TotalSeconds:F2}s. Releasing validation context.");
    }

    if (etcHashNonceSelfTest)
    {
        const ulong expectedNonce = 0x495732e0ed7a801cUL;
        var header = Convert.FromHexString(
            "372ECA2454EAD349C3DF0AB5D00B0B706B23E49D469387DB91811CEE0358FC6D");
        Console.WriteLine("Building ETCHash epoch 0 DAG and running the official CUDA nonce vector...");
        using var etcEpoch = NativeDiagnostics.CreateEtcHashCudaEpoch(22, 0);
        var result = etcEpoch.Search(
            22, header, Enumerable.Repeat((byte)0xff, 32).ToArray(), expectedNonce, 1);
        var reference = NativeDiagnostics.ComputeEtcHashReferenceHash(22, header, expectedNonce);
        if (!result.SolutionFound || result.Nonce != expectedNonce ||
            !result.MixHash.AsSpan().SequenceEqual(reference.MixHash) ||
            !result.FinalHash.AsSpan().SequenceEqual(reference.FinalHash) ||
            Convert.ToHexString(result.FinalHash) !=
            "00000B184F1FDD88BFD94C86C39E65DB0C36144D5E43F745F722196E730CB614")
            throw new InvalidOperationException("ETCHash CUDA nonce self-test failed.");
        // Report short/medium/large batches so tuning does not hide launch overhead
        // or make replacement-job latency unnecessarily long.
        uint[] benchmarkSizes = [65_536, 262_144, 1_048_576];
        var benchmarkRates = new List<string>(benchmarkSizes.Length);
        foreach (var benchmarkHashes in benchmarkSizes)
        {
            var benchmark = etcEpoch.Search(22, header, new byte[32], 0, benchmarkHashes);
            if (benchmark.SolutionFound || benchmark.HashesSearched != benchmarkHashes ||
                benchmark.SearchTime <= TimeSpan.Zero)
                throw new InvalidOperationException("ETCHash CUDA batch search self-test failed.");
            var hashesPerSecond = benchmarkHashes / benchmark.SearchTime.TotalSeconds;
            benchmarkRates.Add(
                $"{benchmarkHashes / 1024}K={hashesPerSecond / 1_000_000:F2} MH/s/{benchmark.SearchTime.TotalMilliseconds:F1}ms");
        }
        Console.WriteLine(
            $"ETCHash CUDA nonce self-test passed: {result.SearchTime.TotalMilliseconds:F2}ms, " +
            $"hash={Convert.ToHexString(result.FinalHash).ToLowerInvariant()}, " +
            $"batches=[{string.Join(", ", benchmarkRates)}].");
    }

    if (octopusMultiPointSelfTest)
    {
        var header = Convert.FromHexString(
            "4D99D0B41C7EB0DD1A801C35AAE2DF28AE6B53BC7743F0818A34B6EC97F5B4AE");
        var result = TrMadenci.Core.Algorithms.OctopusMultiPoint.Evaluate(header, 0x2333333320);
        if (result.Compressed != 0xe4592b66ac531f54UL || result.Points.Count != 32)
            throw new InvalidOperationException("Octopus multi-point reference vector failed.");
        var finalHash = NativeDiagnostics.ComputeOctopusReferenceHash(
            2, header, 0x2333333320, result.Compressed, result.Points.ToArray());
        if (Convert.ToHexString(finalHash) !=
            "D45C965D3707E27A42995132637854234385CBF5626897259F1EE980554DDD5C")
            throw new InvalidOperationException("Octopus full CPU reference vector failed.");
        Console.WriteLine(
            $"Octopus multi-point self-test passed: compressed=0x{result.Compressed:x16}, " +
            $"points={result.Points.Count}, hash={Convert.ToHexString(finalHash).ToLowerInvariant()}. " +
            "A 16 MiB light cache was built; no DAG was allocated.");
    }

    if (octopusCudaSelfTest)
    {
        Console.WriteLine("Validating 8 Octopus CUDA DAG items against the epoch-0 CPU oracle...");
        NativeDiagnostics.ValidateOctopusCudaDagItems(2, 8);
        Console.WriteLine("Octopus CUDA DAG validation passed. No full DAG was allocated.");
    }

    if (probe)
    {
        if (string.Equals(coin.Algorithm, "etchash", StringComparison.OrdinalIgnoreCase))
            await ProbeEtcHashPoolAsync(
                options.Pool, verboseProtocol, nativeProbe, etcHashLiveBenchmark);
        else if (string.Equals(coin.Algorithm, "octopus", StringComparison.OrdinalIgnoreCase))
            await ProbeOctopusPoolAsync(
                options.Pool,
                verboseProtocol,
                nativeProbe,
                octopusBuildDag,
                options.GpuDevices);
        else
            await ProbePoolAsync(options.Pool, verboseProtocol, nativeProbe, buildDag);
    }

    if (etcHashQualification && (!mine ||
        !string.Equals(coin.Algorithm, "etchash", StringComparison.OrdinalIgnoreCase)))
        throw new ArgumentException(
            "--etchash-qualification requires --mine and an ETCHash coin profile.");
    if (etcHashSoakDuration is not null && (!mine ||
        !string.Equals(coin.Algorithm, "etchash", StringComparison.OrdinalIgnoreCase)))
        throw new ArgumentException(
            "--etchash-soak-hours requires --mine and an ETCHash coin profile.");
    if (etcHashQualification && etcHashSoakDuration is not null)
        throw new ArgumentException(
            "Use either --etchash-qualification or --etchash-soak-hours, not both.");

    if (mine)
    {
        var qualificationAllowed = (etcHashQualification || etcHashSoakDuration is not null) &&
            string.Equals(coin.Algorithm, "etchash", StringComparison.OrdinalIgnoreCase);
        if (!coin.MiningEnabled && !qualificationAllowed)
            throw new InvalidOperationException(
                $"Mining for {coin.Ticker} is not enabled: {coin.MiningBlockedReason}");
        if (etcHashQualification)
            Console.WriteLine(
                "ETCHash QUALIFICATION MODE: mining stops automatically after the first accepted share; " +
                "this does not enable the public ETC profile.");
        if (etcHashSoakDuration is { } soakDuration)
            Console.WriteLine(
                $"ETCHash SOAK MODE: requested duration={soakDuration}; the public ETC profile remains gated.");
        using var shutdown = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            shutdown.Cancel();
        };
        using var dashboard = ConsoleDashboard.CreateIfSupported(options, coin, !plainConsole);
        using var soakRecorder = etcHashSoakDuration is { } requestedDuration
            ? new EtcHashSoakRecorder(requestedDuration)
            : null;
        Action<string> consoleMiningLog = dashboard is null
            ? message => Console.WriteLine($"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}] {message}")
            : dashboard.Log;
        Action<string> miningLog = soakRecorder is null
            ? consoleMiningLog
            : message =>
            {
                consoleMiningLog(message);
                soakRecorder.RecordEvent(message);
            };
        if (soakRecorder is not null)
            Console.WriteLine(
                $"Soak evidence: {soakRecorder.EventsPath}{Environment.NewLine}" +
                $"Soak summary:  {soakRecorder.SummaryPath}");
        if (dashboard is null)
            Console.WriteLine("Mining started. Press Ctrl+C to stop safely.");
        else
            dashboard.Log("Mining started. Press Ctrl+C to stop safely.");
        Action<MiningStatusSnapshot>? statusUpdate = dashboard is null && soakRecorder is null
            ? null
            : snapshot =>
            {
                dashboard?.Update(snapshot);
                soakRecorder?.RecordStatus(snapshot);
                if (dashboard is null)
                    miningLog(
                        $"Soak status: {snapshot.CurrentHashesPerSecond / 1_000_000:F2} MH/s, " +
                        $"shares={snapshot.AcceptedShares}/{snapshot.RejectedShares}, " +
                        $"invalid={snapshot.InvalidShares}, energy={snapshot.SessionEnergyKwh:F3} kWh.");
            };
        try
        {
            if (string.Equals(coin.Algorithm, "etchash", StringComparison.OrdinalIgnoreCase))
            {
                var session = new EtcHashMiningSession(
                    options,
                    miningLog,
                    statusUpdate,
                    stopAfterAcceptedShares: etcHashQualification ? 1 : null,
                    stopAfterDuration: etcHashSoakDuration);
                await session.RunAsync(shutdown.Token);
            }
            else if (string.Equals(coin.Algorithm, "kawpow", StringComparison.OrdinalIgnoreCase))
            {
                var session = new KawPowMiningSession(options, miningLog, statusUpdate);
                await session.RunAsync(shutdown.Token);
            }
            else
            {
                throw new NotSupportedException($"Mining session is not implemented for {coin.Algorithm}.");
            }
            soakRecorder?.Complete("completed");
        }
        catch
        {
            soakRecorder?.Complete(shutdown.IsCancellationRequested ? "cancelled" : "failed");
            throw;
        }
    }

    return 0;
}
catch (OperationCanceledException)
{
    Console.WriteLine("TrMadenci stopped.");
    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine($"Invalid configuration: {exception.Message}");
    return 1;
}

static async Task ProbePoolAsync(PoolOptions pool, bool verboseProtocol, bool nativeProbe, bool buildDag)
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
    await using var connection = new StratumConnection();

    Console.WriteLine("Connecting to Stratum pool...");
    await connection.ConnectAsync(pool.Host, pool.Port, timeout.Token);
    Console.WriteLine("TCP connection established.");

    await connection.SendAsync(StratumRequest.Subscribe(1, "TrMadenci/0.1.0"), timeout.Token);
    await connection.SendAsync(StratumRequest.Authorize(2, pool.Username, pool.Password), timeout.Token);

    var subscribed = false;
    var authorized = false;
    var jobReceived = false;

    while (!subscribed || !authorized || !jobReceived)
    {
        using var message = await connection.ReadAsync(timeout.Token);
        if (verboseProtocol)
            Console.WriteLine($"< {message.Root.GetRawText()}");

        switch (message.Id)
        {
            case 1:
                subscribed = !message.HasError;
                Console.WriteLine(subscribed ? "Stratum subscription accepted." : "Stratum subscription rejected.");
                break;
            case 2:
                authorized = message.BooleanResult == true && !message.HasError;
                Console.WriteLine(authorized ? "Worker authorization accepted." : "Worker authorization rejected.");
                if (!authorized)
                    throw new InvalidOperationException("The pool rejected the worker authorization.");
                break;
            default:
                if (message.Method is { } method)
                {
                    Console.WriteLine($"Pool notification received: {method}");
                    if (string.Equals(method, "mining.notify", StringComparison.Ordinal))
                    {
                        var job = KawPowJob.Parse(message);
                        Console.WriteLine($"KAWPOW job parsed: {job.JobId}, block {job.BlockHeight}, clean={job.CleanJobs}");
                        if (nativeProbe)
                        {
                            var epoch = NativeDiagnostics.GetEpochInfo(checked((int)job.BlockHeight));
                            Console.WriteLine($"KAWPOW epoch {epoch.EpochNumber}: cache={epoch.LightCacheBytes / 1024 / 1024} MiB, DAG={epoch.FullDatasetBytes / 1024 / 1024} MiB");
                        }
                        if (buildDag)
                        {
                            Console.WriteLine("Building full CUDA DAG; the GPU will be busy...");
                            using var cudaEpoch = NativeDiagnostics.CreateCudaEpoch(checked((int)job.BlockHeight), 0);
                            Console.WriteLine($"CUDA DAG ready: {cudaEpoch.BuildInfo.DatasetBytes / 1024 / 1024} MiB in {cudaEpoch.BuildInfo.BuildTime.TotalSeconds:F2}s");
                        }
                        jobReceived = true;
                    }
                }
                break;
        }
    }
}

static async Task ProbeEtcHashPoolAsync(
    PoolOptions pool,
    bool verboseProtocol,
    bool nativeProbe,
    bool liveBenchmark)
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
    await using var connection = new StratumConnection();
    Console.WriteLine("Connecting to ETCHash Stratum pool...");
    await connection.ConnectAsync(pool.Host, pool.Port, timeout.Token);
    await connection.SendAsync(StratumRequest.Subscribe(1, "TrMadenci/0.2.0"), timeout.Token);
    await connection.SendAsync(StratumRequest.Authorize(2, pool.Username, pool.Password), timeout.Token);

    var subscribed = false;
    var authorized = false;
    var jobReceived = false;
    while (!subscribed || !authorized || !jobReceived)
    {
        using var message = await connection.ReadAsync(timeout.Token);
        if (verboseProtocol)
            Console.WriteLine($"< {message.Root.GetRawText()}");
        if (message.Id == 1)
        {
            subscribed = message.BooleanResult == true && !message.HasError;
            if (!subscribed)
                throw new InvalidOperationException("ETCHash subscription was rejected.");
            Console.WriteLine("ETCHash Stratum subscription accepted.");
        }
        else if (message.Id == 2)
        {
            authorized = message.BooleanResult == true && !message.HasError;
            if (!authorized)
                throw new InvalidOperationException("ETCHash worker authorization was rejected.");
            Console.WriteLine("ETCHash worker authorization accepted.");
        }
        else if (string.Equals(message.Method, "mining.notify", StringComparison.Ordinal))
        {
            var job = EtcHashJob.Parse(message);
            Console.WriteLine(
                $"ETCHash job parsed: {job.JobId}, seed={Convert.ToHexString(job.SeedHash).ToLowerInvariant()}, " +
                $"clean={job.CleanJobs}.");
            if (nativeProbe || liveBenchmark)
            {
                var seedEpoch = NativeDiagnostics.FindEtcHashSeedEpoch(job.SeedHash);
                var datasetEpoch = TrMadenci.Core.Algorithms.EtcHashParameters
                    .GetDatasetEpochFromSeedEpoch(seedEpoch);
                var representativeBlock = TrMadenci.Core.Algorithms.EtcHashParameters
                    .GetRepresentativeBlock(seedEpoch);
                Console.WriteLine(
                    $"ETCHash job epoch resolved: seed={seedEpoch}, DAG={datasetEpoch}, " +
                    $"representative block={representativeBlock}.");
                if (liveBenchmark)
                {
                    Console.WriteLine(
                        "Building the live ETCHash DAG for a non-submitting benchmark...");
                    using var epoch = NativeDiagnostics.CreateEtcHashCudaEpoch(representativeBlock, 0);
                    const uint nonceCount = 1_048_576;
                    var result = epoch.Search(
                        representativeBlock, job.HeaderHash, new byte[32], 0, nonceCount);
                    if (result.SolutionFound || result.HashesSearched != nonceCount)
                        throw new InvalidOperationException("Live ETCHash benchmark returned an invalid result.");
                    Console.WriteLine(
                        $"Live ETCHash benchmark: DAG epoch {datasetEpoch}, " +
                        $"DAG={epoch.BuildInfo.DatasetBytes / 1024 / 1024} MiB, " +
                        $"build={epoch.BuildInfo.BuildTime.TotalSeconds:F2}s, " +
                        $"rate={nonceCount / result.SearchTime.TotalSeconds / 1_000_000:F2} MH/s, " +
                        $"batch={result.SearchTime.TotalMilliseconds:F1}ms. No share was submitted.");
                }
            }
            jobReceived = true;
        }
    }
}

static async Task ProbeOctopusPoolAsync(
    PoolOptions pool,
    bool verboseProtocol,
    bool nativeProbe,
    bool buildDag,
    int[] selectedDevices)
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
    await using var connection = new StratumConnection();
    Console.WriteLine("Connecting to Octopus Stratum pool...");
    await connection.ConnectAsync(pool.Host, pool.Port, timeout.Token);
    await connection.SendAsync(
        StratumRequest.SubscribeOctopus(1, pool.Username, pool.Password), timeout.Token);

    var subscribed = false;
    while (true)
    {
        using var message = await connection.ReadAsync(timeout.Token);
        if (verboseProtocol)
            Console.WriteLine($"< {message.Root.GetRawText()}");
        if (message.Id == 1)
        {
            subscribed = message.BooleanResult == true && !message.HasError;
            if (!subscribed)
                throw new InvalidOperationException(
                    $"Octopus worker subscription/authorization was rejected for {pool.Username}.");
            Console.WriteLine("Octopus worker subscription and authorization accepted.");
        }
        else if (string.Equals(message.Method, "mining.notify", StringComparison.Ordinal))
        {
            if (!subscribed)
                throw new InvalidOperationException("Octopus pool sent work before authorization succeeded.");
            var job = OctopusJob.Parse(message);
            var epoch = TrMadenci.Core.Algorithms.OctopusParameters.GetEpoch(job.BlockHeight);
            var required = TrMadenci.Core.Algorithms.OctopusParameters.RequiredDeviceMemoryBytes(epoch.EpochNumber);
            Console.WriteLine(
                $"Octopus job parsed: {job.JobId}, block {job.BlockHeight}, epoch {epoch.EpochNumber}, " +
                $"cache={epoch.LightCacheBytes / 1024d / 1024:F1} MiB, " +
                $"DAG={epoch.FullDatasetBytes / 1024d / 1024 / 1024:F3} GiB, " +
                $"estimated device requirement={required / 1024d / 1024 / 1024:F3} GiB.");

            if (nativeProbe)
            {
                var devices = NativeDiagnostics.GetCudaDevices();
                var selected = selectedDevices.Length == 0
                    ? devices
                    : devices.Where(device => selectedDevices.Contains(device.Index)).ToArray();
                foreach (var device in selected)
                {
                    var fits = TrMadenci.Core.Algorithms.OctopusParameters.CanFitDeviceMemory(
                        epoch.EpochNumber, device.TotalMemoryBytes);
                    Console.WriteLine(
                        $"CUDA{device.Index}: {device.Name}, {device.TotalMemoryBytes / 1024d / 1024 / 1024:F2} GiB, " +
                        $"Octopus memory check={(fits ? "PASS" : "FAIL")}.");
                }
            }
            if (buildDag)
            {
                var deviceIndex = selectedDevices.Length == 0 ? 0 : selectedDevices[0];
                Console.WriteLine(
                    $"Building the full Octopus epoch {epoch.EpochNumber} CUDA DAG on CUDA{deviceIndex}...");
                using var cudaEpoch = NativeDiagnostics.CreateOctopusCudaEpoch(
                    job.BlockHeight, deviceIndex);
                Console.WriteLine(
                    $"Octopus CUDA DAG ready: {cudaEpoch.BuildInfo.DatasetBytes / 1024d / 1024 / 1024:F3} GiB " +
                    $"in {cudaEpoch.BuildInfo.BuildTime.TotalSeconds:F2}s. Releasing validation context.");
            }
            return;
        }
    }
}
