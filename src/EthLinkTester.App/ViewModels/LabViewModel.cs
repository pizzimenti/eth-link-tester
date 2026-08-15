using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net.NetworkInformation;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EthLinkTester.Core;
using EthLinkTester.Core.Adapters;
using EthLinkTester.Core.Engine;
using EthLinkTester.Core.Simulation;
using EthLinkTester.Platform;
using Microsoft.UI.Xaml;

namespace EthLinkTester.App.ViewModels;

/// <summary>A selectable link speed with the text to show for it.</summary>
internal sealed record LinkSpeedOption(LinkSpeed Value, string Label);

/// <summary>
/// One end of the rig: an adapter the engine can open, with its MAC already parsed.
/// </summary>
/// <remarks>
/// The MAC is carried as bytes rather than re-parsed at start, so an adapter whose address the
/// framework cannot read is excluded from the list instead of failing when the user presses Start.
/// </remarks>
internal sealed record AdapterOption(string Id, string Label, byte[] Mac, LinkSpeed? NegotiatedSpeed);

/// <summary>Where a run's numbers come from.</summary>
internal enum EngineSource
{
    /// <summary>Real frames across real copper.</summary>
    Hardware,

    /// <summary>Synthesised. Debug builds only.</summary>
    Simulated,
}

/// <summary>
/// Drives the Lab Mode live view.
/// </summary>
/// <remarks>
/// <para>
/// Two engines sit behind the same interface: the native one, which puts frames on a cable, and
/// the simulator, which is compiled into Debug builds only so the whole UI is developable with no
/// NICs and no Npcap. Which one ran is stated permanently on screen rather than inferred, because
/// a plausible chart that never touched a cable is worse than no chart at all.
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

    /// <summary>
    /// The RFC 2544 sweep, which is what a report has to quote against. 64 bytes is the stress
    /// case: it maximises frames per second rather than bits per second, and on this rig it is the
    /// only size that does not reach line rate.
    /// </summary>
    private static readonly int[] AllFrameSizes = [64, 128, 256, 512, 1024, 1280, 1518];

    private readonly IAdapterProvider _provider;

    [SuppressMessage(
        "Performance",
        "CA1859:Use concrete types when possible for improved performance",
        Justification = "The interface is the point. Two implementations are selected between at " +
                        "run time, and narrowing this field would let one's assumptions leak into " +
                        "the view model.")]
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
    public partial int FrameBytes { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    [NotifyPropertyChangedFor(nameof(SimulationVisibility))]
    [NotifyPropertyChangedFor(nameof(RigVisibility))]
    [NotifyPropertyChangedFor(nameof(LinkSpeedIsChosen))]
    public partial EngineSource Source { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    [NotifyPropertyChangedFor(nameof(LinkSpeedIsChosen))]
    public partial AdapterOption? TransmitAdapter { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    public partial AdapterOption? ReceiveAdapter { get; set; }

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
    [NotifyPropertyChangedFor(nameof(CaptureDropsDisplay))]
    public partial long CaptureDrops { get; set; }

    /// <summary>
    /// Telemetry windows the consumer never collected, cumulative for the run.
    /// </summary>
    /// <remarks>
    /// Shown rather than logged because a chart cannot tell a gap from continuity and will draw a
    /// line straight across one. Non-zero means a stretch of the plot covers more elapsed time
    /// than its width suggests.
    /// </remarks>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TelemetryGapsDisplay))]
    public partial long TelemetryGaps { get; set; }

    [ObservableProperty]
    public partial long TxFrames { get; set; }

    /// <summary>Null when nothing has gone wrong. Surfaced as an InfoBar, never swallowed.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? ErrorMessage { get; set; }

    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

    public ObservableCollection<AdapterOption> Adapters { get; } = [];

    /// <summary>
    /// False in release builds, where simulation is compiled out.
    /// </summary>
    public static bool SimulationAvailable =>
#if SIMULATION
        true;
#else
        false;
#endif

    public static IReadOnlyList<EngineSource> Sources => SimulationAvailable
        ? [EngineSource.Hardware, EngineSource.Simulated]
        : [EngineSource.Hardware];

    /// <summary>
    /// Hides the link-behaviour picker unless a simulated run is actually selected, rather than
    /// offering a control that cannot affect anything.
    /// </summary>
    public Visibility SimulationVisibility =>
        Source == EngineSource.Simulated ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Adapter pickers are meaningless for a simulated run.</summary>
    public Visibility RigVisibility =>
        Source == EngineSource.Hardware ? Visibility.Visible : Visibility.Collapsed;

    public LabViewModel()
        : this(new WindowsAdapterProvider())
    {
    }

    public LabViewModel(IAdapterProvider provider)
    {
        _provider = provider;
        Profile = SimulationProfile.Healthy;
        LinkSpeed = LinkSpeed.Mbps1000;
        FrameBytes = EthernetFrame.MaximumBytes;
        Source = SimulationAvailable ? EngineSource.Simulated : EngineSource.Hardware;
    }

    public static IReadOnlyList<SimulationProfile> Profiles => AllProfiles;

    public static IReadOnlyList<LinkSpeedOption> LinkSpeedOptions => AllLinkSpeeds;

    public static IReadOnlyList<int> FrameSizes => AllFrameSizes;

    /// <summary>
    /// Exists so the view can bind enablement directly. A property is cheaper than registering
    /// a boolean-negating value converter, and it reads better at the binding site.
    /// </summary>
    public bool IsIdle => !IsRunning;

    /// <summary>
    /// Whether the link speed is the user's to pick.
    /// </summary>
    /// <remarks>
    /// It is not, on real hardware: 802.3 requires auto-negotiation at 1000BASE-T and above, so
    /// the rate is whatever the two PHYs settled on. Offering a control that claims otherwise
    /// would misrepresent the one thing this app is careful about - a forced setting that the
    /// hardware silently ignored.
    /// </remarks>
    public bool LinkSpeedIsChosen =>
        Source == EngineSource.Simulated || TransmitAdapter?.NegotiatedSpeed is null;

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

    public string CaptureDropsDisplay => CaptureDrops.ToString("n0", CultureInfo.InvariantCulture);

    public string TelemetryGapsDisplay => TelemetryGaps.ToString("n0", CultureInfo.InvariantCulture);

    public LinkSpeedOption SelectedLinkSpeed
    {
        get => AllLinkSpeeds.First(o => o.Value == LinkSpeed);
        set => LinkSpeed = value.Value;
    }

    /// <summary>
    /// True whenever the displayed numbers are synthesised. The UI must state this loudly:
    /// a plausible chart that never touched a cable is worse than no chart at all.
    /// </summary>
    public bool IsSimulated => _engine?.IsSimulated ?? Source == EngineSource.Simulated;

    /// <summary>The engine the pump should poll, or null when idle.</summary>
    public IPacketEngine? Engine => _engine;

    /// <summary>Raised when a run starts, so the view can reset its plots.</summary>
    public event EventHandler? RunStarted;

    /// <summary>
    /// Lists the adapters a run could use. Called on navigation rather than in the constructor so
    /// the enumeration - which reads real hardware - is not on the UI thread's construction path.
    /// </summary>
    public async Task LoadAdaptersAsync()
    {
        if (IsRunning)
        {
            return;
        }

        try
        {
            var found = await _provider.GetPhysicalAdaptersAsync();

            Adapters.Clear();
            foreach (var adapter in found)
            {
                // An adapter whose address will not parse cannot be used, and dropping it here is
                // better than failing at Start with a message about byte counts.
                if (PhysicalAddress.TryParse(adapter.MacAddress, out var mac))
                {
                    Adapters.Add(new AdapterOption(
                        adapter.Id,
                        $"{AdapterNickname.From(adapter.Description, adapter.Name)} — {DescribeLink(adapter)}",
                        mac.GetAddressBytes(),
                        adapter.NegotiatedSpeed));
                }
            }

            // The rig is two adapters wired to each other, so the common case needs no choosing.
            TransmitAdapter ??= Adapters.FirstOrDefault();
            ReceiveAdapter ??= Adapters.FirstOrDefault(a => a.Id != TransmitAdapter?.Id);
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Could not list adapters: {ex.Message}";
        }
    }

    private static string DescribeLink(NetworkAdapterInfo adapter) =>
        adapter.NegotiatedSpeed?.ShortName() ?? (adapter.IsUp ? "linked" : "no link");

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
        CaptureDrops = sample.RxCaptureDrops;
        TxFrames = sample.TxFrames;
        TelemetryGaps = _engine?.DroppedSamples ?? 0;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // Blocking on teardown is acceptable here and nowhere else: this runs on window close,
        // and the native engine's stop joins three threads that must not outlive the process
        // holding the restore journal.
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
                ErrorMessage = "No packet engine is available in this build.";
                return;
            }

            await _engine.StartAsync(new EngineRunSettings
            {
                LinkSpeed = EffectiveLinkSpeed(),
                FrameBytes = FrameBytes,
            });

            TelemetryGaps = 0;
            IsRunning = true;
            OnPropertyChanged(nameof(IsSimulated));
            RunStarted?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            // Without this the faulted task escapes AsyncRelayCommand onto the UI thread and
            // terminates the app with no message. On real hardware this is routine rather than
            // exotic: Npcap missing, adapter already in use, elevation denied.
            ErrorMessage = $"Could not start the run: {ex.Message}";
            IsRunning = false;
            await DisposeEngineAsync();
        }
    }

    /// <summary>
    /// The rate the run is measured against: what the adapters negotiated, or the user's pick when
    /// nothing has negotiated anything.
    /// </summary>
    private LinkSpeed EffectiveLinkSpeed() =>
        Source == EngineSource.Hardware
            ? TransmitAdapter?.NegotiatedSpeed ?? LinkSpeed
            : LinkSpeed;

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
        Justification = "Returns one of two implementations chosen at run time.")]
    private IPacketEngine? CreateEngine()
    {
#if SIMULATION
        if (Source == EngineSource.Simulated)
        {
            return new SimulatedPacketEngine(Profile);
        }
#endif
        if (TransmitAdapter is null || ReceiveAdapter is null)
        {
            return null;
        }

        return new NativePacketEngine(
            NativePacketEngine.DeviceName(TransmitAdapter.Id),
            NativePacketEngine.DeviceName(ReceiveAdapter.Id),
            TransmitAdapter.Mac,
            ReceiveAdapter.Mac);
    }

    /// <summary>
    /// Tears the run down after the engine reported a fault, and says why.
    /// </summary>
    /// <remarks>
    /// The teardown is the important half. A native fault stops one worker, not the run: the
    /// engine's own <c>running</c> flag is untouched, so the transmitter carries on sending after
    /// a capture failure and both adapters stay open. Setting <c>IsRunning</c> to false only stops
    /// the telemetry pump and greys out the Stop button - it leaves traffic on the wire with no
    /// control on screen that can end it, until another run starts or the app exits.
    /// </remarks>
    public async Task ReportFaultAsync()
    {
        // Read before disposing: the description belongs to the engine being torn down.
        //
        // The engine's own account matters. "Faulted" alone reads as one failure; a stopped capture
        // and a stopped transmit mean opposite things about whether the numbers on screen are a
        // measurement of the cable.
        var reason = _engine?.FaultDescription;

        try
        {
            await DisposeEngineAsync();
        }
        catch (Exception ex)
        {
            // Nothing above this can handle it: the caller is an event handler, so an escaping
            // exception terminates the process. An engine that will not shut down is worth
            // reporting, and it is not worth taking the app down over.
            reason = $"{reason} The engine also failed to shut down: {ex.Message}".TrimStart();
            _engine = null;
        }

        IsRunning = false;
        OnPropertyChanged(nameof(IsSimulated));
        ErrorMessage = reason
            ?? "The engine faulted and the run was stopped. The readings above are stale.";
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

    private bool CanStart() =>
        !IsRunning &&
        (Source == EngineSource.Simulated ||
         (TransmitAdapter is not null && ReceiveAdapter is not null &&
          TransmitAdapter.Id != ReceiveAdapter.Id));

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
