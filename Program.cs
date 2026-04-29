using System.Diagnostics;
using CaptivePortalAutoLogin;
using CaptivePortalAutoLogin.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

const string DefaultServiceName = "CaptivePortalAutoLogin";

if (args.Any(a => string.Equals(a, "--help", StringComparison.OrdinalIgnoreCase)))
{
	PrintHelp();
	return 0;
}

if (args.Any(a => string.Equals(a, "--install-service", StringComparison.OrdinalIgnoreCase)))
{
	if (!OperatingSystem.IsWindows())
	{
		Console.Error.WriteLine("[Main] Service installation is only supported on Windows.");
		return 1;
	}

	return InstallWindowsService(args, DefaultServiceName);
}

if (args.Any(a => string.Equals(a, "--uninstall-service", StringComparison.OrdinalIgnoreCase)))
{
	if (!OperatingSystem.IsWindows())
	{
		Console.Error.WriteLine("[Main] Service removal is only supported on Windows.");
		return 1;
	}

	return UninstallWindowsService(args, DefaultServiceName);
}

var builder = Host.CreateApplicationBuilder(args);

builder.Configuration
	.SetBasePath(AppContext.BaseDirectory)
	.AddJsonFile("appsettings.json", optional: false, reloadOnChange: false)
	.AddEnvironmentVariables();

if (OperatingSystem.IsWindows())
	builder.Services.AddWindowsService();

builder.Services
	.AddOptions<PortalConfig>()
	.Bind(builder.Configuration.GetSection("PortalConfig"))
	.PostConfigure(cfg =>
	{
		if (Environment.GetEnvironmentVariable("PORTAL_USERNAME") is { Length: > 0 } envUser)
			cfg.Username = envUser;
		if (Environment.GetEnvironmentVariable("PORTAL_PASSWORD") is { Length: > 0 } envPass)
			cfg.Password = envPass;
	});

builder.Services.AddHostedService<CaptivePortalWorker>();

var host = builder.Build();
await host.RunAsync();
return 0;

static int InstallWindowsService(string[] args, string defaultServiceName)
{
	var serviceName = GetArgValue(args, "--service-name") ?? defaultServiceName;
	var processPath = Environment.ProcessPath;
	if (string.IsNullOrWhiteSpace(processPath))
	{
		Console.Error.WriteLine("[Main] Could not determine executable path.");
		return 1;
	}

	if (Path.GetFileName(processPath).Equals("dotnet", StringComparison.OrdinalIgnoreCase) ||
		Path.GetFileName(processPath).Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase))
	{
		Console.Error.WriteLine(
			"[Main] Install from the built EXE, not dotnet run. Example: bin\\Release\\net8.0\\publish\\CaptivePortalAutoLogin.exe --install-service");
		return 1;
	}

	Console.WriteLine($"[Main] Installing Windows service '{serviceName}' for {processPath}");

	var quotedPath = $"\"{processPath}\"";
	var createExit = RunSc($"create \"{serviceName}\" binPath= {quotedPath} start= auto DisplayName= \"{serviceName}\"");
	if (createExit != 0)
		return createExit;

	_ = RunSc($"description \"{serviceName}\" \"Captive portal auto-login service\"");

	if (args.Any(a => string.Equals(a, "--start", StringComparison.OrdinalIgnoreCase)))
	{
		var startExit = RunSc($"start \"{serviceName}\"");
		if (startExit != 0)
			return startExit;
	}

	Console.WriteLine($"[Main] Service '{serviceName}' installed with automatic startup.");
	return 0;
}

static int UninstallWindowsService(string[] args, string defaultServiceName)
{
	var serviceName = GetArgValue(args, "--service-name") ?? defaultServiceName;
	Console.WriteLine($"[Main] Removing Windows service '{serviceName}'");

	_ = RunSc($"stop \"{serviceName}\"");
	var deleteExit = RunSc($"delete \"{serviceName}\"");
	if (deleteExit != 0)
		return deleteExit;

	Console.WriteLine($"[Main] Service '{serviceName}' removed.");
	return 0;
}

static string? GetArgValue(string[] args, string key)
{
	for (var i = 0; i < args.Length - 1; i++)
	{
		if (string.Equals(args[i], key, StringComparison.OrdinalIgnoreCase))
			return args[i + 1];
	}

	return null;
}

static int RunSc(string arguments)
{
	using var proc = Process.Start(new ProcessStartInfo
	{
		FileName = "sc.exe",
		Arguments = arguments,
		UseShellExecute = false,
		CreateNoWindow = true,
		RedirectStandardOutput = true,
		RedirectStandardError = true,
	});

	if (proc is null)
	{
		Console.Error.WriteLine("[Main] Failed to start sc.exe.");
		return 1;
	}

	var output = proc.StandardOutput.ReadToEnd();
	var error = proc.StandardError.ReadToEnd();
	proc.WaitForExit();

	if (!string.IsNullOrWhiteSpace(output))
		Console.WriteLine($"[Main] sc.exe: {output.Trim()}");
	if (!string.IsNullOrWhiteSpace(error))
		Console.Error.WriteLine($"[Main] sc.exe error: {error.Trim()}");

	return proc.ExitCode;
}

static void PrintHelp()
{
	Console.WriteLine("Captive Portal Auto-Login");
	Console.WriteLine();
	Console.WriteLine("Usage:");
	Console.WriteLine("  CaptivePortalAutoLogin.exe");
	Console.WriteLine("  CaptivePortalAutoLogin.exe --install-service [--service-name Name] [--start]");
	Console.WriteLine("  CaptivePortalAutoLogin.exe --uninstall-service [--service-name Name]");
	Console.WriteLine("  CaptivePortalAutoLogin.exe --help");
}

