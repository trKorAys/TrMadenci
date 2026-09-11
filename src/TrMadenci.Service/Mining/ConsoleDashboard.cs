using System.Text;
using TrMadenci.Core.Configuration;

namespace TrMadenci.Service.Mining;

internal sealed class ConsoleDashboard : IDisposable
{
    private const int MaximumRetainedEvents = 1_000;
    private readonly object _gate = new();
    private readonly List<string> _events = [];
    private readonly MinerOptions _options;
    private readonly CoinProfile _coin;
    private MiningStatusSnapshot? _status;
    private bool _stopped;
    private bool _disposed;

    private ConsoleDashboard(MinerOptions options, CoinProfile coin)
    {
        _options = options;
        _coin = coin;
        Console.OutputEncoding = Encoding.UTF8;
        try
        {
            Console.CursorVisible = false;
            Console.Clear();
        }
        catch (IOException)
        {
            // Some terminal hosts do not expose cursor controls. Rendering still works.
        }
        Render();
    }

    public static ConsoleDashboard? CreateIfSupported(
        MinerOptions options, CoinProfile coin, bool enabled) =>
        enabled && !Console.IsOutputRedirected && !Console.IsErrorRedirected
            ? new ConsoleDashboard(options, coin)
            : null;

    public void Log(string message)
    {
        lock (_gate)
        {
            _events.Add($"{DateTimeOffset.Now:HH:mm:ss}  {message}");
            if (_events.Count > MaximumRetainedEvents)
                _events.RemoveRange(0, _events.Count - MaximumRetainedEvents);
            Render();
        }
    }

    public void Update(MiningStatusSnapshot status)
    {
        lock (_gate)
        {
            _status = status;
            Render();
        }
    }

    private void Render()
    {
        if (_disposed)
            return;

        try
        {
            var width = Math.Max(40, Console.WindowWidth);
            var height = Math.Max(12, Console.WindowHeight);
            var contentWidth = width - 1;
            var separator = new string('-', contentWidth);
            var header = BuildHeader(separator);
            var footer = BuildFooter(separator);
            var eventRows = Math.Max(1, height - header.Count - footer.Count);
            var visibleEvents = _events.TakeLast(eventRows).ToArray();

            var frame = new List<string>(height);
            frame.AddRange(header);
            frame.AddRange(visibleEvents);
            while (frame.Count < height - footer.Count)
                frame.Add(string.Empty);
            frame.AddRange(footer);

            Console.SetCursorPosition(0, 0);
            var output = new StringBuilder(height * width);
            for (var index = 0; index < height; index++)
            {
                var line = Fit(frame[index], contentWidth).PadRight(contentWidth);
                output.Append(line);
                if (index + 1 < height)
                    output.AppendLine();
            }
            Console.Write(output.ToString());
        }
        catch (Exception exception) when (exception is IOException or ArgumentOutOfRangeException)
        {
            // A concurrent terminal resize can temporarily invalidate cursor coordinates.
        }
    }

    private List<string> BuildHeader(string separator)
    {
        var status = _status;
        var accepted = status?.AcceptedShares ?? 0;
        var rejected = status?.RejectedShares ?? 0;
        var invalid = status?.InvalidShares ?? 0;
        var submitted = accepted + rejected;
        var acceptance = submitted > 0 ? accepted * 100d / submitted : 0;
        var pool = status?.Pool ?? $"{_options.Pool.Host}:{_options.Pool.Port}";
        var beneficiary = status?.Beneficiary.ToString() ?? "User";
        var health = Health(status);

        return
        [
            "TrMadenci | KAWPOW Mining Dashboard",
            $"Coin / Ağ  : {_coin.Name} ({_coin.Ticker}) | {_coin.Network} | Algoritma: {_coin.Algorithm.ToUpperInvariant()}",
            $"Havuz      : {pool} | Hedef: {beneficiary} | Sağlık: {health}",
            $"Oturum     : {Duration(status?.SessionElapsed ?? TimeSpan.Zero)} | Aktif: {Duration(status?.ActiveMiningTime ?? TimeSpan.Zero)} | Anlık: {(status?.CurrentHashesPerSecond ?? 0) / 1_000_000:F2} MH/s",
            $"Geçmiş     : {accepted} kabul / {rejected} ret / {invalid} invalid | Kabul: %{acceptance:F2} | Toplam hash: {Compact(status?.TotalHashes ?? 0)}",
            $"Ortalama   : {(status?.AverageHashesPerSecond ?? 0) / 1_000_000:F2} MH/s | Enerji: {status?.SessionEnergyKwh ?? 0:F3} kWh | Ort. güç: {Power(status?.AveragePowerWatts)} | Günlük: {ProjectedEnergy(status)}",
            separator,
            "AKTİF İŞLEMLER / GEÇMİŞ OLAYLAR"
        ];
    }

