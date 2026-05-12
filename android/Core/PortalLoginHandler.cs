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

	private bool HasCredentials =>
		!string.IsNullOrWhiteSpace(_config.Username) &&
		!string.IsNullOrWhiteSpace(_config.Password);

	private const string FallbackUsername = "no@example.com";
	private const string FallbackPassword = "nowayjose";

	private static string RandomGarbage() =>
		Guid.NewGuid().ToString("N")[..12];

	public async Task<bool> LoginAsync(string loginPageUrl, CancellationToken ct = default)
	{
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

	private LoginForm? FindLoginForm(string html, Uri pageUri)
	{
		var doc = new HtmlDocument();
		doc.LoadHtml(html);

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

			if (type == "hidden")
			{
				form.Fields[name] = value;
				continue;
			}

			if (type is "submit" or "button" or "image")
			{
				var buttonLabel = !string.IsNullOrWhiteSpace(input.InnerText)
					? input.InnerText.Trim()
					: value;
				if (form.ClickThroughButtonName is null &&
					MatchesHints(buttonLabel, _clickThroughButtonHints))
				{
					form.ClickThroughButtonName = !string.IsNullOrWhiteSpace(name) ? name : id;
				}

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
			if (form.UsernameFieldName is not null) score += 2;
			if (form.PasswordFieldName is not null) score += 3;
		}
		else
		{
			if (form.ClickThroughButtonName is not null) score += 5;
			if (form.PasswordFieldName is not null) score -= 3;
		}
		return score;
	}

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

internal sealed class LoginForm
{
	public string ActionUrl { get; set; } = string.Empty;
	public string Method { get; set; } = "POST";
	public string? UsernameFieldName { get; set; }
	public string? PasswordFieldName { get; set; }
	public string? ClickThroughButtonName { get; set; }
	public Dictionary<string, string> Fields { get; } = new(StringComparer.OrdinalIgnoreCase);
}
