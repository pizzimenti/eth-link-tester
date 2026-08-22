using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EthLinkTester.Core;
using EthLinkTester.Core.Adapters;
using EthLinkTester.Core.Preflight;
using EthLinkTester.Core.Safety;
using EthLinkTester.Platform;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace EthLinkTester.App.ViewModels;

/// <summary>
/// Drives the Rig page: what hardware is present, what it can test, and what is unsafe about it.
/// </summary>
/// <remarks>
/// This is the first page that reads real hardware, so it is also where the app's two standing
/// obligations are discharged on launch: recover any adapter settings a previous run left behind,
/// and say plainly whether live testing is possible at all.
/// </remarks>
internal sealed partial class RigViewModel : ObservableObject, IDisposable
{
    private readonly IAdapterProvider _provider;
    private readonly IAdapterConfigurator _configurator;
    private readonly INpcapProbe _npcap;

    /// <summary>
    /// Entries a failed recovery left behind, carried into the pre-run check.
    /// </summary>
    /// <remarks>
    /// Without this the <see cref="RunHazard.UnrestoredChanges"/> warning is unreachable, which
    /// makes it the most dangerous kind of dead code: the hazard it describes - a new run
    /// recording leftover values as the originals, and so making them permanent - is real whether
    /// or not anything is watching for it.
    /// </remarks>
    private IReadOnlyList<PendingRestore> _unrestored = [];

    /// <summary>Non-null only when this view model created the journal, and so must dispose it.</summary>
    private readonly FileRestoreJournal? _ownedJournal;

    public RigViewModel()
        : this(new WindowsAdapterProvider(), new FileRestoreJournal(DefaultJournalPath, location: new WindowsJournalLocation()), new WindowsNpcapProbe())
    {
    }

    /// <summary>
    /// Takes the journal rather than a configurator so ownership is explicit: it holds a named
    /// mutex, and the page that creates one is the only thing that can dispose it.
    /// </summary>
    private RigViewModel(IAdapterProvider provider, FileRestoreJournal journal, INpcapProbe npcap)
        : this(provider, new GuardedAdapterConfigurator(new WindowsAdapterPropertyWriter(), journal), npcap) =>
        _ownedJournal = journal;

    public RigViewModel(
        IAdapterProvider provider,
        IAdapterConfigurator configurator,
        INpcapProbe npcap)
    {
        _provider = provider;
        _configurator = configurator;
        _npcap = npcap;

        // The probe is built per detection rather than once, because it resolves adapter ids to
        // device names and MACs from the list this page is holding at the time. Resolving them
        // twice, from two enumerations taken at different moments, is how two components come to
        // disagree about which adapter is which.
        // Re-probe re-enumerates the adapters, and a detection in flight is holding two of them
        // and may be restarting a miniport - which takes an adapter out of the CIM enumeration
        // entirely, so a refresh landing mid-detection can degrade the rig card to "only one
        // physical Ethernet adapter" about hardware that is present and working.
        HardwareSession.Changed += (_, _) => RefreshCommand.NotifyCanExecuteChanged();

        Topology = new TopologyViewModel(
            provider,
            configurator,
            new WindowsSoftwareBridgeProbe(),
            pair => new NativeTopologyProbe(
                NativePacketEngine.DeviceName,
                id =>
                {
                    var adapter = pair.First(a => a.Id == id);
                    return ParseMac(adapter.Name, adapter.MacAddress);
                }));
    }

    /// <summary>Whether these two adapters are wired to each other, and what says so.</summary>
    public TopologyViewModel Topology { get; }

