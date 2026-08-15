using System.Text.RegularExpressions;

namespace EthLinkTester.Core.Preflight;

/// <summary>Whether the capture driver can carry a run, and if not, why not.</summary>
public enum NpcapReadiness
{
    /// <summary>Installed, running, and configured correctly.</summary>
    Ready,

    /// <summary>Not installed at all. The app runs, but cannot put a frame on the wire.</summary>
    NotInstalled,

    /// <summary>Installed but the driver service is not running.</summary>
    ServiceStopped,

    /// <summary>Installed but too old to rely on.</summary>
    TooOld,

    /// <summary>Installed in WinPcap compatibility mode, which this app cannot use safely.</summary>
    WinPcapCompatibilityMode,

    /// <summary>Registered, but the driver files are not there. An incomplete install or removal.</summary>
    FilesMissing,
}

/// <summary>
/// What the machine's Npcap installation looks like.
/// </summary>
/// <remarks>
/// <para>
/// Npcap is a prerequisite rather than a bundled component: its free licence does not permit
/// redistribution, so the app cannot ship it and must instead detect it and guide the user. That
/// constraint is why this type exists at all.
/// </para>
/// <para>
/// A missing driver is not a fatal error. Simulation mode is fully functional without it, which is
/// deliberate - contributors can work on the UI with no capture driver and no second NIC.
/// </para>
/// </remarks>
public sealed partial record NpcapStatus
{
    /// <summary>
    /// The oldest release this app will vouch for.
    /// </summary>
    /// <remarks>
    /// 1.0 is the first stable release; the 0.9x betas differ in loopback and raw-injection
    /// behaviour in ways not worth carrying compatibility code for. This is a floor chosen for
    /// confidence, not a known incompatibility, so the message says "not verified" rather than
    /// claiming the older build is broken.
    /// </remarks>
    public static readonly Version MinimumVersion = new(1, 0);

    public const string DownloadUrl = "https://npcap.com/#download";

    public required bool Installed { get; init; }

    /// <summary>
    /// Whether the driver's own files are present, as distinct from a registry key claiming they
    /// are.
    /// </summary>
    /// <remarks>
    /// A registry key routinely survives an incomplete uninstall, so it proves registration and
    /// nothing else. Without <c>wpcap.dll</c> nothing can capture whatever the registry says, and
    /// the remedy is a reinstall - not "start the service", which is the advice a machine in this
    /// state would otherwise be given.
    /// </remarks>
    public bool DriverFilesPresent { get; init; }

    public Version? Version { get; init; }

    public bool ServiceRunning { get; init; }

    /// <summary>
    /// Whether Npcap was installed with "WinPcap API-compatible mode" enabled.
    /// </summary>
    /// <remarks>
    /// In that mode Npcap replaces the system-wide WinPcap DLLs rather than installing alongside
    /// them. Any other application on the machine expecting WinPcap silently binds to Npcap
    /// instead, so a fault introduced here surfaces somewhere unrelated - and this app cannot tell
    /// which library it is actually talking to.
    /// <para>
    /// The installer ships this option <b>ticked</b> - observed directly on 1.88 while installing
    /// it here. Earlier guidance in this file said the opposite, which would have walked a user
    /// straight into the one configuration the app documents as unusable.
    /// </para>
    /// </remarks>
    public bool WinPcapCompatibilityMode { get; init; }

    public string? InstallPath { get; init; }

    /// <remarks>
    /// Ordered by which remedy subsumes the others. Reinstalling fixes compatibility mode, a
    /// missing file, and an old version, and restarts the service on the way - so leading with a
    /// stopped service would send the user round the loop twice.
    /// </remarks>
    public NpcapReadiness Readiness =>
        !Installed ? NpcapReadiness.NotInstalled
        : !DriverFilesPresent ? NpcapReadiness.FilesMissing
        : WinPcapCompatibilityMode ? NpcapReadiness.WinPcapCompatibilityMode
        : Version is not null && Version < MinimumVersion ? NpcapReadiness.TooOld
        : !ServiceRunning ? NpcapReadiness.ServiceStopped
        : NpcapReadiness.Ready;

