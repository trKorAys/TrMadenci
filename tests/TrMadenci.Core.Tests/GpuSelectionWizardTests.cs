using System.Text.Json;
using TrMadenci.NativeBridge;
using TrMadenci.Service.Mining;

namespace TrMadenci.Core.Tests;

public sealed class GpuSelectionWizardTests
{
    [Fact]
    public async Task Selecting_one_of_multiple_gpus_saves_modern_device_id_and_preserves_pool()
    {
        var (directory, path) = CreateConfiguration("auto", "[0]");
        try
        {
            var cuda = Backend(
                ComputeBackendKind.Cuda,
                Device(ComputeBackendKind.Cuda, 0, 0, "RTX 3060"),
                Device(ComputeBackendKind.Cuda, 0, 1, "RTX 3070"));
            var openCl = Backend(ComputeBackendKind.OpenCl, throwOnEnumeration: true);
            var output = new StringWriter();

            var outcome = await GpuSelectionWizard.RunAsync(
                path, new StringReader("2\n"), output, cuda, openCl);

            Assert.Equal(GpuSelectionOutcome.Saved, outcome);
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path));
            var root = document.RootElement;
            Assert.Equal("cuda", root.GetProperty("computeBackend").GetString());
            Assert.Equal(
                ["cuda:1"],
                root.GetProperty("computeDevices").EnumerateArray()
                    .Select(item => item.GetString()!).ToArray());
            Assert.Empty(root.GetProperty("gpuDevices").EnumerateArray());
            Assert.Equal("KorayAltiner.Milena", root.GetProperty("pool")
                .GetProperty("username").GetString());
            Assert.Contains("RTX 3060", output.ToString(), StringComparison.Ordinal);
            Assert.Contains("RTX 3070", output.ToString(), StringComparison.Ordinal);
            Assert.Equal(0, openCl.EnumerationCount);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task All_selects_every_discovered_gpu_in_display_order()
    {
        var (directory, path) = CreateConfiguration("cuda", "[]");
        try
        {
            var cuda = Backend(
                ComputeBackendKind.Cuda,
                Device(ComputeBackendKind.Cuda, 0, 2, "GPU C"),
                Device(ComputeBackendKind.Cuda, 0, 0, "GPU A"));

            await GpuSelectionWizard.RunAsync(
                path,
                new StringReader("A\n"),
                new StringWriter(),
                cuda,
                Backend(ComputeBackendKind.OpenCl));

            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path));
            Assert.Equal(
                ["cuda:2", "cuda:0"],
                document.RootElement.GetProperty("computeDevices").EnumerateArray()
                    .Select(item => item.GetString()!).ToArray());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Auto_falls_back_to_opencl_and_records_backend()
    {
        var (directory, path) = CreateConfiguration("auto", "[]");
        try
        {
            var outcome = await GpuSelectionWizard.RunAsync(
                path,
                new StringReader("1\n"),
                new StringWriter(),
                Backend(ComputeBackendKind.Cuda),
                Backend(
                    ComputeBackendKind.OpenCl,
                    Device(ComputeBackendKind.OpenCl, 1, 3, "Intel Arc")));

            Assert.Equal(GpuSelectionOutcome.Saved, outcome);
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path));
            Assert.Equal("openCl", document.RootElement.GetProperty("computeBackend").GetString());
            Assert.Equal(
                "opencl:1:3",
                document.RootElement.GetProperty("computeDevices")[0].GetString());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Cancel_does_not_change_configuration()
    {
        var (directory, path) = CreateConfiguration("auto", "[0]");
        try
        {
            var before = await File.ReadAllTextAsync(path);

            var outcome = await GpuSelectionWizard.RunAsync(
                path,
                new StringReader("q\n"),
                new StringWriter(),
                Backend(ComputeBackendKind.Cuda, Device(ComputeBackendKind.Cuda, 0, 0, "GPU")),
                Backend(ComputeBackendKind.OpenCl));

            Assert.Equal(GpuSelectionOutcome.Cancelled, outcome);
            Assert.Equal(before, await File.ReadAllTextAsync(path));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static (string Directory, string Path) CreateConfiguration(
        string backend,
        string gpuDevices)
    {
        var directory = Path.Combine(
            Path.GetTempPath(), "TrMadenciGpuSelectionTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "trmadenci.json");
        File.WriteAllText(path, $$"""
            {
              "coin": "rvn",
              "algorithm": "kawpow",
              "pool": {
                "host": "pool.example",
                "port": 1234,
                "username": "KorayAltiner.Milena",
                "password": "x"
              },
              "computeBackend": "{{backend}}",
              "computeDevices": [],
              "gpuDevices": {{gpuDevices}}
            }
            """);
        return (directory, path);
    }

    private static FakeBackend Backend(
        ComputeBackendKind kind,
        params ComputeDeviceInfo[] devices) =>
        new(kind, devices, false);

    private static FakeBackend Backend(
        ComputeBackendKind kind,
        bool throwOnEnumeration) =>
        new(kind, [], throwOnEnumeration);

    private static ComputeDeviceInfo Device(
        ComputeBackendKind kind,
        int platform,
        int index,
        string name) =>
        new(
            new ComputeDeviceId(kind, platform, index),
            name,
            kind == ComputeBackendKind.Cuda ? "NVIDIA" : "Intel",
            "test runtime",
            8UL * 1024 * 1024 * 1024,
            1);

    private sealed class FakeBackend(
        ComputeBackendKind kind,
        IReadOnlyList<ComputeDeviceInfo> devices,
        bool throwOnEnumeration) : IComputeBackend
    {
        public ComputeBackendKind Kind => kind;
        public int EnumerationCount { get; private set; }

        public IReadOnlyList<ComputeDeviceInfo> GetDevices()
        {
            EnumerationCount++;
            if (throwOnEnumeration)
                throw new InvalidOperationException("Backend must not be enumerated.");
            return devices;
        }
    }
}
