using System.Globalization;

namespace DisplayBook.App.Views;

/// <summary>
/// Formats a byte count as a human readable size (B, KB, MB, GB).
/// </summary>
public sealed class ByteSizeConverter : IValueConverter
{
	public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
	{
		long bytes = value switch
		{
			long l => l,
			int i => i,
			null => 0L,
			_ => 0L
		};

		return FormatSize(bytes);
	}

	public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
	{
		throw new NotSupportedException();
	}

	internal static string FormatSize(long bytes)
	{
		return bytes < 0
			? string.Empty
			: bytes switch
			{
				< 1_024L => $"{bytes} B",
				< 1_024L * 1_024L => $"{bytes / 1_024.0:0.#} KB",
				< 1_024L * 1_024L * 1_024L => $"{bytes / (1_024.0 * 1_024.0):0.#} MB",
				_ => $"{bytes / (1_024.0 * 1_024.0 * 1_024.0):0.##} GB"
			};
	}
}

/// <summary>
/// Inverts a boolean (e.g. to show a placeholder when no cover image is present).
/// </summary>
public sealed class InverseBoolConverter : IValueConverter
{
	public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
	{
		return value is bool b ? !b : value is null;
	}

	public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
	{
		throw new NotSupportedException();
	}
}

/// <summary>
/// Formats a 0..1 fraction as a percentage string.
/// </summary>
public sealed class PercentageConverter : IValueConverter
{
	public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
	{
		double fraction = value switch
		{
			double d => d,
			int i => i,
			null => 0d,
			_ => 0d
		};

		return $"{fraction * 100:0}%";
	}

	public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
	{
		throw new NotSupportedException();
	}
}

/// <summary>
/// Maps a collection count to visibility: visible when the count is zero (empty-state hints).
/// </summary>
public sealed class ZeroCountConverter : IValueConverter
{
	public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
	{
		int count = value switch
		{
			int i => i,
			null => 0,
			_ => int.TryParse(value.ToString(), out int parsed) ? parsed : 0
		};

		return count == 0;
	}

	public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
	{
		throw new NotSupportedException();
	}
}

/// <summary>
/// Maps a string to visibility: visible when the value is non-null and non-whitespace
/// (used for optional error/status labels).
/// </summary>
public sealed class StringNotNullConverter : IValueConverter
{
	public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
	{
		return value is string s && !string.IsNullOrWhiteSpace(s);
	}

	public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
	{
		throw new NotSupportedException();
	}
}

/// <summary>
/// Compares a bound value (typically an enum) against the converter <c>parameter</c>
/// (its string form, e.g. <c>x:Static</c> or a plain string matching <c>ToString()</c>).
/// Used to gate per-state layout visibility off a single state property.
/// </summary>
public sealed class EnumEqualsConverter : IValueConverter
{
	public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
	{
		return value is not null && parameter is not null && string.Equals(value.ToString(), parameter.ToString(), StringComparison.Ordinal);
	}

	public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
	{
		throw new NotSupportedException();
	}
}

/// <summary>
/// The negation of <see cref="EnumEqualsConverter"/> — visible whenever the bound value
/// does NOT match the converter <c>parameter</c>.
/// </summary>
public sealed class EnumNotEqualsConverter : IValueConverter
{
	public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
	{
		return value is null || parameter is null || !string.Equals(value.ToString(), parameter.ToString(), StringComparison.Ordinal);
	}

	public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
	{
		throw new NotSupportedException();
	}
}

/// <summary>
/// Maps a <see cref="Models.DownloadStatus"/> to a fixed color that reads well on both light
/// and dark themes (used for status text and progress bars).
/// </summary>
public sealed class DownloadStatusColorConverter : IValueConverter
{
	public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
	{
		return value is not Models.DownloadStatus status
			? new Color(0x6E, 0x6E, 0x6E)
			: status switch
			{
				Models.DownloadStatus.Queued => new Color(0x6E, 0x6E, 0x6E),
				Models.DownloadStatus.Downloading => new Color(0x23, 0x6B, 0x5A),
				Models.DownloadStatus.Paused => new Color(0xB4, 0x53, 0x09),
				Models.DownloadStatus.Completed => new Color(0x15, 0x80, 0x3D),
				Models.DownloadStatus.Failed => new Color(0xB4, 0x23, 0x18),
				Models.DownloadStatus.Canceled => new Color(0x6E, 0x6E, 0x6E),
				_ => new Color(0x6E, 0x6E, 0x6E)
			};
	}

	public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
	{
		throw new NotSupportedException();
	}
}