internal sealed class CaptivePortalWorker : BackgroundService
{
	private readonly PortalConfig _config;
	private readonly CaptivePortalDetector _detector;
	private readonly PortalLoginHandler _loginHandler;
	private readonly WifiScanner? _wifiScanner;
	private readonly IHostApplicationLifetime _lifetime;

	public CaptivePortalWorker(IOptions<PortalConfig> options, IHostApplicationLifetime lifetime)
	{
		_config = options.Value ?? new PortalConfig();
		_detector = new CaptivePortalDetector(_config);
		_loginHandler = new PortalLoginHandler(_config);
		_lifetime = lifetime;

		if (_config.EnableWifiScanning)
		{
			_wifiScanner = new WifiScanner(_config.BlockedSsids);
			Console.WriteLine("[Main] Wi-Fi scanning enabled.");
		}

		if (string.IsNullOrWhiteSpace(_config.Username) || string.IsNullOrWhiteSpace(_config.Password))
			Console.WriteLine("[Main] No credentials configured - click-through mode will be attempted.");
	}

	protected override async Task ExecuteAsync(CancellationToken stoppingToken)
	{
		Console.WriteLine("[Main] Captive Portal Auto-Login started.");
		int retries = 0;

		while (!stoppingToken.IsCancellationRequested)
		{
			try
			{
				var result = await _detector.CheckAsync(stoppingToken);

				if (result.IsConnectivityConfirmed)
				{
					retries = 0;
					Console.WriteLine($"[Main] Internet confirmed. Next check in {_config.PollIntervalSeconds}s.");
				}
				else if (result.IsPortalDetected)
				{
					retries++;
					Console.WriteLine($"[Main] Captive portal detected (attempt {retries}/{_config.MaxRetries}).");

					if (retries > _config.MaxRetries)
					{
						Console.Error.WriteLine($"[Main] Exceeded maximum retries ({_config.MaxRetries}). Giving up.");
						Environment.ExitCode = 2;
						_lifetime.StopApplication();
						break;
					}
					else
					{
						var success = await _loginHandler.LoginAsync(result.LoginPageUrl!, stoppingToken);
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
				else if (_wifiScanner is not null)
				{
					// No connectivity confirmed and no portal detected — try to find an open AP.
					await TryScanAndJoinAsync(stoppingToken);
				}
				else
				{
					Console.WriteLine($"[Main] No captive portal. Next check in {_config.PollIntervalSeconds}s.");
				}
			}
			catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
			{
				break;
			}
			catch (Exception ex)
			{
				Console.Error.WriteLine($"[Main] Unexpected error: {ex}");
			}

			try
			{
				await Task.Delay(TimeSpan.FromSeconds(_config.PollIntervalSeconds), stoppingToken);
			}
			catch (OperationCanceledException)
			{
				break;
			}
		}

		Console.WriteLine("[Main] Exited cleanly.");
	}

	/// <summary>
	/// Scans for open Wi-Fi access points and attempts to connect to each one.
	/// For each candidate: connects → probes → attempts portal login if needed.
	/// If an AP cannot provide internet access (or login fails), it is blocked for
	/// the remainder of the session and the next candidate is tried.
	/// </summary>
	private async Task TryScanAndJoinAsync(CancellationToken ct)
	{
		Console.WriteLine("[Main] No connectivity detected. Scanning for open Wi-Fi networks...");
		var openAps = await _wifiScanner!.ScanOpenNetworksAsync(ct);

		if (openAps.Count == 0)
		{
			Console.WriteLine("[Main] No open Wi-Fi networks found.");
			return;
		}

		Console.WriteLine($"[Main] Found {openAps.Count} open AP(s): {string.Join(", ", openAps.Select(s => $"'{s}'"))}");

		foreach (var ssid in openAps)
		{
			if (ct.IsCancellationRequested)
				break;

			Console.WriteLine($"[Main] Trying AP: '{ssid}'");

			var connected = await _wifiScanner.ConnectAsync(ssid, ct);
			if (!connected)
			{
				Console.WriteLine($"[Main] Could not connect to '{ssid}'. Blocking.");
				_wifiScanner.BlockSsid(ssid);
				continue;
			}

			// Allow the network stack to settle after association.
			await Task.Delay(TimeSpan.FromSeconds(3), ct);

			var probe = await _detector.CheckAsync(ct);

			if (probe.IsConnectivityConfirmed)
			{
				Console.WriteLine($"[Main] Internet confirmed via '{ssid}'.");
				return;
			}

			if (probe.IsPortalDetected)
			{
				Console.WriteLine($"[Main] Captive portal detected on '{ssid}'. Attempting login...");
				var loginOk = await _loginHandler.LoginAsync(probe.LoginPageUrl!, ct);
				if (loginOk)
				{
					Console.WriteLine($"[Main] Login succeeded on '{ssid}'.");
					return;
				}

				Console.Error.WriteLine($"[Main] Login failed on '{ssid}'. Blocking AP.");
			}
			else
			{
				Console.WriteLine($"[Main] No internet reachable via '{ssid}'. Blocking AP.");
			}

			_wifiScanner.BlockSsid(ssid);
			await _wifiScanner.DisconnectAsync(ct);
		}

		Console.WriteLine("[Main] No usable open Wi-Fi network found.");
	}
}
