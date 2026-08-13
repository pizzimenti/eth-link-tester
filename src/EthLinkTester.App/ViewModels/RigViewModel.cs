using System.Collections.ObjectModel;
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
internal sealed partial class RigViewModel : ObservableObject
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

    public RigViewModel()
        : this(
            new WindowsAdapterProvider(),
            new GuardedAdapterConfigurator(
                new WindowsAdapterPropertyWriter(),
                new FileRestoreJournal(DefaultJournalPath)),
            new WindowsNpcapProbe())
    {
    }

    public RigViewModel(
        IAdapterProvider provider,
        IAdapterConfigurator configurator,
        INpcapProbe npcap)
    {
        _provider = provider;
        _configurator = configurator;
        _npcap = npcap;
    }

    /// <summary>
    /// Machine-wide rather than per-user, because a run can outlive the session that started it
    /// and any administrator must be able to recover it.
    /// </summary>
    public static string DefaultJournalPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "EthLinkTester",
        "pending-restore.json");

    public ObservableCollection<AdapterCardViewModel> Adapters { get; } = [];

    public ObservableCollection<string> Limitations { get; } = [];

    public ObservableCollection<RunWarning> Warnings { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRig))]
    public partial string RigSummary { get; set; } = "Not probed yet.";

    [ObservableProperty]
    public partial string TestableSpeeds { get; set; } = "—";

    [ObservableProperty]
    public partial string Ceiling { get; set; } = "—";

    [ObservableProperty]
    public partial string ForceableSettings { get; set; } = "—";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRig))]
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

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? ErrorMessage { get; set; }

    public bool IsIdle => !IsBusy;

    public bool HasRecoveryMessage => !string.IsNullOrEmpty(RecoveryMessage);

    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

    public bool HasRig => RigIsComplete;

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
            var outcome = await _configurator.RestoreAllAsync();

            if (outcome.NothingToDo)
            {
                return;
            }

            // Anything still unrestored has to reach the pre-run check, or a new run would record
            // the leftover values as the originals and make them permanent.
            _unrestored = [.. outcome.Failures.Select(f => f.Entry)];

            RecoverySeverity = outcome.NeedsAttention ? InfoBarSeverity.Error : InfoBarSeverity.Success;
            RecoveryMessage = DescribeRecovery(outcome);
        }
        catch (Exception ex)
        {
            RecoverySeverity = InfoBarSeverity.Error;
            RecoveryMessage = $"Could not read the restore journal at {DefaultJournalPath}: {ex.Message}";
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
                    ErrorMessage = $"Could not probe {adapter.Name}: {ex.Message}";
                }

                capabilities.Add(probed);
                Adapters.Add(new AdapterCardViewModel(adapter, probed));
            }

            foreach (var warning in RunSafety.Inspect(adapters, _unrestored))
            {
                Warnings.Add(warning);
            }

            DescribeRig(adapters, capabilities);
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
        RigSummary =
            $"{AdapterNickname.From(adapters[0].Description, adapters[0].Name)} ↔ " +
            $"{AdapterNickname.From(adapters[1].Description, adapters[1].Name)}";
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
