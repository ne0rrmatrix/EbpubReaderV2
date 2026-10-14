using Microsoft.Maui.Controls.Internals;

namespace DisplayBook.DeviceTests.Support;

/// <summary>
/// Answers <c>DisplayAlertAsync</c>, <c>DisplayActionSheetAsync</c> and <c>DisplayPromptAsync</c>
/// without showing anything, through the keyed services MAUI checks before it shows a platform
/// dialog (registered in <see cref="MauiProgram"/>). A test queues the answers it wants before
/// triggering the prompt; with nothing queued an alert is declined, so a test can never hang on
/// a dialog nobody will dismiss.
/// </summary>
static class TestDialogs
{
	public const string AlertKey = "Microsoft.Maui.Controls.DisplayAlert";
	public const string ActionSheetKey = "Microsoft.Maui.Controls.DisplayActionSheet";
	public const string PromptKey = "Microsoft.Maui.Controls.DisplayPrompt";

	static readonly Lock gate = new();
	static readonly Queue<bool> alertAnswers = new();
	static readonly List<AlertArguments> alerts = [];

	/// <summary>Every alert shown since the last <see cref="Reset"/>, oldest first.</summary>
	public static IReadOnlyList<AlertArguments> Alerts
	{
		get
		{
			lock (gate)
			{
				return [.. alerts];
			}
		}
	}

	/// <summary>Accepts (true) or declines (false) the next alert.</summary>
	public static void AnswerNextAlert(bool accept)
	{
		lock (gate)
		{
			alertAnswers.Enqueue(accept);
		}
	}

	public static void Reset()
	{
		lock (gate)
		{
			alertAnswers.Clear();
			alerts.Clear();
		}
	}

	internal static Task<bool> OnAlertAsync(Page page, AlertArguments arguments)
	{
		lock (gate)
		{
			alerts.Add(arguments);
			return Task.FromResult(alertAnswers.TryDequeue(out bool accept) && accept);
		}
	}

	internal static Task<string> OnActionSheetAsync(Page page, ActionSheetArguments arguments) =>
		Task.FromResult(arguments.Cancel ?? string.Empty);

	internal static Task<string> OnPromptAsync(Page page, PromptArguments arguments) =>
		Task.FromResult<string>(null!);
}
