using System.Text;
using System.Text.Json;
using TrMadenci.Core.Configuration;
using TrMadenci.Protocols.Stratum;
using TrMadenci.NativeBridge;
using TrMadenci.Service.Mining;

var soakVerificationArguments = args.Where(argument =>
    argument.StartsWith("--verify-etc-soak=", StringComparison.OrdinalIgnoreCase) ||
    argument.StartsWith("--verify-cfx-soak=", StringComparison.OrdinalIgnoreCase)).ToArray();
if (soakVerificationArguments.Length > 0)
{
    if (soakVerificationArguments.Length != 1 || args.Length != 1)
    {
        Console.Error.WriteLine(
            "Use exactly one --verify-etc-soak=<summary.json> or " +
            "--verify-cfx-soak=<summary.json> standalone command.");
        return ServiceExitCodes.InvalidInvocation;
    }

    var verificationArgument = soakVerificationArguments[0];
    var summaryPath = verificationArgument[(verificationArgument.IndexOf('=') + 1)..];
    if (string.IsNullOrWhiteSpace(summaryPath))
    {
        Console.Error.WriteLine("The soak verification command requires a summary JSON path.");
        return ServiceExitCodes.InvalidInvocation;
    }

    var verifyCfx = verificationArgument.StartsWith(
        "--verify-cfx-soak=", StringComparison.OrdinalIgnoreCase);
    var result = verifyCfx
        ? CfxSoakQualification.Evaluate(summaryPath)
        : EtcSoakQualification.Evaluate(summaryPath);
    var qualificationCoin = verifyCfx ? "CFX" : "ETC";
    Console.WriteLine(result.Passed
        ? $"{qualificationCoin} PRODUCTION SOAK: PASS"
        : $"{qualificationCoin} PRODUCTION SOAK: FAIL");
    Console.WriteLine(
        $"Requested={result.RequestedDuration:c}, wall={result.WallClockDuration:c}, " +
        $"active={result.ActiveMiningTime:c}, samples={result.StatusSamples}");
    Console.WriteLine(
        $"Shares accepted={result.AcceptedShares} (user={result.AcceptedUserShares}), " +
        $"rejected={result.RejectedShares}, invalid={result.InvalidShares}, " +
        $"max-temp={result.MaximumTemperatureC} C, recoveries={result.MaximumRecoveries}");
    foreach (var failure in result.Failures)
        Console.Error.WriteLine($"- {failure}");
    return result.Passed ? ServiceExitCodes.Success : ServiceExitCodes.Failure;
}

var gpuSelectionRequested = args.Contains("--select-gpus", StringComparer.OrdinalIgnoreCase);
if (gpuSelectionRequested)
{
    var unsupportedArguments = args.Where(argument =>
        argument.StartsWith("--", StringComparison.Ordinal) &&
        !string.Equals(argument, "--select-gpus", StringComparison.OrdinalIgnoreCase)).ToArray();
    var configurationArguments = args.Where(argument =>
        !argument.StartsWith("--", StringComparison.Ordinal)).ToArray();
    if (unsupportedArguments.Length > 0 || configurationArguments.Length > 1)
    {
        Console.Error.WriteLine(
            "Use --select-gpus as a standalone command with an optional configuration path.");
        return ServiceExitCodes.InvalidInvocation;
    }

    var selectionConfigurationPath = configurationArguments.SingleOrDefault() ?? "trmadenci.json";
    try
    {
        await GpuSelectionWizard.RunAsync(
            selectionConfigurationPath,
            Console.In,
            Console.Out,
            new CudaComputeBackend(),
            new OpenClComputeBackend());
        return ServiceExitCodes.Success;
    }
    catch (Exception exception) when (
        exception is IOException or JsonException or ArgumentException or InvalidOperationException)
    {
        Console.Error.WriteLine($"GPU selection failed: {exception.Message}");
        return ServiceExitCodes.Failure;
    }
}

