using System.Security.Cryptography;

namespace TrMadenci.Core.Fees;

public sealed class CryptographicFeeWindowSource : IFeeWindowSource
{
    public TimeSpan Next(TimeSpan minimum, TimeSpan maximum)
    {
        var minSeconds = checked((int)Math.Ceiling(minimum.TotalSeconds));
        var maxSeconds = checked((int)Math.Floor(maximum.TotalSeconds));

        if (minSeconds == maxSeconds)
            return TimeSpan.FromSeconds(minSeconds);

        return TimeSpan.FromSeconds(RandomNumberGenerator.GetInt32(minSeconds, maxSeconds + 1));
    }
}
