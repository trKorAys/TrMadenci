using System.Text.Json;
using System.Text.Json.Serialization;

namespace TrMadenci.Service.Mining;

internal sealed class EtcHashSoakRecorder : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = CreateOptions(false);
    private static readonly JsonSerializerOptions SummaryOptions = CreateOptions(true);
    private readonly object _gate = new();
    private readonly StreamWriter _events;
    private MiningStatusSnapshot? _lastSnapshot;
    private bool _completed;

    public EtcHashSoakRecorder(TimeSpan requestedDuration)
    {
        RequestedDuration = requestedDuration;
        StartedAt = DateTimeOffset.Now;
        var directory = Path.GetFullPath(Path.Combine("artifacts", "soak"));
        Directory.CreateDirectory(directory);
        var stem = $"etchash-soak-{StartedAt:yyyyMMdd-HHmmss}";
        EventsPath = Path.Combine(directory, stem + ".jsonl");
        SummaryPath = Path.Combine(directory, stem + ".summary.json");
        _events = new StreamWriter(EventsPath, append: false) { AutoFlush = true };
        Write(new
        {
            type = "start",
            timestamp = StartedAt,
            requestedDuration,
            processId = Environment.ProcessId
        });
    }

    public DateTimeOffset StartedAt { get; }
    public TimeSpan RequestedDuration { get; }
    public string EventsPath { get; }
    public string SummaryPath { get; }

    public void RecordEvent(string message) =>
        Write(new { type = "event", timestamp = DateTimeOffset.Now, message });

    public void RecordStatus(MiningStatusSnapshot snapshot)
    {
        lock (_gate)
            _lastSnapshot = snapshot;
        Write(new { type = "status", timestamp = snapshot.Timestamp, snapshot });
    }

    public void Complete(string outcome)
    {
        lock (_gate)
        {
            if (_completed)
                return;
            _completed = true;
            var finishedAt = DateTimeOffset.Now;
            var summary = new
            {
                algorithm = "ETCHASH",
                outcome,
                startedAt = StartedAt,
                finishedAt,
                requestedDuration = RequestedDuration,
                wallClockDuration = finishedAt - StartedAt,
                processId = Environment.ProcessId,
                lastSnapshot = _lastSnapshot
            };
            _events.WriteLine(JsonSerializer.Serialize(
                new { type = "complete", timestamp = finishedAt, outcome }, JsonOptions));
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