var controlCommands = new List<MiningControlCommand>();
foreach (var argument in args)
{
    if (string.Equals(argument, "--pause", StringComparison.OrdinalIgnoreCase))
        controlCommands.Add(MiningControlCommand.Pause);
    else if (string.Equals(argument, "--resume", StringComparison.OrdinalIgnoreCase))
        controlCommands.Add(MiningControlCommand.Resume);
    else if (string.Equals(argument, "--mining-status", StringComparison.OrdinalIgnoreCase))
        controlCommands.Add(MiningControlCommand.Status);
}
if (controlCommands.Count > 0)
{
    if (controlCommands.Count != 1 ||
        args.Contains("--mine", StringComparer.OrdinalIgnoreCase) ||
        args.Contains("--supervise", StringComparer.OrdinalIgnoreCase))
    {
        Console.Error.WriteLine(
            "Use exactly one of --pause, --resume or --mining-status as a standalone command.");
        return ServiceExitCodes.InvalidInvocation;
    }

    try
    {
        var response = await MiningControlClient.SendAsync(controlCommands[0]);
        Console.WriteLine(
            $"{response.Message} State={response.State}, PID={response.ProcessId}, " +
            $"pauses={response.PauseCount}, paused time={response.TotalPausedDuration:c}.");
        return response.Success ? ServiceExitCodes.Success : ServiceExitCodes.Failure;
    }
    catch (Exception exception) when (
        exception is IOException or TimeoutException or OperationCanceledException or UnauthorizedAccessException)
    {
        Console.Error.WriteLine(
            $"No controllable TrMadenci mining process was found: {exception.Message}");
        return ServiceExitCodes.Failure;
    }
}

var supervise = args.Contains("--supervise", StringComparer.OrdinalIgnoreCase);
var miningRequested = args.Contains("--mine", StringComparer.OrdinalIgnoreCase);
var instanceRole = supervise
    ? ServiceInstanceLock.SupervisorRole
    : miningRequested
        ? ServiceInstanceLock.MiningRole
        : null;
using var instanceLock = instanceRole is null ? null : ServiceInstanceLock.TryAcquire(instanceRole);
if (instanceLock is { Acquired: false })
{
    var owner = instanceLock.Owner is null
        ? "owner metadata is unavailable"
        : $"PID {instanceLock.Owner.ProcessId}, started {instanceLock.Owner.StartedAt:O}";
    Console.Error.WriteLine(
        $"Another TrMadenci {instanceRole} instance is already active ({owner}). " +
        $"Lock: {instanceLock.LockPath}");
    return ServiceExitCodes.AlreadyRunning;
}

if (supervise)
{
    var childArguments = args
        .Where(argument => !string.Equals(argument, "--supervise", StringComparison.OrdinalIgnoreCase))
        .ToArray();
    if (!childArguments.Contains("--supervised-child", StringComparer.OrdinalIgnoreCase))
        childArguments = [.. childArguments, "--supervised-child"];
    using var supervisorShutdown = new CancellationTokenSource();
    ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
    {
        eventArgs.Cancel = true;
        supervisorShutdown.Cancel();
    };
    Console.CancelKeyPress += cancelHandler;
    using var supervisorDashboard = ConsoleDashboard.CreateForSupervisor(
        !args.Contains("--plain-console", StringComparer.OrdinalIgnoreCase));
    Action<string> supervisorLog = supervisorDashboard is null
        ? Console.WriteLine
        : supervisorDashboard.Log;
    Action<string> supervisorError = supervisorDashboard is null
        ? Console.Error.WriteLine
        : message => supervisorDashboard.Log($"HATA: {message}");
    await using var supervisorKeys = ConsoleMiningKeyListener.CreateRemote(
        supervisorLog,
        supervisorDashboard is null ? null : supervisorDashboard.UpdateControlState);
    supervisorKeys.Start();
    try
    {
        return await MiningSupervisor.RunAsync(
            childArguments,
            supervisorShutdown.Token,
            consoleOutput: supervisorLog,
            consoleError: supervisorError,
            updateStatus: supervisorDashboard is null ? null : supervisorDashboard.Update);
    }
    catch (Exception exception)
    {
        Console.Error.WriteLine($"Supervisor failed: {exception.Message}");
        return ServiceExitCodes.Failure;
    }
    finally
    {
        Console.CancelKeyPress -= cancelHandler;
    }
}

