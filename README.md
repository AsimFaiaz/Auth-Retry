<section id="authretry-overview">
  <h1>AuthRetryHandler</h1>

  ![Status](https://img.shields.io/badge/status-stable-blue)
  ![Build](https://img.shields.io/badge/build-passing-brightgreen)
  ![License](https://img.shields.io/badge/license-MIT-lightgrey)

  <h2>Overview</h2>
  <p>
    <strong>AuthRetryHandler</strong> is a universal, dependency-free C# <em>HTTP message handler</em> 
    that automatically manages access tokens and retries requests after a 401 Unauthorized response.
    It ensures only one token refresh happens at a time (avoiding race conditions), attaches valid tokens
    to every outgoing request, and transparently replays failed requests after refresh.
  </p>

  <p>
    Designed as a plug-and-play <code>DelegatingHandler</code>, it works with any token source 
    -- OAuth2, OpenID Connect, JWT APIs, or custom providers -- through a minimal abstraction layer.
    Drop it into any <code>HttpClient</code> pipeline to eliminate "401 Unauthorized" race conditions
    and simplify authentication logic in .NET applications.
  </p>

  <h2>Developer Note</h2>
  <p>
    This project is part of a broader cleanup of my personal playground -- where I’m 
    organizing standalone mini-projects that demonstrate core programming concepts, 
    clean design, and practical problem-solving in small, focused doses.
  </p>

  <h2>Key Features</h2>
  <ul>
    <li><strong>Automatic Token Attachment:</strong> adds your access token to every HTTP request</li>
    <li><strong>Single-Flight Refresh:</strong> only one refresh runs at a time -- others await it</li>
    <li><strong>Transparent 401 Retry:</strong> replays failed requests after a successful refresh</li>
    <li><strong>Universal Provider:</strong> works with any token source via <code>IAccessTokenProvider</code></li>
    <li><strong>Proactive Expiry Handling:</strong> refreshes before expiry with configurable skew</li>
    <li><strong>Safe & Simple:</strong> no loops, no spamming, no external dependencies</li>
  </ul>

  <h2>How It Works</h2>
  <p>
    Every outgoing request goes through <code>AuthRetryHandler</code>, which:
  </p>
  <ol>
    <li>Retrieves a valid token from your provider.</li>
    <li>Attaches it as an <code>Authorization</code> header.</li>
    <li>If a <code>401 Unauthorized</code> response is returned, it forces a token refresh once.</li>
    <li>Clones and replays the original request using the new token.</li>
  </ol>

  <p>
    The handler guarantees that all concurrent requests wait for the same refresh task 
    -- preventing token race conditions and repeated 401 errors.
  </p>

  <h2>Example Usage</h2>
  <pre>
  // 1️⃣ Implement your token provider
  public sealed class MyTokenProvider : IAccessTokenProvider
  {
      private string? _token;
      private DateTimeOffset _expires = DateTimeOffset.MinValue;

      public Task&lt;AccessToken?&gt; GetAsync(CancellationToken ct)
          =&gt; Task.FromResult(_expires &gt; DateTimeOffset.UtcNow.AddSeconds(60)
              ? new AccessToken(_token!, _expires)
              : (AccessToken?)null);

      public async Task&lt;AccessToken?&gt; ForceRefreshAsync(CancellationToken ct)
      {
          // Simulate getting a new token from your API or Identity provider
          await Task.Delay(200, ct);
          _token = Guid.NewGuid().ToString("N");
          _expires = DateTimeOffset.UtcNow.AddMinutes(5);
          return new AccessToken(_token, _expires);
      }
  }

  // 2️⃣ Wire the handler to HttpClient
  var http = new HttpClient(
      new AuthRetryHandler(new MyTokenProvider(), new AuthRetryOptions())
  );

  // 3️⃣ Use it normally
  var res = await http.GetAsync("https://api.example.com/data");
  Console.WriteLine((int)res.StatusCode);
  </pre>

  <h2>Sample Console Output</h2>
  <pre>
  [INFO] Token missing/near expiry; refreshing.
  [WARN] 401 received; attempting token refresh and single replay.
  [INFO] Request replayed successfully after refresh.
  </pre>

  <h2>Interface Summary</h2>
  <pre>
  public interface IAccessTokenProvider
  {
      Task&lt;AccessToken?&gt; GetAsync(CancellationToken ct);
      Task&lt;AccessToken?&gt; ForceRefreshAsync(CancellationToken ct);
  }

  public readonly struct AccessToken
  {
      public string Value { get; }
      public DateTimeOffset ExpiresAtUtc { get; }
  }
  </pre>

  <h2>Configuration</h2>
  <pre>
  new AuthRetryOptions
  {
      Scheme = "Bearer",               // default
      ExpirySkew = TimeSpan.FromSeconds(60),
      RetryOnceOn401 = true,
      OnInfo = msg => Console.WriteLine("[INFO] " + msg),
      OnWarn = msg => Console.WriteLine("[WARN] " + msg),
      OnError = msg => Console.WriteLine("[ERROR] " + msg)
  };
  </pre>

  <h2>Why AuthRetryHandler?</h2>
  <p>
    Many developers face intermittent <strong>401 Unauthorized</strong> errors when tokens expire or 
    refresh asynchronously. Instead of manually adding retry loops or delays, 
    <strong>AuthRetryHandler</strong> cleanly centralizes the logic at the HTTP pipeline level.
  </p>

  <p><strong>Without handler:</strong></p>
  <pre>
  if (tokenExpired)
  {
      token = await RefreshToken();
  }

  client.DefaultRequestHeaders.Authorization = 
      new AuthenticationHeaderValue("Bearer", token);

  var res = await client.GetAsync(url);
  if (res.StatusCode == HttpStatusCode.Unauthorized)
  {
      token = await RefreshToken();
      res = await client.GetAsync(url);
  }
  </pre>

  <p><strong>With AuthRetryHandler:</strong></p>
  <pre>
  var http = new HttpClient(new AuthRetryHandler(new MyProvider()));
  var res = await http.GetAsync(url); // auto-attach, auto-refresh, auto-retry
  </pre>

  <p>
    Cleaner, more resilient, and completely reusable across APIs and applications.
  </p>

  <section id="tech-stack">
    <h2>Tech Stack</h2>
    <pre>☑ C# (.NET 8 or newer)</pre>
    <pre>☑ HttpClient DelegatingHandler</pre>
    <pre>☑ No external dependencies</pre>
  </section>

  <h2>Build Status</h2>
  <p>
    This is a single-file demonstration repository and does not include a build pipeline.  
    Future updates may introduce automated tests and CI workflows via GitHub Actions.
  </p>

  <h2>License</h2>
  <p>
    Licensed under the <a href="LICENSE">MIT License</a>.<br>
  </p>
</section>
