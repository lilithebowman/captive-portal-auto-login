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
				Console.Error.WriteLine($"[Detector] Probe {endpoint.Url} failed: {ex.Message}");
			}
		}

		return DetectionResult.NoPortal;
	}

	private async Task<DetectionResult?> ProbeEndpointAsync(ProbeEndpoint endpoint, CancellationToken ct)
	{
		using var request = new HttpRequestMessage(HttpMethod.Get, endpoint.Url);
		request.Headers.UserAgent.ParseAdd(
			"Mozilla/5.0 (Linux; Android 14) " +
			"AppleWebKit/537.36 (KHTML, like Gecko) " +
			"Chrome/124.0.0.0 Mobile Safari/537.36");

		using var response = await _probeClient.SendAsync(request, ct);

		if (IsRedirect(response.StatusCode))
		{
			var loginUrl = response.Headers.Location?.ToString();
			if (!string.IsNullOrWhiteSpace(loginUrl))
			{
				Console.WriteLine($"[Detector] Redirect detected → {loginUrl}");
				return new DetectionResult(IsPortalDetected: true, LoginPageUrl: loginUrl);
			}
		}

		if (response.IsSuccessStatusCode)
		{
			var body = await response.Content.ReadAsStringAsync(ct);
			if (!body.Contains(endpoint.ExpectedContent, StringComparison.OrdinalIgnoreCase))
			{
				Console.WriteLine($"[Detector] Content mismatch on {endpoint.Url} — portal suspected.");
				return new DetectionResult(IsPortalDetected: true, LoginPageUrl: endpoint.Url);
			}

			Console.WriteLine($"[Detector] Internet reachable via {endpoint.Url}");
			return DetectionResult.Connected;
		}

		return null;
	}

	private static bool IsRedirect(System.Net.HttpStatusCode code) =>
		code is System.Net.HttpStatusCode.Moved
			  or System.Net.HttpStatusCode.Found
			  or System.Net.HttpStatusCode.SeeOther
			  or System.Net.HttpStatusCode.TemporaryRedirect
			  or System.Net.HttpStatusCode.PermanentRedirect;
}

/// <param name="IsPortalDetected">True when a captive portal intercept was detected.</param>
/// <param name="LoginPageUrl">URL of the portal login page, or null when no portal detected.</param>
/// <param name="IsConnectivityConfirmed">True when at least one probe confirmed real internet access.</param>
public sealed record DetectionResult(bool IsPortalDetected, string? LoginPageUrl, bool IsConnectivityConfirmed = false)
{
	public static readonly DetectionResult NoPortal = new(false, null, false);
	public static readonly DetectionResult Connected = new(false, null, true);
}
