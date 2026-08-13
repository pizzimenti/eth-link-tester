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
        await RecoverAsync();
        await CheckNpcapAsync();
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

            // A non-empty journal at startup is proof that a previous run did not clean up after
            // itself, so this is stated as fact rather than hedged.
            RecoverySeverity = outcome.JournalCleared ? InfoBarSeverity.Success : InfoBarSeverity.Error;
            RecoveryMessage = outcome.JournalCleared
                ? $"A previous run ended without restoring {outcome.Restored.Count} adapter " +
                  $"setting{(outcome.Restored.Count == 1 ? "" : "s")}. " +
                  $"Put back: {string.Join("; ", outcome.Restored)}."
                : $"Could not restore {outcome.Failures.Count} adapter setting" +
                  $"{(outcome.Failures.Count == 1 ? "" : "s")} left by a previous run. " +
                  $"{string.Join("; ", outcome.Failures)}. These will be retried on the next launch.";
        }
        catch (Exception ex)
        {
            RecoverySeverity = InfoBarSeverity.Error;
            RecoveryMessage = $"Could not read the restore journal at {DefaultJournalPath}: {ex.Message}";
        }
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

            foreach (var warning in RunSafety.Inspect(adapters))
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
        RigSummary = $"{adapters[0].Name} ↔ {adapters[1].Name}";
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
