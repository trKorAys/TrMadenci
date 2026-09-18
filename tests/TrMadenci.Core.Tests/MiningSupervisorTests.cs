using System.Text.Json;
using System.Collections.Concurrent;
using TrMadenci.Core.Fees;
using TrMadenci.Service.Mining;

namespace TrMadenci.Core.Tests;

public sealed class MiningSupervisorTests
{
    [Fact]
    public void Restart_policy_restarts_only_watchdog_and_native_crash_exits()
    {
        Assert.True(SupervisorRestartPolicy.IsRestartableExit(
            ServiceExitCodes.ComputeWatchdogRestartRequired));
        Assert.True(SupervisorRestartPolicy.IsRestartableExit(unchecked((int)0xC0000005)));
        Assert.False(SupervisorRestartPolicy.IsRestartableExit(ServiceExitCodes.Success));
        Assert.False(SupervisorRestartPolicy.IsRestartableExit(ServiceExitCodes.Failure));
        Assert.False(SupervisorRestartPolicy.IsRestartableExit(ServiceExitCodes.InvalidInvocation));
    }

    [Fact]
    public void Restart_policy_applies_backoff_and_opens_circuit()
    {
        var options = Options(maximumRestarts: 3);
        var policy = new SupervisorRestartPolicy(options);
        var now = new DateTimeOffset(2026, 9, 12, 12, 0, 0, TimeSpan.Zero);

        var first = policy.Reserve(now, TimeSpan.FromSeconds(1));
        var second = policy.Reserve(now.AddMinutes(1), TimeSpan.FromSeconds(1));
        var third = policy.Reserve(now.AddMinutes(2), TimeSpan.FromSeconds(1));
        var blocked = policy.Reserve(now.AddMinutes(3), TimeSpan.FromSeconds(1));

        Assert.Equal(new SupervisorRestartReservation(true, 1, TimeSpan.FromSeconds(1)), first);
        Assert.Equal(new SupervisorRestartReservation(true, 2, TimeSpan.FromSeconds(2)), second);
        Assert.Equal(new SupervisorRestartReservation(true, 3, TimeSpan.FromSeconds(3)), third);
        Assert.False(blocked.Allowed);
    }

    [Fact]
    public void Stable_run_and_elapsed_window_reset_restart_budget()
    {
        var options = Options(maximumRestarts: 1);
        var now = new DateTimeOffset(2026, 9, 12, 12, 0, 0, TimeSpan.Zero);
        var stablePolicy = new SupervisorRestartPolicy(options);
        Assert.True(stablePolicy.Reserve(now, TimeSpan.Zero).Allowed);

        var afterStableRun = stablePolicy.Reserve(
            now.AddMinutes(1), options.StableRunDuration + TimeSpan.FromSeconds(1));
        Assert.True(afterStableRun.Allowed);
        Assert.Equal(1, afterStableRun.Attempt);

        var windowPolicy = new SupervisorRestartPolicy(options);
        Assert.True(windowPolicy.Reserve(now, TimeSpan.Zero).Allowed);
        var afterWindow = windowPolicy.Reserve(
            now + options.RestartWindow + TimeSpan.FromTicks(1), TimeSpan.Zero);
        Assert.True(afterWindow.Allowed);
        Assert.Equal(1, afterWindow.Attempt);
    }

