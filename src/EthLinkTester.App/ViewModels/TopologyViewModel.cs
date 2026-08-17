using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EthLinkTester.Core.Adapters;
using EthLinkTester.Core.Preflight;
using EthLinkTester.Core.Topology;
using Microsoft.UI.Xaml.Controls;

namespace EthLinkTester.App.ViewModels;

/// <summary>
/// One signal's result as the page shows it.
/// </summary>
/// <remarks>
/// <b>Every field here is text.</b> The plan makes this non-negotiable for a tool that grades
/// things: a verdict must never be carried by colour alone, so the glyph is decoration beside a
/// label that says the same thing, and a screen reader gets the whole finding from
/// <see cref="Announcement"/> without seeing either.
/// </remarks>
/// <param name="Signal">Which signal this is, in words.</param>
/// <param name="Status">What it found, or that it did not run.</param>
/// <param name="Detail">The reason, which is what makes a verdict arguable.</param>
/// <param name="Glyph">Segoe Fluent icon, alongside the status text and never instead of it.</param>
internal sealed record TopologyObservationViewModel(
    string Signal,
    string Status,
    string Detail,
    string Glyph)
{
    public string Announcement => $"{Signal}: {Status}. {Detail}";

    public static TopologyObservationViewModel From(TopologyObservation observation) => new(
        Spaced(observation.Signal.ToString()),
        Describe(observation),
        observation.Detail,
        Glyphs(observation));

    /// <summary>
    /// "Not run" and "ran and settled nothing" are different facts and must read as different ones.
    /// </summary>
    /// <remarks>
    /// A report that cannot tell a declined test from an inconclusive one is claiming coverage it
    /// does not have - and two of these signals are opt-in precisely because they disrupt the link,
    /// so the distinction is the common case rather than an edge.
    /// </remarks>
    private static string Describe(TopologyObservation observation) => observation switch
    {
        { Ran: false } => "Not run",
        { Finding: TopologyFinding.Inconclusive } => "Settled nothing",
        var o => $"{o.Finding} ({o.Strength.ToString().ToLowerInvariant()})",
    };

    /// <summary>
    /// Segoe Fluent glyphs, written as escapes rather than as literal characters.
    /// </summary>
    /// <remarks>
    /// The private-use codepoints these icons live at do not survive every editor, encoding
    /// conversion or diff view intact - and when one is lost the icon silently becomes an empty
    /// string rather than failing anything. An escape is the same character and cannot be dropped
    /// by a tool that does not understand it.
    /// </remarks>
    private static string Glyphs(TopologyObservation observation) => observation switch
    {
        // Remove: a dash, because a test nobody ran is not a failure.
        { Ran: false } => "\uE738",
        { Finding: TopologyFinding.Bridged } => "\uE7BA",
        { Finding: TopologyFinding.Direct } => "\uE73E",
        _ => "\uE946",
    };

    /// <summary>Turns <c>ReservedMulticastProbe</c> into <c>Reserved multicast probe</c>.</summary>
    private static string Spaced(string name) =>
        string.Concat(name.Select(
            (c, i) => i > 0 && char.IsUpper(c) ? $" {char.ToLowerInvariant(c)}" : $"{c}"));
}

/// <summary>
/// Runs topology detection for the rig and presents the verdict.
/// </summary>
/// <remarks>
/// <para>
/// Separate from <see cref="RigViewModel"/> because it owns something that one does not: a probe
/// that puts frames on the wire and, if permitted, changes an adapter setting and puts it back. The
/// rig page describes hardware; this acts on it.
/// </para>
/// <para>
/// <b>The disruptive tests are off by default and the cost of that is stated, not hidden.</b>
/// Forcing a speed bounces the link for several seconds and needs the restore journal to put the
/// setting back. Without it nothing in the set can positively demonstrate a direct cable, so a
/// healthy direct rig reports an unestablished topology - which is the honest answer and looks like
/// a failure unless the page says why.
/// </para>
/// </remarks>
internal sealed partial class TopologyViewModel : ObservableObject
{
    private readonly IAdapterProvider _provider;
    private readonly IAdapterConfigurator _configurator;
    private readonly ISoftwareBridgeProbe _bridges;
    private readonly Func<IReadOnlyList<NetworkAdapterInfo>, ITopologyProbe> _probeFactory;

    private IReadOnlyList<NetworkAdapterInfo> _pair = [];

    public TopologyViewModel(
        IAdapterProvider provider,
        IAdapterConfigurator configurator,
        ISoftwareBridgeProbe bridges,
        Func<IReadOnlyList<NetworkAdapterInfo>, ITopologyProbe> probeFactory)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(configurator);
        ArgumentNullException.ThrowIfNull(bridges);
        ArgumentNullException.ThrowIfNull(probeFactory);

