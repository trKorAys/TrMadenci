using TrMadenci.Service.Mining;

namespace TrMadenci.Core.Tests;

public sealed class MiningSoakRequestTests
{
    [Fact]
    public void General_soak_duration_supports_fractional_invariant_hours()
    {
        var request = MiningSoakRequest.Parse(["trmadenci.json", "--soak-hours=1.5", "--mine"]);

        Assert.NotNull(request);
        Assert.Equal(TimeSpan.FromMinutes(90), request.Duration);
        Assert.False(request.UsesLegacyEtcOption);
        request.Validate(mine: true, algorithm: "kawpow", qualificationRequested: false);
    }

    [Fact]
    public void Legacy_etchash_option_remains_compatible_only_with_etchash()
    {
        var request = MiningSoakRequest.Parse(["--etchash-soak-hours=24"]);

        Assert.NotNull(request);
        Assert.True(request.UsesLegacyEtcOption);
        request.Validate(mine: true, algorithm: "etchash", qualificationRequested: false);
        Assert.Throws<ArgumentException>(() =>
            request.Validate(mine: true, algorithm: "kawpow", qualificationRequested: false));
    }

    [Theory]
    [InlineData("--soak-hours")]
    [InlineData("--soak-hours=0")]
    [InlineData("--soak-hours=169")]
    [InlineData("--soak-hours=NaN")]
    [InlineData("--soak-hours=1,5")]
    public void Invalid_soak_duration_is_rejected(string argument)
    {
        Assert.Throws<ArgumentException>(() => MiningSoakRequest.Parse([argument]));
    }

    [Fact]
    public void Duplicate_soak_options_are_rejected()
    {
        Assert.Throws<ArgumentException>(() => MiningSoakRequest.Parse(
            ["--soak-hours=1", "--etchash-soak-hours=1"]));
    }

    [Theory]
    [InlineData(false, "kawpow", false)]
    [InlineData(true, "octopus", false)]
    [InlineData(true, "etchash", true)]
    public void Unsupported_soak_context_is_rejected(
        bool mine,
        string algorithm,
        bool qualificationRequested)
    {
        var request = new MiningSoakRequest(TimeSpan.FromHours(1), UsesLegacyEtcOption: false);

        Assert.Throws<ArgumentException>(() =>
            request.Validate(mine, algorithm, qualificationRequested));
    }
}
