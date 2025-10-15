// AuthRetryHandlerV2.cs
// Universal HTTP auth middleman: attaches access tokens, retries once on 401 after refresh,
// prevents token races (single-flight), clones and replays the original request
// Author: Asim Faiaz 
// License: MIT
//==========================================================================================
// V2 - Slighly better version than V1 - guarantees “retry once” even if multiple delegating handlers are chained or a proxy replays; stops accidental double loops.

using System;
using System.Linq;
using System.Net.Http;
using System.Net;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;

namespace Demo.AuthRetry
{
    // Minimal token info used by the handler - Modify your way according to the token provider you are using
    public readonly struct AccessToken
    {
        public string Value { get; }
        public DateTimeOffset ExpiresAtUtc { get; }
        public bool IsExpired(DateTimeOffset? now = null, TimeSpan? skew = null)
            => (now ?? DateTimeOffset.UtcNow) >= (ExpiresAtUtc - (skew ?? TimeSpan.FromSeconds(60)));
        public AccessToken(string value, DateTimeOffset expiresAtUtc) { Value = value; ExpiresAtUtc = expiresAtUtc; }
    }

    //Provider abstraction so any token source can plug in
    public interface IAccessTokenProvider
    {
        Task<AccessToken?> GetAsync(CancellationToken ct);           // return current token if available & fresh
        Task<AccessToken?> ForceRefreshAsync(CancellationToken ct);  // fetch/refresh from source
    }

    //Optional knobs for the handler’s behavior
    public sealed class AuthRetryOptions
    {
        public string Scheme { get; init; } = "Bearer";
        public TimeSpan ExpirySkew { get; init; } = TimeSpan.FromSeconds(60);
        public bool RetryOnceOn401 { get; init; } = true;
        public Action<string>? OnInfo { get; init; } = null;
        public Action<string>? OnWarn { get; init; } = null;
        public Action<string>? OnError { get; init; } = null;
    }

    /* ==================================================================
     * DelegatingHandler that 
	 (1) ensures a valid token, 
	 (2) attaches it,
     (3) on 401 once, forces a refresh and replays the request
    =====================================================================*/
    public sealed class AuthRetryHandler : DelegatingHandler
    {
        private readonly IAccessTokenProvider _provider;
        private readonly AuthRetryOptions _opt;
        private readonly SemaphoreSlim _refreshLock = new(1, 1);
        private Task<AccessToken?>? _inflightRefresh; // single-flight

        public AuthRetryHandler(IAccessTokenProvider provider, AuthRetryOptions? options = null, HttpMessageHandler? inner = null)
            : base(inner ?? new HttpClientHandler())
        {
            _provider = provider ?? throw new ArgumentNullException(nameof(provider));
            _opt = options ?? new AuthRetryOptions();
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            // Get or refresh token proactively
            var tok = await GetOrRefreshIfNeededAsync(ct).ConfigureAwait(false);
            if (tok is null || string.IsNullOrWhiteSpace(tok.Value))
                return new HttpResponseMessage(HttpStatusCode.Unauthorized) { RequestMessage = request };

            AttachToken(request, tok.Value);

            // First attempt
            var response = await base.SendAsync(request, ct).ConfigureAwait(false);
            if (!ShouldRetry401(response, tok)) return response;

            // 401 with a token - refresh once and replay
            _opt.OnWarn?.Invoke("401 received; attempting token refresh and single replay.");

            try
            {
                var refreshed = await ForceSingleFlightRefreshAsync(ct).ConfigureAwait(false);
                if (refreshed is null || string.IsNullOrWhiteSpace(refreshed.Value))
                {
                    _opt.OnWarn?.Invoke("Refresh returned null/empty token. Bubbling 401.");
                    return response;
                }

                // Clone original request for replay
                var replay = await CloneRequestAsync(request).ConfigureAwait(false);
                AttachToken(replay, refreshed.Value);

                response.Dispose(); // dispose first response
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
            // Ensure only one refresh happens; others await it.
            await _refreshLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (_inflightRefresh is null)
                {
                    _inflightRefresh = _provider.ForceRefreshAsync(ct);
                }
            }
            finally
            {
                _refreshLock.Release();
            }

            AccessToken? result;
            try
            {
                result = await _inflightRefresh.ConfigureAwait(false);
            }
            finally
            {
                // Reset inflight task after completion to allow future refreshes
                await _refreshLock.WaitAsync(ct).ConfigureAwait(false);
                try { _inflightRefresh = null; } finally { _refreshLock.Release(); }
            }
            return result;
        }

        private bool ShouldRetry401(HttpResponseMessage response, AccessToken? tokenUsed)
        {
            if (!_opt.RetryOnceOn401) return false;
            if (response.StatusCode != HttpStatusCode.Unauthorized) return false;

            // Only retry if we actually sent a token (avoid looping on anonymous endpoints)
            return tokenUsed is { } && !string.IsNullOrWhiteSpace(tokenUsed.Value.Value);
        }

        private void AttachToken(HttpRequestMessage req, string token)
        {
            req.Headers.Authorization = new AuthenticationHeaderValue(_opt.Scheme, token);
        }

        // Deep-clone a request so it can be replayed (buffers content)
        private static async Task<HttpRequestMessage> CloneRequestAsync(HttpRequestMessage src)
        {
            var clone = new HttpRequestMessage(src.Method, src.RequestUri);

            // headers
            foreach (var h in src.Headers)
                clone.Headers.TryAddWithoutValidation(h.Key, h.Value);

            // content
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