using CaptivePortalAutoLogin;
using CaptivePortalAutoLogin.Models;
using Microsoft.Extensions.Configuration;

// ---------------------------------------------------------------------------
// Configuration
// ---------------------------------------------------------------------------
var configuration = new ConfigurationBuilder()
	.SetBasePath(AppContext.BaseDirectory)
	.AddJsonFile("appsettings.json", optional: false, reloadOnChange: false)
	// Environment variables override appsettings.json values.
	// Set PORTALCONFIG__USERNAME and PORTALCONFIG__PASSWORD to avoid storing
	// credentials in the config file.
	.AddEnvironmentVariables()
	.Build();

var config = configuration.GetSection("PortalConfig").Get<PortalConfig>()
			 ?? new PortalConfig();

// Allow per-run credential overrides via environment variables.
if (Environment.GetEnvironmentVariable("PORTAL_USERNAME") is { Length: > 0 } envUser)
	config.Username = envUser;
if (Environment.GetEnvironmentVariable("PORTAL_PASSWORD") is { Length: > 0 } envPass)
	config.Password = envPass;

// When no credentials are configured the handler will first try a click-through
// button ("Accept", "Connect", etc.) and fall back to no@example.com / nowayjose.
if (string.IsNullOrWhiteSpace(config.Username) || string.IsNullOrWhiteSpace(config.Password))
	Console.WriteLine("[Main] No credentials configured — click-through mode will be attempted.");

// ---------------------------------------------------------------------------
// Services
// ---------------------------------------------------------------------------
var detector = new CaptivePortalDetector(config);
var loginHandler = new PortalLoginHandler(config);

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
	e.Cancel = true;
	cts.Cancel();
	Console.WriteLine("\n[Main] Shutdown requested.");
};

// ---------------------------------------------------------------------------
// Main loop
// ---------------------------------------------------------------------------
Console.WriteLine("[Main] Captive Portal Auto-Login started. Press Ctrl+C to exit.");

int retries = 0;

while (!cts.Token.IsCancellationRequested)
{
	try
	{
		var result = await detector.CheckAsync(cts.Token);

		if (!result.IsPortalDetected)
		{
			retries = 0; // Reset retry counter — we have connectivity.
			Console.WriteLine($"[Main] No captive portal. Next check in {config.PollIntervalSeconds}s.");
		}
		else
		{
			retries++;
			Console.WriteLine($"[Main] Captive portal detected (attempt {retries}/{config.MaxRetries}).");

			if (retries > config.MaxRetries)
			{
				Console.Error.WriteLine(
					$"[Main] Exceeded maximum retries ({config.MaxRetries}). Giving up.");
				return 2;
			}

			var loginUrl = result.LoginPageUrl!;
			var success = await loginHandler.LoginAsync(loginUrl, cts.Token);

			if (success)
			{
				Console.WriteLine("[Main] Login succeeded. Verifying connectivity...");
				retries = 0;
			}
			else
			{
				Console.Error.WriteLine("[Main] Login attempt failed.");
			}
		}
	}
	catch (OperationCanceledException) when (cts.Token.IsCancellationRequested)
	{
		break;
	}
	catch (Exception ex)
	{
		Console.Error.WriteLine($"[Main] Unexpected error: {ex}");
	}

	try
	{
		await Task.Delay(TimeSpan.FromSeconds(config.PollIntervalSeconds), cts.Token);
	}
	catch (OperationCanceledException)
	{
		break;
	}
}

Console.WriteLine("[Main] Exited cleanly.");
return 0;
