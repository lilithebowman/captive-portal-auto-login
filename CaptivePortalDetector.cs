using CaptivePortalAutoLogin.Models;

namespace CaptivePortalAutoLogin;

/// <summary>
/// Detects whether the current network is behind a captive portal by probing
/// well-known connectivity-check URLs and inspecting whether the response is
/// redirected or contains unexpected content.
/// </summary>
public sealed class CaptivePortalDetector
{
	private readonly PortalConfig _config;

	// A handler that does NOT follow redirects so we can detect them ourselves.
	private static readonly HttpClientHandler _noRedirectHandler = new()
	{
		AllowAutoRedirect = false,
		// Disable certificate validation only for the initial probe over HTTP.
		// The probe URLs are plain HTTP so this has no practical effect; it is
		// here defensively for any misconfigured probe endpoint.
		ServerCertificateCustomValidationCallback = null,
	};

	private static readonly HttpClient _probeClient = new(_noRedirectHandler)
	{
		Timeout = TimeSpan.FromSeconds(8),
	};

	public CaptivePortalDetector(PortalConfig config)
	{
		_config = config;
	}

	/// <summary>
	/// Probes each configured endpoint in order.
	/// Returns a <see cref="DetectionResult"/> describing whether a captive portal
	/// was found and, if so, what URL to use for the login page.
	/// </summary>
	public async Task<DetectionResult> CheckAsync(CancellationToken ct = default)
	{
		foreach (var endpoint in _config.ProbeEndpoints)
		{
			try
			{
				var result = await ProbeEndpointAsync(endpoint, ct);
				if (result is not null)
					return result;
			}
			catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException)
			{
				// Network unavailable entirely — treat as potential captive portal
				// but wait for another probe to confirm before acting.
				Console.Error.WriteLine($"[Detector] Probe {endpoint.Url} failed: {ex.Message}");
			}
		}

		return DetectionResult.NoPortal;
	}

	private async Task<DetectionResult?> ProbeEndpointAsync(ProbeEndpoint endpoint, CancellationToken ct)
	{
		using var request = new HttpRequestMessage(HttpMethod.Get, endpoint.Url);
		// Mimic a browser so some portals do not return a 200 for bot-like requests.
		request.Headers.UserAgent.ParseAdd(
			"Mozilla/5.0 (Windows NT 10.0; Win64; x64) " +
			"AppleWebKit/537.36 (KHTML, like Gecko) " +
			"Chrome/124.0.0.0 Safari/537.36");

		using var response = await _probeClient.SendAsync(request, ct);

		// 3xx redirect → captive portal intercept
		if (IsRedirect(response.StatusCode))
		{
			var loginUrl = response.Headers.Location?.ToString();
			if (!string.IsNullOrWhiteSpace(loginUrl))
			{
				Console.WriteLine($"[Detector] Redirect detected → {loginUrl}");
				return new DetectionResult(IsPortalDetected: true, LoginPageUrl: loginUrl);
			}
		}

		// Some captive portals return 200 but with a different body (e.g. an HTML login page).
		if (response.IsSuccessStatusCode)
		{
			var body = await response.Content.ReadAsStringAsync(ct);
			if (!body.Contains(endpoint.ExpectedContent, StringComparison.OrdinalIgnoreCase))
			{
				// The body does not contain the expected probe text; likely intercepted.
				// Use the request URI as the "login page" — a follow-up GET with redirects
				// enabled will land on the actual portal page.
				Console.WriteLine($"[Detector] Content mismatch on {endpoint.Url} — portal suspected.");
				return new DetectionResult(IsPortalDetected: true, LoginPageUrl: endpoint.Url);
			}

			// Content matches → we have real internet access through this probe.
			Console.WriteLine($"[Detector] Internet reachable via {endpoint.Url}");
			return DetectionResult.NoPortal;
		}

		return null; // Inconclusive for this endpoint; try the next one.
	}

	private static bool IsRedirect(System.Net.HttpStatusCode code) =>
		code is System.Net.HttpStatusCode.Moved
			  or System.Net.HttpStatusCode.Found
			  or System.Net.HttpStatusCode.SeeOther
			  or System.Net.HttpStatusCode.TemporaryRedirect
			  or System.Net.HttpStatusCode.PermanentRedirect;
}

/// <param name="IsPortalDetected">True when a captive portal intercept was detected.</param>
/// <param name="LoginPageUrl">
/// URL of the portal login page, or <c>null</c> when no portal was detected.
/// </param>
public sealed record DetectionResult(bool IsPortalDetected, string? LoginPageUrl)
{
	public static readonly DetectionResult NoPortal = new(false, null);
}