    [Fact]
    public void Crash_report_redacts_sensitive_arguments_and_retains_bounded_output()
    {
        var directory = Path.Combine(
            Path.GetTempPath(), "TrMadenciSupervisorTests", Guid.NewGuid().ToString("N"));
        try
        {
            string reportPath;
            string eventsPath;
            string combinedLogPath;
            using (var writer = new MiningSupervisorReportWriter(
                directory,
                retainedOutputLines: 2,
                maximumCombinedLogBytes: 1024 * 1024))
            {
                writer.RecordEvent("test-start");
                writer.RecordOutput("stdout", "discarded");
                writer.RecordOutput("stdout", "retained-one");
                writer.RecordOutput("stderr", "retained-two");
                var startedAt = new DateTimeOffset(2026, 9, 12, 12, 0, 0, TimeSpan.Zero);
                reportPath = writer.WriteCrashReport(
                    42,
                    startedAt,
                    startedAt.AddSeconds(5),
                    ServiceExitCodes.ComputeWatchdogRestartRequired,
                    "TrMadenci.Service.exe",
                    [
                        "C:\\safe-secret-folder\\trmadenci.json",
                        "--mine",
                        "--password=hunter2",
                        "--token",
                        "abc123"
                    ],
                    restartPlanned: true,
                    restartAttempt: 1);
                eventsPath = writer.EventsPath;
                combinedLogPath = writer.CombinedLogPath;
            }

            using var report = JsonDocument.Parse(File.ReadAllText(reportPath));
            var root = report.RootElement;
            var arguments = root.GetProperty("arguments")
                .EnumerateArray()
                .Select(element => element.GetString())
                .ToArray();
            Assert.Equal("C:\\safe-secret-folder\\trmadenci.json", arguments[0]);
            Assert.Equal("--mine", arguments[1]);
            Assert.Equal("--password=[REDACTED]", arguments[2]);
            Assert.Equal("--token", arguments[3]);
            Assert.Equal("[REDACTED]", arguments[4]);
            var output = root.GetProperty("recentOutput").EnumerateArray().ToArray();
            Assert.Equal(2, output.Length);
            Assert.Equal("retained-one", output[0].GetProperty("Line").GetString());
            Assert.Equal("retained-two", output[1].GetProperty("Line").GetString());
            Assert.True(File.Exists(eventsPath));
            Assert.True(File.Exists(combinedLogPath));
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Supervisor_requires_explicit_mining_argument()
    {
        var exception = await Assert.ThrowsAsync<ArgumentException>(() =>
            MiningSupervisor.RunAsync(
                ["trmadenci.json"],
                CancellationToken.None,
                Options(maximumRestarts: 1),
                executablePath: Environment.ProcessPath,
                reportDirectory: Path.GetTempPath()));

        Assert.Equal("--supervise requires --mine.", exception.Message);
    }

    [Fact]
    public void Combined_child_log_is_capped_without_losing_recent_crash_output()
    {
        var directory = Path.Combine(
            Path.GetTempPath(), "TrMadenciSupervisorTests", Guid.NewGuid().ToString("N"));
        try
        {
            string reportPath;
            string combinedLogPath;
            using (var writer = new MiningSupervisorReportWriter(
                directory,
                retainedOutputLines: 2,
                maximumCombinedLogBytes: 128))
            {
                writer.BeginChild();
                writer.RecordOutput("stdout", new string('a', 256));
                writer.RecordOutput("stderr", "retained-after-cap");
                var now = DateTimeOffset.Now;
                reportPath = writer.WriteCrashReport(
                    7,
                    now,
                    now.AddSeconds(1),
                    ServiceExitCodes.ComputeWatchdogRestartRequired,
                    "TrMadenci.Service.exe",
                    ["trmadenci.json", "--mine"],
                    restartPlanned: true,
                    restartAttempt: 1);
                combinedLogPath = writer.CombinedLogPath;
            }

            var combinedLog = File.ReadAllText(combinedLogPath);
            Assert.Contains("Combined child log reached the 128-byte cap", combinedLog);
            Assert.DoesNotContain(new string('a', 256), combinedLog);

            using var report = JsonDocument.Parse(File.ReadAllText(reportPath));
            var output = report.RootElement.GetProperty("recentOutput").EnumerateArray().ToArray();
            Assert.Equal(2, output.Length);
            Assert.Equal(new string('a', 256), output[0].GetProperty("Line").GetString());
            Assert.Equal("retained-after-cap", output[1].GetProperty("Line").GetString());
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Supervisor_restarts_watchdog_exit_then_opens_bounded_circuit()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var directory = Path.Combine(
            Path.GetTempPath(), "TrMadenciSupervisorTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var childScript = Path.Combine(directory, "watchdog-exit.cmd");
        await File.WriteAllTextAsync(
            childScript,
            $"@exit /b {ServiceExitCodes.ComputeWatchdogRestartRequired}{Environment.NewLine}");
        try
        {
            var commandProcessor = Environment.GetEnvironmentVariable("ComSpec") ??
                Path.Combine(Environment.SystemDirectory, "cmd.exe");
            var options = new MiningSupervisorOptions(
                1,
                TimeSpan.FromMinutes(15),
                TimeSpan.FromMinutes(10),
                [TimeSpan.Zero],
                TimeSpan.FromSeconds(1),
                10,
                1024 * 1024);

            var result = await MiningSupervisor.RunAsync(
                ["/d", "/c", childScript, "--mine"],
                CancellationToken.None,
                options,
                commandProcessor,
                directory);

            Assert.Equal(ServiceExitCodes.SupervisorCircuitOpen, result);
            Assert.Equal(2, Directory.GetFiles(directory, "crash-*.json").Length);
            var eventsPath = Assert.Single(Directory.GetFiles(directory, "supervisor-*.jsonl"));
            var events = await File.ReadAllLinesAsync(eventsPath);
            Assert.Contains(events, line => line.Contains("\"restart-scheduled\"", StringComparison.Ordinal));
            Assert.Contains(events, line => line.Contains("\"restart-circuit-open\"", StringComparison.Ordinal));
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Supervisor_decodes_status_for_dashboard_without_printing_transport_payload()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var directory = Path.Combine(
            Path.GetTempPath(), "TrMadenciSupervisorStatusTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var snapshot = new MiningStatusSnapshot(
            DateTimeOffset.Now,
            TimeSpan.FromMinutes(5),
            TimeSpan.FromMinutes(4),
            "Ethereum Classic",
            "ETC",
            "Ethereum Classic Mainnet",
            "ETCHASH",
            "etc.poolbinance.com:1800",
            MiningBeneficiary.User,
            15_000_000,
            14_900_000,
            1_000_000,
            2,
            0,
            0,
            0.01,
            125,
            []);
        var childScript = Path.Combine(directory, "status-exit.cmd");
        await File.WriteAllTextAsync(
            childScript,
            $"@echo {MiningStatusTransport.Encode(snapshot)}{Environment.NewLine}@exit /b 0{Environment.NewLine}");
        var output = new ConcurrentQueue<string>();
        var received = new TaskCompletionSource<MiningStatusSnapshot>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        try
        {
            var commandProcessor = Environment.GetEnvironmentVariable("ComSpec") ??
                Path.Combine(Environment.SystemDirectory, "cmd.exe");
            var result = await MiningSupervisor.RunAsync(
                ["/d", "/c", childScript, "--mine"],
                CancellationToken.None,
                Options(maximumRestarts: 1),
                commandProcessor,
                directory,
                output.Enqueue,
                output.Enqueue,
                status => received.TrySetResult(status));

            Assert.Equal(ServiceExitCodes.Success, result);
            Assert.Equal("ETC", (await received.Task.WaitAsync(TimeSpan.FromSeconds(2))).CoinTicker);
            Assert.DoesNotContain(output, line =>
                line.StartsWith(MiningStatusTransport.Prefix, StringComparison.Ordinal));
            var combinedLog = Assert.Single(Directory.GetFiles(directory, "supervisor-*.log"));
            Assert.DoesNotContain(MiningStatusTransport.Prefix, await File.ReadAllTextAsync(combinedLog));
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    private static MiningSupervisorOptions Options(int maximumRestarts) => new(
        maximumRestarts,
        TimeSpan.FromMinutes(15),
        TimeSpan.FromMinutes(10),
        [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(3)],
        TimeSpan.FromSeconds(1),
        10,
        1024 * 1024);
}
