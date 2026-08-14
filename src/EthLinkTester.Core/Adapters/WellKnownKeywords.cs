namespace EthLinkTester.Core.Adapters;

/// <summary>
/// Driver registry keywords this app reasons about by name.
/// </summary>
/// <remarks>
/// Named once rather than spelled inline wherever they are needed. These are the strings that
/// decide whether a speed can be forced and whether a diagnostic is available at all, so a typo
/// in one of two copies would not fail loudly - it would quietly report the capability as absent.
/// </remarks>
public static class WellKnownKeywords
{
    /// <summary>Forced speed and duplex. Every NDIS driver exposes this one.</summary>
    public const string SpeedDuplex = "*SpeedDuplex";

    public const string JumboPacket = "*JumboPacket";

    /// <summary>
    /// Whether a keyword is the vendor's MDI/MDI-X control.
    /// </summary>
    /// <remarks>
    /// Matched by substring because there is no standard name: vendors ship <c>*MDIX</c>,
    /// <c>MDIXMode</c>, <c>AutoMDIX</c> and others. Being wrong in the permissive direction only
    /// means claiming a diagnostic is available; being wrong the other way suppresses the one
    /// control that can distinguish an MDI artifact from a genuine cable fault.
    /// </remarks>
    public static bool IsMdiControl(string keyword) =>
        keyword.Contains("MDI", StringComparison.OrdinalIgnoreCase);
}
