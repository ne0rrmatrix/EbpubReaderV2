namespace DisplayBook.App.Services.Sync;

public static class SyncConstants
{
    public const string HttpClientName = "firebase";

    public static readonly TimeSpan HttpClientTimeout = TimeSpan.FromSeconds(20);
}
