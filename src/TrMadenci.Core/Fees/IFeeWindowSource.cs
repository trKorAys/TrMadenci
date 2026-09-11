namespace TrMadenci.Core.Fees;

public interface IFeeWindowSource
{
    TimeSpan Next(TimeSpan minimum, TimeSpan maximum);
}
