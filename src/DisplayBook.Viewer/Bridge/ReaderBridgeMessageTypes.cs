namespace DisplayBook.Viewer.Bridge;

public static class ReaderBridgeMessageTypes
{
	public const string ReaderReady = "readerReady";
	public const string LocationChanged = "locationChanged";
	public const string ReaderError = "readerError";
	public const string ThemeChanged = "themeChanged";
	public const string RequestExit = "requestExit";
	public const string RequestSettings = "requestSettings";
	public const string DictionaryLookupRequested = "dictionaryLookupRequested";
}