var etcHashLiveBenchmark = args.Contains("--etchash-live-benchmark", StringComparer.OrdinalIgnoreCase);
var probe = args.Contains("--probe", StringComparer.OrdinalIgnoreCase) || etcHashLiveBenchmark;
var verboseProtocol = args.Contains("--verbose-protocol", StringComparer.OrdinalIgnoreCase);
var nativeProbe = args.Contains("--native-probe", StringComparer.OrdinalIgnoreCase);
var openClProbe = args.Contains("--opencl-probe", StringComparer.OrdinalIgnoreCase);
var openClSelfTest = args.Contains("--opencl-self-test", StringComparer.OrdinalIgnoreCase);
var etcHashOpenClSelfTest = args.Contains("--etchash-opencl-self-test", StringComparer.OrdinalIgnoreCase);
var etcHashOpenClBuildDag = args.Contains("--etchash-opencl-build-dag", StringComparer.OrdinalIgnoreCase);
var etcHashOpenClNonceSelfTest = args.Contains("--etchash-opencl-nonce-self-test", StringComparer.OrdinalIgnoreCase);
var buildDag = args.Contains("--build-dag", StringComparer.OrdinalIgnoreCase);
var nonceSelfTest = args.Contains("--nonce-self-test", StringComparer.OrdinalIgnoreCase);
var etcHashSelfTest = args.Contains("--etchash-self-test", StringComparer.OrdinalIgnoreCase);
var etcHashCudaSelfTest = args.Contains("--etchash-cuda-self-test", StringComparer.OrdinalIgnoreCase);
var etcHashBuildDag = args.Contains("--etchash-build-dag", StringComparer.OrdinalIgnoreCase);
var etcHashNonceSelfTest = args.Contains("--etchash-nonce-self-test", StringComparer.OrdinalIgnoreCase);
var octopusMultiPointSelfTest = args.Contains("--octopus-multipoint-self-test", StringComparer.OrdinalIgnoreCase);
var octopusCudaSelfTest = args.Contains("--octopus-cuda-self-test", StringComparer.OrdinalIgnoreCase);
var octopusBuildDag = args.Contains("--octopus-build-dag", StringComparer.OrdinalIgnoreCase);
var octopusNonceSelfTest = args.Contains("--octopus-nonce-self-test", StringComparer.OrdinalIgnoreCase);
var octopusQualification = args.Contains("--octopus-qualification", StringComparer.OrdinalIgnoreCase);
var randomXSelfTest = args.Contains("--randomx-self-test", StringComparer.OrdinalIgnoreCase);
var mine = args.Contains("--mine", StringComparer.OrdinalIgnoreCase);
var etcHashQualification = args.Contains("--etchash-qualification", StringComparer.OrdinalIgnoreCase);
var etcHashOpenClQualification = args.Contains(
    "--etchash-opencl-qualification", StringComparer.OrdinalIgnoreCase);
var qualifiedOpenClDevices = new HashSet<ComputeDeviceId>();
var plainConsole = args.Contains("--plain-console", StringComparer.OrdinalIgnoreCase);
var supervisedChild = args.Contains("--supervised-child", StringComparer.OrdinalIgnoreCase);
var configPath = args.FirstOrDefault(argument => !argument.StartsWith("--", StringComparison.Ordinal)) ?? "trmadenci.json";

if (!File.Exists(configPath))
{
    Console.Error.WriteLine($"Configuration not found: {Path.GetFullPath(configPath)}");
    return 2;
}

