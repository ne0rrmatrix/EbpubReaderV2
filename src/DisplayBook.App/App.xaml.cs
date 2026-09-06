namespace DisplayBook.App;

public partial class App : Application
{
	readonly AppShell appShell;

	public App(AppShell appShell)
	{
		this.appShell = appShell;
		InitializeComponent();
	}

	protected override Window CreateWindow(IActivationState? activationState)
	{
		if (appShell is null)
		{
			return new Window(new AppShell());
		}

		return new Window(appShell);
	}
}