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
    /// correct. <see cref="LabViewModel.ReportFaultAsync"/> handles its own failures, so nothing
    /// can escape to terminate the process.
    /// </summary>
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
