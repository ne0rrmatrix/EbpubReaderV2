namespace DisplayBook.Viewer.Models;

/// <summary>
/// Stable reader setting identifiers and defaults shared by DisplayBook reader hosts.
/// Values are intentionally CSS-friendly strings so the model can be passed safely to the HTML viewer.
/// </summary>
public sealed record EpubReaderSettings
{
    /// <summary>
    /// Shared sentinel meaning "leave the publication's own styling in place for this
    /// setting." Used as the default for every setting that would otherwise override
    /// author CSS, so a book only picks up reader overrides once the user explicitly
    /// changes a setting away from this value.
    /// </summary>
    public const string Original = "original";

    public const string ThemePaper = "paper";
    public const string ThemeSepia = "sepia";
    public const string ThemeNight = "night";

    public const string FontFamilySerif = "serif";
    public const string FontFamilySans = "sans";
    public const string FontFamilyHumanist = "humanist";
    public const string FontFamilyMonospace = "monospace";
    public const string FontSizeDefault = "100%";

    public const string PaginationPaged = "paged";
    public const string PaginationScroll = "scroll";

    public const string ColumnSingle = "single";
    public const string ColumnTwo = "two";
    public const string ColumnCountOne = "1";
    public const string ColumnCountTwo = "2";
    public const string LineLengthFull = "100%";

    /// <summary>Leaves the publication's own text-align in place.</summary>
    public const string TextAlignAuto = "auto";
    public const string TextAlignLeft = "left";
    public const string TextAlignJustify = "justify";

    public const string HyphenationAuto = "auto";
    public const string HyphenationNone = "none";
    public const string WordSpacingDefault = "0";
    public const string LetterSpacingDefault = "normal";

    public const string ImageTreatmentNormal = "normal";
    public const string ImageTreatmentDim = "dim";
    public const string ImageTreatmentInvert = "invert";
    public const string ImageTreatmentDimAndInvert = "dim-invert";

    public string FontFamily { get; init; } = Original;
    public string FontSize { get; init; } = FontSizeDefault;
    public string LineHeight { get; init; } = Original;
    public string Theme { get; init; } = Original;
    public string PaginationMode { get; init; } = PaginationPaged;
    public string ColumnMode { get; init; } = ColumnSingle;
    public string ColumnCount { get; init; } = ColumnCountOne;
    public string LineLength { get; init; } = LineLengthFull;
    public string TextAlignment { get; init; } = TextAlignAuto;
    public string Hyphenation { get; init; } = Original;
    public string ParagraphSpacing { get; init; } = Original;
    public string ParagraphIndent { get; init; } = Original;
    public string WordSpacing { get; init; } = WordSpacingDefault;
    public string LetterSpacing { get; init; } = LetterSpacingDefault;
    public string FontWeight { get; init; } = Original;
    public string ImageTreatment { get; init; } = ImageTreatmentNormal;

    public static EpubReaderSettings Defaults => new();
}
