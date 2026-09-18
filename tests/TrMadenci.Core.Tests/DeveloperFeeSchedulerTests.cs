using TrMadenci.Core.Configuration;
using TrMadenci.Core.Fees;

namespace TrMadenci.Core.Tests;

public sealed class DeveloperFeeSchedulerTests
{
    [Fact]
    public void Short_session_never_enters_fee_window()
    {
        var scheduler = CreateScheduler(rate: 0.01m, window: TimeSpan.FromSeconds(30));

        scheduler.Record(TimeSpan.FromMinutes(20));

        Assert.Equal(MiningBeneficiary.User, scheduler.Beneficiary);
    }

    [Fact]
    public void Fee_starts_only_after_enough_time_has_accrued()
    {
        var scheduler = CreateScheduler(rate: 0.01m, window: TimeSpan.FromSeconds(30));

        scheduler.Record(TimeSpan.FromSeconds(2_970));

        Assert.Equal(MiningBeneficiary.Developer, scheduler.Beneficiary);
        Assert.Equal(TimeSpan.FromSeconds(30), scheduler.Snapshot().RemainingWindow);
    }

    [Fact]
    public void Completed_window_returns_to_user_and_preserves_ratio()
    {
        var scheduler = CreateScheduler(rate: 0.01m, window: TimeSpan.FromSeconds(30));
        scheduler.Record(TimeSpan.FromSeconds(2_970));

        scheduler.Record(TimeSpan.FromSeconds(30));

        Assert.Equal(MiningBeneficiary.User, scheduler.Beneficiary);
        Assert.Equal(TimeSpan.Zero, scheduler.Snapshot().AccruedFee);
    }

    [Fact]
    public void Snapshot_can_be_restored_without_resetting_accrual()
    {
        var first = CreateScheduler(rate: 0.01m, window: TimeSpan.FromSeconds(30));
        first.Record(TimeSpan.FromSeconds(2_000));

        var restored = new DeveloperFeeScheduler(
            Policy(0.01m, TimeSpan.FromSeconds(30)),
            new FixedWindowSource(TimeSpan.FromSeconds(30)),
            first.Snapshot());
        restored.Record(TimeSpan.FromSeconds(970));

        Assert.Equal(MiningBeneficiary.Developer, restored.Beneficiary);
    }

    [Fact]
    public void Cryptographic_windows_always_stay_inside_product_policy_bounds()
    {
        var source = new CryptographicFeeWindowSource();
        var observed = new HashSet<TimeSpan>();

        for (var index = 0; index < 128; index++)
        {
            var window = source.Next(
                ProductPolicy.MinimumDeveloperWindow,
                ProductPolicy.MaximumDeveloperWindow);
            Assert.InRange(
                window,
                ProductPolicy.MinimumDeveloperWindow,
                ProductPolicy.MaximumDeveloperWindow);
            observed.Add(window);
        }

        Assert.True(observed.Count > 1, "Cryptographic window selection unexpectedly produced one fixed duration.");
    }

    private static DeveloperFeeScheduler CreateScheduler(decimal rate, TimeSpan window) =>
        new(Policy(rate, window), new FixedWindowSource(window));

    private static DeveloperFeePolicy Policy(decimal rate, TimeSpan window) => new()
    {
        Rate = rate,
        MinimumWindow = window,
        MaximumWindow = window
    };

    private sealed class FixedWindowSource(TimeSpan window) : IFeeWindowSource
    {
        public TimeSpan Next(TimeSpan minimum, TimeSpan maximum) => window;
    }
}
