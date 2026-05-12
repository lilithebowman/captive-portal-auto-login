using CaptivePortalAutoLogin.Models;

namespace CaptivePortalAutoLogin;

/// <summary>
/// Platform-independent detection-and-login loop.
/// Extracted from the desktop CaptivePortalWorker so it can be hosted inside
/// an Android Foreground Service (or any other execution context).
/// </summary>
public sealed class PortalWorker
{
	private readonly PortalConfig _config;
	private readonly CaptivePortalDetector _detector;
	private readonly PortalLoginHandler _loginHandler;
	private readonly IWifiScanner? _wifiScanner;
	private readonly Action<string> _log;

	/// <param name="config">Portal configuration loaded from user preferences.</param>
	/// <param name="wifiScanner">Optional platform Wi-Fi scanner; pass null to skip Wi-Fi scanning.</param>
	/// <param name="log">
	/// Optional log sink. Defaults to Console.WriteLine.
	/// The delegate is invoked from the worker task — marshal to the UI thread when needed.
	/// </param>
	public PortalWorker(
		PortalConfig config,
		IWifiScanner? wifiScanner = null,
		Action<string>? log = null)
	{
		_config = config;
		_detector = new CaptivePortalDetector(config);
		_loginHandler = new PortalLoginHandler(config);
		_wifiScanner = wifiScanner;
		_log = log ?? Console.WriteLine;

		if (string.IsNullOrWhiteSpace(config.Username) || string.IsNullOrWhiteSpace(config.Password))
			_log("[Main] No credentials configured — click-through mode will be attempted.");
	}

	/// <summary>Runs the detection/login loop until <paramref name="ct"/> is cancelled.</summary>
	public async Task RunAsync(CancellationToken ct)
	{
		_log("[Main] Captive Portal Auto-Login started.");
		int retries = 0;

		while (!ct.IsCancellationRequested)
		{
			try
			{
				_log("[Main] Checking connectivity…");
				var result = await _detector.CheckAsync(ct);

				if (result.IsConnectivityConfirmed)
				{
					retries = 0;
					_log($"[Main] ✓ Internet confirmed. Next check in {_config.PollIntervalSeconds}s.");
				}
				else if (result.IsPortalDetected)
				{
					retries++;
					_log($"[Main] ⚠ Captive portal detected (attempt {retries}/{_config.MaxRetries}).");

					if (retries > _config.MaxRetries)
					{
						_log($"[Main] ✗ Exceeded maximum retries ({_config.MaxRetries}). Giving up.");
						break;
					}

					_log($"[Main] Attempting login at {result.LoginPageUrl}…");
					var success = await _loginHandler.LoginAsync(result.LoginPageUrl!, ct);
					if (success)
					{
						_log("[Main] ✓ Login succeeded. Verifying connectivity…");
						retries = 0;
					}
					else
					{
						_log("[Main] ✗ Login attempt failed.");
					}
				}
				else if (_wifiScanner is not null)
				{
					await TryScanAndJoinAsync(ct);
				}
				else
				{
					_log($"[Main] No captive portal detected. Next check in {_config.PollIntervalSeconds}s.");
				}
			}
			catch (OperationCanceledException) when (ct.IsCancellationRequested)
			{
				break;
			}
			catch (Exception ex)
			{
				_log($"[Main] ✗ Unexpected error: {ex.Message}");
			}

			try
			{
				await Task.Delay(TimeSpan.FromSeconds(_config.PollIntervalSeconds), ct);
			}
			catch (OperationCanceledException)
			{
				break;
			}
		}

		_log("[Main] Service stopped.");
	}

	private async Task TryScanAndJoinAsync(CancellationToken ct)
	{
		_log("[Main] Scanning for open Wi-Fi networks…");
		var openAps = await _wifiScanner!.ScanOpenNetworksAsync(ct);

		if (openAps.Count == 0)
		{
			_log("[Main] No open Wi-Fi networks found. Next check in {_config.PollIntervalSeconds}s.");
			return;
		}

		_log($"[Main] ⊘ Found {openAps.Count} open network(s): {string.Join(", ", openAps.Select(s => $"'{s}'"))}");

		foreach (var ssid in openAps)
		{
			if (ct.IsCancellationRequested) break;

			_log($"[Main] Connecting to '{ssid}'…");

			var connected = await _wifiScanner.ConnectAsync(ssid, ct);
			if (!connected)
			{
				_log($"[Main] ✗ Connection to '{ssid}' failed. Blocking.");
				_wifiScanner.BlockSsid(ssid);
				continue;
			}

			_log($"[Main] ✓ Connected to '{ssid}'. Settling network stack…");

			// Let the network stack settle after association.
			await Task.Delay(TimeSpan.FromSeconds(3), ct);

			_log($"[Main] Checking connectivity through '{ssid}'…");
			var probe = await _detector.CheckAsync(ct);

			if (probe.IsConnectivityConfirmed)
			{
				_log($"[Main] ✓ Internet confirmed via '{ssid}'.");
				return;
			}

			if (probe.IsPortalDetected)
			{
				_log($"[Main] ⚠ Captive portal detected on '{ssid}'. Attempting login…");
				var loginOk = await _loginHandler.LoginAsync(probe.LoginPageUrl!, ct);
				if (loginOk)
				{
					_log($"[Main] ✓ Login succeeded on '{ssid}'.");
					return;
				}
			}

			_log($"[Main] AP '{ssid}' could not provide internet. Blocking and moving on.");
			_wifiScanner.BlockSsid(ssid);
			await _wifiScanner.DisconnectAsync(ct);
		}

		_log($"[Main] No viable networks found. Next check in {_config.PollIntervalSeconds}s.");
	}
}
