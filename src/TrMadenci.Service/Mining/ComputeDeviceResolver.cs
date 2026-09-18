using TrMadenci.Core.Configuration;
using TrMadenci.NativeBridge;

namespace TrMadenci.Service.Mining;

internal static class ComputeDeviceResolver
{
    public static IReadOnlyList<ComputeDeviceInfo> Resolve(
        MinerOptions options,
        IComputeBackend cuda,
        IComputeBackend openCl)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(cuda);
        ArgumentNullException.ThrowIfNull(openCl);
        if (cuda.Kind != ComputeBackendKind.Cuda || openCl.Kind != ComputeBackendKind.OpenCl)
            throw new ArgumentException("Compute backends were supplied in the wrong role.");
        options.Validate();

        return options.ComputeBackend switch
        {
            ComputeBackendMode.Cuda => ResolveRequested(
                cuda.GetDevices(), RequestedCudaIds(options), "CUDA"),
            ComputeBackendMode.OpenCl => ResolveRequested(
                openCl.GetDevices(), RequestedOpenClIds(options), "OpenCL"),
            ComputeBackendMode.Auto => ResolveAuto(options, cuda, openCl),
            _ => throw new NotSupportedException($"Unsupported compute backend: {options.ComputeBackend}.")
        };
    }

    private static IReadOnlyList<ComputeDeviceInfo> ResolveAuto(
        MinerOptions options,
        IComputeBackend cuda,
        IComputeBackend openCl)
    {
        var cudaDevices = cuda.GetDevices();
        if (cudaDevices.Count > 0)
            return ResolveRequested(cudaDevices, RequestedCudaIds(options), "CUDA");
        if (options.GpuDevices.Length > 0)
            throw new InvalidOperationException(
                "Legacy gpuDevices explicitly select CUDA indexes, but no CUDA device is available.");
        return ResolveRequested(openCl.GetDevices(), [], "OpenCL");
    }

    private static IReadOnlyList<ComputeDeviceInfo> ResolveRequested(
        IReadOnlyList<ComputeDeviceInfo> available,
        IReadOnlyList<ComputeDeviceId> requested,
        string backendName)
    {
        if (available.Count == 0)
            throw new InvalidOperationException($"No {backendName} GPU is available.");
        if (requested.Count == 0)
            return available.ToArray();

        var byId = available.ToDictionary(device => device.Id);
        var selected = new List<ComputeDeviceInfo>(requested.Count);
        foreach (var id in requested)
        {
            if (!byId.TryGetValue(id, out var device))
                throw new InvalidOperationException(
                    $"Selected compute device '{id}' was not found in the {backendName} runtime.");
            selected.Add(device);
        }
        return selected;
    }

    private static IReadOnlyList<ComputeDeviceId> RequestedCudaIds(MinerOptions options) =>
        options.ComputeDevices.Length > 0
            ? options.ComputeDevices.Select(ComputeDeviceId.Parse).ToArray()
            : options.GpuDevices.Select(index =>
                new ComputeDeviceId(ComputeBackendKind.Cuda, 0, index)).ToArray();

    private static IReadOnlyList<ComputeDeviceId> RequestedOpenClIds(MinerOptions options) =>
        options.ComputeDevices.Select(ComputeDeviceId.Parse).ToArray();
}
