using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ScottPlot.Plottables;
using ScottPlot.WinUI;

namespace EthLinkTester.App.Controls;

/// <summary>
/// A rolling two-series line chart for live telemetry.
/// </summary>
/// <remarks>
/// <para>
/// Two series and no more. Throughput and latency get their own StripChart rather than sharing
/// one with two y-axes: they have no common scale, and a dual-axis plot invites the reader to
/// see a relationship that the geometry invented.
/// </para>
/// <para>
/// Built in code rather than XAML because both instances need identical configuration, and a
/// shared setup method is easier to keep honest than two XAML blocks that drift apart.
/// </para>
/// </remarks>
internal sealed class StripChart : UserControl
{
    /// <summary>
    /// 1200 samples at the engine's 60 Hz is twenty seconds of history - long enough to see a
    /// trend develop, short enough that a transient is still visible rather than averaged into
    /// a flat line.
    /// </summary>
    private const int CapacitySamples = 1_200;

    /// <summary>
    /// The x-axis is sample index divided by this, <b>not</b> elapsed time.
    /// </summary>
    /// <remarks>
    /// A known limitation, deliberately left until Phase 3 rather than guessed at now. Every
    /// sample carries a real monotonic <c>TelemetrySample.TimestampTicks</c> and this control
    /// discards it, so screen-time equals wall-clock only while the producer runs at exactly this
    /// rate. Two ways that breaks on an instrument: a native engine whose sample rate drifts, and
    /// a ring-buffer overflow, where the dropped window is appended with no gap marker - so a
    /// stretch of screen silently represents more history than it appears to.
    /// <para>
    /// Not fixed here because it cannot be validated here. The only producer today is the
    /// simulator, which emits at exactly this rate by construction, so a time-indexed axis would
    /// be untestable code written against a source that can never exercise it. Phase 3 brings both
    /// the varying rate and the overflow signal that make the fix verifiable, and it is tracked as
    /// engine work rather than chart work for that reason.
    /// </para>
    /// </remarks>
    private const double SampleIntervalSeconds = 1.0 / 60.0;
    private const float SeriesLineWidth = 2;

    private readonly WinUIPlot _plot = new();
    private readonly DataStreamer _series1;
    private readonly DataStreamer _series2;
    private readonly string _yLabel;

    public StripChart(string yLabel, string series1Label, string series2Label)
    {
        _yLabel = yLabel;

        _series1 = _plot.Plot.Add.DataStreamer(CapacitySamples, SampleIntervalSeconds);
        _series2 = _plot.Plot.Add.DataStreamer(CapacitySamples, SampleIntervalSeconds);

        foreach (var (streamer, label) in new[] { (_series1, series1Label), (_series2, series2Label) })
        {
            streamer.LineWidth = SeriesLineWidth;
            streamer.LegendText = label;
            streamer.ManageAxisLimits = true;

            // The default view overwrites in place left-to-right, which makes the newest data
            // hard to find. Scrolling keeps "now" pinned at the right edge.
            streamer.ViewScrollLeft();
        }

        Content = _plot;
        ApplyTheme();

        ActualThemeChanged += (_, _) => ApplyTheme();
        Loaded += (_, _) => _plot.DetectDisplayScale();
    }

    /// <summary>
    /// The window the plot belongs to. ScottPlot's save-image menu dereferences this without a
    /// null check, so right-clicking the chart throws unless it is set.
    /// </summary>
    public Window? OwnerWindow
    {
        get => _plot.AppWindow;
        set => _plot.AppWindow = value;
    }

    public void Append(double series1Value, double series2Value)
    {
        _series1.Add(series1Value);
        _series2.Add(series2Value);
    }

    /// <summary>
    /// Redraws. Call once per frame after appending a whole batch, never once per sample -
    /// Refresh invalidates the canvas, so per-sample calls would queue 60 repaints a second to
    /// show one frame's worth of change.
    /// </summary>
    public void Render() => _plot.Refresh();

    public void Clear()
    {
        _series1.Clear();
        _series2.Clear();
        _plot.Refresh();
    }

    private void ApplyTheme()
    {
        var scheme = VizPalette.For(ActualTheme == ElementTheme.Dark);
        var plot = _plot.Plot;

        var surface = ScottPlot.Color.FromHex(scheme.Surface);
        var axis = ScottPlot.Color.FromHex(scheme.Axis);
        var ink = ScottPlot.Color.FromHex(scheme.Ink);

        plot.FigureBackground.Color = surface;
        plot.DataBackground.Color = surface;

        // Grid and axes stay recessive; the data is what should carry contrast.
        plot.Grid.MajorLineColor = ScottPlot.Color.FromHex(scheme.Gridline);
        plot.Grid.MinorLineColor = ScottPlot.Color.FromHex(scheme.Gridline);
        plot.Axes.Color(axis);

        _series1.Color = ScottPlot.Color.FromHex(scheme.Series1);
        _series2.Color = ScottPlot.Color.FromHex(scheme.Series2);

        plot.XLabel("Seconds");
        plot.YLabel(_yLabel);

        // Two series always carry a legend: identity must never rest on colour alone.
        plot.ShowLegend();
        plot.Legend.BackgroundColor = surface;
        plot.Legend.FontColor = ink;
        plot.Legend.OutlineColor = axis;

        _plot.Refresh();
    }
}
