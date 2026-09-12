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
		return appShell is null ? new Window(new AppShell()) : new Window(appShell);
	}
}