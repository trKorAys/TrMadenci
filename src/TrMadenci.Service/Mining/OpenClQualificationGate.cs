using TrMadenci.Core.Configuration;
using TrMadenci.NativeBridge;

namespace TrMadenci.Service.Mining;

internal static class OpenClQualificationGate
{
    public static void ValidateRequest(
        bool qualificationRequested,
        bool mineRequested,
        string algorithm,
        ComputeBackendMode backend,
        bool nonceSelfTestRequested)
    {
        if (!qualificationRequested)
            return;
        if (!mineRequested ||
            !string.Equals(algorithm, "etchash", StringComparison.OrdinalIgnoreCase) ||
            backend != ComputeBackendMode.OpenCl ||
            !nonceSelfTestRequested)
            throw new ArgumentException(
                "--etchash-opencl-qualification requires --mine, an ETCHash profile, " +
                "computeBackend 'openCl', and --etchash-opencl-nonce-self-test in the same run.");
    }

    public static void EnsureSelectedDevicesPassed(
        IReadOnlyCollection<ComputeDeviceId> selected,
        IReadOnlySet<ComputeDeviceId> qualified)
    {
        if (selected.Count == 0)
            throw new InvalidOperationException("OpenCL qualification requires at least one selected GPU.");
        var missing = selected.Where(device => !qualified.Contains(device)).ToArray();
        if (missing.Length > 0)
            throw new InvalidOperationException(
                "OpenCL qualification cannot start because these selected devices did not pass " +
                $"the nonce vector in this process: {string.Join(", ", missing.Select(device => device.ToString()))}.");
    }
}
