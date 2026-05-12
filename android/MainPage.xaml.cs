#if ANDROID
using Android.Content;
#endif

using Microsoft.Maui.Storage;

namespace CaptivePortalAutoLogin;

public partial class MainPage : ContentPage
{
	private bool _serviceRunning;
	private readonly List<string> _logLines = [];
	private const int MaxLogLines = 200;

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

	private void OnStartStopClicked(object? sender, EventArgs e)
	{
		if (_serviceRunning)
			StopService();
		else
			StartService();
	}

	private void StartService()
	{
		SaveSettings();
#if ANDROID
        var intent = new Intent(Platform.AppContext, typeof(CaptivePortalForegroundService));
        intent.SetAction(CaptivePortalForegroundService.ActionStart);
        Platform.AppContext.StartForegroundService(intent);
#endif
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
			}
			else
			{
				StartStopButton.Text = "Start Service";
				StartStopButton.BackgroundColor = Color.FromArgb("#512BD4");
				StatusLabel.Text = "Service stopped";
				StatusFrame.BackgroundColor = Color.FromArgb("#FAFAFA");
				StatusLabel.TextColor = Color.FromArgb("#757575");
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
		});
	}

	private void OnClearLogClicked(object? sender, EventArgs e)
	{
		_logLines.Clear();
		LogLabel.Text = string.Empty;
	}
}
