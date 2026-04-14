using CaptivePortalAutoLogin.Models;
using HtmlAgilityPack;

namespace CaptivePortalAutoLogin;

/// <summary>
/// Fetches the captive-portal login page, locates the login form, populates
/// credentials, and submits it — mimicking what a browser would do.
/// </summary>
public sealed class PortalLoginHandler
{
	private readonly PortalConfig _config;

	// A client that DOES follow redirects so we always land on the actual portal page.
	private static readonly HttpClient _browserClient = new(new HttpClientHandler
	{
		AllowAutoRedirect = true,
		MaxAutomaticRedirections = 10,
	})
	{
		Timeout = TimeSpan.FromSeconds(20),
	};

	public PortalLoginHandler(PortalConfig config)
	{
		_config = config;
	}

	// True when the caller has supplied explicit credentials.
	private bool HasCredentials =>
		!string.IsNullOrWhiteSpace(_config.Username) &&
		!string.IsNullOrWhiteSpace(_config.Password);

	private const string FallbackUsername = "no@example.com";
	private const string FallbackPassword = "nowayjose";

	// Random alphanumeric string used to fill unknown form fields.
	private static string RandomGarbage() =>
		Guid.NewGuid().ToString("N")[..12];

	/// <summary>
	/// Attempts to log in through the captive portal.
	/// </summary>
	/// <param name="loginPageUrl">
	/// Starting URL — may be the initial probe URL that was intercepted; the client
	/// will follow redirects to reach the actual login page.
	/// </param>
	/// <returns><c>true</c> if the login attempt was submitted successfully.</returns>
	public async Task<bool> LoginAsync(string loginPageUrl, CancellationToken ct = default)
	{
		// Allow appsettings to hard-code a specific portal URL.
		var targetUrl = !string.IsNullOrWhiteSpace(_config.OverrideLoginUrl)
			? _config.OverrideLoginUrl
			: loginPageUrl;

		Console.WriteLine($"[Login] Fetching login page: {targetUrl}");

		string html;
		Uri pageUri;
		try
		{
			using var pageResponse = await _browserClient.GetAsync(targetUrl, ct);
			pageUri = pageResponse.RequestMessage?.RequestUri ?? new Uri(targetUrl);
			html = await pageResponse.Content.ReadAsStringAsync(ct);
		}
		catch (Exception ex)
		{
			Console.Error.WriteLine($"[Login] Failed to fetch login page: {ex.Message}");
			return false;
		}

		var form = FindLoginForm(html, pageUri);
		if (form is null)
		{
			Console.Error.WriteLine("[Login] Could not locate a login form on the page.");
			return false;
		}

		Console.WriteLine($"[Login] Submitting form to: {form.ActionUrl}");
		return await SubmitFormAsync(form, ct);
	}

	// -------------------------------------------------------------------------
	// Form discovery
	// -------------------------------------------------------------------------

	private LoginForm? FindLoginForm(string html, Uri pageUri)
	{
		var doc = new HtmlDocument();
		doc.LoadHtml(html);

		// Look for every <form> element and score it by how many credential-like
		// inputs it contains.  Pick the highest-scoring form.
		var forms = doc.DocumentNode.SelectNodes("//form");
		if (forms is null || forms.Count == 0)
		{
			Console.Error.WriteLine("[Login] No <form> elements found in the page.");
			return null;
		}

		LoginForm? best = null;
		int bestScore = -1;

		foreach (var formNode in forms)
		{
			var candidate = BuildLoginForm(formNode, pageUri);
			if (candidate is null) continue;

			int score = ScoreForm(candidate);
			if (score > bestScore)
			{
				bestScore = score;
				best = candidate;
			}
		}

		if (best is null)
			Console.Error.WriteLine("[Login] No suitable login form found.");
		else
		{
			var mode = best.ClickThroughButtonName is not null && !HasCredentials
				? $"click-through via '{best.ClickThroughButtonName}'"
				: "credential login";
			Console.WriteLine($"[Login] Selected form (score={bestScore}, mode={mode}) → action={best.ActionUrl}");
		}

		return best;
	}

	// Text/value keywords that suggest a click-through "accept / connect" button.
	private static readonly string[] _clickThroughButtonHints =
	[
		"accept", "connect", "continue", "agree", "proceed",
		"access", "join", "enter", "guest", "skip", "free", "go",
		"login without", "bypass", "ok",
	];

