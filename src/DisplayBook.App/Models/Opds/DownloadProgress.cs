namespace DisplayBook.App.Models;

public enum DownloadStatus
{
    Queued,
    Downloading,
    Paused,
    Completed,
    Failed,
    Canceled
}

public sealed class DownloadLink
{
    public string Url { get; set; } = string.Empty;

    /// <summary>
    /// MIME type of the resource (e.g. application/epub+zip).
    /// </summary>
    public string? Format { get; set; }

    /// <summary>
    /// Human-friendly format name derived from the MIME type (EPUB, PDF, MOBI, ...).
    /// </summary>
    public string FormatName { get; set; } = "File";

    public long Size { get; set; }

    public string? Title { get; set; }

    public bool IsAcquisition { get; set; } = true;

    public Dictionary<string, string> Properties { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class DownloadProgress
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string BookTitle { get; set; } = string.Empty;

    public string FileName { get; set; } = string.Empty;

    public string Url { get; set; } = string.Empty;

    public string? ServerId { get; set; }

    public long BytesDownloaded { get; set; }

    public long TotalBytes { get; set; }

    public double Percentage { get; set; }

    /// <summary>
    /// Download speed in bytes per second.
    /// </summary>
    public double Speed { get; set; }

    public TimeSpan TimeRemaining { get; set; }

    public DownloadStatus Status { get; set; } = DownloadStatus.Queued;

    public string? Error { get; set; }

    public DateTime StartedAt { get; set; }

    public DateTime? CompletedAt { get; set; }

    /// <summary>
    /// Absolute path of the partially written file (used to resume interrupted downloads).
    /// </summary>
    public string? TempFilePath { get; set; }

    /// <summary>
    /// Batch this item belongs to (null for standalone downloads).
    /// </summary>
    public string? BatchId { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public CancellationTokenSource? Cts { get; set; }
}

public sealed class DownloadBatchProgress
{
    public string BatchId { get; set; } = Guid.NewGuid().ToString("N");

    public int TotalItems { get; set; }

    public int CompletedItems { get; set; }

    public int FailedItems { get; set; }

    public long TotalBytes { get; set; }

    public long DownloadedBytes { get; set; }

    public string? CurrentItem { get; set; }

    public double Percentage
    {
        get
        {
            if (TotalBytes > 0)
            {
                return Math.Clamp((double)DownloadedBytes / TotalBytes, 0, 1);
            }

            return TotalItems > 0 ? (double)CompletedItems / TotalItems : 0;
        }
    }
}
