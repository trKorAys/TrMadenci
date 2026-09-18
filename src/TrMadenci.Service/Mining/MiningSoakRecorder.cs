using System.Text.Json;
using System.Text.Json.Serialization;
using TrMadenci.Core.Configuration;

namespace TrMadenci.Service.Mining;

internal sealed class MiningSoakRecorder : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = CreateOptions(false);
    private static readonly JsonSerializerOptions SummaryOptions = CreateOptions(true);
    private readonly object _gate = new();
    private readonly StreamWriter _events;
    private readonly CoinProfile _coin;
    private readonly MiningPauseController? _pauseController;
    private MiningStatusSnapshot? _lastSnapshot;
    private long _statusSamples;
    private uint? _maximumTemperatureC;
    private double? _maximumTotalPowerWatts;
    private long _maximumTotalRecoveries;
    private readonly Dictionary<string, ulong> _maximumGpuMemoryUsedBytes =
        new(StringComparer.OrdinalIgnoreCase);
    private bool _completed;

    public MiningSoakRecorder(
        CoinProfile coin,
        TimeSpan requestedDuration,
        string? outputDirectory = null,
        MiningPauseController? pauseController = null)
    {
        _coin = coin;
        _pauseController = pauseController;
        RequestedDuration = requestedDuration;
        StartedAt = DateTimeOffset.Now;
        var directory = Path.GetFullPath(outputDirectory ?? Path.Combine("artifacts", "soak"));
        Directory.CreateDirectory(directory);
        var stem = $"{coin.Ticker.ToLowerInvariant()}-soak-{StartedAt:yyyyMMdd-HHmmss}";
        EventsPath = Path.Combine(directory, stem + ".jsonl");
        SummaryPath = Path.Combine(directory, stem + ".summary.json");
        _events = new StreamWriter(EventsPath, append: false) { AutoFlush = true };
        Write(new
        {
            schemaVersion = 1,
            type = "start",
            timestamp = StartedAt,
            requestedDuration,
            processId = Environment.ProcessId,
            coin = coin.Ticker,
            network = coin.Network,
            algorithm = coin.Algorithm.ToUpperInvariant()
        });
    }

    public DateTimeOffset StartedAt { get; }
    public TimeSpan RequestedDuration { get; }
    public string EventsPath { get; }
    public string SummaryPath { get; }

    public void RecordEvent(string message) =>
        Write(new { schemaVersion = 1, type = "event", timestamp = DateTimeOffset.Now, message });

    public void RecordStatus(MiningStatusSnapshot snapshot)
    {
        lock (_gate)
        {
            _lastSnapshot = snapshot;
            _statusSamples++;
            var temperatures = snapshot.Gpus
                .Where(gpu => gpu.Telemetry?.TemperatureC is not null)
                .Select(gpu => gpu.Telemetry!.TemperatureC!.Value)
                .ToArray();
            if (temperatures.Length > 0)
            {
                var sampleMaximum = temperatures.Max();
                _maximumTemperatureC = Math.Max(_maximumTemperatureC ?? 0, sampleMaximum);
            }
            var powerValues = snapshot.Gpus
                .Where(gpu => gpu.Telemetry?.PowerWatts is not null)
                .Select(gpu => gpu.Telemetry!.PowerWatts!.Value)
                .ToArray();
            if (powerValues.Length > 0)
            {
                var sampleTotal = powerValues.Sum();
                _maximumTotalPowerWatts = Math.Max(_maximumTotalPowerWatts ?? 0, sampleTotal);
            }
            var totalRecoveries = snapshot.Gpus.Sum(gpu => gpu.RecoveryCount);
            _maximumTotalRecoveries = Math.Max(_maximumTotalRecoveries, totalRecoveries);
            foreach (var gpu in snapshot.Gpus.Where(gpu => gpu.Telemetry?.MemoryUsedBytes is not null))
            {
                var label = gpu.DeviceLabel ?? $"gpu:{gpu.DeviceIndex}";
                var usedBytes = gpu.Telemetry!.MemoryUsedBytes!.Value;
                _maximumGpuMemoryUsedBytes[label] = Math.Max(
                    _maximumGpuMemoryUsedBytes.GetValueOrDefault(label),
                    usedBytes);
            }
        }
        Write(new { schemaVersion = 1, type = "status", timestamp = snapshot.Timestamp, snapshot });
    }

    public void Complete(string outcome)
    {
        lock (_gate)
        {
            if (_completed)
                return;
            _completed = true;
            var finishedAt = DateTimeOffset.Now;
            var pause = _pauseController?.Snapshot ?? new MiningPauseSnapshot(false, 0, TimeSpan.Zero);
            var summary = new
            {
                schemaVersion = 1,
                coin = _coin.Ticker,
                network = _coin.Network,
                algorithm = _coin.Algorithm.ToUpperInvariant(),
                outcome,
                startedAt = StartedAt,
                finishedAt,
                requestedDuration = RequestedDuration,
                wallClockDuration = finishedAt - StartedAt,
                manualPauseCount = pause.PauseCount,
                manualPausedDuration = pause.TotalPausedDuration,
                finishedWhileManuallyPaused = pause.IsPaused,
                processId = Environment.ProcessId,
                statusSamples = _statusSamples,
                maximumTemperatureC = _maximumTemperatureC,
                maximumTotalPowerWatts = _maximumTotalPowerWatts,
                maximumTotalRecoveries = _maximumTotalRecoveries,
                maximumGpuMemoryUsedBytes = _maximumGpuMemoryUsedBytes,
                lastSnapshot = _lastSnapshot
            };
            _events.WriteLine(JsonSerializer.Serialize(
                new { schemaVersion = 1, type = "complete", timestamp = finishedAt, outcome },
                JsonOptions));
            File.WriteAllText(SummaryPath, JsonSerializer.Serialize(summary, SummaryOptions));
        }
    }

    private void Write<T>(T value)
    {
        lock (_gate)
            _events.WriteLine(JsonSerializer.Serialize(value, JsonOptions));
    }

    private static JsonSerializerOptions CreateOptions(bool indented)
    {
        var options = new JsonSerializerOptions { WriteIndented = indented };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    public void Dispose()
    {
        Complete("disposed");
        _events.Dispose();
    }
}
