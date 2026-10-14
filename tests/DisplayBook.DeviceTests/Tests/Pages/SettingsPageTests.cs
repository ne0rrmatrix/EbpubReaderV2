using DisplayBook.App.Interfaces;
using DisplayBook.App.ViewModels;
using DisplayBook.App.Views;
using DisplayBook.DeviceTests.Support;

namespace DisplayBook.DeviceTests.Tests.Pages;

/// <summary>
/// The settings page with the real FirebaseAuthService against <see cref="FakeFirebase"/>:
/// sign-in/up/out, the two-factor sign-in step, email verification and authenticator enrollment.
/// </summary>
public class SettingsPageTests : AppDataTest
{
	static async Task<SettingsViewModel> OpenSettingsAsync()
	{
		await TestApp.GetShellAsync();
		await TestApp.ResetNavigationAsync();
		SettingsPage page = await TestApp.GoToAsync<SettingsPage>("settings");
		SettingsViewModel settings = (SettingsViewModel)page.BindingContext;
		await settings.OnPageAppearingAsync();
		return settings;
	}

	static async Task SignInAsync(SettingsViewModel settings)
	{
		settings.Email = $" {FakeFirebase.Email} ";
		settings.Password = "secret";
		await settings.SignInCommand.ExecuteAsync(null);
	}

	[UIFact]
	public async Task SignedOut_ShowsOnlyTheSignInForm()
	{
		FakeFirebase.Serve();
		SettingsViewModel settings = await OpenSettingsAsync();

		Assert.False(settings.IsSignedIn);
		Assert.True(settings.ShowSignInForm);
		Assert.False(settings.ShowVerifyEmailPrompt || settings.ShowStartTotpEnrollment || settings.ShowTotpEnrolledStatus);
		Assert.False(settings.SignInCommand.CanExecute(null));

		settings.Email = FakeFirebase.Email;
		settings.Password = "secret";
		Assert.True(settings.SignInCommand.CanExecute(null));
		Assert.True(settings.SignUpCommand.CanExecute(null));
	}

	[UIFact]
	public async Task SignIn_Unverified_AsksForEmailVerification()
	{
		FakeFirebase.Serve();
		SettingsViewModel settings = await OpenSettingsAsync();

		await SignInAsync(settings);

		Assert.True(settings.IsSignedIn);
		Assert.Equal(FakeFirebase.Email, settings.CurrentEmail);
		Assert.Equal(string.Empty, settings.Password);
		Assert.True(settings.ShowVerifyEmailPrompt);

		await settings.SendVerificationEmailCommand.ExecuteAsync(null);
		Assert.Equal("Verification email sent. Open the link, then tap refresh.", settings.StatusMessage);

		FakeFirebase.Failures["accounts:sendOobCode"] = "TOO_MANY_ATTEMPTS_TRY_LATER";
		await settings.SendVerificationEmailCommand.ExecuteAsync(null);
		Assert.Equal("Too many attempts. Try again later.", settings.StatusMessage);

		FakeFirebase.EmailVerified = true;
		await settings.RefreshSecurityStateCommand.ExecuteAsync(null);
		Assert.True(settings.ShowStartTotpEnrollment);
	}

	[UIFact]
	public async Task SignIn_WrongPassword_ShowsTheError()
	{
		FakeFirebase.Serve();
		FakeFirebase.Failures["accounts:signInWithPassword"] = "INVALID_LOGIN_CREDENTIALS";
		SettingsViewModel settings = await OpenSettingsAsync();

		await SignInAsync(settings);

		Assert.False(settings.IsSignedIn);
		Assert.Equal("Incorrect email or password.", settings.StatusMessage);
	}

	[UIFact]
	public async Task SignUp_CreatesTheAccountOrExplainsWhyNot()
	{
		FakeFirebase.Serve();
		SettingsViewModel settings = await OpenSettingsAsync();
		settings.Email = FakeFirebase.Email;
		settings.Password = "abc";
		FakeFirebase.Failures["accounts:signUp"] = "WEAK_PASSWORD : Password should be at least 6 characters";

		await settings.SignUpCommand.ExecuteAsync(null);
		Assert.Equal("Password should be at least 6 characters.", settings.StatusMessage);

		FakeFirebase.Failures["accounts:signUp"] = "EMAIL_EXISTS";
		await settings.SignUpCommand.ExecuteAsync(null);
		Assert.Equal("An account with that email already exists.", settings.StatusMessage);

		await settings.SignUpCommand.ExecuteAsync(null);
		Assert.True(settings.IsSignedIn);
	}

	[UIFact]
	public async Task SignIn_WithAuthenticator_AsksForTheCode()
	{
		FakeFirebase.Serve(emailVerified: true, totpEnrolled: true);
		SettingsViewModel settings = await OpenSettingsAsync();

		await SignInAsync(settings);
		Assert.True(settings.IsAwaitingMfaCode);
		Assert.False(settings.ShowSignInForm);
		Assert.False(settings.SubmitMfaCodeCommand.CanExecute(null));

		settings.MfaCode = "000000";
		await settings.SubmitMfaCodeCommand.ExecuteAsync(null);
		Assert.Equal("That code didn't match. Try again.", settings.StatusMessage);
		Assert.True(settings.IsAwaitingMfaCode);

		settings.MfaCode = FakeFirebase.TotpCode;
		await settings.SubmitMfaCodeCommand.ExecuteAsync(null);
		Assert.True(settings.IsSignedIn);
		Assert.False(settings.IsAwaitingMfaCode);
		Assert.True(settings.ShowTotpEnrolledStatus);
	}