    public bool CanRunLiveTests => Readiness == NpcapReadiness.Ready;

    /// <summary>The headline, written so the user knows what to do next.</summary>
    public string Headline => Readiness switch
    {
        NpcapReadiness.Ready => $"Npcap {Version?.ToString() ?? "(version unknown)"} is ready.",
        NpcapReadiness.NotInstalled =>
            "Npcap is not installed. Live tests are unavailable; simulation mode still works.",
        NpcapReadiness.ServiceStopped =>
            "Npcap is installed but its driver service is not running.",
        NpcapReadiness.TooOld =>
            $"Npcap {Version} is older than {MinimumVersion}, which this app has not verified.",
        NpcapReadiness.WinPcapCompatibilityMode =>
            "Npcap is installed in WinPcap compatibility mode, which this app cannot use safely.",
        NpcapReadiness.FilesMissing =>
            "Npcap is registered but its driver files are missing.",
        _ => "Npcap status is unknown.",
    };

    /// <summary>What to do about it, or empty when there is nothing to do.</summary>
    public string? Remedy => Readiness switch
    {
        NpcapReadiness.Ready => null,
        NpcapReadiness.NotInstalled =>
            $"Install Npcap from {DownloadUrl}. On the installer's options page, <b>untick</b> " +
            "\"Install Npcap in WinPcap API-compatible Mode\" - it is ticked by default, and this " +
            "app cannot use it. Npcap cannot be bundled here because its licence does not permit " +
            "redistribution.",
        NpcapReadiness.ServiceStopped =>
            "Start the npcap service, or reboot. Installing Npcap without restarting leaves it " +
            "stopped until the driver loads.",
        NpcapReadiness.TooOld =>
            $"Upgrade to {MinimumVersion} or later from {DownloadUrl}.",
        NpcapReadiness.WinPcapCompatibilityMode =>
            "Reinstall Npcap with \"WinPcap API-compatible mode\" unchecked. In that mode Npcap " +
            "replaces the system-wide WinPcap libraries, so this app cannot tell which " +
            "implementation it is bound to and any other capture software on this machine is " +
            "silently affected too.",
        NpcapReadiness.FilesMissing =>
            $"Reinstall Npcap from {DownloadUrl}. A registry key without driver files is what an " +
            "interrupted install or a partial uninstall leaves behind - nothing can capture in " +
            "this state, and starting the service will not help.",
        _ => null,
    };

    /// <summary>
    /// Parses a version from any of the forms Npcap has shipped, or null when there is none.
    /// </summary>
    /// <remarks>
    /// One parser for every source - registry, uninstall entry, file resources - because a version
    /// check fails asymmetrically: a value it cannot read becomes "unknown", and
    /// <see cref="Readiness"/> treats unknown as <see cref="NpcapReadiness.Ready"/>. So a format it
    /// rejects silently disables the minimum-version gate for exactly the builds that gate exists
    /// to catch. Npcap's older releases used suffixes like <c>0.99-r9</c>, which
    /// <see cref="Version.TryParse(string, out Version)"/> refuses outright, so the numeric prefix
    /// is taken first.
    /// <para>
    /// In Core rather than the Windows probe because parsing a version string is not a platform
    /// concern, and here it can be tested against the forms that actually caused trouble.
    /// </para>
    /// </remarks>
    public static Version? ParseVersion(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var match = NumericPrefix().Match(text.Trim());
        if (!match.Success)
        {
            return null;
        }

        // Version needs at least major.minor; a bare "1" is legitimate in a resource.
        var numbers = match.Value.Contains('.', StringComparison.Ordinal)
            ? match.Value
            : match.Value + ".0";

        return Version.TryParse(numbers, out var parsed) && parsed != new Version(0, 0)
            ? parsed
            : null;
    }

    [GeneratedRegex(@"^\d+(\.\d+)*")]
    private static partial Regex NumericPrefix();

    /// <summary>The state of a machine with no capture driver at all.</summary>
    public static NpcapStatus Absent { get; } = new() { Installed = false };
}
