namespace FPVBlackboxTuningAssistant.Services;

public sealed record BetaflightDTermParameterPair(string BaseParameter, string? MaximumParameter);

/// <summary>
/// Maps D-term variable names only for firmware branches whose CLI semantics
/// have been checked. Unknown branches must not inherit a nearby version's map.
/// </summary>
public static class BetaflightPidSemantics
{
    public static bool SupportsActualRatesTrial(string firmwareVersion)
    {
        var versionText = BetaflightCliConfigurationParser.ExtractFirmwareVersion(firmwareVersion);
        return Version.TryParse(versionText, out var version)
            && (IsVersionFamily(version, 2025, 12) || IsVersionFamily(version, 2026, 6));
    }

    public static bool TryGetDTermParameters(
        string firmwareVersion,
        string axis,
        out BetaflightDTermParameterPair parameters)
    {
        parameters = default!;
        var versionText = BetaflightCliConfigurationParser.ExtractFirmwareVersion(firmwareVersion);
        if (!Version.TryParse(versionText, out var version)
            || axis is not ("roll" or "pitch"))
            return false;

        if (IsVersionFamily(version, 2025, 12) || IsVersionFamily(version, 2026, 6))
        {
            parameters = new BetaflightDTermParameterPair($"d_{axis}", $"d_max_{axis}");
            return true;
        }

        if (version >= new Version(4, 3) && version < new Version(4, 6))
        {
            parameters = new BetaflightDTermParameterPair($"d_min_{axis}", $"d_{axis}");
            return true;
        }

        if (version >= new Version(4, 0) && version < new Version(4, 3))
        {
            parameters = new BetaflightDTermParameterPair($"d_{axis}", null);
            return true;
        }

        return false;
    }

    private static bool IsVersionFamily(Version version, int major, int minor) =>
        version.Major == major && version.Minor == minor;
}