	[UIFact]
	public async Task CancelMfa_ReturnsToTheSignInForm()
	{
		FakeFirebase.Serve(totpEnrolled: true);
		SettingsViewModel settings = await OpenSettingsAsync();
		await SignInAsync(settings);

		settings.CancelMfaCommand.Execute(null);

		Assert.False(settings.IsAwaitingMfaCode);
		Assert.True(settings.ShowSignInForm);
		await settings.SubmitMfaCodeCommand.ExecuteAsync(null);
		Assert.False(settings.IsSignedIn);
	}

	[UIFact]
	public async Task TotpEnrollment_ConfirmsTheCodeAndCanBeRemoved()
	{
		FakeFirebase.Serve(emailVerified: true);
		SettingsViewModel settings = await OpenSettingsAsync();
		await SignInAsync(settings);

		await settings.StartTotpEnrollmentCommand.ExecuteAsync(null);
		Assert.True(settings.IsEnrollingTotp);
		Assert.Equal("SECRETKEY234", settings.TotpSecret);
		await settings.CopyTotpSecretCommand.ExecuteAsync(null);
		Assert.Equal("SECRETKEY234", await Clipboard.Default.GetTextAsync());

		settings.TotpCode = "999999";
		await settings.ConfirmTotpEnrollmentCommand.ExecuteAsync(null);
		Assert.Equal("That code didn't match. Try again.", settings.StatusMessage);

		settings.TotpCode = FakeFirebase.TotpCode;
		await settings.ConfirmTotpEnrollmentCommand.ExecuteAsync(null);
		Assert.Equal("Two-factor authentication is on.", settings.StatusMessage);
		Assert.True(settings.ShowTotpEnrolledStatus);

		await settings.RemoveTotpEnrollmentCommand.ExecuteAsync(null);
		Assert.Equal("Two-factor authentication removed.", settings.StatusMessage);
		Assert.True(settings.ShowStartTotpEnrollment);
	}

	[UIFact]
	public async Task TotpEnrollment_CanBeCancelledAndReportsFailures()
	{
		FakeFirebase.Serve(emailVerified: true);
		SettingsViewModel settings = await OpenSettingsAsync();
		await SignInAsync(settings);

		await settings.StartTotpEnrollmentCommand.ExecuteAsync(null);
		settings.CancelTotpEnrollmentCommand.Execute(null);
		Assert.False(settings.IsEnrollingTotp);
		Assert.Equal(string.Empty, settings.TotpSecret);
		await settings.ConfirmTotpEnrollmentCommand.ExecuteAsync(null);

		FakeFirebase.Failures["mfaEnrollment:start"] = "OPERATION_NOT_ALLOWED : TOTP based MFA not enabled.";
		await settings.StartTotpEnrollmentCommand.ExecuteAsync(null);
		Assert.Equal("Could not start two-factor setup. Try again.", settings.StatusMessage);

		// Nothing enrolled, so there is nothing to remove.
		await settings.RemoveTotpEnrollmentCommand.ExecuteAsync(null);
		Assert.Equal("Could not start two-factor setup. Try again.", settings.StatusMessage);
	}

	[UIFact]
	public async Task TotpRemoval_Failure_IsReported()
	{
		FakeFirebase.Serve(emailVerified: true, totpEnrolled: true);
		SettingsViewModel settings = await OpenSettingsAsync();
		settings.Email = FakeFirebase.Email;
		settings.Password = "secret";
		await settings.SignInCommand.ExecuteAsync(null);
		settings.MfaCode = FakeFirebase.TotpCode;
		await settings.SubmitMfaCodeCommand.ExecuteAsync(null);
		Assert.True(settings.ShowTotpEnrolledStatus);

		FakeFirebase.Failures["mfaEnrollment:withdraw"] = "TOKEN_EXPIRED";
		await settings.RemoveTotpEnrollmentCommand.ExecuteAsync(null);

		Assert.Equal("Sign-in failed: TOKEN_EXPIRED", settings.StatusMessage);
	}

	[UIFact]
	public async Task SignOut_ClearsTheAccountState()
	{
		FakeFirebase.Serve(emailVerified: true, totpEnrolled: false);
		SettingsViewModel settings = await OpenSettingsAsync();
		await SignInAsync(settings);

		await settings.SignOutCommand.ExecuteAsync(null);

		Assert.False(settings.IsSignedIn);
		Assert.False(settings.IsEmailVerified);
		Assert.Equal("Signed out.", settings.StatusMessage);
		Assert.True(settings.ShowSignInForm);
	}

	[UIFact]
	public async Task ReturningSignedIn_RefreshesTheAccountState()
	{
		FakeFirebase.Serve(emailVerified: true);
		await TestApp.GetService<IFirebaseAuthService>().SignInAsync(FakeFirebase.Email, "secret");

		SettingsViewModel settings = await OpenSettingsAsync();

		Assert.True(settings.IsSignedIn);
		Assert.True(settings.IsEmailVerified);
		Assert.True(settings.ShowStartTotpEnrollment);

		await settings.GoBackCommand.ExecuteAsync(null);
		await TestApp.WaitForPageAsync<LibraryPage>();
	}
}
