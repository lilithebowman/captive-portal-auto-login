using Android.Content;
using Android.Net;
using Android.Net.Wifi;
using Android.OS;

namespace CaptivePortalAutoLogin;

/// <summary>
/// Android implementation of <see cref="IWifiScanner"/> using the Android WifiManager APIs.
///
/// Scanning notes:
///   Android 9+  — <c>startScan()</c> is throttled (4 scans/2 min from background;
///                 foreground services get more leeway). We call it optimistically and
///                 fall back to cached results returned by <c>getScanResults()</c>.
///   Android 10+ — <c>WifiConfiguration</c> APIs are deprecated; we use
///                 <c>WifiNetworkSpecifier</c> + <c>ConnectivityManager.requestNetwork()</c>
///                 instead. Note: this binds only *this process* to the requested network.
///   Android 10+ — Programmatic connection requires ACCESS_FINE_LOCATION at runtime.
/// </summary>
public sealed class AndroidWifiScanner : IWifiScanner
{
	private readonly HashSet<string> _blockedSsids;
	private ConnectivityManager.NetworkCallback? _activeCallback;
	private readonly ConnectivityManager? _cm;

	public AndroidWifiScanner(IEnumerable<string>? initiallyBlocked = null)
	{
		_blockedSsids = new HashSet<string>(
			initiallyBlocked ?? [],
			StringComparer.OrdinalIgnoreCase);

		_cm = Platform.AppContext.GetSystemService(Context.ConnectivityService) as ConnectivityManager;
	}

	// -------------------------------------------------------------------
	// IWifiScanner
	// -------------------------------------------------------------------

	public void BlockSsid(string ssid)
	{
		_blockedSsids.Add(ssid);
		global::Android.Util.Log.Debug("CaptivePortal", $"[WiFi] Blocked SSID: {ssid}");
	}

	public bool IsBlocked(string ssid) => _blockedSsids.Contains(ssid);

	public Task<IReadOnlyList<string>> ScanOpenNetworksAsync(CancellationToken ct = default)
	{
		var wm = Platform.AppContext.GetSystemService(Context.WifiService) as WifiManager;
		if (wm is null)
			return Task.FromResult<IReadOnlyList<string>>([]);

		// Trigger a fresh scan (may be throttled or return false; cached results still valid).
		wm.StartScan();

		var results = wm.ScanResults ?? [];
		var open = results
			.Where(r => !string.IsNullOrWhiteSpace(r.Ssid)
					 && IsOpenNetwork(r)
					 && !IsBlocked(r.Ssid!))
			.Select(r => r.Ssid!)
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.ToList();

		return Task.FromResult<IReadOnlyList<string>>(open);
	}

	public async Task<bool> ConnectAsync(string ssid, CancellationToken ct = default)
	{
		global::Android.Util.Log.Debug("CaptivePortal", $"[WiFi] Connecting to '{ssid}'…");

		if (Build.VERSION.SdkInt >= BuildVersionCodes.Q)
			return await ConnectModernAsync(ssid, ct);

		return ConnectLegacy(ssid);
	}

	public Task DisconnectAsync(CancellationToken ct = default)
	{
		ReleaseNetworkCallback();
		return Task.CompletedTask;
	}

	// -------------------------------------------------------------------
	// Modern connection (Android 10+ / API 29+)
	// -------------------------------------------------------------------

	private async Task<bool> ConnectModernAsync(string ssid, CancellationToken ct)
	{
		if (_cm is null) return false;

		// Release any previous request before issuing a new one.
		ReleaseNetworkCallback();

		var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

		var specifier = new WifiNetworkSpecifier.Builder()
			.SetSsid(ssid)
			.Build();

		var request = new NetworkRequest.Builder()
			.AddTransportType(TransportType.Wifi)
			.SetNetworkSpecifier(specifier)
			.Build()!;

		var callback = new SimpleNetworkCallback(
			onAvailable: network =>
			{
				// Bind this process to the Wi-Fi network so our HttpClient uses it.
				_cm.BindProcessToNetwork(network);
				tcs.TrySetResult(true);
			},
			onUnavailable: () => tcs.TrySetResult(false));

		_activeCallback = callback;

		using var reg = ct.Register(() =>
		{
			ReleaseNetworkCallback();
			tcs.TrySetCanceled(ct);
		});

		// 15-second timeout passed to the OS — it will call onUnavailable if not satisfied.
		_cm.RequestNetwork(request, callback, 15_000);

		try
		{
			return await tcs.Task;
		}
		catch (System.OperationCanceledException)
		{
			return false;
		}
	}

	private void ReleaseNetworkCallback()
	{
		if (_activeCallback is null || _cm is null) return;
		try
		{
			_cm.UnregisterNetworkCallback(_activeCallback);
			_cm.BindProcessToNetwork(null);
		}
		catch { /* best-effort */ }
		_activeCallback = null;
	}

	// -------------------------------------------------------------------
	// Legacy connection (Android 8–9 / API 26–28)
	// -------------------------------------------------------------------

#pragma warning disable CA1422, CS0618 // WifiConfiguration is deprecated in API 29; call site guards on SdkInt < Q
	private static bool ConnectLegacy(string ssid)
	{
		var wm = Platform.AppContext.GetSystemService(Context.WifiService) as WifiManager;
		if (wm is null) return false;

		var config = new WifiConfiguration();
		config.Ssid = $"\"{ssid}\"";
		// KeyMgmt.NONE = 0 — open network, no authentication.
		config.AllowedKeyManagement!.Set(0);

		var netId = wm.AddNetwork(config);
		if (netId == -1) return false;

		return wm.EnableNetwork(netId, true);
	}
#pragma warning restore CA1422, CS0618

	// -------------------------------------------------------------------
	// Helpers
	// -------------------------------------------------------------------

	private static bool IsOpenNetwork(ScanResult result)
	{
		// Open (unencrypted) networks have no security flags in capabilities.
		var cap = result.Capabilities ?? string.Empty;
		return !cap.Contains("WEP", StringComparison.OrdinalIgnoreCase)
			&& !cap.Contains("WPA", StringComparison.OrdinalIgnoreCase)
			&& !cap.Contains("WPA2", StringComparison.OrdinalIgnoreCase)
			&& !cap.Contains("WPA3", StringComparison.OrdinalIgnoreCase)
			&& !cap.Contains("EAP", StringComparison.OrdinalIgnoreCase);
	}

	// -------------------------------------------------------------------
	// NetworkCallback implementation
	// -------------------------------------------------------------------

	private sealed class SimpleNetworkCallback : ConnectivityManager.NetworkCallback
	{
		private readonly Action<Network> _onAvailable;
		private readonly Action _onUnavailable;

		public SimpleNetworkCallback(Action<Network> onAvailable, Action onUnavailable)
		{
			_onAvailable = onAvailable;
			_onUnavailable = onUnavailable;
		}

		public override void OnAvailable(Network network) => _onAvailable(network);
		public override void OnUnavailable() => _onUnavailable();
	}
}
