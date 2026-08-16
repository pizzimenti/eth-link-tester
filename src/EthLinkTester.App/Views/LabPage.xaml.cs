using System.ComponentModel;
using EthLinkTester.App.Controls;
using EthLinkTester.App.Telemetry;
using EthLinkTester.App.ViewModels;
using EthLinkTester.Core.Engine;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace EthLinkTester.App.Views;

public sealed partial class LabPage : Page, IDisposable
{
    private readonly TelemetryPump _pump;
    private readonly StripChart _throughputChart;
    private readonly StripChart _latencyChart;
    private bool _disposed;

    internal LabViewModel ViewModel { get; } = new();

    public LabPage()
    {
        InitializeComponent();

        // Separate plots, separate y-axes. Megabits and microseconds share no scale.
        // Axis labels are the short unit rather than the spelled-out name: the rotated y-label
        // gets clipped when the plot is short, and the legend already names the series.
        _throughputChart = new StripChart("Mbps", "Transmit", "Receive")
        {
            OwnerWindow = App.MainWindow,
        };
        _latencyChart = new StripChart("µs", "p50", "p99")
        {
            OwnerWindow = App.MainWindow,
        };

        ThroughputHost.Child = _throughputChart;
        LatencyHost.Child = _latencyChart;

        _pump = new TelemetryPump(DispatcherQueue, OnBatch);
        _pump.EngineFaulted += OnEngineFaulted;
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        ViewModel.RunStarted += OnRunStarted;

        // Deliberately NOT subscribing to Unloaded. The page is cached
        // (NavigationCacheMode="Required") so a run survives the user visiting another section,
        // but Unloaded still fires when the page leaves the visual tree - tearing down there
        // would kill the very run the caching exists to protect. The cached page lives for the
        // application's lifetime and is released with the process.
    }

    /// <summary>
    /// Refreshes the adapter list on every visit rather than once at construction. The page is
    /// cached for the application's lifetime, so a USB adapter plugged in after launch would
    /// otherwise never appear.
    /// </summary>
    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        await ViewModel.LoadAdaptersAsync();
    }

    /// <summary>
    /// <c>async void</c> because it is an event handler, which is the one place the pattern is
    /// correct.
    /// </summary>
    /// <remarks>
    /// <see cref="LabViewModel.ReportFaultAsync"/> guards the engine teardown that can realistically
    /// throw. It is not a blanket guarantee: the property reads, assignments and change
    /// notifications around that guard sit outside it, and an exception from any of them would
    /// escape an <c>async void</c> handler onto the dispatcher and terminate the process. The claim
    /// here used to be that nothing could escape at all, which was a stronger promise than the code
    /// keeps.
    /// </remarks>
    private async void OnEngineFaulted(object? sender, EventArgs e) =>
        await ViewModel.ReportFaultAsync();

    private void OnRunStarted(object? sender, EventArgs e)
    {
        _throughputChart.Clear();
        _latencyChart.Clear();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(LabViewModel.IsRunning))
        {
            return;
        }

        if (ViewModel.IsRunning && ViewModel.Engine is { } engine)
        {
            _pump.Start(engine);
        }
        else
        {
            _pump.Stop();
        }
    }

    /// <summary>
    /// Applies one drained batch: every sample feeds the plots so the trace stays continuous,
    /// but the readouts and the redraw happen once. Refreshing per sample would queue sixty
    /// canvas invalidations a second to show one frame of change.
    /// </summary>
    private void OnBatch(ReadOnlySpan<TelemetrySample> samples)
    {
        if (samples.IsEmpty)
        {
            return;
        }

        // KNOWN LIMITATION - the charts draw straight through a telemetry gap.
        //
        // When the ring overwrites samples the host never read, those samples are counted (the
        // "Telemetry gaps" tile) but not represented here: the next sample is appended right
        // beside the last one, so a seventeen-second hole is drawn as one sample interval and the
        // line joins across it as though the run had been continuous. That is the same misleading
        // continuity the dropped-sample contract exists to expose, surviving in the one place a
        // user actually looks.
        //
        // Not fixed here on purpose. StripChart plots a ScottPlot DataStreamer, which is a
        // fixed-capacity buffer at a fixed sample interval and carries no per-sample timestamp -
        // the compression is inherent to that choice, so honest time needs a timestamped x-axis
        // and a different plot type. The cheap half, appending NaN to break the line, risks
        // DataStreamer deriving NaN axis limits and blanking the chart, and a WinUI 3 window
        // cannot be screenshotted here to check (it captures black under both GDI CopyFromScreen
        // and PrintWindow with PW_RENDERFULLCONTENT), so it would ship unverified.
        //
        // Phase 8 owns Lab Mode's charts. This belongs there, with a rig in front of it.
        foreach (ref readonly var sample in samples)
        {
            _throughputChart.Append(sample.TxMegabitsPerSecond, sample.RxMegabitsPerSecond);
            _latencyChart.Append(sample.LatencyP50Microseconds, sample.LatencyP99Microseconds);
        }

        _throughputChart.Render();
        _latencyChart.Render();

        ViewModel.ApplyLatest(samples[^1]);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _pump.EngineFaulted -= OnEngineFaulted;
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        ViewModel.RunStarted -= OnRunStarted;
        _pump.Dispose();
        ViewModel.Dispose();
    }
}
