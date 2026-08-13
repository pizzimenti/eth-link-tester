using EthLinkTester.Core.Engine;
using Microsoft.UI.Dispatching;
using Windows.Foundation;

namespace EthLinkTester.App.Telemetry;

/// <summary>
/// Receives a batch of samples drained from the engine.
/// </summary>
/// <remarks>
/// A bespoke delegate rather than <c>EventHandler&lt;T&gt;</c> because <see cref="Span{T}"/>
/// cannot be a generic type argument. Keeping the span means the whole path from the engine's
/// buffer to the chart allocates nothing per sample.
/// </remarks>
internal delegate void TelemetryBatchHandler(ReadOnlySpan<TelemetrySample> samples);

/// <summary>
/// Polls an <see cref="IPacketEngine"/> on the UI thread and hands batches to a consumer.
/// </summary>
/// <remarks>
/// <para>
/// Polling rather than subscribing is deliberate. The native engine publishes into an unmanaged
/// ring buffer and cannot call into managed code per sample without destroying the performance
/// that motivated writing it in Rust. A timer that drains whatever accumulated is the shape the
/// real engine requires, so the simulated engine is held to it too.
/// </para>
/// <para>
/// 30 Hz is chosen to be visually smooth while staying well under the engine's 60 Hz production
/// rate, so each tick has something to collect and the buffer never has to grow.
/// </para>
/// </remarks>
internal sealed class TelemetryPump : IDisposable
{
    private const int PollIntervalMilliseconds = 33;

    /// <summary>
    /// Sized for several poll intervals of production so a scheduling hiccup does not force
    /// the pump to leave samples behind and fall further behind on the next tick.
    /// </summary>
    private const int BatchCapacity = 512;

    private readonly TelemetrySample[] _buffer = new TelemetrySample[BatchCapacity];
    private readonly DispatcherQueueTimer _timer;
    private readonly TelemetryBatchHandler _onBatch;

    /// <summary>
    /// Held so Dispose can unsubscribe. Subscribing with an inline lambda would leave the timer
    /// holding the pump, its buffer, and transitively the page, view model, and engine alive
    /// for as long as the timer lives.
    /// </summary>
    private readonly TypedEventHandler<DispatcherQueueTimer, object> _tickHandler;

    private IPacketEngine? _engine;
    private bool _disposed;

    public TelemetryPump(DispatcherQueue dispatcher, TelemetryBatchHandler onBatch)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        _onBatch = onBatch ?? throw new ArgumentNullException(nameof(onBatch));

        _tickHandler = (_, _) => Poll();

        _timer = dispatcher.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(PollIntervalMilliseconds);
        _timer.IsRepeating = true;
        _timer.Tick += _tickHandler;
    }

    /// <summary>
    /// Raised when the engine reports <see cref="EngineState.Faulted"/>. Without this the pump
    /// would just keep draining zero samples forever, leaving a frozen but entirely plausible
    /// chart on screen - which is worse than showing nothing, because it reads as a healthy
    /// link that was never actually measured.
    /// </summary>
    public event EventHandler? EngineFaulted;

    public bool IsRunning => _timer.IsRunning;

    public void Start(IPacketEngine engine)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _timer.Start();
    }

    public void Stop()
    {
        _timer.Stop();
        _engine = null;
    }

    private void Poll()
    {
        var engine = _engine;
        if (engine is null)
        {
            return;
        }

        if (engine.State == EngineState.Faulted)
        {
            Stop();
            EngineFaulted?.Invoke(this, EventArgs.Empty);
            return;
        }

        // Loop so a backlog is cleared within one tick rather than trickling out over several.
        int written;
        while ((written = engine.Drain(_buffer)) > 0)
        {
            _onBatch(_buffer.AsSpan(0, written));

            if (written < _buffer.Length)
            {
                break;
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _timer.Stop();
        _timer.Tick -= _tickHandler;
        _engine = null;
    }
}
