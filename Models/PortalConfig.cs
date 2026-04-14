namespace CaptivePortalAutoLogin.Models;

public sealed class PortalConfig
{
	/// <summary>
	/// Username / email to submit on the captive portal login form.
	/// Can also be set via the PORTAL_USERNAME environment variable.
	/// </summary>
	public string Username { get; set; } = string.Empty;

	/// <summary>
	/// Password to submit on the captive portal login form.
	/// Can also be set via the PORTAL_PASSWORD environment variable.
	/// </summary>
	public string Password { get; set; } = string.Empty;

	/// <summary>
	/// How often (in seconds) to poll for a captive portal when not yet logged in.
	/// </summary>
	public int PollIntervalSeconds { get; set; } = 10;

	/// <summary>
	/// Maximum number of automatic login retries before giving up.
	/// </summary>
	public int MaxRetries { get; set; } = 5;

	/// <summary>
	/// Known probe URLs used to test for internet connectivity / captive-portal redirect.
	/// The first URL that responds with the expected content wins the check.
	/// </summary>
	public List<ProbeEndpoint> ProbeEndpoints { get; set; } =
	[
		new() { Url = "http://www.msftconnecttest.com/connecttest.txt",  ExpectedContent = "Microsoft Connect Test" },
		new() { Url = "http://detectportal.firefox.com/success.txt",     ExpectedContent = "success" },
		new() { Url = "http://captive.apple.com/hotspot-detect.html",    ExpectedContent = "Success" },
	];

	/// <summary>
	/// Optional: override the login page URL instead of following the captive-portal redirect.
	/// Leave empty to use the automatically detected redirect target.
	/// </summary>
	public string OverrideLoginUrl { get; set; } = string.Empty;

	/// <summary>
	/// HTML name/id attribute values that are tried (in order) when locating the username field.
	/// </summary>
	public List<string> UsernameFieldHints { get; set; } =
	[
		"username", "user", "email", "login", "userid", "user_name", "user_email"
	];

	/// <summary>
	/// HTML name/id attribute values that are tried (in order) when locating the password field.
	/// </summary>
	public List<string> PasswordFieldHints { get; set; } =
	[
		"password", "pass", "passwd", "pwd"
	];
}

public sealed class ProbeEndpoint
{
	public string Url { get; set; } = string.Empty;
	public string ExpectedContent { get; set; } = string.Empty;
}
