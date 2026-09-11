#if ANDROID
using System.Text.Json;
using Android.Views;
using DisplayBook.Viewer.Serialization;
using Microsoft.Maui.Handlers;
using Microsoft.Maui.Platform;

namespace DisplayBook.Viewer.Handlers;

/// <summary>
/// The reader's Android WebView. Subclasses MAUI's <c>MauiWebView</c> (so navigation,
/// cookies, and scroll-capture behavior are unchanged) to replace the operating system's
/// text-selection toolbar ("Search with Google", "Share", "Translate", ...) with the
/// app's own single "Look up" action whenever text is selected in the reader.
/// </summary>
sealed class ReaderSelectionWebView(WebViewHandler handler, Android.Content.Context context) : MauiWebView(handler, context)
{
	/// <summary>
	/// Raised with the currently selected text when the user taps "Look up" in the
	/// replacement selection toolbar.
	/// </summary>
	public event EventHandler<string>? SelectionLookupRequested;

	public override ActionMode? StartActionMode(ActionMode.ICallback? callback, ActionModeType type)
	{
		// The Chromium-backed WebView requests the floating toolbar (API 23+) for
		// long-press text selection. Any other ActionMode request (e.g. the primary
		// toolbar) keeps the caller's original callback.
		return type != ActionModeType.Floating || !OperatingSystem.IsAndroidVersionAtLeast(23)
			? base.StartActionMode(callback, type)
			: base.StartActionMode(new ReaderSelectionActionModeCallback(this), type);
	}

	internal void RaiseSelectionLookup(string selectedText)
	{
		if (!string.IsNullOrEmpty(selectedText))
		{
			SelectionLookupRequested?.Invoke(this, selectedText);
		}
	}
}

/// <summary>
/// Owns the replacement selection toolbar. The menu is cleared before anything is added,
/// which suppresses the OS items ("Search with Google", "Share", "Translate", ...) that
/// would otherwise populate the selection toolbar.
/// </summary>
sealed class ReaderSelectionActionModeCallback(ReaderSelectionWebView view) : Java.Lang.Object, Android.Views.ActionMode.ICallback
{
	const int lookupItemId = 1001;
	const string lookupLabel = "Look up";

	public bool OnCreateActionMode(ActionMode? mode, IMenu? menu)
	{
		// Wipe the default system items, then add the app's own.
		menu?.Clear();
		menu?.Add(IMenu.None, lookupItemId, IMenu.None, lookupLabel);
		return true;
	}

	public bool OnPrepareActionMode(ActionMode? mode, IMenu? menu) => false;

	public bool OnActionItemClicked(ActionMode? mode, IMenuItem? item)
	{
		if (item?.ItemId != lookupItemId)
		{
			return false;
		}

		mode?.Finish();
		_ = HandleLookupAsync();
		return true;
	}

	public void OnDestroyActionMode(ActionMode? mode)
	{
	}

	/// <summary>
	/// The public Android WebView API has no way to read the current selection directly,
	/// so it's read back via the reader's own <c>getSelectionInfo()</c> JS helper, using
	/// the same <see cref="EvaluateJavaScriptAsyncRequest"/> plumbing MAUI's own WebView
	/// uses.
	/// </summary>
	async Task HandleLookupAsync()
	{
		EvaluateJavaScriptAsyncRequest request = new(
			"JSON.stringify(window.DisplayBookReader?.getSelectionInfo()?.text ?? null)");
		view.EvaluateJavaScript(request);
		string rawResult = await request.Task;
		string? selectedText = JsonSerializer.Deserialize(rawResult, ReaderJsonContext.Default.String)?.Trim();
		if (!string.IsNullOrEmpty(selectedText))
		{
			view.RaiseSelectionLookup(selectedText);
		}
	}
}
#endif
