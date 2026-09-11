using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Views;

namespace DisplayBook.App;

[Activity(Theme = "@style/Maui.SplashTheme", MainLauncher = true, LaunchMode = LaunchMode.SingleTop, ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
public class MainActivity : MauiAppCompatActivity
{
	const int folderPickerRequestCode = 4107;
	TaskCompletionSource<string?>? folderPickerCompletion;

	protected override void OnCreate(Bundle? savedInstanceState)
	{
		base.OnCreate(savedInstanceState);
		ConfigureSystemBars();
	}

	protected override void OnResume()
	{
		base.OnResume();
		ConfigureSystemBars();
	}

	void ConfigureSystemBars()
	{
		if (!OperatingSystem.IsAndroidVersionAtLeast(35))
		{
			Window?.SetStatusBarColor(Android.Graphics.Color.Transparent);
		}

		if (Window?.DecorView is not { } decorView)
		{
			return;
		}

		if (OperatingSystem.IsAndroidVersionAtLeast(30) || (!OperatingSystem.IsAndroidVersionAtLeast(23)))
		{
			return;
		}

		SystemUiFlags systemUiFlags = decorView.SystemUiFlags |
			SystemUiFlags.LayoutStable |
			SystemUiFlags.LayoutFullscreen;

		if (Microsoft.Maui.Controls.Application.Current?.RequestedTheme == AppTheme.Light)
		{
			systemUiFlags |= SystemUiFlags.LightStatusBar;
		}
		else
		{
			systemUiFlags &= ~SystemUiFlags.LightStatusBar;
		}
		decorView.SystemUiFlags = systemUiFlags;
	}

	public Task<string?> PickFolderUriAsync()
	{
		if (folderPickerCompletion is not null)
		{
			throw new InvalidOperationException("A folder picker is already active.");
		}

		folderPickerCompletion = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
		Intent intent = new(Intent.ActionOpenDocumentTree);
		intent.AddFlags(ActivityFlags.GrantReadUriPermission | ActivityFlags.GrantPersistableUriPermission | ActivityFlags.GrantPrefixUriPermission);
		StartActivityForResult(intent, folderPickerRequestCode);
		return folderPickerCompletion.Task;
	}

	protected override void OnActivityResult(int requestCode, Result resultCode, Intent? data)
	{
		base.OnActivityResult(requestCode, resultCode, data);
		if (requestCode != folderPickerRequestCode || folderPickerCompletion is null)
		{
			return;
		}

		TaskCompletionSource<string?> completion = folderPickerCompletion;
		folderPickerCompletion = null;
		if (resultCode == Result.Ok && data?.Data is not null)
		{
			try
			{
				ActivityFlags takeFlags = data.Flags & (ActivityFlags.GrantReadUriPermission | ActivityFlags.GrantWriteUriPermission);
				ContentResolver?.TakePersistableUriPermission(data.Data, takeFlags);
			}
			catch (Java.Lang.SecurityException)
			{
				// The copied content is still available for this import even when persistence is denied.
			}

			completion.TrySetResult(data.Data.ToString());
			return;
		}

		completion.TrySetResult(null);
	}
}
