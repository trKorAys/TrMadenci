using TrMadenci.Core.Algorithms;
using TrMadenci.NativeBridge;
using TrMadenci.Protocols.Stratum;

namespace TrMadenci.Service.Mining;

internal static class OctopusShareVerifier
{
    public static bool Verify(OctopusJob job, CudaShare share, ReadOnlySpan<byte> target)
    {
        if (target.Length != 32 || share.FinalHash.Length != 32)
            return false;

        var multiPoint = OctopusMultiPoint.Evaluate(job.HeaderHash, share.Nonce);
        var reference = NativeDiagnostics.ComputeOctopusReferenceHash(
            job.BlockHeight,
            job.HeaderHash,
            share.Nonce,
            multiPoint.Compressed,
            multiPoint.Points.ToArray());
        return reference.AsSpan().SequenceEqual(share.FinalHash) && MeetsTarget(reference, target);
    }

    internal static bool MeetsTarget(ReadOnlySpan<byte> hash, ReadOnlySpan<byte> target)
    {
        if (hash.Length != 32 || target.Length != 32)
            return false;
        for (var index = 0; index < 32; index++)
        {
            if (hash[index] < target[index])
                return true;
            if (hash[index] > target[index])
                return false;
        }
        return true;
    }
}
