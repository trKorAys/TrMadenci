using TrMadenci.Core.Configuration;
using TrMadenci.NativeBridge;
using TrMadenci.Service.Mining;

namespace TrMadenci.Core.Tests;

public sealed class OpenClQualificationGateTests
{
    [Fact]
    public void Complete_explicit_request_is_allowed()
    {
        OpenClQualificationGate.ValidateRequest(
            true, true, "etchash", ComputeBackendMode.OpenCl, true);
    }

    [Theory]
    [InlineData(false, "etchash", ComputeBackendMode.OpenCl, true)]
    [InlineData(true, "kawpow", ComputeBackendMode.OpenCl, true)]
    [InlineData(true, "etchash", ComputeBackendMode.Cuda, true)]
    [InlineData(true, "etchash", ComputeBackendMode.OpenCl, false)]
    public void Incomplete_request_is_rejected(
        bool mine,
        string algorithm,
        ComputeBackendMode backend,
        bool selfTest)
    {
        Assert.Throws<ArgumentException>(() => OpenClQualificationGate.ValidateRequest(
            true, mine, algorithm, backend, selfTest));
    }

    [Fact]
    public void Every_selected_device_must_have_passed_in_the_same_process()
    {
        ComputeDeviceId[] selected =
        [
            new(ComputeBackendKind.OpenCl, 0, 0),
            new(ComputeBackendKind.OpenCl, 1, 0)
        ];
        HashSet<ComputeDeviceId> qualified = [selected[0]];

        var exception = Assert.Throws<InvalidOperationException>(() =>
            OpenClQualificationGate.EnsureSelectedDevicesPassed(selected, qualified));

        Assert.Contains("opencl:1:0", exception.Message, StringComparison.Ordinal);
    }
}
