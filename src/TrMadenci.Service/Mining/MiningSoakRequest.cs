using System.Globalization;

namespace TrMadenci.Service.Mining;

internal sealed record MiningSoakRequest(TimeSpan Duration, bool UsesLegacyEtcOption)
{
    private const string GeneralPrefix = "--soak-hours";
    private const string LegacyEtcPrefix = "--etchash-soak-hours";

    public static MiningSoakRequest? Parse(IReadOnlyList<string> arguments)
    {
        var candidates = arguments.Where(argument =>
            argument.StartsWith(GeneralPrefix, StringComparison.OrdinalIgnoreCase) ||
            argument.StartsWith(LegacyEtcPrefix, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (candidates.Length == 0)
            return null;
        if (candidates.Length > 1)
            throw new ArgumentException("Specify exactly one soak duration option.");

        var argument = candidates[0];
        var legacy = argument.StartsWith(LegacyEtcPrefix, StringComparison.OrdinalIgnoreCase);
        var expectedPrefix = legacy ? LegacyEtcPrefix + "=" : GeneralPrefix + "=";
        if (!argument.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"Use {expectedPrefix}<hours>.");
        var value = argument[expectedPrefix.Length..];
        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var hours) ||
            !double.IsFinite(hours) || hours <= 0 || hours > 168)
            throw new ArgumentException(
                $"{expectedPrefix[..^1]} must be greater than 0 and no more than 168 hours.");
        return new MiningSoakRequest(TimeSpan.FromHours(hours), legacy);
    }

    public void Validate(bool mine, string algorithm, bool qualificationRequested)
    {
        if (!mine)
            throw new ArgumentException("A soak duration requires --mine.");
        if (UsesLegacyEtcOption &&
            !string.Equals(algorithm, "etchash", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("--etchash-soak-hours requires an ETCHash coin profile.");
        if (!string.Equals(algorithm, "kawpow", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(algorithm, "etchash", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(algorithm, "octopus", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException(
                "--soak-hours currently supports KAWPOW, ETCHASH and OCTOPUS profiles.");
        if (qualificationRequested)
            throw new ArgumentException("Use either a qualification mode or a soak duration, not both.");
    }
}
