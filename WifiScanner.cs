using System.Diagnostics;
using System.Text.RegularExpressions;

namespace CaptivePortalAutoLogin;

/// <summary>
/// Scans for open (unsecured) Wi-Fi access points, connects to them,
/// and tracks which SSIDs must not be joined again during this session.
/// </summary>
/// <remarks>
/// Platform support:
///   Windows — uses <c>netsh wlan</c>.
///   Linux   — uses <c>nmcli</c> (NetworkManager CLI).
/// </remarks>
public sealed class WifiScanner
{
	private readonly HashSet<string> _blockedSsids;

	public WifiScanner(IEnumerable<string>? initiallyBlocked = null)
	{
		_blockedSsids = new HashSet<string>(
			initiallyBlocked ?? Enumerable.Empty<string>(),
			StringComparer.OrdinalIgnoreCase);
	}

	/// <summary>Prevents the given SSID from being joined again this session.</summary>
	public void BlockSsid(string ssid)
	{
		_blockedSsids.Add(ssid);
		Console.WriteLine($"[WiFi] Blocked SSID: {ssid}");
	}

	public bool IsBlocked(string ssid) => _blockedSsids.Contains(ssid);

	/// <summary>
	/// Returns visible open (no-security) Wi-Fi SSIDs, excluding blocked ones.
	/// The list is in the order reported by the OS (typically descending signal strength).
	/// </summary>
	public async Task<IReadOnlyList<string>> ScanOpenNetworksAsync(CancellationToken ct = default)
	{
		IReadOnlyList<string> all;

		if (OperatingSystem.IsWindows())
			all = await ScanWindowsAsync(ct);
		else if (OperatingSystem.IsLinux())
			all = await ScanLinuxAsync(ct);
		else
		{
			Console.WriteLine("[WiFi] Wi-Fi scanning is not supported on this platform.");
			return [];
		}

		return all.Where(s => !IsBlocked(s)).ToList();
	}

	/// <summary>
	/// Attempts to connect to an open SSID.
	/// Returns <c>true</c> when the OS reports a successful connection.
	/// </summary>
	public async Task<bool> ConnectAsync(string ssid, CancellationToken ct = default)
	{
		Console.WriteLine($"[WiFi] Connecting to '{ssid}'...");

		if (OperatingSystem.IsWindows())
			return await ConnectWindowsAsync(ssid, ct);
		if (OperatingSystem.IsLinux())
			return await ConnectLinuxAsync(ssid, ct);

		return false;
	}

	/// <summary>Disconnects from the current Wi-Fi network.</summary>
	public async Task DisconnectAsync(CancellationToken ct = default)
	{
		Console.WriteLine("[WiFi] Disconnecting from current network.");

		if (OperatingSystem.IsWindows())
		{
			await RunCommandAsync("netsh", ["wlan", "disconnect"], ct);
		}
		else if (OperatingSystem.IsLinux())
		{
			var iface = await GetWifiInterfaceLinuxAsync(ct);
			if (iface is not null)
				await RunCommandAsync("nmcli", ["dev", "disconnect", iface], ct);
		}
	}

	// ── Windows ──────────────────────────────────────────────────────────────

	private static async Task<IReadOnlyList<string>> ScanWindowsAsync(CancellationToken ct)
	{
		var output = await RunCommandAsync(
			"netsh", ["wlan", "show", "networks", "mode=bssid"], ct);
		return ParseNetshNetworks(output);
	}

	/// <summary>
	/// Parses <c>netsh wlan show networks mode=bssid</c> output and returns SSIDs
	/// whose Authentication line contains "Open".
	/// </summary>
	internal static List<string> ParseNetshNetworks(string output)
	{
		var networks = new List<string>();
		string? currentSsid = null;
		bool isOpen = false;

		foreach (var rawLine in output.Split('\n'))
		{
			var line = rawLine.Trim();

			var ssidMatch = Regex.Match(line, @"^SSID \d+\s*:\s*(.+)$");
			if (ssidMatch.Success)
			{
				if (currentSsid is not null && isOpen)
					networks.Add(currentSsid);

				currentSsid = ssidMatch.Groups[1].Value.Trim();
				isOpen = false;
				continue;
			}

			if (line.StartsWith("Authentication", StringComparison.OrdinalIgnoreCase)
				&& line.Contains(": Open", StringComparison.OrdinalIgnoreCase))
			{
				isOpen = true;
			}
		}

		// Flush last entry.
		if (currentSsid is not null && isOpen)
			networks.Add(currentSsid);

		return networks;
	}

