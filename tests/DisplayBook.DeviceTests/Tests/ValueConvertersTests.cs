using System.Globalization;
using DisplayBook.App.Models;
using DisplayBook.App.Views;

namespace DisplayBook.DeviceTests.Tests;

public class ValueConvertersTests
{
	static readonly CultureInfo invariant = CultureInfo.InvariantCulture;

	// The size and percentage strings are interpolated, which formats with the current culture.
	static T WithInvariantCulture<T>(Func<T> action)
	{
		CultureInfo original = CultureInfo.CurrentCulture;
		CultureInfo.CurrentCulture = invariant;
		try
		{
			return action();
		}
		finally
		{
			CultureInfo.CurrentCulture = original;
		}
	}

	[Theory]
	[InlineData(0L, "0 B")]
	[InlineData(1_023L, "1023 B")]
	[InlineData(1_024L, "1 KB")]
	[InlineData(1_536L, "1.5 KB")]
	[InlineData(5L * 1_024 * 1_024, "5 MB")]
	[InlineData(3L * 1_024 * 1_024 * 1_024 / 2, "1.5 GB")]
	[InlineData(-1L, "")]
	public void ByteSize_FormatsWithTheLargestWholeUnit(long bytes, string expected)
	{
		object result = WithInvariantCulture(() => new ByteSizeConverter().Convert(bytes, typeof(string), null, invariant));

		Assert.Equal(expected, result);
	}

	[Theory]
	[InlineData(2_048, "2 KB")]
	[InlineData(null, "0 B")]
	[InlineData("2048", "0 B")]
	public void ByteSize_AcceptsIntAndTreatsOtherValuesAsZero(object? value, string expected)
	{
		object result = WithInvariantCulture(() => new ByteSizeConverter().Convert(value, typeof(string), null, invariant));

		Assert.Equal(expected, result);
	}

	[Theory]
	[InlineData(true, false)]
	[InlineData(false, true)]
	[InlineData(null, true)]
	[InlineData("text", false)]
	public void InverseBool_NegatesAndTreatsNullAsFalse(object? value, bool expected)
	{
		Assert.Equal(expected, new InverseBoolConverter().Convert(value, typeof(bool), null, invariant));
	}

	[Theory]
	[InlineData(0.0, "0%")]
	[InlineData(0.456, "46%")]
	[InlineData(1.0, "100%")]
	[InlineData(1, "100%")]
	[InlineData(null, "0%")]
	public void Percentage_FormatsAFraction(object? value, string expected)
	{
		object result = WithInvariantCulture(() => new PercentageConverter().Convert(value, typeof(string), null, invariant));

		Assert.Equal(expected, result);
	}

	[Theory]
	[InlineData(0, true)]
	[InlineData(3, false)]
	[InlineData(null, true)]
	[InlineData("0", true)]
	[InlineData("7", false)]
	[InlineData("not a number", true)]
	public void ZeroCount_IsTrueOnlyForZero(object? value, bool expected)
	{
		Assert.Equal(expected, new ZeroCountConverter().Convert(value, typeof(bool), null, invariant));
	}

	[Theory]
	[InlineData("Error", true)]
	[InlineData("", false)]
	[InlineData("   ", false)]
	[InlineData(null, false)]
	[InlineData(5, false)]
	public void StringNotNull_IsTrueForVisibleText(object? value, bool expected)
	{
		Assert.Equal(expected, new StringNotNullConverter().Convert(value, typeof(bool), null, invariant));
	}

	[Theory]
	[InlineData(DownloadStatus.Paused, "Paused", true)]
	[InlineData(DownloadStatus.Paused, "Completed", false)]
	[InlineData(DownloadStatus.Paused, "paused", false)]
	[InlineData(null, "Paused", false)]
	[InlineData(DownloadStatus.Paused, null, false)]
	public void EnumEquals_AndNotEquals_AreOpposites(object? value, object? parameter, bool expectedEquals)
	{
		Assert.Equal(expectedEquals, new EnumEqualsConverter().Convert(value, typeof(bool), parameter, invariant));
		Assert.Equal(!expectedEquals, new EnumNotEqualsConverter().Convert(value, typeof(bool), parameter, invariant));
	}

	[Theory]
	[InlineData(DownloadStatus.Queued, 0x6E, 0x6E, 0x6E)]
	[InlineData(DownloadStatus.Downloading, 0x23, 0x6B, 0x5A)]
	[InlineData(DownloadStatus.Paused, 0xB4, 0x53, 0x09)]
	[InlineData(DownloadStatus.Completed, 0x15, 0x80, 0x3D)]
	[InlineData(DownloadStatus.Failed, 0xB4, 0x23, 0x18)]
	[InlineData(DownloadStatus.Canceled, 0x6E, 0x6E, 0x6E)]
	[InlineData(null, 0x6E, 0x6E, 0x6E)]
	public void DownloadStatusColor_MapsEachStatus(object? status, int red, int green, int blue)
	{
		Color color = Assert.IsType<Color>(new DownloadStatusColorConverter().Convert(status, typeof(Color), null, invariant));

		Assert.Equal(new Color((byte)red, (byte)green, (byte)blue), color);
	}

	public static TheoryData<IValueConverter> Converters =>
	[
		new ByteSizeConverter(),
		new InverseBoolConverter(),
		new PercentageConverter(),
		new ZeroCountConverter(),
		new StringNotNullConverter(),
		new EnumEqualsConverter(),
		new EnumNotEqualsConverter(),
		new DownloadStatusColorConverter(),
	];

	[Theory]
	[MemberData(nameof(Converters), DisableDiscoveryEnumeration = true)]
	public void ConvertBack_IsNotSupported(IValueConverter converter)
	{
		Assert.Throws<NotSupportedException>(() => converter.ConvertBack(null, typeof(object), null, invariant));
	}
}
