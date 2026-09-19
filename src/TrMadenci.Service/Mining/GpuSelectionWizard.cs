using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using TrMadenci.Core.Configuration;
using TrMadenci.NativeBridge;

namespace TrMadenci.Service.Mining;

internal enum GpuSelectionOutcome
{
    Saved,
    Cancelled
}

internal static class GpuSelectionWizard
{
    private static readonly JsonSerializerOptions ConfigurationOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true
    };

    public static async Task<GpuSelectionOutcome> RunAsync(
        string configurationPath,
        TextReader input,
        TextWriter output,
        IComputeBackend cuda,
        IComputeBackend openCl,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configurationPath);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(cuda);
        ArgumentNullException.ThrowIfNull(openCl);

        var fullPath = Path.GetFullPath(configurationPath);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException("Mining configuration was not found.", fullPath);

        var json = await File.ReadAllTextAsync(fullPath, cancellationToken);
        var root = JsonNode.Parse(json) as JsonObject ??
            throw new JsonException("Mining configuration must be a JSON object.");
        var options = JsonSerializer.Deserialize<MinerOptions>(json, ConfigurationOptions) ??
            throw new JsonException("Mining configuration is empty.");
        options.Validate();
        if (options.ComputeBackend == ComputeBackendMode.Cpu)
            throw new InvalidOperationException(
                "CPU mining does not use the GPU selection wizard; set cpuThreads in the configuration.");

        var (backend, devices) = Discover(options.ComputeBackend, cuda, openCl);
        var current = CurrentSelection(options, backend, devices);

        await output.WriteLineAsync("TrMadenci GPU seçimi (madencilik başlatılmaz)");
        await output.WriteLineAsync($"Yapılandırma: {fullPath}");
        await output.WriteLineAsync($"Backend: {BackendName(backend)}");
        if (backend == ComputeBackendKind.OpenCl)
            await output.WriteLineAsync(
                "Not: OpenCL normal madencilik yolu imzalı donanım yeterliliği tamamlanana kadar kapalıdır.");
        for (var index = 0; index < devices.Count; index++)
        {
            var device = devices[index];
            var selected = current.Contains(device.Id) ? " [seçili]" : string.Empty;
            await output.WriteLineAsync(
                $"  {index + 1}) {device.Id} | {device.Name} | " +
                $"{device.TotalMemoryBytes / 1024d / 1024 / 1024:F2} GiB{selected}");
        }

        var defaultSelection = DefaultSelection(options, backend, devices);
        if (defaultSelection.Count == 0 && HasExplicitSelection(options))
            await output.WriteLineAsync(
                "Uyarı: Kayıtlı GPU seçimi mevcut donanımla eşleşmiyor; yeni bir seçim yapın.");

        while (true)
        {
            await output.WriteAsync(
                "GPU sıra numaralarını virgülle ayırın; A=tümü, Enter=mevcut seçim, Q=iptal: ");
            var response = await input.ReadLineAsync(cancellationToken);
            if (response is null)
                throw new EndOfStreamException("GPU selection input was closed.");

            var normalized = response.Trim();
            if (string.Equals(normalized, "q", StringComparison.OrdinalIgnoreCase))
            {
                await output.WriteLineAsync("GPU seçimi iptal edildi; yapılandırma değiştirilmedi.");
                return GpuSelectionOutcome.Cancelled;
            }

            IReadOnlyList<ComputeDeviceInfo>? selected;
            if (string.Equals(normalized, "a", StringComparison.OrdinalIgnoreCase))
                selected = devices;
            else if (normalized.Length == 0)
                selected = defaultSelection.Count > 0 ? defaultSelection : null;
            else
                selected = ParseSelection(normalized, devices);

            if (selected is null || selected.Count == 0)
            {
                await output.WriteLineAsync(
                    $"Geçersiz seçim. 1-{devices.Count} arasında benzersiz sıra numaraları kullanın.");
                continue;
            }

            SetProperty(root, "computeBackend", JsonValue.Create(
                backend == ComputeBackendKind.Cuda ? "cuda" : "openCl"));
            SetProperty(root, "computeDevices", new JsonArray(
                selected.Select(device => JsonValue.Create(device.Id.ToString())).ToArray()));
            SetProperty(root, "gpuDevices", new JsonArray());
            await WriteAtomicallyAsync(fullPath, root.ToJsonString(WriteOptions), cancellationToken);

            await output.WriteLineAsync(
                $"GPU seçimi kaydedildi: {string.Join(", ", selected.Select(device => device.Id))}");
            return GpuSelectionOutcome.Saved;
        }
    }

    private static (ComputeBackendKind Backend, IReadOnlyList<ComputeDeviceInfo> Devices) Discover(
        ComputeBackendMode requested,
        IComputeBackend cuda,
        IComputeBackend openCl)
    {
        if (cuda.Kind != ComputeBackendKind.Cuda || openCl.Kind != ComputeBackendKind.OpenCl)
            throw new ArgumentException("Compute backends were supplied in the wrong role.");

        if (requested != ComputeBackendMode.OpenCl)
        {
            var cudaDevices = cuda.GetDevices();
            if (cudaDevices.Count > 0)
                return (ComputeBackendKind.Cuda, cudaDevices);
            if (requested == ComputeBackendMode.Cuda)
                throw new InvalidOperationException("No CUDA GPU is available.");
        }

        var openClDevices = openCl.GetDevices();
        if (openClDevices.Count == 0)
            throw new InvalidOperationException("No compatible GPU is available.");
        return (ComputeBackendKind.OpenCl, openClDevices);
    }

    private static HashSet<ComputeDeviceId> CurrentSelection(
        MinerOptions options,
        ComputeBackendKind backend,
        IReadOnlyList<ComputeDeviceInfo> devices)
    {
        var requested = RequestedIds(options, backend);
        return requested.Count == 0
            ? devices.Select(device => device.Id).ToHashSet()
            : requested.ToHashSet();
    }

    private static IReadOnlyList<ComputeDeviceInfo> DefaultSelection(
        MinerOptions options,
        ComputeBackendKind backend,
        IReadOnlyList<ComputeDeviceInfo> devices)
    {
        var requested = RequestedIds(options, backend);
        if (requested.Count == 0)
            return devices;
        var available = devices.ToDictionary(device => device.Id);
        return requested.All(available.ContainsKey)
            ? requested.Select(id => available[id]).ToArray()
            : [];
    }

    private static IReadOnlyList<ComputeDeviceId> RequestedIds(
        MinerOptions options,
        ComputeBackendKind backend)
    {
        if (options.ComputeDevices.Length > 0)
        {
            var requested = options.ComputeDevices.Select(ComputeDeviceId.Parse)
                .Where(id => id.Backend == backend)
                .ToArray();
            return requested;
        }

        return backend == ComputeBackendKind.Cuda
            ? options.GpuDevices.Select(index =>
                new ComputeDeviceId(ComputeBackendKind.Cuda, 0, index)).ToArray()
            : [];
    }

    private static bool HasExplicitSelection(MinerOptions options) =>
        options.ComputeDevices.Length > 0 || options.GpuDevices.Length > 0;

    private static IReadOnlyList<ComputeDeviceInfo>? ParseSelection(
        string value,
        IReadOnlyList<ComputeDeviceInfo> devices)
    {
        var fields = value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length == 0)
            return null;

        var indexes = new List<int>(fields.Length);
        foreach (var field in fields)
        {
            if (!int.TryParse(field, out var oneBased) || oneBased < 1 || oneBased > devices.Count)
                return null;
            var index = oneBased - 1;
            if (!indexes.Contains(index))
                indexes.Add(index);
        }
        return indexes.Select(index => devices[index]).ToArray();
    }

    private static void SetProperty(JsonObject root, string name, JsonNode? value)
    {
        var existing = root.Select(property => property.Key)
            .FirstOrDefault(key => string.Equals(key, name, StringComparison.OrdinalIgnoreCase));
        root[existing ?? name] = value;
    }

    private static async Task WriteAtomicallyAsync(
        string path,
        string json,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(path) ?? Directory.GetCurrentDirectory();
        var temporaryPath = Path.Combine(
            directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllTextAsync(
                temporaryPath, json + Environment.NewLine, new UTF8Encoding(false), cancellationToken);
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    private static string BackendName(ComputeBackendKind backend) =>
        backend == ComputeBackendKind.Cuda ? "CUDA" : "OpenCL";
}
