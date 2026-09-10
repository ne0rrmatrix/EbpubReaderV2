using CommunityToolkit.Mvvm.ComponentModel;

namespace DisplayBook.App.Models.BookMetadata;

/// <summary>
/// One row in the Book Details "Update metadata" diff panel: a single field the
/// source API proposed a different value for, with the checkbox state that decides
/// whether <c>ApplyMetadataCommand</c> writes it.
/// </summary>
public sealed partial class MetadataFieldChange : ObservableObject
{
    private bool _isSelected;

    public required string Field { get; init; }

    public required string Label { get; init; }

    public string? OldValueDisplay { get; init; }

    public required string NewValueDisplay { get; init; }

    public bool IsCover { get; init; }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }
}
