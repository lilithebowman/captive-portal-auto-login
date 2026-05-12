namespace CaptivePortalAutoLogin;

/// <summary>
/// Abstraction over platform-specific Wi-Fi scanning and connection APIs.
/// </summary>
public interface IWifiScanner
{
	/// <summary>Prevents the given SSID from being joined again this session.</summary>
	void BlockSsid(string ssid);

	bool IsBlocked(string ssid);

	/// <summary>Returns visible open (no-security) Wi-Fi SSIDs, excluding blocked ones.</summary>
	Task<IReadOnlyList<string>> ScanOpenNetworksAsync(CancellationToken ct = default);

	/// <summary>Attempts to connect to an open SSID. Returns true on success.</summary>
	Task<bool> ConnectAsync(string ssid, CancellationToken ct = default);

	/// <summary>Releases the current network connection.</summary>
	Task DisconnectAsync(CancellationToken ct = default);
}
