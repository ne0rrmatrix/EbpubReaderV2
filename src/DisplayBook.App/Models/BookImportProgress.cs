namespace DisplayBook.App.Models;

public sealed record BookImportProgress(
	string Stage,
	string CurrentItem,
	int Completed,
	int Total)
{
	public double Fraction => Total > 0
		? Math.Clamp((double)Completed / Total, 0, 1)
		: 0;
}
