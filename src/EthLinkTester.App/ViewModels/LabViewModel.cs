using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EthLinkTester.Core;
using EthLinkTester.Core.Engine;
using EthLinkTester.Core.Simulation;
using Microsoft.UI.Xaml;

namespace EthLinkTester.App.ViewModels;

/// <summary>A selectable link speed with the text to show for it.</summary>
internal sealed record LinkSpeedOption(LinkSpeed Value, string Label);

/// <summary>
/// Drives the Lab Mode live view.
/// </summary>
/// <remarks>
/// <para>
/// In v0.1.0 the only engine available is the simulator, so this deliberately exposes the
/// simulation profile as a first-class control: being able to watch what a marginal cable looks
/// like next to a healthy one is the whole reason the simulator models fault signatures rather
/// than just emitting pleasant noise.
/// </para>
/// <para>
/// Observable members are declared as <c>partial</c> properties rather than annotated fields.
/// The field form generates code that CsWinRT cannot produce marshalling for, which breaks
/// ahead-of-time compilation in WinUI 3 specifically.
/// </para>
/// </remarks>
internal sealed partial class LabViewModel : ObservableObject, IDisposable
{
    private static readonly SimulationProfile[] AllProfiles = Enum.GetValues<SimulationProfile>();

    /// <summary>
    /// Speeds paired with their display text. The enum members are named for their megabit
    /// value (Mbps2500) because that keeps the enum value and its name consistent, but
    /// "2.5 Gbps" is what belongs in front of a person.
    /// </summary>
    private static readonly LinkSpeedOption[] AllLinkSpeeds =
        [.. Enum.GetValues<LinkSpeed>().Select(s => new LinkSpeedOption(s, s.ShortName()))];

    [SuppressMessage(
        "Performance",
        "CA1859:Use concrete types when possible for improved performance",
        Justification = "The interface is the point. The simulated engine is a stand-in for the " +
                        "native one arriving in Phase 3, and narrowing this field would let " +
                        "simulator-only assumptions leak into the view model.")]
    private IPacketEngine? _engine;

    private bool _disposed;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    [NotifyCanExecuteChangedFor(nameof(StopCommand))]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    public partial bool IsRunning { get; set; }

    [ObservableProperty]
    public partial SimulationProfile Profile { get; set; }

    [ObservableProperty]
    public partial LinkSpeed LinkSpeed { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TxDisplay))]
    public partial double TxMegabitsPerSecond { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RxDisplay))]
    public partial double RxMegabitsPerSecond { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LatencyP50Display))]
    public partial double LatencyP50Microseconds { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LatencyP99Display))]
    public partial double LatencyP99Microseconds { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RxErrorsDisplay))]
    public partial long RxErrors { get; set; }

    [ObservableProperty]
    public partial long TxFrames { get; set; }

    /// <summary>Null when nothing has gone wrong. Surfaced as an InfoBar, never swallowed.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? ErrorMessage { get; set; }

    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

    /// <summary>
    /// False in release builds, where simulation is compiled out.
    /// </summary>
    public static bool SimulationAvailable =>
#if SIMULATION
        true;
#else
        false;
