using System.Net;
using System.Net.Http.Headers;

namespace AJBrowser.Core.Tests.Downloads;

/// <summary>
/// An in-memory HTTP "server" for downloader tests: serves <see cref="Content"/>, optionally honours Range/If-Range,
/// and can fail in scripted ways (status codes, a connection drop after N bytes, a stall).
/// </summary>
internal sealed class FakeFileServer : HttpMessageHandler
{
    public byte[] Content { get; set; } = [];
    public string? ETag { get; set; } = "\"v1\"";
    public bool SupportsRanges { get; set; } = true;

    /// <summary>Status codes to return for the next requests instead of serving the file (consumed in order).</summary>
    public Queue<HttpStatusCode> ScriptedStatuses { get; } = new();

    /// <summary>For the next N requests, the body breaks (IOException) after this many bytes.</summary>
    public int? DropAfterBytes { get; set; }
    public int DropCount { get; set; } = 1;

    /// <summary>The next response body never sends data (to test the stall timeout).</summary>
    public bool StallNext { get; set; }

    public List<HttpRequestMessage> Requests { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        if (ScriptedStatuses.TryDequeue(out var status))
        {
            return Task.FromResult(new HttpResponseMessage(status));
        }

        long from = 0;
        var partial = false;
        if (SupportsRanges && request.Headers.Range?.Ranges.FirstOrDefault() is { From: { } start })
        {
            var ifRange = request.Headers.IfRange?.EntityTag?.ToString();
            if (ifRange is null || ifRange == ETag)
            {
                from = start;
                partial = true;
            }
        }

        var body = Content.AsMemory((int)from).ToArray();
        Stream stream = new MemoryStream(body);
        if (StallNext)
        {
            StallNext = false;
            stream = new StallingStream();
        }
        else if (DropAfterBytes is { } drop && DropCount > 0)
        {
            DropCount--;
            stream = new BreakingStream(body, drop);
        }

        var response = new HttpResponseMessage(partial ? HttpStatusCode.PartialContent : HttpStatusCode.OK)
        {
            Content = new StreamContent(stream),
        };
        response.Content.Headers.ContentLength = body.Length;
        if (partial)
        {
            response.Content.Headers.ContentRange = new ContentRangeHeaderValue(from, Content.Length - 1, Content.Length);
        }

        if (SupportsRanges)
        {
            response.Headers.AcceptRanges.Add("bytes");
        }

        if (ETag is not null)
        {
            response.Headers.ETag = EntityTagHeaderValue.Parse(ETag);
        }

        return Task.FromResult(response);
    }

    public static byte[] MakeContent(int length)
    {
        var bytes = new byte[length];
        new Random(42).NextBytes(bytes);
        return bytes;
    }

    /// <summary>Delivers the first N bytes, then throws like a dropped connection.</summary>
    private sealed class BreakingStream(byte[] data, int breakAfter) : Stream
    {
        private int _position;

        public override int Read(byte[] buffer, int offset, int count) => ReadCore(buffer.AsSpan(offset, count));

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(ReadCore(buffer.Span));

        private int ReadCore(Span<byte> destination)
        {
            if (_position >= breakAfter)
            {
                throw new IOException("connection reset (fake)");
            }

            var n = Math.Min(Math.Min(destination.Length, breakAfter - _position), data.Length - _position);
            data.AsSpan(_position, n).CopyTo(destination);
            _position += n;
            return n;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => data.Length;
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>Never returns data until cancelled.</summary>
    private sealed class StallingStream : Stream
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => 0;
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
