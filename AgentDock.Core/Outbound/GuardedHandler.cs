using System.Diagnostics;
using System.Net;
using Microsoft.Extensions.Logging;

namespace AgentDock.Core.Outbound;

public sealed class OutboundHttpClientOptions
{
    public required Uri BaseUrl { get; init; }
    public OutboundCredential? Credential { get; init; }
    public IReadOnlyDictionary<string, string> StaticHeaders { get; init; } = new Dictionary<string, string>();
    public int MaxResponseBytes { get; init; } = 2 * 1024 * 1024;
}

/// <summary>
/// For SDKs that need a real HttpClient (MCP). Applies the same per-request rules as <see cref="OutboundClient"/>:
/// URL validation, origin-bound credential and static headers, no redirects (a 3xx is an error), a response byte cap,
/// and redacted logging. DNS/IP enforcement happens in the inner SafeOutboundHandler.
/// </summary>
internal sealed class GuardedHandler(HttpMessageHandler inner, OutboundOptions options, OutboundHttpClientOptions client, ILogger logger) : DelegatingHandler(inner)
{
    private static readonly HashSet<string> Forbidden = new(StringComparer.OrdinalIgnoreCase)
    { "Host", "Proxy-Authorization", "Proxy-Connection", "Cookie", "Upgrade" };

    protected override void Dispose(bool disposing) { }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var url = request.RequestUri ?? throw new OutboundException(OutboundErrors.InvalidRequest, "The URL must be absolute.");
        var sw = Stopwatch.StartNew();
        var origin = OutboundClient.Origin(url);
        try
        {
            if (OutboundUrlValidator.Validate(url, options) is { } blocked) throw blocked;
            foreach (var h in Forbidden) request.Headers.Remove(h);
            request.Headers.Remove("Authorization");
            if (OutboundUrlValidator.SameOrigin(url, client.BaseUrl))
            {
                foreach (var (k, v) in client.StaticHeaders) request.Headers.TryAddWithoutValidation(k, v);
                if (client.Credential != null) request.Headers.TryAddWithoutValidation(client.Credential.HeaderName, client.Credential.Value);
            }

            HttpResponseMessage response;
            try { response = await base.SendAsync(request, ct); }
            catch (HttpRequestException e) when (Unwrap(e) is { } oe) { throw oe; }
            catch (HttpRequestException e) { throw new OutboundException(OutboundErrors.ConnectionFailed, "The request failed.", e); }

            if ((int)response.StatusCode is >= 300 and < 400)
            {
                response.Dispose();
                throw new OutboundException(OutboundErrors.RedirectNotAllowed, "Redirects are not followed for this call.");
            }
            if (response.Content.Headers.ContentLength > client.MaxResponseBytes)
            {
                response.Dispose();
                throw new OutboundException(OutboundErrors.ResponseTooLarge, "The response is too large.");
            }
            response.Content = new CappedContent(response.Content, client.MaxResponseBytes);
            logger.LogInformation("Outbound {Method} {Origin} -> {Status} in {ElapsedMs} ms", request.Method.Method, origin, (int)response.StatusCode, sw.ElapsedMilliseconds);
            return response;
        }
        catch (OutboundException e)
        {
            logger.LogWarning("Outbound {Method} {Origin} failed: {Code} after {ElapsedMs} ms", request.Method.Method, origin, e.Code, sw.ElapsedMilliseconds);
            throw;
        }
    }

    private static OutboundException? Unwrap(Exception e)
    {
        for (var x = e.InnerException; x != null; x = x.InnerException) if (x is OutboundException oe) return oe;
        return null;
    }

    private sealed class CappedContent : HttpContent
    {
        private readonly HttpContent _inner;
        private readonly int _max;

        public CappedContent(HttpContent inner, int max)
        {
            _inner = inner; _max = max;
            foreach (var h in inner.Headers) Headers.TryAddWithoutValidation(h.Key, h.Value);
        }

        protected override async Task<Stream> CreateContentReadStreamAsync() => new CappedStream(await _inner.ReadAsStreamAsync(), _max);
        protected override async Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken) => new CappedStream(await _inner.ReadAsStreamAsync(cancellationToken), _max);
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            await using var s = await CreateContentReadStreamAsync();
            await s.CopyToAsync(stream);
        }
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override void Dispose(bool disposing) { if (disposing) _inner.Dispose(); base.Dispose(disposing); }
    }

    private sealed class CappedStream(Stream inner, int max) : Stream
    {
        private long _read;
        public override int Read(byte[] buffer, int offset, int count) => Count(inner.Read(buffer, offset, count));
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => Count(await inner.ReadAsync(buffer, ct));
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();
        private int Count(int n)
        {
            _read += n;
            if (_read > max) throw new OutboundException(OutboundErrors.ResponseTooLarge, "The response is too large.");
            return n;
        }
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
    }

    public HttpClient ToClient(TimeSpan timeout) => new(this, disposeHandler: true) { Timeout = timeout };
}
