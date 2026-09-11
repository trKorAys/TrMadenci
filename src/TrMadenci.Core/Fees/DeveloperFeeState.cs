namespace TrMadenci.Core.Fees;

public sealed record DeveloperFeeState(
    TimeSpan AccruedFee,
    TimeSpan TargetWindow,
    TimeSpan RemainingWindow,
    MiningBeneficiary Beneficiary);