    /// <summary>
    /// Turns the provider's MAC into the six bytes the engine puts in a frame header.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Colons as well as dashes, because the two spellings both reach this app: the CIM provider
    /// gives dashes and every command-line tool on the machine prints colons, so a user pasting one
    /// in is the ordinary case rather than the odd one.
    /// </para>
    /// <para>
    /// <b>Validated rather than parsed hopefully.</b> An adapter whose address the provider could
    /// not read comes through as an empty string, and one written without separators comes through
    /// as a single twelve-character part; both reach <c>byte.Parse</c> and surface as a bare
    /// <c>FormatException</c> that names neither the adapter nor the field. Worse, a wrong octet
    /// count parses cleanly and produces an array the native side reads six bytes from regardless -
    /// so an address of four octets is a buffer over-read at an FFI boundary, and one of eight is a
    /// frame sent from an address nobody chose.
    /// </para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">The address is not six hexadecimal octets.</exception>
    private static byte[] ParseMac(string adapterName, string address)
    {
        var parts = (address ?? string.Empty).Split('-', ':');

        if (parts.Length != 6
            || !parts.All(p => byte.TryParse(p, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _)))
        {
            throw new InvalidOperationException(
                $"{adapterName} reports its hardware address as '{address}', which is not six "
                + "hexadecimal octets. The engine puts this address in every frame it sends, so it "
                + "cannot run against this adapter.");
        }

        return [.. parts.Select(p => byte.Parse(p, NumberStyles.HexNumber, CultureInfo.InvariantCulture))];
    }

