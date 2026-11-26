namespace AJBrowser.Core.Downloads;

/// <summary>
/// Download speed and time left, smoothed over the last few seconds so the numbers don't jump around.
/// Feed it (time, bytes received so far) samples; it keeps those inside <see cref="Window"/> and computes the
/// average speed across them. Not thread-safe: one instance per download, used from one place.
/// </summary>
public sealed class TransferRateEstimator
{
    private readonly Queue<(DateTime Time, long Bytes)> _samples = new();

    // The newest sample, kept separately: Queue has no O(1) "last", and LINQ's Last() walks the whole queue, which
    // added up with WebView2's frequent progress events (hundreds of samples in the window).
    private (DateTime Time, long Bytes) _last;

    public TransferRateEstimator(TimeSpan? window = null) => Window = window ?? TimeSpan.FromSeconds(5);

    public TimeSpan Window { get; }

    /// <summary>Adds a sample. Going backwards in bytes (a restart from zero) clears the history.</summary>
    public void Add(DateTime timeUtc, long bytesReceived)
    {
        if (_samples.Count > 0 && bytesReceived < _last.Bytes)
        {
            _samples.Clear();
        }

        _last = (timeUtc, bytesReceived);
        _samples.Enqueue(_last);
        while (_samples.Count > 2 && timeUtc - _samples.Peek().Time > Window)
        {
            _samples.Dequeue();
        }
    }

    public void Reset() => _samples.Clear();

    /// <summary>Bytes per second over the window, or null until there are two samples at least 0.25 s apart.</summary>
    public double? BytesPerSecond
    {
        get
        {
            if (_samples.Count < 2)
            {
                return null;
            }

            var first = _samples.Peek();
            var seconds = (_last.Time - first.Time).TotalSeconds;
            return seconds < 0.25 ? null : Math.Max(0, (_last.Bytes - first.Bytes) / seconds);
        }
    }

    /// <summary>Time left for <paramref name="totalBytes"/>, or null if the total or the speed is unknown (or zero).</summary>
    public TimeSpan? Remaining(long? totalBytes)
    {
        if (totalBytes is not > 0 || _samples.Count == 0 || BytesPerSecond is not { } rate || rate <= 0)
        {
            return null;
        }

        var left = Math.Max(0, totalBytes.Value - _last.Bytes);
        var seconds = left / rate;
        return seconds > TimeSpan.FromDays(30).TotalSeconds ? null : TimeSpan.FromSeconds(Math.Ceiling(seconds));
    }
}