	private static async Task<bool> ConnectWindowsAsync(string ssid, CancellationToken ct)
	{
		// netsh requires a WLAN profile to connect. For open networks we generate a
		// minimal temporary profile, add it, connect, and clean it up afterwards.
		var profileXml = BuildOpenNetworkProfileXml(ssid);
		var tempFile = Path.Combine(
			Path.GetTempPath(), $"wlan_profile_{Guid.NewGuid():N}.xml");

		try
		{
			await File.WriteAllTextAsync(tempFile, profileXml, ct);

			await RunCommandAsync(
				"netsh", ["wlan", "add", "profile", $"filename={tempFile}"], ct);

			var output = await RunCommandAsync(
				"netsh", ["wlan", "connect", $"name={ssid}"], ct);

			// Allow time for the OS to associate and obtain a DHCP lease.
			await Task.Delay(TimeSpan.FromSeconds(5), ct);

			return output.Contains("completed successfully", StringComparison.OrdinalIgnoreCase);
		}
		finally
		{
			try { File.Delete(tempFile); } catch { /* best-effort */ }

			// Remove the temporary profile so it does not linger in Windows after this session.
			await RunCommandAsync(
				"netsh", ["wlan", "delete", "profile", $"name={ssid}"],
				CancellationToken.None);
		}
	}

	private static string BuildOpenNetworkProfileXml(string ssid)
	{
		// XML-escape the SSID to prevent a malformed profile from a crafted AP name.
		var safe = System.Security.SecurityElement.Escape(ssid) ?? ssid;
		return $"""
			<?xml version="1.0"?>
			<WLANProfile xmlns="http://www.microsoft.com/networking/WLAN/profile/v1">
				<name>{safe}</name>
				<SSIDConfig>
					<SSID>
						<name>{safe}</name>
					</SSID>
					<nonBroadcast>false</nonBroadcast>
				</SSIDConfig>
				<connectionType>ESS</connectionType>
				<connectionMode>manual</connectionMode>
				<MSM>
					<security>
						<authEncryption>
							<authentication>open</authentication>
							<encryption>none</encryption>
							<useOneX>false</useOneX>
						</authEncryption>
					</security>
				</MSM>
			</WLANProfile>
			""";
	}

	// ── Linux ─────────────────────────────────────────────────────────────────

	private static async Task<IReadOnlyList<string>> ScanLinuxAsync(CancellationToken ct)
	{
		// Trigger a fresh scan, then wait briefly for results to populate.
		await RunCommandAsync("nmcli", ["dev", "wifi", "rescan"], ct);
		await Task.Delay(TimeSpan.FromSeconds(3), ct);

		var output = await RunCommandAsync(
			"nmcli", ["--terse", "--fields", "SSID,SECURITY", "dev", "wifi", "list"], ct);
		return ParseNmcliNetworks(output);
	}

	/// <summary>
	/// Parses <c>nmcli --terse --fields SSID,SECURITY dev wifi list</c> output.
	/// Open networks have SECURITY value <c>--</c>.
	/// nmcli terse mode escapes colons inside field values as <c>\:</c>.
	/// </summary>
	internal static List<string> ParseNmcliNetworks(string output)
	{
		var networks = new List<string>();

		foreach (var line in output.Split('\n'))
		{
			if (string.IsNullOrWhiteSpace(line))
				continue;

			// Terse format: SSID:SECURITY  — last unescaped colon is the delimiter.
			var lastColon = line.LastIndexOf(':');
			if (lastColon <= 0)
				continue;

			var rawSsid = line[..lastColon];
			var security = line[(lastColon + 1)..].Trim();

			// Un-escape nmcli's \: escaping inside the SSID.
			var ssid = rawSsid.Replace("\\:", ":").Trim();

			// '--' means no security (open network); skip empty/hidden SSIDs.
			if (!string.IsNullOrEmpty(ssid) && security == "--")
				networks.Add(ssid);
		}

		return networks;
	}

	private static async Task<bool> ConnectLinuxAsync(string ssid, CancellationToken ct)
	{
		var output = await RunCommandAsync(
			"nmcli", ["dev", "wifi", "connect", ssid], ct);

		// Allow time for the interface to associate and receive a DHCP lease.
		await Task.Delay(TimeSpan.FromSeconds(5), ct);

		return output.Contains("successfully", StringComparison.OrdinalIgnoreCase);
	}

	private static async Task<string?> GetWifiInterfaceLinuxAsync(CancellationToken ct)
	{
		var output = await RunCommandAsync(
			"nmcli", ["-t", "-f", "DEVICE,TYPE", "dev", "status"], ct);

		foreach (var line in output.Split('\n'))
		{
			var parts = line.Split(':');
			if (parts.Length >= 2
				&& parts[1].Trim().Equals("wifi", StringComparison.OrdinalIgnoreCase))
			{
				return parts[0].Trim();
			}
		}

		return null;
	}

	// ── Shared helpers ────────────────────────────────────────────────────────

	private static async Task<string> RunCommandAsync(
		string fileName, string[] args, CancellationToken ct)
	{
		var psi = new ProcessStartInfo
		{
			FileName = fileName,
			UseShellExecute = false,
			CreateNoWindow = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
		};

		foreach (var arg in args)
			psi.ArgumentList.Add(arg);

		using var proc = Process.Start(psi);
		if (proc is null)
			return string.Empty;

		var stdout = await proc.StandardOutput.ReadToEndAsync(ct);
		await proc.WaitForExitAsync(ct);
		return stdout;
	}
}
