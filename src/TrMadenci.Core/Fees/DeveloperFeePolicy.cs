namespace TrMadenci.Core.Fees;

public sealed record DeveloperFeePolicy
{
    public bool Enabled { get; init; } = true;
    public decimal Rate { get; init; } = 0.0075m;
    public TimeSpan MinimumWindow { get; init; } = TimeSpan.FromSeconds(20);
    public TimeSpan MaximumWindow { get; init; } = TimeSpan.FromSeconds(40);

    public void Validate()
    {
        if (Rate is < 0m or > 0.05m)
            throw new ArgumentOutOfRangeException(nameof(Rate), "Developer fee rate must be between 0% and 5%.");

        if (MinimumWindow <= TimeSpan.Zero || MaximumWindow < MinimumWindow)
            throw new ArgumentException("Developer fee window bounds are invalid.");
    }
}
