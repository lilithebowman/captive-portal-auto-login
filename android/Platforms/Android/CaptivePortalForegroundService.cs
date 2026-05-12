using Android.App;
using Android.Content;
using Android.OS;
using Microsoft.Maui.Storage;

namespace CaptivePortalAutoLogin;

/// <summary>
/// Android Foreground Service that hosts the captive-portal detection/login loop.
/// Started and stopped via explicit Intents from <see cref="MainPage"/>.
/// Persists as a foreground service so Android does not kill the loop in the background.
/// </summary>
[Service(
	Name = "com.captiveportal.autologin.PortalService",
	Exported = false)]
public class CaptivePortalForegroundService : Service
{
	public const string ActionStart = "com.captiveportal.autologin.ACTION_START";
	public const string ActionStop = "com.captiveportal.autologin.ACTION_STOP";

	private const int NotificationId = 2001;
	private const string ChannelId = "portal_service";

	private CancellationTokenSource? _cts;
	private Task? _workerTask;

	// Static events used to push log lines and status updates to the UI without
	// requiring a bound service connection.
	public static event Action<string>? LogReceived;
	public static event Action<bool>? StatusChanged;

	// -------------------------------------------------------------------
	// Service lifecycle
	// -------------------------------------------------------------------

	public override IBinder? OnBind(Intent? intent) => null;

	public override StartCommandResult OnStartCommand(
		Intent? intent, StartCommandFlags flags, int startId)
	{
		var action = intent?.Action;

		if (action == ActionStop)
		{
			StopWorker();
			StopForeground(StopForegroundFlags.Remove);
			StopSelf();
			return StartCommandResult.NotSticky;
		}

		// Ignore duplicate starts if the worker is already running.
		if (_cts is not null)
			return StartCommandResult.Sticky;

		EnsureNotificationChannel();
		var notification = BuildNotification("Starting…");

		// Android 10+ (Q) requires the foreground service type to be declared at runtime
		// AND in the manifest. Android 14+ (UpsideDownCake) enforces this strictly.
		if (OperatingSystem.IsAndroidVersionAtLeast(29))
			StartForeground(NotificationId, notification,
				global::Android.Content.PM.ForegroundService.TypeDataSync);
		else
			StartForeground(NotificationId, notification);

		StartWorker();
		StatusChanged?.Invoke(true);
		return StartCommandResult.Sticky;
	}

	public override void OnDestroy()
	{
		StopWorker();
		StatusChanged?.Invoke(false);
		base.OnDestroy();
	}

	// -------------------------------------------------------------------
	// Worker management
	// -------------------------------------------------------------------

	private void StartWorker()
	{
		_cts = new CancellationTokenSource();
		var config = LoadConfig();

		IWifiScanner? wifi = config.EnableWifiScanning
			? new AndroidWifiScanner(config.BlockedSsids)
			: null;

		var worker = new PortalWorker(config, wifi, OnLog);
		var token = _cts.Token;

		_workerTask = Task.Run(async () =>
		{
			try
			{
				await worker.RunAsync(token);
			}
			catch (System.OperationCanceledException) { }
			catch (Exception ex)
			{
				OnLog($"[Service] Worker crashed: {ex.Message}");
			}
			finally
			{
				MainThread.BeginInvokeOnMainThread(() => StatusChanged?.Invoke(false));
			}
		}, token);
	}

	private void StopWorker()
	{
		_cts?.Cancel();
		_cts?.Dispose();
		_cts = null;
	}

	// -------------------------------------------------------------------
	// Config loading from MAUI Preferences
	// -------------------------------------------------------------------

	private static Models.PortalConfig LoadConfig() => new()
	{
		Username = Preferences.Get("username", string.Empty),
		Password = Preferences.Get("password", string.Empty),
		PollIntervalSeconds = Preferences.Get("pollInterval", 10),
		MaxRetries = Preferences.Get("maxRetries", 5),
		EnableWifiScanning = Preferences.Get("enableWifiScan", false),
		OverrideLoginUrl = Preferences.Get("overrideLoginUrl", string.Empty),
	};

	// -------------------------------------------------------------------
	// Logging
	// -------------------------------------------------------------------

	private void OnLog(string message)
	{
		global::Android.Util.Log.Debug("CaptivePortal", message);
		LogReceived?.Invoke(message);

		// Extract status for notification
		var notificationText = ExtractStatusForNotification(message);
		UpdateNotification(notificationText);
	}

	private string ExtractStatusForNotification(string message)
	{
		// Keep notification text concise and actionable
		return message switch
		{
			var m when m.Contains("Checking connectivity") => "Checking connectivity…",
			var m when m.Contains("Internet confirmed") => "✓ Connected",
			var m when m.Contains("Captive portal detected") => "⚠ Portal detected, logging in…",
			var m when m.Contains("Attempting login") => "Submitting login form…",
			var m when m.Contains("Login succeeded") => "✓ Login successful",
			var m when m.Contains("Login attempt failed") => "✗ Login failed, retrying…",
			var m when m.Contains("Scanning for") => "⊘ Scanning for networks…",
			var m when m.Contains("Connected to") => "✓ Connected to network",
			var m when m.Contains("Connecting to") => "Connecting to network…",
			var m when m.Contains("Exceeded maximum retries") => "✗ Max retries exceeded",
			_ => message.Length > 60 ? message[..60] + "…" : message
		};
	}

	// -------------------------------------------------------------------
	// Notification helpers
	// -------------------------------------------------------------------

	private void UpdateNotification(string text)
	{
		var nm = GetSystemService(NotificationService) as NotificationManager;
		nm?.Notify(NotificationId, BuildNotification(text));
	}

	private void EnsureNotificationChannel()
	{
		if (!OperatingSystem.IsAndroidVersionAtLeast(26)) return;

		var channel = new NotificationChannel(
			ChannelId,
			"Captive Portal Service",
			NotificationImportance.Low)
		{
			Description = "Shows captive portal auto-login service status."
		};

		var nm = GetSystemService(NotificationService) as NotificationManager;
		nm?.CreateNotificationChannel(channel);
	}

	private Notification BuildNotification(string contentText)
	{
		// Tapping the notification reopens the app.
		var pendingIntentFlags = OperatingSystem.IsAndroidVersionAtLeast(23)
			? PendingIntentFlags.Immutable
			: PendingIntentFlags.UpdateCurrent;

		var pendingIntent = PendingIntent.GetActivity(
			this,
			0,
			new Intent(this, typeof(MainActivity)),
			pendingIntentFlags);

		return new Notification.Builder(this, ChannelId)
			.SetContentTitle("Captive Portal Auto-Login")
			.SetContentText(contentText)
			.SetSmallIcon(global::Android.Resource.Drawable.IcDialogInfo)
			.SetOngoing(true)
			.SetContentIntent(pendingIntent)
			.Build()!;
	}
}
