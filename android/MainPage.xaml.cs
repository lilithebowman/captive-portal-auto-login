#if ANDROID
using Android.Content;
#endif

using Microsoft.Maui.Controls;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Devices;
using Microsoft.Maui.Storage;

namespace CaptivePortalAutoLogin;

public partial class MainPage : ContentPage
{
	private bool _serviceRunning;
	private readonly List<string> _logLines = [];
	private const int MaxLogLines = 200;
	private string _currentDetailedStatus = string.Empty;
	private DateTime _lastStatusUpdate = DateTime.Now;

	public MainPage()
	{
		InitializeComponent();
		LoadSettings();

#if ANDROID
        CaptivePortalForegroundService.LogReceived += OnLogReceived;
        CaptivePortalForegroundService.StatusChanged += OnStatusChanged;
#endif
	}

	protected override void OnDisappearing()
	{
		base.OnDisappearing();
#if ANDROID
        CaptivePortalForegroundService.LogReceived -= OnLogReceived;
        CaptivePortalForegroundService.StatusChanged -= OnStatusChanged;
#endif
	}

	private void LoadSettings()
	{
		UsernameEntry.Text = Preferences.Get("username", string.Empty);
		PasswordEntry.Text = Preferences.Get("password", string.Empty);
		PollStepper.Value = Preferences.Get("pollInterval", 10);
		RetryStepper.Value = Preferences.Get("maxRetries", 5);
		WifiScanSwitch.IsToggled = Preferences.Get("enableWifiScan", false);
		OverrideUrlEntry.Text = Preferences.Get("overrideLoginUrl", string.Empty);
		PollLabel.Text = $"{(int)PollStepper.Value} s";
		RetryLabel.Text = $"{(int)RetryStepper.Value}";
	}

	private void SaveSettings()
	{
		Preferences.Set("username", UsernameEntry.Text?.Trim() ?? string.Empty);
		Preferences.Set("password", PasswordEntry.Text ?? string.Empty);
		Preferences.Set("pollInterval", (int)PollStepper.Value);
		Preferences.Set("maxRetries", (int)RetryStepper.Value);
		Preferences.Set("enableWifiScan", WifiScanSwitch.IsToggled);
		Preferences.Set("overrideLoginUrl", OverrideUrlEntry.Text?.Trim() ?? string.Empty);
	}

	private void OnPollStepperChanged(object? sender, ValueChangedEventArgs e)
		=> PollLabel.Text = $"{(int)e.NewValue} s";

	private void OnRetryStepperChanged(object? sender, ValueChangedEventArgs e)
		=> RetryLabel.Text = $"{(int)e.NewValue}";

	private async void OnStartStopClicked(object? sender, EventArgs e)
	{
		try
		{
			if (_serviceRunning)
				StopService();
			else
				await StartServiceAsync();
		}
		catch (Exception ex)
		{
			OnLogReceived($"[Main] ✗ Failed to update service state: {ex.Message}");
			OnStatusChanged(false);
		}
	}

	private async Task StartServiceAsync()
	{
		SaveSettings();

		if (!await EnsureRequiredPermissionsAsync())
			return;

#if ANDROID
		var intent = new Intent(Platform.AppContext, typeof(CaptivePortalForegroundService));
		intent.SetAction(CaptivePortalForegroundService.ActionStart);
		if (OperatingSystem.IsAndroidVersionAtLeast(26))
			Platform.AppContext.StartForegroundService(intent);
		else
			Platform.AppContext.StartService(intent);
#endif
	}

	private async Task<bool> EnsureRequiredPermissionsAsync()
	{
		try
		{
			var missingPermissions = new List<string>();

			if (WifiScanSwitch.IsToggled)
			{
				var locationStatus = await Permissions.CheckStatusAsync<Permissions.LocationWhenInUse>();
				if (locationStatus != PermissionStatus.Granted)
					locationStatus = await Permissions.RequestAsync<Permissions.LocationWhenInUse>();

				if (locationStatus != PermissionStatus.Granted)
					missingPermissions.Add("Location");
			}

			if (DeviceInfo.Platform == DevicePlatform.Android && OperatingSystem.IsAndroidVersionAtLeast(33))
			{
				var notificationStatus = await Permissions.CheckStatusAsync<Permissions.PostNotifications>();
				if (notificationStatus != PermissionStatus.Granted)
					notificationStatus = await Permissions.RequestAsync<Permissions.PostNotifications>();

				if (notificationStatus != PermissionStatus.Granted)
					missingPermissions.Add("Notifications");
			}

			if (missingPermissions.Count == 0)
				return true;

			var deniedList = string.Join(", ", missingPermissions);
			var message = $"[Main] Required permissions denied: {deniedList}. Service not started.";
			OnLogReceived(message);
			OnStatusChanged(false);
			_currentDetailedStatus = $"Permission required: {deniedList}";
			DetailedStatusLabel.Text = _currentDetailedStatus;
			DetailedStatusLabel.TextColor = Color.FromArgb("#C62828");
			return false;
		}
		catch (Exception ex)
		{
			OnLogReceived($"[Main] ✗ Permission check failed: {ex.Message}");
			OnStatusChanged(false);
			return false;
		}
	}