try
{
    var soakRequest = MiningSoakRequest.Parse(args);
    var json = await File.ReadAllTextAsync(configPath);
    var options = JsonSerializer.Deserialize<MinerOptions>(json, new JsonSerializerOptions
    {
        PropertyNameCaseInsensitive = true
    }) ?? throw new InvalidOperationException("Configuration is empty.");

    options.Validate();
    var coin = CoinProfileCatalog.GetRequired(options.Coin);
    if (randomXSelfTest &&
        !string.Equals(coin.Algorithm, "randomx", StringComparison.OrdinalIgnoreCase))
        throw new ArgumentException("--randomx-self-test requires a RandomX coin profile.");
    if (probe && string.Equals(coin.Algorithm, "randomx", StringComparison.OrdinalIgnoreCase))
        throw new InvalidOperationException(
            "RandomX live pool probing remains gated until CPU session integration and qualification are complete.");
    OpenClQualificationGate.ValidateRequest(
        etcHashOpenClQualification,
        mine,
        coin.Algorithm,
        options.ComputeBackend,
        etcHashOpenClNonceSelfTest);
    soakRequest?.Validate(
        mine,
        coin.Algorithm,
        etcHashQualification || etcHashOpenClQualification || octopusQualification);
    if (octopusBuildDag && (!probe ||
        !string.Equals(coin.Algorithm, "octopus", StringComparison.OrdinalIgnoreCase)))
        throw new ArgumentException(
            "--octopus-build-dag requires --probe and an Octopus coin profile.");

    Console.WriteLine("TrMadenci service bootstrap is ready.");
    Console.WriteLine($"Coin/network: {coin.Name} ({coin.Ticker}) / {coin.Network}");
    Console.WriteLine($"Algorithm: {coin.Algorithm.ToUpperInvariant()}");
    Console.WriteLine($"Compute backend: {options.ComputeBackend.ToString().ToUpperInvariant()}");
    Console.WriteLine($"Pool: {options.Pool.Host}:{options.Pool.Port}");
    Console.WriteLine($"User worker: {options.Pool.Username} (explicit configuration; no automatic fallback)");
    Console.WriteLine($"Developer fee: {ProductPolicy.DeveloperFeeRate:P2} (embedded, transparent randomized windows)");
    if (ProductPolicy.TryCreateDeveloperPool(coin, out var developerPool))
        Console.WriteLine($"Developer destination: {developerPool!.Host}:{developerPool.Port} / {developerPool.Username}");
    else
        Console.WriteLine($"Developer destination: not configured for {coin.Ticker}; mining is disabled.");

    if (randomXSelfTest)
    {
        var flags = NativeDiagnostics.GetRandomXRecommendedFlags();
        var hash = NativeDiagnostics.ComputeRandomXLightHash(
            Encoding.UTF8.GetBytes("test key 000"),
            Encoding.UTF8.GetBytes("This is a test"));
        const string expected = "639183AAE1BF4C9A35884CB46B09CAD9175F04EFD7684E7262A0AC1C2F0B4E3F";
        if (!string.Equals(Convert.ToHexString(hash), expected, StringComparison.Ordinal))
            throw new InvalidOperationException("RandomX official light-mode vector failed.");
        Console.WriteLine(
            $"RandomX v1 light-mode self-test passed: hash={Convert.ToHexString(hash).ToLowerInvariant()}, " +
            $"recommended CPU flags={flags}. No pool connection or mining was started.");
    }

    if (nativeProbe)
    {
        foreach (var device in NativeDiagnostics.GetCudaDevices())
        {
            Console.WriteLine($"CUDA{device.Index}: {device.Name}, {device.TotalMemoryBytes / 1024 / 1024} MiB, sm_{device.ComputeMajor}{device.ComputeMinor}");
            NativeDiagnostics.TryGetGpuTelemetry(device.Index, out var telemetry);
            Console.WriteLine(GpuTelemetryFormatter.Format(device.Index, 0, telemetry));
        }
    }

    if (openClProbe || openClSelfTest || etcHashOpenClSelfTest || etcHashOpenClBuildDag ||
        etcHashOpenClNonceSelfTest)
    {
        var openClDevices = NativeDiagnostics.GetOpenClDevices();
        Console.WriteLine($"OpenCL GPU devices: {openClDevices.Count}");
        foreach (var device in openClDevices)
            Console.WriteLine(
                $"OpenCL P{device.PlatformIndex}/D{device.DeviceIndex}: {device.Name} | " +
                $"{device.Vendor} | {device.TotalMemoryBytes / 1024d / 1024 / 1024:F2} GiB | " +
                $"compute units={device.ComputeUnits} | {device.Version}");
        if (openClSelfTest)
        {
            if (openClDevices.Count == 0)
                throw new InvalidOperationException("No OpenCL GPU is available for the runtime self-test.");
            foreach (var device in openClDevices)
            {
                var checksum = NativeDiagnostics.RunOpenClSelfTest(
                    device.PlatformIndex, device.DeviceIndex);
                Console.WriteLine(
                    $"OpenCL P{device.PlatformIndex}/D{device.DeviceIndex} runtime self-test passed: " +
                    $"checksum=0x{checksum:x8}.");
            }
        }
        if (etcHashOpenClSelfTest)
        {
            if (openClDevices.Count == 0)
                throw new InvalidOperationException("No OpenCL GPU is available for ETCHash validation.");
            foreach (var device in openClDevices)
            {
                NativeDiagnostics.ValidateEtcHashOpenClDagItems(
                    11_700_000, device.PlatformIndex, device.DeviceIndex, 8);
                Console.WriteLine(
                    $"OpenCL P{device.PlatformIndex}/D{device.DeviceIndex}: 8 ETCHash DAG items " +
                    "match the 256-parent CPU oracle.");
            }
        }
        if (etcHashOpenClBuildDag)
        {
            if (openClDevices.Count == 0)
                throw new InvalidOperationException("No OpenCL GPU is available for the ETCHash DAG build.");
            const int activationBlock = 11_700_000;
            var device = openClDevices[0];
            Console.WriteLine(
                $"Building the full ETCHash OpenCL DAG on P{device.PlatformIndex}/D{device.DeviceIndex}...");
            using var epoch = NativeDiagnostics.CreateEtcHashOpenClEpoch(
                activationBlock, device.PlatformIndex, device.DeviceIndex);
            Console.WriteLine(
                $"ETCHash OpenCL DAG ready: epoch {epoch.BuildInfo.EpochNumber}, " +
                $"{epoch.BuildInfo.DatasetBytes / 1024 / 1024} MiB in " +
                $"{epoch.BuildInfo.BuildTime.TotalSeconds:F2}s. Releasing validation context.");
        }
        if (etcHashOpenClNonceSelfTest)
        {
            if (openClDevices.Count == 0)
                throw new InvalidOperationException("No OpenCL GPU is available for the ETCHash nonce test.");
            const int blockNumber = 22;
            const ulong expectedNonce = 0x495732e0ed7a801cUL;
            var header = Convert.FromHexString(
                "372ECA2454EAD349C3DF0AB5D00B0B706B23E49D469387DB91811CEE0358FC6D");
            var requestedDeviceIds = options.ComputeBackend == ComputeBackendMode.OpenCl &&
                options.ComputeDevices.Length > 0
                    ? options.ComputeDevices.Select(ComputeDeviceId.Parse).ToHashSet()
                    : null;
            var nonceTestDevices = openClDevices.Where(device => requestedDeviceIds is null ||
                requestedDeviceIds.Contains(new ComputeDeviceId(
                    ComputeBackendKind.OpenCl, device.PlatformIndex, device.DeviceIndex))).ToArray();
            if (requestedDeviceIds is not null && nonceTestDevices.Length != requestedDeviceIds.Count)
                throw new InvalidOperationException(
                    "One or more configured OpenCL devices were not found for nonce qualification.");
            foreach (var device in nonceTestDevices)
            {
                var deviceId = new ComputeDeviceId(
                    ComputeBackendKind.OpenCl, device.PlatformIndex, device.DeviceIndex);
                Console.WriteLine(
                    $"Building ETCHash epoch 0 OpenCL DAG on {deviceId} and running the official nonce vector...");
                using var epoch = NativeDiagnostics.CreateEtcHashOpenClEpoch(
                    blockNumber, device.PlatformIndex, device.DeviceIndex);
                var result = epoch.Search(
                    blockNumber, header, Enumerable.Repeat((byte)0xff, 32).ToArray(), expectedNonce, 1);
                var reference = NativeDiagnostics.ComputeEtcHashReferenceHash(blockNumber, header, expectedNonce);
                if (!result.SolutionFound || result.Nonce != expectedNonce ||
                    !result.MixHash.AsSpan().SequenceEqual(reference.MixHash) ||
                    !result.FinalHash.AsSpan().SequenceEqual(reference.FinalHash) ||
                    Convert.ToHexString(result.FinalHash) !=
                    "00000B184F1FDD88BFD94C86C39E65DB0C36144D5E43F745F722196E730CB614")
                    throw new InvalidOperationException($"ETCHash OpenCL nonce vector failed on {deviceId}.");
                const uint rejectionBatchSize = 4_096;
                var rejection = epoch.Search(blockNumber, header, new byte[32], 0, rejectionBatchSize);
                if (rejection.SolutionFound || rejection.HashesSearched != rejectionBatchSize ||
                    rejection.SearchTime <= TimeSpan.Zero)
                    throw new InvalidOperationException($"ETCHash OpenCL rejection batch failed on {deviceId}.");
                qualifiedOpenClDevices.Add(deviceId);
                Console.WriteLine(
                    $"ETCHash OpenCL nonce self-test passed on {deviceId}: " +
                    $"DAG={epoch.BuildInfo.DatasetBytes / 1024 / 1024} MiB, " +
                    $"vector={result.SearchTime.TotalMilliseconds:F2}ms, " +
                    $"4K rejection batch={rejection.SearchTime.TotalMilliseconds:F2}ms, " +
                    $"hash={Convert.ToHexString(result.FinalHash).ToLowerInvariant()}.");
            }
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

    if (octopusNonceSelfTest)
    {
        const ulong blockNumber = 2;
        const ulong nonce = 0x2333333320;
        var header = Convert.FromHexString(
            "4D99D0B41C7EB0DD1A801C35AAE2DF28AE6B53BC7743F0818A34B6EC97F5B4AE");
        Console.WriteLine("Building the Octopus epoch-0 DAG and checking one CUDA nonce...");
        using var epoch = NativeDiagnostics.CreateOctopusCudaEpoch(blockNumber, 0);
        var result = epoch.Search(
            blockNumber, header, Enumerable.Repeat((byte)0xff, 32).ToArray(), nonce, 1);
        var multiPoint = TrMadenci.Core.Algorithms.OctopusMultiPoint.Evaluate(header, nonce);
        var reference = NativeDiagnostics.ComputeOctopusReferenceHash(
            blockNumber, header, nonce, multiPoint.Compressed, multiPoint.Points.ToArray());
        if (!result.SolutionFound || result.Nonce != nonce ||
            !result.FinalHash.AsSpan().SequenceEqual(reference) ||
            Convert.ToHexString(result.FinalHash) !=
            "D45C965D3707E27A42995132637854234385CBF5626897259F1EE980554DDD5C")
            throw new InvalidOperationException("Octopus CUDA nonce self-test failed.");
        Console.WriteLine(
            $"Octopus CUDA nonce self-test passed: DAG={epoch.BuildInfo.DatasetBytes / 1024d / 1024 / 1024:F3} GiB, " +
            $"build={epoch.BuildInfo.BuildTime.TotalSeconds:F2}s, " +
            $"search={result.SearchTime.TotalMilliseconds:F2}ms.");
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
                options.GetCudaDeviceIndexes());
        else
            await ProbePoolAsync(options.Pool, verboseProtocol, nativeProbe, buildDag);
    }

    if (etcHashQualification && (!mine ||
        !string.Equals(coin.Algorithm, "etchash", StringComparison.OrdinalIgnoreCase)))
        throw new ArgumentException(
            "--etchash-qualification requires --mine and an ETCHash coin profile.");
    if (etcHashQualification && etcHashOpenClQualification)
        throw new ArgumentException("Use either CUDA or OpenCL ETCHash qualification, not both.");
    if (octopusQualification && (!mine ||
        !string.Equals(coin.Algorithm, "octopus", StringComparison.OrdinalIgnoreCase)))
        throw new ArgumentException(
            "--octopus-qualification requires --mine and an Octopus coin profile.");

    if (mine)
    {
        if (options.ComputeBackend == ComputeBackendMode.OpenCl && !etcHashOpenClQualification)
            throw new InvalidOperationException(
                "OpenCL mining remains gated until the signed ETCHash nonce-vector qualification passes.");
        var qualificationAllowed =
            ((etcHashQualification || etcHashOpenClQualification || soakRequest is not null) &&
                string.Equals(coin.Algorithm, "etchash", StringComparison.OrdinalIgnoreCase)) ||
            ((octopusQualification || soakRequest is not null) &&
                string.Equals(coin.Algorithm, "octopus", StringComparison.OrdinalIgnoreCase));
        if (!coin.MiningEnabled && !qualificationAllowed)
            throw new InvalidOperationException(
                $"Mining for {coin.Ticker} is not enabled: {coin.MiningBlockedReason}");
        if (etcHashQualification)
            Console.WriteLine(
                "ETCHash QUALIFICATION MODE: mining stops automatically after the first accepted share; " +
                "this does not enable the public ETC profile.");
        if (etcHashOpenClQualification)
            Console.WriteLine(
                "ETCHash OPENCL QUALIFICATION MODE: every selected device passed the official vector " +
                "in this process; mining stops after the first CPU-verified, pool-accepted share.");
        if (soakRequest is { Duration: { } soakDuration })
            Console.WriteLine(
                $"{coin.Algorithm.ToUpperInvariant()} SOAK MODE: requested duration={soakDuration}; " +
                "evidence and a final health summary will be recorded.");
        if (octopusQualification)
            Console.WriteLine(
                "OCTOPUS QUALIFICATION MODE: experimental mining stops automatically after the first " +
                "CPU-verified, pool-accepted share; this does not enable the public CFX profile.");
        using var shutdown = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            shutdown.Cancel();
        };
        var pauseController = new MiningPauseController();
        using var dashboard = ConsoleDashboard.CreateIfSupported(
            options, coin, pauseController, !plainConsole);
        using var soakRecorder = soakRequest is { Duration: { } requestedDuration }
            ? new MiningSoakRecorder(coin, requestedDuration, pauseController: pauseController)
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
        await using var miningControlServer = new MiningControlServer(pauseController, miningLog);
        miningControlServer.Start();
        await using var miningKeys = ConsoleMiningKeyListener.CreateLocal(pauseController, miningLog);
        miningKeys.Start();
        if (soakRecorder is not null)
            Console.WriteLine(
                $"Soak evidence: {soakRecorder.EventsPath}{Environment.NewLine}" +
                $"Soak summary:  {soakRecorder.SummaryPath}");
        if (dashboard is null)
            Console.WriteLine(
                "Mining started. P=pause, S=start/resume, D=status, Ctrl+C=safe stop. " +
                "A second terminal may use --pause/--resume/--mining-status.");
        else
            dashboard.Log(
                "Mining started. P=pause, S=start/resume, D=status, Ctrl+C=safe stop.");
        Action<MiningStatusSnapshot>? statusUpdate =
            dashboard is null && soakRecorder is null && !supervisedChild
            ? null
            : snapshot =>
            {
                dashboard?.Update(snapshot);
                soakRecorder?.RecordStatus(snapshot);
                if (supervisedChild)
                    Console.WriteLine(MiningStatusTransport.Encode(snapshot));
                if (dashboard is null)
                    miningLog(
                        $"{(soakRecorder is null ? "Mining" : "Soak")} status: " +
                        $"{snapshot.CurrentHashesPerSecond / 1_000_000:F2} MH/s, " +
                        $"shares={snapshot.AcceptedShares}/{snapshot.RejectedShares}, " +
                        $"invalid={snapshot.InvalidShares}, energy={snapshot.SessionEnergyKwh:F3} kWh.");
            };
        try
        {
            if (string.Equals(coin.Algorithm, "etchash", StringComparison.OrdinalIgnoreCase))
            {
                IReadOnlyList<ComputeDeviceId>? selectedComputeDevices = null;
                if (etcHashOpenClQualification)
                {
                    var selected = ComputeDeviceResolver.Resolve(
                        options, new CudaComputeBackend(), new OpenClComputeBackend());
                    selectedComputeDevices = selected.Select(device => device.Id).ToArray();
                    OpenClQualificationGate.EnsureSelectedDevicesPassed(
                        selectedComputeDevices, qualifiedOpenClDevices);
                }
                var session = new EtcHashMiningSession(
                    options,
                    miningLog,
                    statusUpdate,
                    stopAfterAcceptedShares: etcHashQualification || etcHashOpenClQualification ? 1 : null,
                    stopAfterDuration: soakRequest?.Duration,
                    selectedComputeDevices: selectedComputeDevices,
                    pauseController: pauseController);
                await session.RunAsync(shutdown.Token);
            }
            else if (string.Equals(coin.Algorithm, "kawpow", StringComparison.OrdinalIgnoreCase))
            {
                var session = new KawPowMiningSession(
                    options,
                    miningLog,
                    statusUpdate,
                    stopAfterDuration: soakRequest?.Duration,
                    pauseController: pauseController);
                await session.RunAsync(shutdown.Token);
            }
            else if (string.Equals(coin.Algorithm, "octopus", StringComparison.OrdinalIgnoreCase) &&
                (octopusQualification || soakRequest is not null))
            {
                var session = new OctopusMiningSession(
                    options,
                    miningLog,
                    statusUpdate,
                    stopAfterAcceptedShares: octopusQualification ? 1 : null,
                    stopAfterDuration: soakRequest?.Duration,
                    pauseController: pauseController);
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
catch (DllNotFoundException exception)
{
    Console.Error.WriteLine($"Native runtime unavailable: {exception.Message}");
    return 1;
}
catch (ComputeWorkerWatchdogException exception)
{
    Console.Error.WriteLine($"GPU watchdog stopped mining: {exception.Message}");
    return ServiceExitCodes.ComputeWatchdogRestartRequired;
}
catch (Exception exception)
{
    Console.Error.WriteLine($"TrMadenci failed: {exception.Message}");
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