#endif

    /// <summary>
    /// Hides the link-behaviour picker in builds that have no simulator, rather than offering a
    /// control that cannot do anything.
    /// </summary>
    public static Visibility SimulationVisibility =>
        SimulationAvailable ? Visibility.Visible : Visibility.Collapsed;

    public LabViewModel()
    {
        Profile = SimulationProfile.Healthy;
        LinkSpeed = LinkSpeed.Mbps1000;
    }

    public static IReadOnlyList<SimulationProfile> Profiles => AllProfiles;

    public static IReadOnlyList<LinkSpeedOption> LinkSpeedOptions => AllLinkSpeeds;

    /// <summary>
    /// Exists so the view can bind enablement directly. A property is cheaper than registering
    /// a boolean-negating value converter, and it reads better at the binding site.
    /// </summary>
    public bool IsIdle => !IsRunning;

    // Formatting lives here rather than in XAML converters: it is one line per value, it is
    // unit-testable, and the view stays a layout concern.
    //
    // Invariant culture is deliberate and matches InvariantGlobalization=true in
    // Directory.Build.props. This app is US-English only by decision; internationalization is
    // out of scope. Asking for CurrentCulture would be misleading, because that build setting
    // pins CurrentCulture to invariant anyway - the code would look locale-aware while never
    // being able to act on it.
    public string TxDisplay => TxMegabitsPerSecond.ToString("n0", CultureInfo.InvariantCulture);

    public string RxDisplay => RxMegabitsPerSecond.ToString("n0", CultureInfo.InvariantCulture);

    public string LatencyP50Display => LatencyP50Microseconds.ToString("n0", CultureInfo.InvariantCulture);

    public string LatencyP99Display => LatencyP99Microseconds.ToString("n0", CultureInfo.InvariantCulture);

    public string RxErrorsDisplay => RxErrors.ToString("n0", CultureInfo.InvariantCulture);

    public LinkSpeedOption SelectedLinkSpeed
    {
        get => AllLinkSpeeds.First(o => o.Value == LinkSpeed);
        set => LinkSpeed = value.Value;
    }

    /// <summary>
    /// True whenever the displayed numbers are synthesised. The UI must state this loudly:
    /// a plausible chart that never touched a cable is worse than no chart at all.
    /// </summary>
    public bool IsSimulated => _engine?.IsSimulated ?? true;

    /// <summary>The engine the pump should poll, or null when idle.</summary>
    public IPacketEngine? Engine => _engine;

    /// <summary>Raised when a run starts, so the view can reset its plots.</summary>
    public event EventHandler? RunStarted;

    /// <summary>
    /// Applies a drained batch's newest sample. The intermediate samples belong to the plots;
    /// raising change notification for each would flood the binding system to redraw text that
    /// nobody can read at 60 Hz.
    /// </summary>
    public void ApplyLatest(in TelemetrySample sample)
    {
        TxMegabitsPerSecond = sample.TxMegabitsPerSecond;
        RxMegabitsPerSecond = sample.RxMegabitsPerSecond;
        LatencyP50Microseconds = sample.LatencyP50Microseconds;
        LatencyP99Microseconds = sample.LatencyP99Microseconds;
        RxErrors = sample.RxErrors;
        TxFrames = sample.TxFrames;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // Blocking is acceptable only because the simulated engine's teardown completes
        // synchronously. A native engine will need a real async shutdown path.
        _engine?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _engine = null;
    }

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync()
    {
        ErrorMessage = null;

        try
        {
            await DisposeEngineAsync();
            _engine = CreateEngine();

            if (_engine is null)
            {
                ErrorMessage = "No packet engine is available in this build. " +
                               "The measurement engine arrives in Phase 3.";
                return;
            }

            await _engine.StartAsync(new EngineRunSettings { LinkSpeed = LinkSpeed });

            IsRunning = true;
            OnPropertyChanged(nameof(IsSimulated));
            RunStarted?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            // Without this the faulted task escapes AsyncRelayCommand onto the UI thread and
            // terminates the app with no message. Phase 3 makes this routine rather than
            // exotic: Npcap missing, adapter already in use, elevation denied.
            ErrorMessage = $"Could not start the run: {ex.Message}";
            IsRunning = false;
            await DisposeEngineAsync();
        }
    }

    /// <summary>
    /// Builds the engine for a run, or null when this build has none.
    /// </summary>
    /// <remarks>
    /// Simulation is compiled into Debug builds only. Contributors get the full UI with no NICs
    /// and no Npcap, while a released binary has no code path that can render synthetic data as
    /// though it were a measurement.
    /// </remarks>
    [SuppressMessage(
        "Performance",
        "CA1859:Use concrete types when possible for improved performance",
        Justification = "The interface is the point. Phase 3 returns the native engine from " +
                        "here, and narrowing the return type now would have to be undone then.")]
    [SuppressMessage(
        "Performance",
        "CA1822:Mark members as static",
        Justification = "Reads the Profile instance property in Debug builds. Only appears " +
                        "static in Release, where the simulation branch is compiled out.")]
    private IPacketEngine? CreateEngine()
    {
#if SIMULATION
        return new SimulatedPacketEngine(Profile);
#else
        return null;
#endif
    }

    /// <summary>Reports that the engine stopped because it faulted rather than because it finished.</summary>
    public void ReportFault()
    {
        IsRunning = false;
        ErrorMessage = "The engine faulted and the run was stopped. The readings above are stale.";
    }

    [RelayCommand(CanExecute = nameof(CanStop))]
    private async Task StopAsync()
    {
        if (_engine is not null)
        {
            await _engine.StopAsync();
        }

        IsRunning = false;
    }

    private bool CanStart() => !IsRunning;

    private bool CanStop() => IsRunning;

    private async Task DisposeEngineAsync()
    {
        if (_engine is not null)
        {
            await _engine.DisposeAsync();
            _engine = null;
        }
    }
}
