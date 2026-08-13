using System.Globalization;
using System.Text.RegularExpressions;

namespace EthLinkTester.Core.Adapters;

/// <summary>
/// Parses the human-readable speed/duplex strings a Windows driver reports.
/// </summary>
/// <remarks>
/// Lives in Core rather than the platform layer so it can be tested against real driver strings
/// with no adapter present. The strings come from the <c>*SpeedDuplex</c> advanced property's
/// valid values and are written for people, not machines: "Auto Negotiation",
/// "100 Mbps Full Duplex", "1.0 Gbps Full Duplex".
/// </remarks>
public static partial class SpeedDuplexParser
{
    [GeneratedRegex(
        @"^\s*(?<value>\d+(?:\.\d+)?)\s*(?<unit>Mbps|Gbps)\s+(?<duplex>Half|Full)\s+Duplex\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SettingPattern { get; }

    /// <summary>
    /// Parses one driver string. Returns false for "Auto Negotiation" and anything unrecognised,
    /// which is the correct outcome: auto-negotiation is the absence of a forced setting.
    /// </summary>
    public static bool TryParse(string? displayValue, out SpeedDuplex setting)
    {
        setting = default;

        if (string.IsNullOrWhiteSpace(displayValue))
        {
            return false;
        }

        var match = SettingPattern.Match(displayValue);
        if (!match.Success)
        {
            return false;
        }

        if (!double.TryParse(
                match.Groups["value"].Value,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var magnitude))
        {
            return false;
        }

        var megabits = match.Groups["unit"].Value.Equals("Gbps", StringComparison.OrdinalIgnoreCase)
            ? magnitude * 1000
            : magnitude;

        // Only tiers the app knows about. A driver advertising something exotic is ignored
        // rather than guessed at.
        if (!Enum.IsDefined(typeof(LinkSpeed), (int)megabits))
        {
            return false;
        }

        var duplex = match.Groups["duplex"].Value.Equals("Half", StringComparison.OrdinalIgnoreCase)
            ? DuplexMode.Half
            : DuplexMode.Full;

        setting = new SpeedDuplex((LinkSpeed)(int)megabits, duplex);
        return true;
    }

    /// <summary>Parses a driver's whole valid-value list, skipping entries it does not recognise.</summary>
    public static IReadOnlyList<SpeedDuplex> ParseAll(IEnumerable<string>? displayValues)
    {
        if (displayValues is null)
        {
            return [];
        }

        var parsed = new List<SpeedDuplex>();
        foreach (var value in displayValues)
        {
            if (TryParse(value, out var setting))
            {
                parsed.Add(setting);
            }
        }

        return parsed;
    }
}
