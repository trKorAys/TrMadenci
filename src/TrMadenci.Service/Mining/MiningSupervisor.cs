using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace TrMadenci.Service.Mining;

internal sealed record MiningSupervisorOptions(
    int MaximumRestarts,
    TimeSpan RestartWindow,
    TimeSpan StableRunDuration,
    IReadOnlyList<TimeSpan> RestartDelays,
    TimeSpan GracefulShutdownTimeout,
    int RetainedOutputLines,
    long MaximumCombinedLogBytes)
{
    public static MiningSupervisorOptions Production { get; } = new(
        MaximumRestarts: 3,
        RestartWindow: TimeSpan.FromMinutes(15),
        StableRunDuration: TimeSpan.FromMinutes(10),
        RestartDelays:
        [
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(15),
            TimeSpan.FromSeconds(30)
        ],
        GracefulShutdownTimeout: TimeSpan.FromSeconds(15),
        RetainedOutputLines: 200,
        MaximumCombinedLogBytes: 16 * 1024 * 1024);

    public void Validate()
    {
        if (MaximumRestarts <= 0)
            throw new ArgumentOutOfRangeException(nameof(MaximumRestarts));
        if (RestartWindow <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(RestartWindow));
        if (StableRunDuration <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(StableRunDuration));
        if (RestartDelays.Count == 0 || RestartDelays.Any(delay => delay < TimeSpan.Zero))
            throw new ArgumentOutOfRangeException(nameof(RestartDelays));
        if (GracefulShutdownTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(GracefulShutdownTimeout));
        if (RetainedOutputLines <= 0)
            throw new ArgumentOutOfRangeException(nameof(RetainedOutputLines));
        if (MaximumCombinedLogBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(MaximumCombinedLogBytes));
    }
}

internal readonly record struct SupervisorRestartReservation(
    bool Allowed,
    int Attempt,
    TimeSpan Delay);

internal sealed class SupervisorRestartPolicy(MiningSupervisorOptions options)
{
    private readonly Queue<DateTimeOffset> _restartTimes = new();

    public SupervisorRestartReservation Reserve(DateTimeOffset now, TimeSpan childRuntime)
    {
        if (childRuntime >= options.StableRunDuration)
            _restartTimes.Clear();
        while (_restartTimes.TryPeek(out var oldest) && now - oldest >= options.RestartWindow)
            _restartTimes.Dequeue();
        if (_restartTimes.Count >= options.MaximumRestarts)
            return new SupervisorRestartReservation(false, _restartTimes.Count, TimeSpan.Zero);

        _restartTimes.Enqueue(now);
        var attempt = _restartTimes.Count;
        var delayIndex = Math.Min(attempt - 1, options.RestartDelays.Count - 1);
        return new SupervisorRestartReservation(true, attempt, options.RestartDelays[delayIndex]);
    }

    public static bool IsRestartableExit(int exitCode) =>
        exitCode == ServiceExitCodes.ComputeWatchdogRestartRequired || exitCode < 0;

    public static string ClassifyExit(int exitCode) => exitCode switch
    {
        ServiceExitCodes.Success => "normal",
        ServiceExitCodes.InvalidInvocation => "invalid-invocation",
        ServiceExitCodes.ComputeWatchdogRestartRequired => "compute-watchdog",
        ServiceExitCodes.AlreadyRunning => "already-running",
        < 0 => $"native-crash-0x{unchecked((uint)exitCode):X8}",
        _ => "non-restartable-failure"
    };
}

internal static class ServiceExitCodes
{
    public const int Success = 0;
    public const int Failure = 1;
    public const int InvalidInvocation = 2;
    public const int ComputeWatchdogRestartRequired = 3;
    public const int SupervisorCircuitOpen = 4;
    public const int AlreadyRunning = 5;
}

