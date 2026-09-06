using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;

namespace DisplayBook.App;

[Activity(Theme = "@style/Maui.SplashTheme", MainLauncher = true, LaunchMode = LaunchMode.SingleTop, ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
public class MainActivity : MauiAppCompatActivity
{
	private const int FolderPickerRequestCode = 4107;
	private TaskCompletionSource<string?>? _folderPickerCompletion;

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

	private void ConfigureSystemBars()
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

        var systemUiFlags = decorView.SystemUiFlags |
			Android.Views.SystemUiFlags.LayoutStable |
			Android.Views.SystemUiFlags.LayoutFullscreen;

		if (Microsoft.Maui.Controls.Application.Current?.RequestedTheme == AppTheme.Light)
		{
			systemUiFlags |= Android.Views.SystemUiFlags.LightStatusBar;
		}
		else
		{
			systemUiFlags &= ~Android.Views.SystemUiFlags.LightStatusBar;
		}
        decorView.SystemUiFlags = systemUiFlags;
    }

	public Task<string?> PickFolderUriAsync()
	{
		if (_folderPickerCompletion is not null)
		{
			throw new InvalidOperationException("A folder picker is already active.");
		}

		_folderPickerCompletion = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
		var intent = new Intent(Intent.ActionOpenDocumentTree);
		intent.AddFlags(ActivityFlags.GrantReadUriPermission | ActivityFlags.GrantPersistableUriPermission | ActivityFlags.GrantPrefixUriPermission);
		StartActivityForResult(intent, FolderPickerRequestCode);
		return _folderPickerCompletion.Task;
	}

	protected override void OnActivityResult(int requestCode, Result resultCode, Intent? data)
	{
		base.OnActivityResult(requestCode, resultCode, data);
		if (requestCode != FolderPickerRequestCode || _folderPickerCompletion is null)
		{
			return;
		}

		var completion = _folderPickerCompletion;
		_folderPickerCompletion = null;
		if (resultCode == Result.Ok && data?.Data is not null)
		{
			try
			{
				var takeFlags = data.Flags & (ActivityFlags.GrantReadUriPermission | ActivityFlags.GrantWriteUriPermission);
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
