using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace AgentDock.Core.Outbound;

public interface IOutboundHttpClientFactory
{
    IOutboundClient Create();
    /// <summary>A guarded HttpClient for SDKs that need one (MCP). Dispose it when done; the shared handler stays alive.</summary>
    HttpClient CreateHttpClient(OutboundHttpClientOptions options, TimeSpan timeout);
}

public sealed class OutboundHttpClientFactory : IOutboundHttpClientFactory, IDisposable
{
    private readonly OutboundClient _client;
    private readonly HttpMessageInvoker _invoker;
    private readonly HttpMessageHandler _handler;
    private readonly OutboundOptions _options;
    private readonly ILogger<OutboundClient> _logger;

    public OutboundHttpClientFactory(OutboundOptions options, IDnsResolver resolver, IIpPolicy policy, ILogger<OutboundClient> logger)
    {
        _options = options; _logger = logger;
        _handler = SafeOutboundHandler.Create(options, resolver, policy);
        _invoker = new HttpMessageInvoker(_handler, disposeHandler: true);
        _client = new OutboundClient(_invoker, options, logger);
    }

    public IOutboundClient Create() => _client;
    public HttpClient CreateHttpClient(OutboundHttpClientOptions options, TimeSpan timeout) =>
        new GuardedHandler(_handler, _options, options, _logger).ToClient(timeout);
    public void Dispose() => _invoker.Dispose();
}

/// <summary>
/// Sends outbound requests with manual redirect handling, per-hop validation, origin-bound credentials,
/// byte-capped bodies and a deadline. Logs only method, scheme://host, status and duration.
/// </summary>
public sealed partial class OutboundClient(HttpMessageInvoker invoker, OutboundOptions options, ILogger<OutboundClient> logger) : IOutboundClient
{
    private static readonly HashSet<string> ForbiddenHeaders = new(StringComparer.OrdinalIgnoreCase)
    { "Host", "Content-Length", "Transfer-Encoding", "Connection", "Upgrade", "Proxy-Authorization", "Proxy-Connection", "Cookie" };

    public async Task<OutboundResponse> SendAsync(OutboundRequest request, OutboundCallOptions? callOptions = null, CancellationToken ct = default)
    {
        callOptions ??= new();
        var sw = Stopwatch.StartNew();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(callOptions.Timeout ?? options.DefaultTimeout);

        var maxRedirects = Math.Min(callOptions.MaxRedirects, options.MaxRedirects);
        var url = request.Url;
        var method = request.Method;
        var body = request.Body;
        var contentType = request.ContentType;
        var redirects = 0;

        try
        {
            foreach (var h in request.Headers.Keys)
                if (ForbiddenHeaders.Contains(h)) throw new OutboundException(OutboundErrors.InvalidRequest, $"Header '{h}' is not allowed.");

            while (true)
            {
                if (OutboundUrlValidator.Validate(url, options) is { } blocked) throw blocked;

                using var msg = new HttpRequestMessage(method, url) { Version = System.Net.HttpVersion.Version11 };
                foreach (var (k, v) in request.Headers) msg.Headers.TryAddWithoutValidation(k, v);
                if (request.Credential != null && request.CredentialBaseUrl != null && OutboundUrlValidator.SameOrigin(url, request.CredentialBaseUrl))
                    msg.Headers.TryAddWithoutValidation(request.Credential.HeaderName, request.Credential.Value);
                if (body != null)
                {
                    msg.Content = new ByteArrayContent(body);
                    if (contentType != null) msg.Content.Headers.TryAddWithoutValidation("Content-Type", contentType);
                }

                HttpResponseMessage response;
                try { response = await invoker.SendAsync(msg, deadline.Token); }
                catch (HttpRequestException e) when (Unwrap(e) is { } oe) { throw oe; }
                catch (HttpRequestException e) { throw new OutboundException(OutboundErrors.ConnectionFailed, "The request failed.", e); }

                using (response)
                {
                    var status = (int)response.StatusCode;
                    if (status is >= 300 and < 400 && response.Headers.Location != null)
                    {
                        if (maxRedirects == 0) throw new OutboundException(OutboundErrors.RedirectNotAllowed, "Redirects are not followed for this call.");
                        if (++redirects > maxRedirects) throw new OutboundException(OutboundErrors.TooManyRedirects, "Too many redirects.");
                        var next = response.Headers.Location.IsAbsoluteUri ? response.Headers.Location : new Uri(url, response.Headers.Location);
                        if (body != null && status is 307 or 308 && !OutboundUrlValidator.SameOrigin(url, next))
                            throw new OutboundException(OutboundErrors.RedirectNotAllowed, "A request body is not forwarded across origins.");
                        if (status is 301 or 302 or 303 && method != HttpMethod.Get && method != HttpMethod.Head)
                        { method = HttpMethod.Get; body = null; contentType = null; }
                        url = next;
                        continue;
                    }

                    var bytes = await ReadCappedAsync(response, callOptions.MaxBytes, deadline.Token);
                    var headers = response.Headers.Concat(response.Content.Headers)
                        .ToDictionary(h => h.Key, h => string.Join(", ", h.Value), StringComparer.OrdinalIgnoreCase);
                    Log(request.Method.Method, Origin(url), status, sw.ElapsedMilliseconds, redirects);
                    return new OutboundResponse(status, headers, bytes, url, redirects);
                }
            }
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            LogFailure(request.Method.Method, Origin(url), OutboundErrors.Timeout, sw.ElapsedMilliseconds);
            throw new OutboundException(OutboundErrors.Timeout, "The request timed out.");
        }
        catch (OutboundException e)
        {
            LogFailure(request.Method.Method, Origin(url), e.Code, sw.ElapsedMilliseconds);
            throw;
        }
    }

    private static OutboundException? Unwrap(Exception e)
    {
        for (var x = e.InnerException; x != null; x = x.InnerException) if (x is OutboundException oe) return oe;
        return null;
    }

    private static async Task<byte[]> ReadCappedAsync(HttpResponseMessage response, int max, CancellationToken ct)
    {
        if (response.Content.Headers.ContentLength > max) throw new OutboundException(OutboundErrors.ResponseTooLarge, $"The response is larger than {max:N0} bytes.");
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var ms = new MemoryStream();
        var buffer = new byte[16 * 1024];
        int n;
        while ((n = await stream.ReadAsync(buffer, ct)) > 0)
        {
            if (ms.Length + n > max) throw new OutboundException(OutboundErrors.ResponseTooLarge, $"The response is larger than {max:N0} bytes.");
            ms.Write(buffer, 0, n);
        }
        return ms.ToArray();
    }

    /// <summary>scheme://host[:port] only; the path and query are never logged.</summary>
    public static string Origin(Uri u) => u.IsAbsoluteUri ? u.GetLeftPart(UriPartial.Authority) : "(relative)";

    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Outbound {Method} {Origin} -> {Status} in {ElapsedMs} ms ({Redirects} redirects)")]
    private partial void Log(string method, string origin, int status, long elapsedMs, int redirects);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "Outbound {Method} {Origin} failed: {Code} after {ElapsedMs} ms")]
    private partial void LogFailure(string method, string origin, string code, long elapsedMs);
}