    /// <summary>
    /// Machine-wide rather than per-user, because a run can outlive the session that started it
    /// and any administrator must be able to recover it.
    /// </summary>
    public static string DefaultJournalPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "EthLinkTester",
        "pending-restore.json");

    public void Dispose() => _ownedJournal?.Dispose();

    public ObservableCollection<AdapterCardViewModel> Adapters { get; } = [];

    public ObservableCollection<string> Limitations { get; } = [];

    public ObservableCollection<RunWarning> Warnings { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial string RigSummary { get; set; } = "Not probed yet.";

    [ObservableProperty]
    public partial string TestableSpeeds { get; set; } = "—";

    [ObservableProperty]
    public partial string Ceiling { get; set; } = "—";

    [ObservableProperty]
    public partial string ForceableSettings { get; set; } = "—";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RigVisibility))]
    public partial bool RigIsComplete { get; set; }

    [ObservableProperty]
    public partial string NpcapHeadline { get; set; } = "Checking for Npcap...";

    [ObservableProperty]
    public partial string? NpcapRemedy { get; set; }

    [ObservableProperty]
    public partial InfoBarSeverity NpcapSeverity { get; set; } = InfoBarSeverity.Informational;

    /// <summary>Set when a previous run's settings were put back on launch. Null otherwise.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRecoveryMessage))]
    public partial string? RecoveryMessage { get; set; }

    [ObservableProperty]
    public partial InfoBarSeverity RecoverySeverity { get; set; } = InfoBarSeverity.Success;

    /// <summary>
    /// Title for the recovery notice, which must not contradict its severity.
    /// </summary>
    /// <remarks>
    /// It was hard-coded to "Adapter settings restored", so a pass that failed to restore anything
    /// still announced success in the one place a hurried reader looks.
    /// </remarks>
    [ObservableProperty]
    public partial string RecoveryTitle { get; set; } = "Adapter settings restored";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? ErrorMessage { get; set; }

    /// <summary>
    /// True when nothing this page owns is running <i>and</i> nothing else is driving the adapters.
    /// </summary>
    /// <remarks>
    /// The second half matters because Re-probe re-enumerates hardware that a detection may be in
    /// the middle of restarting. Writing <c>*SpeedDuplex</c> takes an adapter out of the CIM
    /// enumeration for a moment, so a refresh landing in that window reports a rig with one NIC and
    /// junk capabilities about hardware that is present and working.
    /// </remarks>
    public bool IsIdle => !IsBusy && !HardwareSession.IsBusy;

    public bool HasRecoveryMessage => !string.IsNullOrEmpty(RecoveryMessage);

    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

    /// <summary>
    /// Whether the capability card has anything to say.
    /// </summary>
    /// <remarks>
    /// A Visibility rather than a bool because x:Bind does not convert one to the other, and a
    /// property here is cheaper than registering a converter - the same reasoning as
    /// <see cref="WarningsVisibility"/>.
    /// </remarks>
    public Visibility RigVisibility => RigIsComplete ? Visibility.Visible : Visibility.Collapsed;

    public Visibility WarningsVisibility =>
        Warnings.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>
    /// Runs on navigation: recover first, then probe. The order matters - probing an adapter that
    /// is still forced to 100 Mbps would record that as its natural state.
    /// </summary>
    public async Task InitializeAsync()
    {
        // Busy for the whole sequence, not just the probe. Recovery writes adapter settings, and
        // leaving Re-probe live during it would let a click run an enumeration concurrently with
        // the restore that is still changing what it would enumerate.
        IsBusy = true;
        RefreshCommand.NotifyCanExecuteChanged();

        try
        {
            await RecoverAsync();
            await CheckNpcapAsync();
        }
        finally
        {
            IsBusy = false;
            RefreshCommand.NotifyCanExecuteChanged();
        }

        await RefreshAsync();
    }

    private async Task RecoverAsync()
    {
        try
        {
            var outcome = await _configurator.RestoreAsync(RestoreScope.Everything);

            if (outcome.NothingToDo)
            {
                return;
            }

            // Anything still unrestored has to reach the pre-run check, or a new run would record
            // the leftover values as the originals and make them permanent.
            _unrestored = [.. outcome.Failures.Select(f => f.Entry)];

            RecoverySeverity = outcome.NeedsAttention ? InfoBarSeverity.Error : InfoBarSeverity.Success;
            RecoveryTitle = outcome.NeedsAttention
                ? "Adapter settings could not be fully restored"
                : "Adapter settings restored";
            RecoveryMessage = DescribeRecovery(outcome);
        }
        catch (Exception ex)
        {
            RecoverySeverity = InfoBarSeverity.Error;
            RecoveryTitle = "Could not read the restore journal";
            RecoveryMessage = $"{DefaultJournalPath}: {ex.Message}";
        }
    }

    /// <summary>
    /// Describes a recovery pass, leading with whatever the user must act on.
    /// </summary>
    /// <remarks>
    /// Unreadable records come first because they are the one outcome the app cannot fix: the
    /// original values are gone, so an adapter may still be altered with no way for the app to
    /// discover which. Everything else it either handled or will retry.
    /// </remarks>
    private static string DescribeRecovery(RestoreOutcome outcome)
    {
        var parts = new List<string>();

        if (outcome.Rejected.Count > 0)
        {
            parts.Add(
                $"{outcome.Rejected.Count} journal entr" +
                $"{(outcome.Rejected.Count == 1 ? "y names a value" : "ies name values")} this app " +
                "did not record, so " +
                $"{(outcome.Rejected.Count == 1 ? "it was" : "they were")} discarded rather than " +
                $"applied to the adapters ({string.Join("; ", outcome.Rejected)}). On a correctly " +
                "permissioned machine this should not happen.");
        }

        if (outcome.UnreadableRecords > 0)
        {
            parts.Add(
                $"{outcome.UnreadableRecords} journal record" +
                $"{(outcome.UnreadableRecords == 1 ? " was" : "s were")} damaged and could not be " +
                "read, so the original values are lost. Check the adapters' speed, duplex, and " +
                "offload settings by hand.");
        }

        if (outcome.Failures.Count > 0)
        {
            parts.Add(
                $"Could not restore {outcome.Failures.Count} setting" +
                $"{(outcome.Failures.Count == 1 ? "" : "s")} left by a previous run " +
                $"({string.Join("; ", outcome.Failures)}). These are retried on the next launch.");
        }

        if (outcome.Restored.Count > 0)
        {
            parts.Add(
                $"A previous run ended without restoring {outcome.Restored.Count} adapter " +
                $"setting{(outcome.Restored.Count == 1 ? "" : "s")}. " +
                $"Put back: {string.Join("; ", outcome.Restored)}.");
        }

        if (outcome.Abandoned.Count > 0)
        {
            parts.Add(
                $"{outcome.Abandoned.Count} setting{(outcome.Abandoned.Count == 1 ? "" : "s")} " +
                "belonged to hardware that is no longer present and " +
                $"{(outcome.Abandoned.Count == 1 ? "was" : "were")} discarded " +
                $"({string.Join("; ", outcome.Abandoned)}).");
        }

        return string.Join(" ", parts);
    }

    private async Task CheckNpcapAsync()
    {
        try
        {
            var status = await _npcap.DetectAsync();

            NpcapHeadline = status.Headline;
            NpcapRemedy = status.Remedy;
            NpcapSeverity = status.Readiness switch
            {
                NpcapReadiness.Ready => InfoBarSeverity.Success,
                // Absent is expected on a fresh machine and simulation still works, so it informs
                // rather than alarms. A broken installation is a real problem.
                NpcapReadiness.NotInstalled => InfoBarSeverity.Informational,
                _ => InfoBarSeverity.Warning,
            };
        }
        catch (Exception ex)
        {
            NpcapSeverity = InfoBarSeverity.Warning;
            NpcapHeadline = "Could not determine whether Npcap is installed.";
            NpcapRemedy = ex.Message;
        }
    }

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private async Task RefreshAsync()
    {
        IsBusy = true;
        ErrorMessage = null;
        RefreshCommand.NotifyCanExecuteChanged();

        try
        {
            Adapters.Clear();
            Limitations.Clear();
            Warnings.Clear();

            var adapters = await _provider.GetPhysicalAdaptersAsync();
            var capabilities = new List<AdapterCapabilities?>();
            var probeFailures = new List<string>();

            foreach (var adapter in adapters)
            {
                // One adapter failing to probe must not hide the others: a machine with a
                // half-broken NIC is exactly when this page needs to work.
                AdapterCapabilities? probed = null;
                try
                {
                    probed = await _provider.ProbeCapabilitiesAsync(adapter.Id);
                }
                catch (Exception ex)
                {
                    // Collected rather than assigned: with two adapters failing, overwriting
                    // showed only the second, and the first is the one that is usually the cause.
                    probeFailures.Add($"{adapter.Name}: {ex.Message}");
                }

                capabilities.Add(probed);
                Adapters.Add(new AdapterCardViewModel(adapter, probed));
            }

            if (probeFailures.Count > 0)
            {
                ErrorMessage = "Could not probe " +
                    $"{probeFailures.Count} adapter{(probeFailures.Count == 1 ? "" : "s")}. " +
                    string.Join("; ", probeFailures);
            }

            foreach (var warning in RunSafety.Inspect(adapters, _unrestored))
            {
                Warnings.Add(warning);
            }

            DescribeRig(adapters, capabilities);
            Topology.SetPair(adapters);
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Could not enumerate adapters: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
            RefreshCommand.NotifyCanExecuteChanged();
            OnPropertyChanged(nameof(WarningsVisibility));
        }
    }

    private void DescribeRig(
        IReadOnlyList<NetworkAdapterInfo> adapters,
        List<AdapterCapabilities?> capabilities)
    {
        RigIsComplete = false;

        if (adapters.Count < 2 || capabilities[0] is null || capabilities[1] is null)
        {
            RigSummary = adapters.Count switch
            {
                0 => "No physical Ethernet adapters found. A rig needs two.",
                1 => $"Only one physical Ethernet adapter ({adapters[0].Name}). A rig needs two, " +
                     "connected to each other by the cable under test.",
                _ => "Adapters found, but at least one could not be probed.",
            };

            TestableSpeeds = "—";
            Ceiling = "—";
            ForceableSettings = "—";
            return;
        }

        var rig = RigCapabilities.Derive(adapters[0], capabilities[0]!, adapters[1], capabilities[1]!);

        RigIsComplete = true;
        RigSummary = $"{Adapters[0].Nickname} ↔ {Adapters[1].Nickname}";
        TestableSpeeds = string.Join(", ", rig.TestableSpeeds.Select(s => s.ShortName()));
        Ceiling = rig.MaximumMutualSpeed?.StandardName() ?? "Unknown";
        ForceableSettings = rig.ForceableSettings.Count == 0
            ? "None"
            : string.Join(", ", rig.ForceableSettings);

        foreach (var limitation in rig.Limitations)
        {
            Limitations.Add(limitation);
        }
    }
}