    private List<string> BuildFooter(string separator)
    {
        var statuses = _status?.Gpus ?? [];
        var activePower = statuses
            .Where(gpu => gpu.Telemetry?.PowerWatts is not null)
            .Sum(gpu => gpu.Telemetry!.PowerWatts!.Value);
        var powerAvailable = statuses.Any(gpu => gpu.Telemetry?.PowerWatts is not null);
        var gpuHealth = statuses.Count == 0
            ? "GPU verisi bekleniyor"
            : string.Join(" | ", statuses.Select(CompactGpu));

        if (_stopped)
        {
            return
            [
                separator,
                $"MADENCİLİK DURDU | Son güç: {(powerAvailable ? $"{activePower:F1} W" : "n/a")} | {gpuHealth}"
            ];
        }

        return
        [
            separator,
            $"AKTİF GÜÇ: {(powerAvailable ? $"{activePower:F1} W" : "n/a")} | {gpuHealth} | Ctrl+C: durdur"
        ];
    }

    private static string CompactGpu(GpuMiningStatus gpu)
    {
        var temperature = gpu.Telemetry?.TemperatureC is { } temp ? $"{temp}C" : "n/a";
        var fan = gpu.Telemetry?.FanPercent is { } fanPercent ? $"fan {fanPercent}%" : "fan n/a";
        var activity = gpu.IsPreparing ? "hazırlanıyor" : $"{gpu.HashesPerSecond / 1_000_000:F2} MH/s";
        return $"GPU{gpu.DeviceIndex} {activity} {temperature} {fan}";
    }

    private string Health(MiningStatusSnapshot? status)
    {
        if (_stopped)
            return "DURDU";
        if (status is null)
            return "HAZIRLANIYOR";
        var temperatures = status.Gpus
            .Where(gpu => gpu.Telemetry?.TemperatureC is not null)
            .Select(gpu => gpu.Telemetry!.TemperatureC!.Value)
            .ToArray();
        if (temperatures.Any(temperature => temperature >= 85))
            return "KRITIK SICAKLIK";
        if (temperatures.Any(temperature => temperature >= 78))
            return "SICAK";
        if (status.Gpus.Any(gpu => gpu.IsPreparing))
            return "HAZIRLANIYOR";
        return status.CurrentHashesPerSecond > 0 ? "SAGLIKLI" : "BEKLIYOR";
    }

    private static string ProjectedEnergy(MiningStatusSnapshot? status) =>
        status?.AveragePowerWatts is { } average
            ? $"{average * 24 / 1000:F2} kWh"
            : "n/a";

    private static string Power(double? watts) => watts is { } value ? $"{value:F1} W" : "n/a";

    private static string Duration(TimeSpan value) =>
        value.TotalDays >= 1
            ? $"{(int)value.TotalDays}g {value:hh\\:mm\\:ss}"
            : value.ToString("hh\\:mm\\:ss");

    private static string Compact(ulong value) => value switch
    {
        >= 1_000_000_000_000 => $"{value / 1_000_000_000_000d:F2}T",
        >= 1_000_000_000 => $"{value / 1_000_000_000d:F2}G",
        >= 1_000_000 => $"{value / 1_000_000d:F2}M",
        >= 1_000 => $"{value / 1_000d:F2}K",
        _ => value.ToString()
    };

    private static string Fit(string value, int width) =>
        value.Length <= width ? value : value[..Math.Max(0, width - 1)] + "…";

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _stopped = true;
            Render();
            _disposed = true;
            try
            {
                Console.CursorVisible = true;
                Console.SetCursorPosition(0, Math.Max(0, Console.WindowHeight - 1));
                Console.WriteLine();
            }
            catch (IOException)
            {
            }
        }
    }
}