	private LoginForm? BuildLoginForm(HtmlNode formNode, Uri pageUri)
	{
		var inputs = formNode.SelectNodes(".//input | .//button | .//textarea | .//select");
		if (inputs is null) return null;

		var method = formNode.GetAttributeValue("method", "post").ToUpperInvariant();
		var rawAction = formNode.GetAttributeValue("action", string.Empty);
		var actionUrl = ResolveUrl(pageUri, rawAction);

		var form = new LoginForm
		{
			ActionUrl = actionUrl,
			Method = method,
		};

		foreach (var input in inputs)
		{
			var type = input.GetAttributeValue("type", "text").ToLowerInvariant();
			var name = input.GetAttributeValue("name", string.Empty);
			var id = input.GetAttributeValue("id", string.Empty);
			var value = input.GetAttributeValue("value", string.Empty);

			if (string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(id))
				continue;

			// Skip disabled or hidden inputs we cannot interact with meaningfully.
			if (type == "hidden")
			{
				// Still include hidden fields — they often carry CSRF/session tokens.
				form.Fields[name] = value;
				continue;
			}

			if (type is "submit" or "button" or "image")
			{
				// Check whether this looks like a click-through (no-credentials) button.
				var buttonLabel = !string.IsNullOrWhiteSpace(input.InnerText)
					? input.InnerText.Trim()
					: value;
				if (form.ClickThroughButtonName is null &&
					MatchesHints(buttonLabel, _clickThroughButtonHints))
				{
					form.ClickThroughButtonName = !string.IsNullOrWhiteSpace(name) ? name : id;
				}

				// Capture the first submit button value (if named) so the server can
				// distinguish which button was "clicked".
				if (!string.IsNullOrWhiteSpace(name) && !form.Fields.ContainsKey(name))
					form.Fields[name] = value;
				continue;
			}

			var nameOrId = !string.IsNullOrWhiteSpace(name) ? name : id;

			if (type == "password" || MatchesHints(nameOrId, _config.PasswordFieldHints))
			{
				form.PasswordFieldName = nameOrId;
				form.Fields[nameOrId] = HasCredentials ? _config.Password : FallbackPassword;
			}
			else if (MatchesHints(nameOrId, _config.UsernameFieldHints))
			{
				form.UsernameFieldName = nameOrId;
				form.Fields[nameOrId] = HasCredentials ? _config.Username : FallbackUsername;
			}
			else
			{
				// Unknown field — fill with random garbage so we do not leave it empty.
				if (!form.Fields.ContainsKey(nameOrId))
					form.Fields[nameOrId] = RandomGarbage();
			}
		}

		return form;
	}

	private int ScoreForm(LoginForm form)
	{
		int score = 0;
		if (HasCredentials)
		{
			// Prefer forms with credential fields when we have credentials to fill.
			if (form.UsernameFieldName is not null) score += 2;
			if (form.PasswordFieldName is not null) score += 3;
		}
		else
		{
			// Without credentials, strongly prefer click-through forms with no password field.
			if (form.ClickThroughButtonName is not null) score += 5;
			if (form.PasswordFieldName is not null) score -= 3; // Penalise login-only forms.
		}
		return score;
	}

	// -------------------------------------------------------------------------
	// Form submission
	// -------------------------------------------------------------------------

	private async Task<bool> SubmitFormAsync(LoginForm form, CancellationToken ct)
	{
		try
		{
			HttpResponseMessage response;
			if (form.Method == "GET")
			{
				var queryString = string.Join("&",
					form.Fields.Select(kv =>
						$"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));
				var getUrl = $"{form.ActionUrl}?{queryString}";
				response = await _browserClient.GetAsync(getUrl, ct);
			}
			else
			{
				var content = new FormUrlEncodedContent(form.Fields);
				response = await _browserClient.PostAsync(form.ActionUrl, content, ct);
			}

			var body = await response.Content.ReadAsStringAsync(ct);
			Console.WriteLine($"[Login] Form submitted. HTTP {(int)response.StatusCode} {response.StatusCode}");

			// Heuristic success check: look for common error indicators in the response.
			if (ContainsLoginError(body))
			{
				Console.Error.WriteLine("[Login] Response suggests login failed (error text detected).");
				return false;
			}

			Console.WriteLine("[Login] Login appears successful.");
			return true;
		}
		catch (Exception ex)
		{
			Console.Error.WriteLine($"[Login] Form submission failed: {ex.Message}");
			return false;
		}
	}

	// -------------------------------------------------------------------------
	// Helpers
	// -------------------------------------------------------------------------

	private static bool MatchesHints(string value, IEnumerable<string> hints) =>
		hints.Any(h => value.Contains(h, StringComparison.OrdinalIgnoreCase));

	private static string ResolveUrl(Uri baseUri, string rawAction)
	{
		if (string.IsNullOrWhiteSpace(rawAction))
			return baseUri.ToString();

		if (Uri.TryCreate(rawAction, UriKind.Absolute, out var abs))
			return abs.ToString();

		if (Uri.TryCreate(baseUri, rawAction, out var rel))
			return rel.ToString();

		return rawAction;
	}

	private static readonly string[] _errorPhrases =
	[
		"invalid", "incorrect", "wrong", "failed", "error",
		"denied", "unauthorized", "try again", "bad credentials",
	];

	private static bool ContainsLoginError(string body) =>
		_errorPhrases.Any(p => body.Contains(p, StringComparison.OrdinalIgnoreCase));
}

/// <summary>Represents a parsed HTML login form ready to be submitted.</summary>
internal sealed class LoginForm
{
	public string ActionUrl { get; set; } = string.Empty;
	public string Method { get; set; } = "POST";
	public string? UsernameFieldName { get; set; }
	public string? PasswordFieldName { get; set; }

	/// <summary>
	/// Name or id of a submit/button element whose label suggests a click-through
	/// ("Accept", "Connect", "Continue", etc.).  <c>null</c> if none found.
	/// </summary>
	public string? ClickThroughButtonName { get; set; }

	/// <summary>All fields (hidden tokens + credential fields) to POST/GET.</summary>
	public Dictionary<string, string> Fields { get; } = new(StringComparer.OrdinalIgnoreCase);
}
