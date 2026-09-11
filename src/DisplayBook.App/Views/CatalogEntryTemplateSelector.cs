using DisplayBook.App.ViewModels;

namespace DisplayBook.App.Views;

/// <summary>
/// Picks the book-cover card for acquisition entries and the compact folder card for
/// navigation entries, so navigation feeds (e.g. "By Newest", "By Title") don't render as
/// oversized, mostly-empty cover boxes.
/// </summary>
public sealed class CatalogEntryTemplateSelector : DataTemplateSelector
{
	public DataTemplate? BookTemplate { get; set; }

	public DataTemplate? NavigationTemplate { get; set; }

	protected override DataTemplate? OnSelectTemplate(object item, BindableObject container)
	{
		return item is CatalogEntryModel { IsBook: true } ? BookTemplate : NavigationTemplate;
	}
}
