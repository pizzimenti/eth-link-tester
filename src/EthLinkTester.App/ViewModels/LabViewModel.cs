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
internal sealed record AdapterOption(
    string Id, string Label, LinkSpeed? NegotiatedSpeed, bool CarriesDefaultRoute)
{
    /// <summary>The adapter's hardware address, six bytes.</summary>
    /// <remarks>
    /// Deliberately not a positional member. A <c>byte[]</c> in a record's primary constructor
    /// joins its generated equality, and arrays compare by reference - so two options describing
    /// the same adapter would test unequal, and any code matching a selection against a refreshed
    /// list would silently fail to find it.
    /// </remarks>
    public required byte[] Mac { get; init; }
}

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

    /// <summary>Guards <see cref="SwapIfBothEndsAreTheSame"/> against triggering itself.</summary>
    private bool _swapping;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    [NotifyCanExecuteChangedFor(nameof(StopCommand))]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    // LinkSpeedIsChosen is now gated on IsIdle, and a computed property built on another computed
    // property gets no notification of its own. IsSimulated switches authority between the engine
    // and the picker on this same flag.
    [NotifyPropertyChangedFor(nameof(LinkSpeedIsChosen))]
    [NotifyPropertyChangedFor(nameof(IsSimulated))]
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
    // IsSimulated drives the permanent "Simulated data" warning. Without this notification the
    // banner only corrected itself when a run started, so switching from Hardware to Simulated
    // while idle left it hidden - which is precisely the failure this file calls unacceptable.
    [NotifyPropertyChangedFor(nameof(IsSimulated))]
    // The default-route hazard applies to hardware runs only, so both follow the source.
    [NotifyPropertyChangedFor(nameof(TargetsDefaultRoute))]
    [NotifyPropertyChangedFor(nameof(DefaultRouteWarning))]
    public partial EngineSource Source { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    [NotifyPropertyChangedFor(nameof(LinkSpeedIsChosen))]
    [NotifyPropertyChangedFor(nameof(DefaultRouteWarning))]
    [NotifyPropertyChangedFor(nameof(TargetsDefaultRoute))]
    public partial AdapterOption? TransmitAdapter { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    [NotifyPropertyChangedFor(nameof(DefaultRouteWarning))]
    [NotifyPropertyChangedFor(nameof(TargetsDefaultRoute))]
    public partial AdapterOption? ReceiveAdapter { get; set; }

    /// <summary>
    /// Set by the user to acknowledge that a selected adapter carries the machine's default route.
    /// </summary>
    /// <remarks>
    /// Cleared whenever the selection changes, so an acknowledgement never carries over to an
    /// adapter it was not given for.
    /// </remarks>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    public partial bool DefaultRouteAcknowledged { get; set; }

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

    /// <summary>
    /// True when either end of the run is the adapter carrying this machine's default route.
    /// </summary>
    /// <remarks>
    /// Hardware only - a simulated run puts nothing on any wire.
    /// </remarks>
    public bool TargetsDefaultRoute =>
        Source == EngineSource.Hardware
        && ((TransmitAdapter?.CarriesDefaultRoute ?? false)
            || (ReceiveAdapter?.CarriesDefaultRoute ?? false));

    /// <summary>
    /// The hazard text for a default-route target, or null when none is selected.
    /// </summary>
    /// <remarks>
    /// Worded to match <see cref="RunSafety"/>'s DefaultRoute warning, which the Rig page shows,
    /// so the two pages describe the same hazard the same way. It is not produced by it: RunSafety
    /// takes <see cref="NetworkAdapterInfo"/> and this view model keeps only the projected
    /// AdapterOption, so routing Lab Mode through it means carrying the full adapter here - worth
    /// doing when the orchestrator needs the other hazards too, and not before. Lab Mode consulted
    /// neither: it discarded
    /// <c>CarriesDefaultRoute</c> when projecting adapters and had no confirmation step at all, so
    /// a first visit - which auto-selects the first two adapters - could put near-line-rate raw
    /// traffic on the NIC carrying the user's network, and their remote session with it, without
    /// anything having been said. The disruption policy allows testing that adapter; it requires
    /// the warning to be loud first.
    /// </remarks>
    public string? DefaultRouteWarning
    {
        get
        {
            if (!TargetsDefaultRoute)
            {
                return null;
            }

            var name = (TransmitAdapter?.CarriesDefaultRoute ?? false)
                ? TransmitAdapter!.Label
                : ReceiveAdapter!.Label;

            return $"{name} carries this machine's default route. Running traffic across it will "
                + "interrupt internet access, remote sessions, and network drives for the duration "
                + "of the run.";
        }
    }

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
    /// <remarks>
    /// Also false while a run is going. The engine captured its link speed at Start and sizes and
    /// validates the whole run against that, so a picker still live mid-run lets the number on
    /// screen drift away from the number being measured against - the same disagreement between
    /// the displayed rate and the used rate that <see cref="AdoptNegotiatedLinkSpeed"/> exists to
    /// prevent, arriving through the other door. Hardware with a negotiated speed was already
    /// locked; this covers simulated runs and hardware that reports no rate.
    /// </remarks>
    public bool LinkSpeedIsChosen =>
        IsIdle && (Source == EngineSource.Simulated || TransmitAdapter?.NegotiatedSpeed is null);

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
    /// <remarks>
    /// The engine answers only while a run is going; otherwise the selected source does. Deferring
    /// to <c>_engine</c> whenever one existed looked equivalent and was not: Stop leaves the
    /// stopped engine in place, so after a hardware run the banner went on reporting that run's
    /// mode. Switching to Simulated while idle left the warning hidden and switching back left it
    /// showing, in both cases until the next Start replaced the engine - and the wrong half of
    /// that is a simulated run presented as though it came off the wire.
    /// <para>
    /// While a run *is* going the engine is the authority, because what is producing the numbers
    /// matters more than what the picker says.
    /// </para>
    /// </remarks>
    public bool IsSimulated =>
        IsRunning ? _engine?.IsSimulated ?? false : Source == EngineSource.Simulated;

    /// <summary>The engine the pump should poll, or null when idle.</summary>
    public IPacketEngine? Engine => _engine;

    /// <summary>Raised when a run starts, so the view can reset its plots.</summary>
    public event EventHandler? RunStarted;

    /// <summary>
    /// Lists the adapters a run could use. Called on navigation rather than in the constructor so
    /// the enumeration - which reads real hardware - is not on the UI thread's construction path.
    /// </summary>
    /// <summary>
    /// Refreshes the adapter list, keeping the current selections where they still exist.
    /// </summary>
    /// <returns>
    /// False when enumeration failed, so a caller that is about to act on the result can stop.
    /// Browsing the page can shrug this off and show the message; starting a run cannot, because
    /// the run would be measured against whatever stale link speed the old objects carried.
    /// </returns>
    public async Task<bool> LoadAdaptersAsync()
    {
        if (IsRunning)
        {
            return true;
        }

        try
        {
            var found = await _provider.GetPhysicalAdaptersAsync();

            // Checked again on the way out of the await. The guard at the top of this method ran
            // before it, and enumeration is slow enough for a run to start meanwhile - the page is
            // cached, so OnNavigatedTo fires a refresh on every visit while old selections keep
            // Start enabled. Replacing the selections during a live run makes the UI name adapters
            // the engine is not using and can rewrite the displayed link speed, while the engine
            // goes on measuring against the pair and rate it captured at Start.
            if (IsRunning)
            {
                return true;
            }

            var previousTransmit = TransmitAdapter?.Id;
            var previousReceive = ReceiveAdapter?.Id;

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
                        adapter.NegotiatedSpeed,
                        adapter.CarriesDefaultRoute)
                    {
                        Mac = mac.GetAddressBytes(),
                    });
                }
            }

            // Restored by id, because Clear() above emptied the ComboBoxes and the two-way bindings
            // wrote null back into both selections. Without this the `??=` fallback re-picked the
            // first two adapters on every visit to the page, so a deliberate choice was silently
            // replaced and the next run transmitted on a different NIC.
            // Both resolved before either is assigned. Assigning transmit first let its fallback
            // land on the adapter receive was about to restore; the receive assignment then fired
            // SwapIfBothEndsAreTheSame with nothing to displace, which cleared transmit again. The
            // pair has to be worked out as a pair.
            var transmit = Adapters.FirstOrDefault(a => a.Id == previousTransmit);
            var receive = Adapters.FirstOrDefault(a => a.Id == previousReceive);

            // The rig is two adapters wired to each other, so the first run needs no choosing.
            // A surviving selection is never displaced to make room for a fallback.
            transmit ??= Adapters.FirstOrDefault(a => a.Id != receive?.Id);
            receive ??= Adapters.FirstOrDefault(a => a.Id != transmit?.Id);

            TransmitAdapter = transmit;
            ReceiveAdapter = receive;
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Could not list adapters: {ex.Message}";
            return false;
        }

        return true;
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

    /// <summary>
    /// Held for the length of a hardware run, so topology detection cannot bounce the link
    /// underneath it. Null for a simulated run, which touches no adapter.
    /// </summary>
    private IDisposable? _hardware;

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync()
    {
        ErrorMessage = null;

        try
        {
            // Claimed before anything is opened, not after the run is under way. The Rig page's
            // topology probe forces *SpeedDuplex on these same adapters, and its page is a
            // navigation away - so the window between "the user pressed Start" and "the engine is
            // sampling" is exactly when a link bounce would be invisible and fatal to the run.
            if (Source == EngineSource.Hardware)
            {
                _hardware = HardwareSession.TryClaim("a Lab Mode run");

                if (_hardware is null)
                {
                    ErrorMessage =
                        $"The adapters are in use by {HardwareSession.Holder}. Wait for it to "
                        + "finish, or stop it, before starting a run.";
                    return;
                }
            }

            if (!await ConfirmRigUnchangedAsync())
            {
                return;
            }

            await DisposeEngineAsync();
            _engine = CreateEngine();

            if (_engine is null)
            {
                ErrorMessage = "No packet engine is available in this build.";
                return;
            }

            await _engine.StartAsync(new EngineRunSettings
            {
                LinkSpeed = LinkSpeed,
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
        finally
        {
            // Every early return out of the try leaves IsRunning false without ever having set it -
            // a rig that changed under the user, a build with no engine - so the property-change
            // hook that normally releases the claim never fires. A leaked claim locks topology
            // detection out for the life of the process with nothing on screen to explain it, so
            // the release is anchored to the outcome rather than to any particular exit.
            //
            // Through ReleaseHardware, not by disposing the claim directly. Disposing it here
            // ignored the one condition that decides whether the adapters are actually free, and
            // there is a path to it: a failed Stop keeps the engine and the claim, the user then
            // switches to Simulated and presses Start, this method skips TryClaim because the
            // source is not Hardware, its cleanup fails again - and the direct dispose handed the
            // adapters away while the old native engine was still transmitting.
            if (!IsRunning)
            {
                ReleaseHardware();
            }
        }
    }

    /// <summary>
    /// Re-reads the rig immediately before a run and confirms it is still the one that was chosen.
    /// </summary>
    /// <returns>False when the run must not proceed; <see cref="ErrorMessage"/> says why.</returns>
    /// <remarks>
    /// <para>
    /// An <c>AdapterOption</c> carries the speed the link had negotiated when the page last
    /// loaded, and on a bench the cable is plugged and unplugged between visits. Load the page
    /// unplugged and the negotiated speed is null, so <see cref="LinkSpeed"/> keeps its 1 Gbps
    /// default; plug into a 2.5 G partner and press Start, and healthy traffic gets measured
    /// against a threshold two and a half times too low. That reads as
    /// <c>TransmitExceedsLineRate</c> - a fault that tears down a good run reporting discarded
    /// frames, which is the opposite of what is happening.
    /// </para>
    /// <para>
    /// The refresh restores selections by id and falls back to whatever is present when an id has
    /// gone. That fallback is right for someone browsing the page and wrong here: it would begin
    /// line-rate raw transmission on an adapter nobody chose, and on a machine whose other NIC
    /// carries the default route that is a network outage arriving without a prompt. Vanishing
    /// between page load and Start is exactly what a USB adapter on a bench does.
    /// </para>
    /// <para>
    /// Hardware only. A simulated run touches no adapter, so enumerating them would be a WMI round
    /// trip spent on nothing - and would fail the comparison the moment the refresh filled in a
    /// selection that was legitimately empty.
    /// </para>
    /// </remarks>
    private async Task<bool> ConfirmRigUnchangedAsync()
    {
        if (Source != EngineSource.Hardware)
        {
            return true;
        }

        var intendedTransmit = TransmitAdapter?.Id;
        var intendedReceive = ReceiveAdapter?.Id;

        if (!await LoadAdaptersAsync())
        {
            // ErrorMessage already says why. Continuing would measure against the stale objects,
            // and against whatever link speed they were still carrying.
            return false;
        }

        if (TransmitAdapter?.Id != intendedTransmit || ReceiveAdapter?.Id != intendedReceive)
        {
            ErrorMessage =
                "The selected adapters changed while the page was open. Check the selection and "
                + "start again.";
            return false;
        }

        // The safety gate again, against the refreshed flags. CanStart enabled the button using
        // the metadata the page loaded with, and an adapter can acquire the default route between
        // then and now - Windows re-homing the route when another link drops, a VPN going away.
        // The ids still match, so the check above passes; the refresh even clears the
        // acknowledgement, because assigning the selections raises the changed handlers. Without
        // this the run started anyway, unacknowledged, on the NIC now carrying the network.
        if (TargetsDefaultRoute && !DefaultRouteAcknowledged)
        {
            ErrorMessage =
                $"{DefaultRouteWarning} Confirm the warning above and start again.";
            return false;
        }

        AdoptNegotiatedLinkSpeed();
        return true;
    }

    /// <summary>
    /// Adopts the negotiated rate as the run's link speed whenever the rig can supply one.
    /// </summary>
    /// <remarks>
    /// The run used to be measured against the negotiated speed while the greyed picker went on
    /// showing the constructor default. On a 100 Mbps link the screen said 1 Gbps and the run was
    /// graded against 100 - the number on display and the number in use were different, which is
    /// the exact dishonesty this app exists to avoid. One value now, shown and used.
    /// </remarks>
    private void AdoptNegotiatedLinkSpeed()
    {
        if (Source == EngineSource.Hardware && TransmitAdapter?.NegotiatedSpeed is { } negotiated)
        {
            LinkSpeed = negotiated;
        }
    }

    partial void OnSourceChanged(EngineSource value) => AdoptNegotiatedLinkSpeed();

    partial void OnTransmitAdapterChanged(AdapterOption? oldValue, AdapterOption? newValue)
    {
        ClearAcknowledgementIfAdapterChanged(oldValue, newValue);
        AdoptNegotiatedLinkSpeed();
        SwapIfBothEndsAreTheSame(oldValue, newValue, receiveChanged: false);
    }

    partial void OnReceiveAdapterChanged(AdapterOption? oldValue, AdapterOption? newValue)
    {
        ClearAcknowledgementIfAdapterChanged(oldValue, newValue);
        SwapIfBothEndsAreTheSame(oldValue, newValue, receiveChanged: true);
    }

    /// <summary>
    /// Drops a default-route acknowledgement when the selection moves to a different adapter.
    /// </summary>
    /// <remarks>
    /// Compared by id, not by object. Clearing on every assignment made the confirmation
    /// impossible to satisfy rather than merely strict: Start refreshes the adapter list first, the
    /// refresh assigns the selections, assignment raises these handlers, and the acknowledgement
    /// the user had just given was gone before the gate read it. Ticking the box and pressing
    /// Start again repeated the cycle, so a default-route run could never begin at all - a safety
    /// gate that had stopped being a gate and become a wall.
    /// <para>
    /// The acknowledgement is about an adapter, so the identity of the adapter is what it should
    /// follow. A refreshed option describing the same NIC is the same consent; a different NIC is
    /// not, and still clears.
    /// </para>
    /// </remarks>
    private void ClearAcknowledgementIfAdapterChanged(AdapterOption? oldValue, AdapterOption? newValue)
    {
        if (oldValue?.Id != newValue?.Id)
        {
            DefaultRouteAcknowledged = false;
        }
    }

    /// <summary>
    /// Moves the displaced adapter to the other end rather than leaving both ends the same.
    /// </summary>
    /// <remarks>
    /// Picking the adapter that is already at the far end is how someone asks to test the other
    /// direction, and on this rig the direction genuinely matters - the same cable measures four
    /// times faster one way than the other at 64-byte frames. Without the swap that click produced
    /// a pair the engine refuses, surfacing only as a Start button that had quietly greyed out.
    /// </remarks>
    private void SwapIfBothEndsAreTheSame(
        AdapterOption? displaced, AdapterOption? chosen, bool receiveChanged)
    {
        var other = receiveChanged ? TransmitAdapter : ReceiveAdapter;

        if (_swapping || chosen is null || other is null || chosen.Id != other.Id)
        {
            return;
        }

        _swapping = true;
        try
        {
            if (receiveChanged)
            {
                TransmitAdapter = displaced;
            }
            else
            {
                ReceiveAdapter = displaced;
            }
        }
        finally
        {
            _swapping = false;
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
            // The engine reference is deliberately kept. A disposal that threw has not proven the
            // transmitter stopped, and ReleaseHardware reads this field to decide whether the
            // adapters are free - so nulling it here would hand them to topology detection on the
            // strength of a teardown that failed.
            reason = $"{reason} The engine also failed to shut down: {ex.Message} The adapters are "
                + "still treated as in use; restart the app to clear this.";
        }
        finally
        {
            ReleaseHardware();
        }

        IsRunning = false;
        OnPropertyChanged(nameof(IsSimulated));
        ErrorMessage = reason
            ?? "The engine faulted and the run was stopped. The readings above are stale.";
    }

    /// <summary>
    /// Stops the run, guarded the same way <see cref="StartAsync"/> is.
    /// </summary>
    /// <remarks>
    /// The asymmetry was the defect: start caught, stop did not, and both are
    /// <c>AsyncRelayCommand</c> bodies whose faulted task escapes to the UI thread and terminates
    /// the app with no message. Today's native engine reports through <c>Fault</c> rather than
    /// throwing from <c>StopAsync</c>, so this was a latent hole rather than a live crash - which
    /// is exactly the kind that gets opened by a change somewhere else entirely.
    /// <para>
    /// <c>IsRunning</c> is cleared either way. A stop that threw has still left the UI unable to
    /// claim a run is in progress, and leaving the button in its running state would strand the
    /// user with no way to try again.
    /// </para>
    /// </remarks>
    [RelayCommand(CanExecute = nameof(CanStop))]
    private async Task StopAsync()
    {
        try
        {
            if (_engine is not null)
            {
                await _engine.StopAsync();

                // Disposed rather than merely dereferenced: nulling the field alone would strand
                // the native handle for the life of the process. This is also what releases the
                // adapter claim, since it is the only place the engine is confirmed gone.
                await DisposeEngineAsync();
            }
        }
        catch (Exception ex)
        {
            // The engine reference is kept, not nulled: it may still be transmitting, and
            // ReleaseHardware reads that reference to decide whether the adapters are free.
            // The engine reference is deliberately kept. It may still be transmitting, and
            // ReleaseHardware reads that reference to decide whether the adapters are free - so
            // holding it is what keeps topology detection from forcing a speed under live traffic.
            // The cost is a dead end, so the remedy is named rather than left to be discovered.
            ErrorMessage =
                $"The run did not stop cleanly: {ex.Message} The adapters are still treated as in "
                + "use, so no new run or topology detection can start. Restart the app to clear "
                + "this; any adapter settings a run changed are in the restore journal and will be "
                + "put back on the next launch.";
        }
        finally
        {
            IsRunning = false;
        }
    }

    /// <summary>
    /// Released on every path out of a run.
    /// </summary>
    /// <remarks>
    /// Hooked to <see cref="IsRunning"/> rather than written into each exit, because there are
    /// three - a start that threw, the fault handler, and Stop - and a claim leaked on any one of
    /// them would lock topology detection out for the life of the process with no way to clear it.
    /// One place that cannot be forgotten beats three that can.
    /// </remarks>
    partial void OnIsRunningChanged(bool value)
    {
        if (!value)
        {
            ReleaseHardware();
        }
    }

    /// <summary>
    /// Releases the adapter claim, but only once the engine is genuinely gone.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A stopped UI is not a stopped engine.</b> Both <c>StopAsync</c> and the fault handler
    /// clear <see cref="IsRunning"/> unconditionally - deliberately, because leaving the button in
    /// its running state would strand the user with no way to try again - and both can reach that
    /// point with <c>_engine.StopAsync()</c> having thrown. This file's own remarks treat that as
    /// realistic: "An engine that will not shut down is worth reporting." A wedged engine may still
    /// be transmitting at line rate.
    /// </para>
    /// <para>
    /// Releasing the claim there would light up the Detect button and let topology detection force
    /// <c>*SpeedDuplex</c> under live traffic, mid-fault - exactly the collision the claim exists to
    /// prevent, arriving at the worst possible moment. So the claim outlives the flag: it is
    /// released when <c>_engine</c> is null, and held otherwise with the holder string saying why.
    /// </para>
    /// </remarks>
    private void ReleaseHardware()
    {
        if (_engine is not null)
        {
            return;
        }

        _hardware?.Dispose();
        _hardware = null;
    }

    private bool CanStart() =>
        !IsRunning &&
        // The acknowledgement gates the button rather than producing an error after the fact,
        // because by the time a run has started the disruption has already happened.
        (!TargetsDefaultRoute || DefaultRouteAcknowledged) &&
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
            ReleaseHardware();
        }
    }
}
