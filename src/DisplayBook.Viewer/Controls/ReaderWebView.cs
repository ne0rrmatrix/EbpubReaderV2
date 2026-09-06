namespace DisplayBook.Viewer.Controls;

/// <summary>
/// The WebView used by <see cref="EpubReaderView"/>.
/// </summary>
public sealed class ReaderWebView : WebView
{
    public static readonly BindableProperty IsReaderContentHostProperty = BindableProperty.Create(
        nameof(IsReaderContentHost),
        typeof(bool),
        typeof(ReaderWebView),
        true);

    /// <summary>
    /// Gets or sets whether the native WebView should receive reader-specific configuration.
    /// </summary>
    public bool IsReaderContentHost
    {
        get => (bool)GetValue(IsReaderContentHostProperty);
        set => SetValue(IsReaderContentHostProperty, value);
    }
}