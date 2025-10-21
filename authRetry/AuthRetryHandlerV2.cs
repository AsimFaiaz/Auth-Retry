// AuthRetryHandlerV2.cs
// Universal HTTP auth middleman: attaches access tokens, retries once on 401 after refresh,
// prevents token races (single-flight), clones and replays the original request
// Adds a per-request replay marker to guarantee "retry once" across handler chains
// Author: Asim Faiaz
// License: MIT
//==========================================================================================
// V2 - Slightly better version than V1 - guarantees “retry once” even if multiple delegating
// handlers are chained or a proxy replays; stops accidental double loops
//==========================================================================================

using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;

//#nullable enable

// AccessToken + Provider Interfaces
public readonly struct AccessToken
{
    public string Value { get; }
    public DateTimeOffset ExpiresAtUtc { get; }

    public bool IsExpired(DateTimeOffset? now = null, TimeSpan? skew = null)
        => (now ?? DateTimeOffset.UtcNow) >= (ExpiresAtUtc - (skew ?? TimeSpan.FromSeconds(60)));

    public AccessToken(string value, DateTimeOffset expiresAtUtc)
    {
        Value = value ?? throw new ArgumentNullException(nameof(value));
        ExpiresAtUtc = expiresAtUtc;
    }
}

public interface IAccessTokenProvider
{
    Task<AccessToken?> GetAsync(CancellationToken ct);
    Task<AccessToken?> ForceRefreshAsync(CancellationToken ct);
}

// AuthRetryOptions
public sealed class AuthRetryOptions
{
    public string Scheme { get; init; } = "Bearer";
    public TimeSpan ExpirySkew { get; init; } = TimeSpan.FromSeconds(60);
    public bool RetryOnceOn401 { get; init; } = true;
    public Action<string>? OnInfo { get; init; }
    public Action<string>? OnWarn { get; init; }
    public Action<string>? OnError { get; init; }
}

// AuthRetryHandler 
public sealed class AuthRetryHandler : DelegatingHandler
{
    private const string RetriedHeaderName = "X-AuthRetry-Retried";
    private readonly IAccessTokenProvider _provider;
    private readonly AuthRetryOptions _opt;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private Task<AccessToken?>? _inflightRefresh;

    public AuthRetryHandler(
        IAccessTokenProvider provider,
        AuthRetryOptions? options = null,
        HttpMessageHandler? inner = null)
        : base(inner ?? new HttpClientHandler())
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _opt = options ?? new AuthRetryOptions();
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        // get current or refresh
        var tok = await GetOrRefreshIfNeededAsync(ct).ConfigureAwait(false);
        if (tok is null || string.IsNullOrWhiteSpace(tok.Value))
        {
            _opt.OnWarn?.Invoke("No token available after refresh attempt. Returning 401.");
            return new HttpResponseMessage(HttpStatusCode.Unauthorized) { RequestMessage = request };
        }

        var tokenValue = tok.Value;
        AttachToken(request, tokenValue);

        var response = await base.SendAsync(request, ct).ConfigureAwait(false);
        if (!ShouldRetry401(response, tok)) return response;

        _opt.OnWarn?.Invoke("401 received; attempting token refresh and single replay.");

        try
        {
            var refreshed = await ForceSingleFlightRefreshAsync(ct).ConfigureAwait(false);
            if (refreshed is null || string.IsNullOrWhiteSpace(refreshed.Value))
            {
                _opt.OnWarn?.Invoke("Refresh returned null/empty token. Bubbling 401.");
                return response;
            }

            var newTokenValue = refreshed.Value;
            var replay = await CloneRequestAsync(request).ConfigureAwait(false);
            AttachToken(replay, newTokenValue);

            if (!replay.Headers.Contains(RetriedHeaderName))
                replay.Headers.TryAddWithoutValidation(RetriedHeaderName, "1");

            response.Dispose();
            return await base.SendAsync(replay, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _opt.OnError?.Invoke($"Refresh/replay failed: {ex.Message}");
            return response;
        }
    }

    private async Task<AccessToken?> GetOrRefreshIfNeededAsync(CancellationToken ct)
    {
        var tok = await _provider.GetAsync(ct).ConfigureAwait(false);
        if (tok is null || tok.Value.IsExpired(skew: _opt.ExpirySkew))
        {
            _opt.OnInfo?.Invoke("Token missing/near expiry; refreshing.");
            tok = await ForceSingleFlightRefreshAsync(ct).ConfigureAwait(false);
        }
        return tok;
    }

    private async Task<AccessToken?> ForceSingleFlightRefreshAsync(CancellationToken ct)
    {
        await _refreshLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_inflightRefresh is null)
                _inflightRefresh = _provider.ForceRefreshAsync(ct);
        }
        finally { _refreshLock.Release(); }

        AccessToken? result;
        try { result = await _inflightRefresh.ConfigureAwait(false); }
        finally
        {
            await _refreshLock.WaitAsync(ct).ConfigureAwait(false);
            try { _inflightRefresh = null; } finally { _refreshLock.Release(); }
        }
        return result;
    }

    private bool ShouldRetry401(HttpResponseMessage response, AccessToken tokenUsed)
    {
        if (!_opt.RetryOnceOn401) return false;
        if (response.StatusCode != HttpStatusCode.Unauthorized) return false;
        if (string.IsNullOrWhiteSpace(tokenUsed.Value)) return false;

        var req = response.RequestMessage;
        if (req is not null && req.Headers.Contains(RetriedHeaderName))
            return false;
        return true;
    }

    private void AttachToken(HttpRequestMessage req, string token)
        => req.Headers.Authorization = new AuthenticationHeaderValue(_opt.Scheme, token);

    private static async Task<HttpRequestMessage> CloneRequestAsync(HttpRequestMessage src)
    {
        var clone = new HttpRequestMessage(src.Method, src.RequestUri);
        foreach (var h in src.Headers)
            clone.Headers.TryAddWithoutValidation(h.Key, h.Value);

        if (src.Content != null)
        {
            var bytes = await src.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
            var content = new ByteArrayContent(bytes);
            foreach (var h in src.Content.Headers)
                content.Headers.TryAddWithoutValidation(h.Key, h.Value);
            clone.Content = content;
        }

// properties/options (RequestOptions in .NET 8)
#if NET8_0_OR_GREATER
            foreach (var opt in src.Options)
                clone.Options.Set(new(opt.Key), opt.Value);
#endif
        return clone;
        }
    }
}