	private void StopService()
	{
#if ANDROID
        var intent = new Intent(Platform.AppContext, typeof(CaptivePortalForegroundService));
        intent.SetAction(CaptivePortalForegroundService.ActionStop);
        Platform.AppContext.StartService(intent);
#endif
	}

	private void OnStatusChanged(bool running)
	{
		MainThread.BeginInvokeOnMainThread(() =>
		{
			_serviceRunning = running;
			if (running)
			{
				StartStopButton.Text = "Stop Service";
				StartStopButton.BackgroundColor = Color.FromArgb("#C62828");
				StatusLabel.Text = "Service running";
				StatusFrame.BackgroundColor = Color.FromArgb("#E8F5E9");
				StatusLabel.TextColor = Color.FromArgb("#2E7D32");
				ActivityIndicator.IsRunning = true;
				ActivityIndicator.IsVisible = true;
				_currentDetailedStatus = "Initializing…";
				DetailedStatusLabel.Text = _currentDetailedStatus;
				DetailedStatusLabel.TextColor = Color.FromArgb("#558B2F");
			}
			else
			{
				StartStopButton.Text = "Start Service";
				StartStopButton.BackgroundColor = Color.FromArgb("#512BD4");
				StatusLabel.Text = "Service stopped";
				StatusFrame.BackgroundColor = Color.FromArgb("#FAFAFA");
				StatusLabel.TextColor = Color.FromArgb("#757575");
				ActivityIndicator.IsRunning = false;
				ActivityIndicator.IsVisible = false;
				_currentDetailedStatus = string.Empty;
				DetailedStatusLabel.Text = _currentDetailedStatus;
			}
		});
	}

	private void OnLogReceived(string message)
	{
		MainThread.BeginInvokeOnMainThread(() =>
		{
			_logLines.Add($"[{DateTime.Now:HH:mm:ss}] {message}");
			if (_logLines.Count > MaxLogLines)
				_logLines.RemoveAt(0);

			LogLabel.Text = string.Join('\n', _logLines);
			LogScrollView.ScrollToAsync(0, double.MaxValue, false);

			// Update detailed status based on log content
			UpdateDetailedStatus(message);
		});
	}

	private void UpdateDetailedStatus(string logMessage)
	{
		// Parse log messages to update the detailed status display
		if (logMessage.Contains("Internet confirmed"))
		{
			_currentDetailedStatus = "✓ Connected • Next check in";
			DetailedStatusLabel.TextColor = Color.FromArgb("#2E7D32");
		}
		else if (logMessage.Contains("Captive portal detected"))
		{
			_currentDetailedStatus = "⚠ Portal detected • Logging in…";
			DetailedStatusLabel.TextColor = Color.FromArgb("#F57F17");
		}
		else if (logMessage.Contains("Login succeeded"))
		{
			_currentDetailedStatus = "✓ Login successful • Verifying…";
			DetailedStatusLabel.TextColor = Color.FromArgb("#2E7D32");
		}
		else if (logMessage.Contains("Login attempt failed"))
		{
			_currentDetailedStatus = "✗ Login failed • Retrying…";
			DetailedStatusLabel.TextColor = Color.FromArgb("#C62828");
		}
		else if (logMessage.Contains("click-through"))
		{
			_currentDetailedStatus = "ℹ Click-through mode active";
			DetailedStatusLabel.TextColor = Color.FromArgb("#1565C0");
		}
		else if (logMessage.Contains("Exceeded maximum retries"))
		{
			_currentDetailedStatus = "✗ Max retries exceeded • Stopped";
			DetailedStatusLabel.TextColor = Color.FromArgb("#C62828");
		}
		else if (logMessage.Contains("Scanning for open networks"))
		{
			_currentDetailedStatus = "⊘ Scanning for Wi-Fi networks…";
			DetailedStatusLabel.TextColor = Color.FromArgb("#1565C0");
		}

		if (!string.IsNullOrEmpty(_currentDetailedStatus))
		{
			DetailedStatusLabel.Text = _currentDetailedStatus;
			_lastStatusUpdate = DateTime.Now;
		}
	}

	private void OnClearLogClicked(object? sender, EventArgs e)
	{
		_logLines.Clear();
		LogLabel.Text = string.Empty;
	}
}
