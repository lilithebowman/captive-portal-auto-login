using Android.App;
using Android.Content.PM;
using Android.OS;

namespace CaptivePortalAutoLogin;

[Activity(
	Theme = "@style/Maui.SplashTheme",
	MainLauncher = true,
	ConfigurationChanges =
		ConfigChanges.ScreenSize |
		ConfigChanges.Orientation |
		ConfigChanges.UiMode |
		ConfigChanges.ScreenLayout |
		ConfigChanges.SmallestScreenSize |
		ConfigChanges.Density)]
public class MainActivity : MauiAppCompatActivity
{
	private const int RequestNotificationPermission = 1001;

	protected override void OnCreate(Bundle? savedInstanceState)
	{
		base.OnCreate(savedInstanceState);

		// Request POST_NOTIFICATIONS permission at runtime (Android 13+).
		if (Build.VERSION.SdkInt >= BuildVersionCodes.Tiramisu)
		{
			RequestPermissions(
				[global::Android.Manifest.Permission.PostNotifications],
				RequestNotificationPermission);
		}

		// Request location permission needed for Wi-Fi scanning (Android 9+).
		if (Build.VERSION.SdkInt >= BuildVersionCodes.P)
		{
			RequestPermissions(
				[global::Android.Manifest.Permission.AccessFineLocation],
				1002);
		}
	}
}