internal sealed class MiningSupervisorReportWriter : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly object _gate = new();
    private readonly Queue<SupervisorOutputLine> _recentOutput = new();
    private readonly int _retainedOutputLines;
    private readonly long _maximumCombinedLogBytes;
    private readonly StreamWriter _events;
    private readonly StreamWriter _combinedLog;
    private long _combinedLogBytes;
    private bool _combinedLogCapped;

    public MiningSupervisorReportWriter(
        string reportDirectory,
        int retainedOutputLines,
        long maximumCombinedLogBytes)
    {
        Directory.CreateDirectory(reportDirectory);
        _retainedOutputLines = retainedOutputLines;
        _maximumCombinedLogBytes = maximumCombinedLogBytes;
        var startedAt = DateTimeOffset.Now;
        var stem = $"supervisor-{startedAt:yyyyMMdd-HHmmss}-{Environment.ProcessId}";
        EventsPath = Path.Combine(reportDirectory, stem + ".jsonl");
        CombinedLogPath = Path.Combine(reportDirectory, stem + ".log");
        _events = new StreamWriter(EventsPath, append: false) { AutoFlush = true };
        _combinedLog = new StreamWriter(CombinedLogPath, append: false) { AutoFlush = true };
    }

    public string EventsPath { get; }
    public string CombinedLogPath { get; }

    public void BeginChild()
    {
        lock (_gate)
            _recentOutput.Clear();
    }

    public void RecordEvent(string type, object? details = null)
    {
        lock (_gate)
        {
            try
            {
                _events.WriteLine(JsonSerializer.Serialize(new
                {
                    type,
                    timestamp = DateTimeOffset.Now,
                    supervisorProcessId = Environment.ProcessId,
                    details
                }));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Reporting failure must not turn a recoverable miner crash into a supervisor crash.
            }
        }
    }

    public void RecordOutput(string stream, string line)
    {
        var item = new SupervisorOutputLine(DateTimeOffset.Now, stream, line);
        lock (_gate)
        {
            _recentOutput.Enqueue(item);
            while (_recentOutput.Count > _retainedOutputLines)
                _recentOutput.Dequeue();
            if (!_combinedLogCapped)
            {
                var formatted = $"{item.Timestamp:O} [{stream}] {line}{Environment.NewLine}";
                var byteCount = Encoding.UTF8.GetByteCount(formatted);
                if (_combinedLogBytes + byteCount <= _maximumCombinedLogBytes)
                {
                    try
                    {
                        _combinedLog.Write(formatted);
                        _combinedLogBytes += byteCount;
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                    {
                        _combinedLogCapped = true;
                    }
                }
                else
                {
                    try
                    {
                        _combinedLog.WriteLine(
                            $"{DateTimeOffset.Now:O} [supervisor] Combined child log reached " +
                            $"the {_maximumCombinedLogBytes}-byte cap; crash reports still retain recent output.");
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                    {
                    }
                    _combinedLogCapped = true;
                }
            }
        }
    }

    public string WriteCrashReport(
        int childProcessId,
        DateTimeOffset startedAt,
        DateTimeOffset exitedAt,
        int exitCode,
        string executable,
        IReadOnlyList<string> arguments,
        bool restartPlanned,
        int restartAttempt)
    {
        SupervisorOutputLine[] output;
        lock (_gate)
            output = _recentOutput.ToArray();
        var path = Path.Combine(
            Path.GetDirectoryName(EventsPath)!,
            $"crash-{exitedAt:yyyyMMdd-HHmmss-fff}-pid{childProcessId}-{Guid.NewGuid():N}.json");
        var report = new
        {
            schemaVersion = 1,
            product = "TrMadenci",
            supervisorProcessId = Environment.ProcessId,
            childProcessId,
            executable,
            arguments = SanitizeArguments(arguments),
            startedAt,
            exitedAt,
            runtime = exitedAt - startedAt,
            exitCode,
            exitCodeHex = $"0x{unchecked((uint)exitCode):X8}",
            reason = SupervisorRestartPolicy.ClassifyExit(exitCode),
            restartPlanned,
            restartAttempt,
            recentOutput = output
        };
        try
        {
            File.WriteAllText(path, JsonSerializer.Serialize(report, JsonOptions));
            return path;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return $"unavailable ({exception.GetType().Name}: {exception.Message})";
        }
    }

    internal static IReadOnlyList<string> SanitizeArguments(IReadOnlyList<string> arguments)
    {
        var sanitized = new string[arguments.Count];
        var redactNext = false;
        for (var index = 0; index < arguments.Count; index++)
        {
            var argument = arguments[index];
            if (redactNext)
            {
                sanitized[index] = "[REDACTED]";
                redactNext = false;
                continue;
            }

            var separator = argument.IndexOf('=');
            var name = separator >= 0 ? argument[..separator] : argument;
            var sensitive = name.StartsWith('-') && IsSensitiveName(name);
            if (sensitive && separator >= 0)
                sanitized[index] = name + "=[REDACTED]";
            else
            {
                sanitized[index] = argument;
                redactNext = sensitive;
            }
        }
        return sanitized;
    }

    private static bool IsSensitiveName(string argumentName) =>
        argumentName.Contains("password", StringComparison.OrdinalIgnoreCase) ||
        argumentName.Contains("secret", StringComparison.OrdinalIgnoreCase) ||
        argumentName.Contains("token", StringComparison.OrdinalIgnoreCase) ||
        argumentName.Contains("credential", StringComparison.OrdinalIgnoreCase) ||
        argumentName.Contains("private-key", StringComparison.OrdinalIgnoreCase);

    public void Dispose()
    {
        lock (_gate)
        {
            try
            {
                _events.Dispose();
            }
            catch (IOException)
            {
            }
            try
            {
                _combinedLog.Dispose();
            }
            catch (IOException)
            {
            }
        }
    }

    private sealed record SupervisorOutputLine(
        DateTimeOffset Timestamp,
        string Stream,
        string Line);
}

internal static class MiningSupervisor
{
    public static async Task<int> RunAsync(
        IReadOnlyList<string> childArguments,
        CancellationToken cancellationToken,
        MiningSupervisorOptions? options = null,
        string? executablePath = null,
        string? reportDirectory = null,
        Action<string>? consoleOutput = null,
        Action<string>? consoleError = null,
        Action<MiningStatusSnapshot>? updateStatus = null)
    {
        if (!childArguments.Contains("--mine", StringComparer.OrdinalIgnoreCase))
            throw new ArgumentException("--supervise requires --mine.");

        var selectedOptions = options ?? MiningSupervisorOptions.Production;
        selectedOptions.Validate();
        var writeOutput = consoleOutput ?? Console.WriteLine;
        var writeError = consoleError ?? Console.Error.WriteLine;
        var executable = Path.GetFullPath(executablePath ?? ResolveCurrentExecutable());
        if (!File.Exists(executable))
            throw new FileNotFoundException("The supervised TrMadenci executable was not found.", executable);

        var reports = reportDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "TrMadenci",
            "crash-reports");
        using var writer = new MiningSupervisorReportWriter(
            reports,
            selectedOptions.RetainedOutputLines,
            selectedOptions.MaximumCombinedLogBytes);
        var policy = new SupervisorRestartPolicy(selectedOptions);
        writer.RecordEvent("supervisor-start", new
        {
            executable,
            arguments = MiningSupervisorReportWriter.SanitizeArguments(childArguments),
            eventsPath = writer.EventsPath,
            combinedLogPath = writer.CombinedLogPath,
            selectedOptions.MaximumRestarts,
            selectedOptions.RestartWindow,
            selectedOptions.StableRunDuration
        });
        writeOutput($"Supervisor reports: {writer.EventsPath}");
        writeOutput($"Supervisor child log: {writer.CombinedLogPath}");

        while (!cancellationToken.IsCancellationRequested)
        {
            using var child = CreateChild(
                executable, childArguments, writer, writeOutput, writeError, updateStatus);
            var startedAt = DateTimeOffset.Now;
            if (!child.Start())
                throw new InvalidOperationException("TrMadenci child process could not be started.");
            writer.BeginChild();
            child.BeginOutputReadLine();
            child.BeginErrorReadLine();
            writer.RecordEvent("child-start", new { childProcessId = child.Id, startedAt });
            writeOutput($"Supervisor started TrMadenci child PID {child.Id}.");

            var forcedShutdown = false;
            try
            {
                await child.WaitForExitAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                writer.RecordEvent("shutdown-requested", new { childProcessId = child.Id });
                using var gracefulTimeout = new CancellationTokenSource(selectedOptions.GracefulShutdownTimeout);
                try
                {
                    await child.WaitForExitAsync(gracefulTimeout.Token);
                }
                catch (OperationCanceledException) when (gracefulTimeout.IsCancellationRequested)
                {
                    forcedShutdown = true;
                    try
                    {
                        if (!child.HasExited)
                            child.Kill(entireProcessTree: true);
                    }
                    catch (InvalidOperationException)
                    {
                        // The child won the race and exited between the timeout and kill request.
                    }
                    await child.WaitForExitAsync(CancellationToken.None);
                }
            }
            child.WaitForExit();

            var exitedAt = DateTimeOffset.Now;
            var runtime = exitedAt - startedAt;
            var exitCode = child.ExitCode;
            writer.RecordEvent("child-exit", new
            {
                childProcessId = child.Id,
                exitedAt,
                runtime,
                exitCode,
                exitCodeHex = $"0x{unchecked((uint)exitCode):X8}",
                reason = forcedShutdown ? "forced-supervisor-shutdown" :
                    SupervisorRestartPolicy.ClassifyExit(exitCode)
            });

            if (cancellationToken.IsCancellationRequested)
            {
                writer.RecordEvent("supervisor-stop", new { reason = "user-request", forcedShutdown });
                return ServiceExitCodes.Success;
            }
            if (!SupervisorRestartPolicy.IsRestartableExit(exitCode))
            {
                if (exitCode != ServiceExitCodes.Success)
                {
                    var report = writer.WriteCrashReport(
                        child.Id, startedAt, exitedAt, exitCode, executable, childArguments, false, 0);
                    writeError($"TrMadenci stopped without restart. Crash report: {report}");
                }
                writer.RecordEvent("supervisor-stop", new { reason = "child-exit", exitCode });
                return exitCode;
            }

            var reservation = policy.Reserve(exitedAt, runtime);
            var crashReport = writer.WriteCrashReport(
                child.Id,
                startedAt,
                exitedAt,
                exitCode,
                executable,
                childArguments,
                reservation.Allowed,
                reservation.Attempt);
            if (!reservation.Allowed)
            {
                writer.RecordEvent("restart-circuit-open", new
                {
                    childProcessId = child.Id,
                    crashReport,
                    selectedOptions.MaximumRestarts,
                    selectedOptions.RestartWindow
                });
                writeError(
                    $"Supervisor restart circuit opened after {selectedOptions.MaximumRestarts} failures. " +
                    $"Crash report: {crashReport}");
                return ServiceExitCodes.SupervisorCircuitOpen;
            }

            writer.RecordEvent("restart-scheduled", new
            {
                childProcessId = child.Id,
                crashReport,
                attempt = reservation.Attempt,
                delay = reservation.Delay
            });
            writeError(
                $"Restartable TrMadenci failure ({SupervisorRestartPolicy.ClassifyExit(exitCode)}). " +
                $"Restart {reservation.Attempt}/{selectedOptions.MaximumRestarts} in " +
                $"{reservation.Delay.TotalSeconds:F0}s. Crash report: {crashReport}");
            try
            {
                await Task.Delay(reservation.Delay, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                writer.RecordEvent("supervisor-stop", new { reason = "user-request-during-backoff" });
                return ServiceExitCodes.Success;
            }
        }

        writer.RecordEvent("supervisor-stop", new { reason = "user-request-before-start" });
        return ServiceExitCodes.Success;
    }

    private static Process CreateChild(
        string executable,
        IReadOnlyList<string> arguments,
        MiningSupervisorReportWriter writer,
        Action<string> writeOutput,
        Action<string> writeError,
        Action<MiningStatusSnapshot>? updateStatus)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = Environment.CurrentDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = false
        };
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, eventArgs) =>
        {
            if (eventArgs.Data is not { } line)
                return;
            if (MiningStatusTransport.TryDecode(line, out var snapshot))
            {
                updateStatus?.Invoke(snapshot!);
                return;
            }
            writer.RecordOutput("stdout", line);
            writeOutput(line);
        };
        process.ErrorDataReceived += (_, eventArgs) =>
        {
            if (eventArgs.Data is not { } line)
                return;
            writer.RecordOutput("stderr", line);
            writeError(line);
        };
        return process;
    }

    private static string ResolveCurrentExecutable()
    {
        var path = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(path))
            throw new InvalidOperationException("The current TrMadenci executable path could not be resolved.");
        if (string.Equals(Path.GetFileNameWithoutExtension(path), "dotnet", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "Supervisor mode requires the TrMadenci apphost executable, not 'dotnet <dll>'.");
        return path;
    }
}
