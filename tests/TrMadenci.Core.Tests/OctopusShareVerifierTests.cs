using TrMadenci.NativeBridge;
using TrMadenci.Protocols.Stratum;
using TrMadenci.Service.Mining;

namespace TrMadenci.Core.Tests;

public sealed class OctopusShareVerifierTests
{
    private const ulong Nonce = 0x2333333320;
    private static readonly byte[] Header = Convert.FromHexString(
        "4D99D0B41C7EB0DD1A801C35AAE2DF28AE6B53BC7743F0818A34B6EC97F5B4AE");
    private static readonly byte[] ExpectedHash = Convert.FromHexString(
        "D45C965D3707E27A42995132637854234385CBF5626897259F1EE980554DDD5C");

    [Fact]
    public void Known_cpu_verified_share_is_accepted_locally()
    {
        var job = Job();
        var share = new CudaShare(Nonce, new byte[32], ExpectedHash);

        Assert.True(OctopusShareVerifier.Verify(job, share, Enumerable.Repeat((byte)0xff, 32).ToArray()));
    }

    [Fact]
    public void Mismatched_gpu_hash_is_never_accepted_locally()
    {
        var invalidHash = ExpectedHash.ToArray();
        invalidHash[31] ^= 1;

        Assert.False(OctopusShareVerifier.Verify(
            Job(),
            new CudaShare(Nonce, new byte[32], invalidHash),
            Enumerable.Repeat((byte)0xff, 32).ToArray()));
    }

    [Fact]
    public void Valid_hash_above_job_boundary_is_never_accepted_locally()
    {
        Assert.False(OctopusShareVerifier.Verify(
            Job(),
            new CudaShare(Nonce, new byte[32], ExpectedHash),
            new byte[32]));
    }

    private static OctopusJob Job() =>
        new("known-vector", 2, Header, Enumerable.Repeat((byte)0xff, 32).ToArray());
}
