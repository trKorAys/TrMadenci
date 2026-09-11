namespace TrMadenci.Core.Fees;

/// <summary>
/// Randomizes fee window lengths while keeping the configured long-run ratio.
/// State is serializable so restarting the process cannot reset or duplicate debt.
/// </summary>
public sealed class DeveloperFeeScheduler
{
    private readonly DeveloperFeePolicy _policy;
    private readonly IFeeWindowSource _windowSource;
    private TimeSpan _accruedFee;
    private TimeSpan _targetWindow;
    private TimeSpan _remainingWindow;
    private MiningBeneficiary _beneficiary;

    public DeveloperFeeScheduler(
        DeveloperFeePolicy policy,
        IFeeWindowSource? windowSource = null,
        DeveloperFeeState? state = null)
    {
        policy.Validate();
        _policy = policy;
        _windowSource = windowSource ?? new CryptographicFeeWindowSource();

        if (state is null)
        {
            _targetWindow = NextWindow();
            _beneficiary = MiningBeneficiary.User;
        }
        else
        {
            _accruedFee = NonNegative(state.AccruedFee);
            _targetWindow = state.TargetWindow > TimeSpan.Zero ? state.TargetWindow : NextWindow();
            _remainingWindow = NonNegative(state.RemainingWindow);
            _beneficiary = state.Beneficiary;
        }

        Normalize();
    }

    public MiningBeneficiary Beneficiary => _beneficiary;

    public DeveloperFeeState Snapshot() =>
        new(_accruedFee, _targetWindow, _remainingWindow, _beneficiary);

    public MiningBeneficiary Record(TimeSpan elapsed)
    {
        if (elapsed < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(elapsed));

        if (!_policy.Enabled || _policy.Rate == 0m)
            return _beneficiary = MiningBeneficiary.User;

        if (_beneficiary == MiningBeneficiary.User)
        {
            // p of total time means p/(1-p) seconds become due per user second.
            var debtRatio = _policy.Rate / (1m - _policy.Rate);
            _accruedFee += Scale(elapsed, debtRatio);
        }
        else
        {
            _remainingWindow -= elapsed;
            _accruedFee -= elapsed;
        }

        Normalize();
        return _beneficiary;
    }

    private void Normalize()
    {
        _accruedFee = NonNegative(_accruedFee);
        _remainingWindow = NonNegative(_remainingWindow);

        if (!_policy.Enabled || _policy.Rate == 0m)
        {
            _beneficiary = MiningBeneficiary.User;
            _remainingWindow = TimeSpan.Zero;
            return;
        }

        if (_beneficiary == MiningBeneficiary.Developer && _remainingWindow == TimeSpan.Zero)
        {
            _beneficiary = MiningBeneficiary.User;
            _targetWindow = NextWindow();
        }

        if (_beneficiary == MiningBeneficiary.User && _accruedFee >= _targetWindow)
        {
            _beneficiary = MiningBeneficiary.Developer;
            _remainingWindow = _targetWindow;
        }
    }

    private TimeSpan NextWindow() =>
        _policy.Enabled && _policy.Rate > 0m
            ? _windowSource.Next(_policy.MinimumWindow, _policy.MaximumWindow)
            : _policy.MinimumWindow;

    private static TimeSpan NonNegative(TimeSpan value) =>
        value < TimeSpan.Zero ? TimeSpan.Zero : value;

    private static TimeSpan Scale(TimeSpan value, decimal factor) =>
        TimeSpan.FromTicks(decimal.ToInt64(decimal.Round(value.Ticks * factor, MidpointRounding.ToEven)));
}