        _provider = provider;
        _configurator = configurator;
        _bridges = bridges;
        _probeFactory = probeFactory;
    }

    public ObservableCollection<TopologyObservationViewModel> Observations { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DetectCommand))]
    public partial bool IsBusy { get; set; }

    /// <summary>Set when the rig has exactly two probed adapters, which is what this needs.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DetectCommand))]
    public partial bool RigIsComplete { get; set; }

    /// <summary>
    /// Whether the forced-speed probe may run. Off by default; see the type remarks.
    /// </summary>
    [ObservableProperty]
    public partial bool AllowDisruptive { get; set; }

    [ObservableProperty]
    public partial string Headline { get; set; } = "Topology not checked yet.";

    [ObservableProperty]
    public partial string Detail { get; set; } =
        "Detection says whether these two adapters are wired to each other or have a switch "
        + "between them. Nothing downstream can tell the difference, so a grade means nothing "
        + "until this does.";

    [ObservableProperty]
    public partial InfoBarSeverity Severity { get; set; } = InfoBarSeverity.Informational;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? ErrorMessage { get; set; }

    /// <summary>Drives the error bar's visibility, matching how the rig page does it elsewhere.</summary>
    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);

    /// <summary>
    /// Whether a cable grade from this rig could honestly be attributed to the cable.
    /// </summary>
    /// <remarks>
    /// Shown rather than merely acted on, because it is the product question and the answer is
    /// usually "no". A user who cannot see why grading is unavailable will assume the tool is
    /// broken instead of learning that a topology has to be established first.
    /// </remarks>
    [ObservableProperty]
    public partial bool GradingIsAttributable { get; set; }

    [ObservableProperty]
    public partial string GradingNote { get; set; } = "Not established.";

    /// <summary>
    /// Something found in preflight that does not stop the run but has to appear on the report.
    /// </summary>
    /// <remarks>
    /// An unrecognised protocol binding is the case: it may be vendor teaming and it may be
    /// harmless, and enumerating every vendor's component id is a losing game. A maybe belongs on
    /// the report rather than in a refusal.
    /// </remarks>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCaveat))]
    public partial string? Caveat { get; set; }

    public bool HasCaveat => !string.IsNullOrWhiteSpace(Caveat);

    /// <summary>Called by the rig page whenever its adapter list changes.</summary>
    public void SetPair(IReadOnlyList<NetworkAdapterInfo> adapters)
    {
        ArgumentNullException.ThrowIfNull(adapters);

        // The first two, matching what RigViewModel already derives the rig's capabilities from.
        // Requiring exactly two here would have made a three-adapter machine show a complete rig
        // beside a topology panel that refused to run, disagreeing about the same hardware. Picking
        // the pair properly is a Suite Mode feature; agreeing with the page it sits on is today's.
        _pair = [.. adapters.Take(2)];
        RigIsComplete = _pair.Count == 2;

        // A verdict describes the pair it was taken on, so it stops meaning anything the moment the
        // pair changes. Clearing is the only honest response; keeping it would let a reading from
        // one cable stand next to another.
        Observations.Clear();
        Headline = "Topology not checked yet.";
        Severity = InfoBarSeverity.Informational;
        GradingIsAttributable = false;
        GradingNote = "Not established.";
    }

    private bool CanDetect() => RigIsComplete && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanDetect))]
    private async Task DetectAsync()
    {
        IsBusy = true;
        ErrorMessage = null;
        Caveat = null;
        Observations.Clear();

        try
        {
            // Preflight before anything is sent. A vSwitch, a Windows bridge or a team means the
            // frames may never reach a wire at all, and a topology verdict taken through one would
            // describe software while pointing at a cable - which is the failure this whole phase
            // is shaped to avoid. Refused loudly rather than hidden: the adapter is present and
            // working, and someone hunting for a NIC that vanished from a list is worse off than
            // someone told what to unbind.
            foreach (var adapter in _pair)
            {
                var bridge = await _bridges.InspectAsync(adapter.Id);

                if (bridge.BlocksRun)
                {
                    Headline = "Cannot measure this adapter";
                    Detail = bridge.Detail;
                    Severity = InfoBarSeverity.Error;
                    GradingIsAttributable = false;
                    GradingNote = "Not established, and nothing here would describe a cable.";
                    return;
                }

                if (bridge.NeedsCaveat)
                {
                    Caveat = bridge.Detail;
                }
            }

            var detector = new TopologyDetector(_provider, _configurator, _probeFactory(_pair));

            var detection = await detector.DetectAsync(new TopologyDetectionRequest
            {
                TransmitAdapterId = _pair[0].Id,
                ReceiveAdapterId = _pair[1].Id,
                AllowDisruptive = AllowDisruptive,
            });

            var verdict = detection.Verdict;

            // A port this run pinned and could not put back is worth more of the user's attention
            // than the verdict is. The journal will replay it on the next launch, which only helps
            // someone who knows there is something to replay.
            if (detection.RestoreNeedsAttention)
            {
                ErrorMessage = detection.RestoreWarning;
            }

            foreach (var observation in verdict.Observations)
            {
                Observations.Add(TopologyObservationViewModel.From(observation));
            }

            Headline = verdict.Conclusion switch
            {
                TopologyConclusion.Direct => "Wired directly to each other",
                TopologyConclusion.Bridged => "Something is in the path",
                _ => "Topology not established",
            };

            Detail = verdict.Summary;

            // Informational rather than Success for Direct: this is a measurement, and dressing a
            // measurement as a congratulation invites it to be read as a pass.
            Severity = verdict.Conclusion switch
            {
                TopologyConclusion.Bridged => InfoBarSeverity.Warning,
                TopologyConclusion.Direct => InfoBarSeverity.Success,
                _ => InfoBarSeverity.Informational,
            };

            GradingIsAttributable = verdict.GradingIsAttributable;
            GradingNote = verdict.GradingIsAttributable
                ? "A grade from this rig can be attributed to the cable."
                : AllowDisruptive
                    ? "A grade from this rig cannot be attributed to the cable, because nothing "
                      + "that ran established a direct link."
                    : "A grade from this rig cannot be attributed to the cable. The one signal "
                      + "that can demonstrate a direct link is the forced-speed test, which is "
                      + "off.";
        }
        catch (Exception ex)
        {
            // Without this the faulted task escapes AsyncRelayCommand onto the UI thread and
            // terminates the app with no message. On real hardware this is routine rather than
            // exotic: Npcap missing, an adapter unplugged mid-probe, elevation denied.
            ErrorMessage = $"Topology detection failed: {ex.Message}";
            Headline = "Topology not established";
            Severity = InfoBarSeverity.Informational;
        }
        finally
        {
            IsBusy = false;
        }
    }
}
