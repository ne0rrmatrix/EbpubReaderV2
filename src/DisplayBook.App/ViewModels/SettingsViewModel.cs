using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DisplayBook.App.Interfaces;
using DisplayBook.App.Picker;
using Microsoft.Extensions.Logging;

namespace DisplayBook.App.ViewModels;

/// <summary>
/// Drives the Settings page: sign in/up/out (with a TOTP second-factor step when the
/// account has one enrolled) plus enrolling/removing TOTP for the signed-in account.
/// </summary>
public sealed partial class SettingsViewModel(
	IFirebaseAuthService authService,
	INavigationService navigation,
	ILogger<SettingsViewModel> logger) : ObservableObject
{
	MfaChallenge? pendingMfaChallenge;
	string? totpSessionInfo;
	string? totpEnrollmentId;

	[RelayCommand]
	Task GoBackAsync() => navigation.GoBackAsync();

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(ShowSignInForm))]
	[NotifyPropertyChangedFor(nameof(ShowVerifyEmailPrompt))]
	[NotifyPropertyChangedFor(nameof(ShowStartTotpEnrollment))]
	[NotifyPropertyChangedFor(nameof(ShowTotpEnrolledStatus))]
	public partial bool IsSignedIn { get; set; }

	[ObservableProperty]
	public partial string? CurrentEmail { get; set; }

	[ObservableProperty]
	[NotifyCanExecuteChangedFor(nameof(SignInCommand))]
	[NotifyCanExecuteChangedFor(nameof(SignUpCommand))]
	public partial string Email { get; set; } = string.Empty;

	[ObservableProperty]
	[NotifyCanExecuteChangedFor(nameof(SignInCommand))]
	[NotifyCanExecuteChangedFor(nameof(SignUpCommand))]
	public partial string Password { get; set; } = string.Empty;

	[ObservableProperty]
	public partial string? StatusMessage { get; set; }

	[ObservableProperty]
	[NotifyCanExecuteChangedFor(nameof(SignInCommand))]
	[NotifyCanExecuteChangedFor(nameof(SignUpCommand))]
	[NotifyCanExecuteChangedFor(nameof(SubmitMfaCodeCommand))]
	[NotifyCanExecuteChangedFor(nameof(ConfirmTotpEnrollmentCommand))]
	public partial bool IsBusy { get; set; }

	// --- Sign-in second-factor step ---

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(ShowSignInForm))]
	public partial bool IsAwaitingMfaCode { get; set; }

	[ObservableProperty]
	[NotifyCanExecuteChangedFor(nameof(SubmitMfaCodeCommand))]
	public partial string MfaCode { get; set; } = string.Empty;

	/// <summary>The plain email/password form: hidden once signed in or mid-MFA-challenge.</summary>
	public bool ShowSignInForm => !IsSignedIn && !IsAwaitingMfaCode;

	// --- Signed-in account security state ---

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(ShowVerifyEmailPrompt))]
	[NotifyPropertyChangedFor(nameof(ShowStartTotpEnrollment))]
	[NotifyPropertyChangedFor(nameof(ShowTotpEnrolledStatus))]
	public partial bool IsEmailVerified { get; set; }

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(ShowStartTotpEnrollment))]
	[NotifyPropertyChangedFor(nameof(ShowTotpEnrolledStatus))]
	public partial bool IsTotpEnrolled { get; set; }

	// --- TOTP enrollment step ---

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(ShowStartTotpEnrollment))]
	[NotifyPropertyChangedFor(nameof(ShowTotpEnrolledStatus))]
	public partial bool IsEnrollingTotp { get; set; }

	[ObservableProperty]
	public partial string TotpSecret { get; set; } = string.Empty;

	[ObservableProperty]
	[NotifyCanExecuteChangedFor(nameof(ConfirmTotpEnrollmentCommand))]
	public partial string TotpCode { get; set; } = string.Empty;

	/// <summary>Signed in, but the server won't allow TOTP enrollment until the email is verified.</summary>
	public bool ShowVerifyEmailPrompt => IsSignedIn && !IsEmailVerified;

	/// <summary>Signed in, verified, no TOTP factor yet, and not already mid-enrollment.</summary>
	public bool ShowStartTotpEnrollment => IsSignedIn && IsEmailVerified && !IsTotpEnrolled && !IsEnrollingTotp;

	/// <summary>Signed in with a TOTP factor already enrolled (and not currently re-enrolling).</summary>
	public bool ShowTotpEnrolledStatus => IsSignedIn && IsEmailVerified && IsTotpEnrolled && !IsEnrollingTotp;

	public async Task OnPageAppearingAsync()
	{
		RefreshAuthState();
		if (IsSignedIn)
		{
			await RefreshSecurityStateAsync();
		}
	}

	void RefreshAuthState()
	{
		IsSignedIn = authService.IsSignedIn;
		CurrentEmail = authService.CurrentEmail;
	}

	[RelayCommand(CanExecute = nameof(CanSubmit))]
	async Task SignInAsync()
	{
		IsBusy = true;
		StatusMessage = null;
		try
		{
			FirebaseAuthResult result = await authService.SignInAsync(Email.Trim(), Password);
			if (result.MfaChallenge is { } challenge)
			{
				pendingMfaChallenge = challenge;
				IsAwaitingMfaCode = true;
				MfaCode = string.Empty;
				return;
			}

			await HandleAuthResult(result);
		}
		catch (Exception exception)
		{
			logger.LogError(exception, "Sign-in failed unexpectedly.");
			StatusMessage = $"Sign-in failed: {exception.Message}";
		}
		finally
		{
			IsBusy = false;
		}
	}

	[RelayCommand(CanExecute = nameof(CanSubmit))]
	async Task SignUpAsync()
	{
		IsBusy = true;
		StatusMessage = null;
		try
		{
			FirebaseAuthResult result = await authService.SignUpAsync(Email.Trim(), Password);
			await HandleAuthResult(result);
		}
		catch (Exception exception)
		{
			logger.LogError(exception, "Sign-up failed unexpectedly.");
			StatusMessage = $"Sign-up failed: {exception.Message}";
		}
		finally
		{
			IsBusy = false;
		}
	}

	[RelayCommand(CanExecute = nameof(CanSubmitMfaCode))]
	async Task SubmitMfaCodeAsync()
	{
		if (pendingMfaChallenge is not { } challenge)
		{
			return;
		}

		IsBusy = true;
		StatusMessage = null;
		try
		{
			FirebaseAuthResult result = await authService.CompleteMfaSignInAsync(challenge, MfaCode.Trim());
			if (result.Succeeded)
			{
				pendingMfaChallenge = null;
				IsAwaitingMfaCode = false;
				MfaCode = string.Empty;
			}

			await HandleAuthResult(result);
		}
		catch (Exception exception)
		{
			logger.LogError(exception, "MFA sign-in failed unexpectedly.");
			StatusMessage = $"Sign-in failed: {exception.Message}";
		}
		finally
		{
			IsBusy = false;
		}
	}

	[RelayCommand]
	void CancelMfa()
	{
		pendingMfaChallenge = null;
		IsAwaitingMfaCode = false;
		MfaCode = string.Empty;
		StatusMessage = null;
	}

	async Task HandleAuthResult(FirebaseAuthResult result)
	{
		if (result.Succeeded)
		{
			Password = string.Empty;
			RefreshAuthState();
			await RefreshSecurityStateAsync();
		}
		else
		{
			StatusMessage = result.ErrorMessage;
		}
	}

	bool CanSubmit() => !IsBusy && Email.Trim().Length > 0 && Password.Length > 0;

	bool CanSubmitMfaCode() => !IsBusy && MfaCode.Trim().Length > 0;

	[RelayCommand]
	async Task SignOutAsync()
	{
		await authService.SignOutAsync();
		RefreshAuthState();
		IsEmailVerified = false;
		IsTotpEnrolled = false;
		IsEnrollingTotp = false;
		totpEnrollmentId = null;
		StatusMessage = "Signed out.";
	}

	// --- Security state (email verification + TOTP) ---

	[RelayCommand]
	async Task RefreshSecurityStateAsync()
	{
		AccountSecurityState? state = await authService.RefreshSecurityStateAsync();
		if (state is null)
		{
			return;
		}

		IsEmailVerified = state.EmailVerified;
		IsTotpEnrolled = state.TotpEnrollmentId is not null;
		totpEnrollmentId = state.TotpEnrollmentId;
	}

	[RelayCommand]
	async Task SendVerificationEmailAsync()
	{
		IsBusy = true;
		StatusMessage = null;
		try
		{
			FirebaseAuthResult result = await authService.SendEmailVerificationAsync();
			StatusMessage = result.Succeeded
				? "Verification email sent. Open the link, then tap refresh."
				: result.ErrorMessage;
		}
		catch (Exception exception)
		{
			logger.LogError(exception, "Could not send the verification email.");
			StatusMessage = $"Could not send the verification email: {exception.Message}";
		}
		finally
		{
			IsBusy = false;
		}
	}

	[RelayCommand]
	async Task StartTotpEnrollmentAsync()
	{
		IsBusy = true;
		StatusMessage = null;
		try
		{
			TotpEnrollmentStart? start = await authService.StartTotpEnrollmentAsync();
			if (start is null)
			{
				StatusMessage = "Could not start two-factor setup. Try again.";
				return;
			}

			totpSessionInfo = start.SessionInfo;
			TotpSecret = start.SharedSecretKey;
			TotpCode = string.Empty;
			IsEnrollingTotp = true;
		}
		catch (Exception exception)
		{
			logger.LogError(exception, "Could not start TOTP enrollment.");
			StatusMessage = $"Could not start two-factor setup: {exception.Message}";
		}
		finally
		{
			IsBusy = false;
		}
	}

	[RelayCommand(CanExecute = nameof(CanConfirmTotpEnrollment))]
	async Task ConfirmTotpEnrollmentAsync()
	{
		if (totpSessionInfo is not { } sessionInfo)
		{
			return;
		}

		IsBusy = true;
		StatusMessage = null;
		try
		{
			FirebaseAuthResult result = await authService.FinalizeTotpEnrollmentAsync(sessionInfo, TotpCode.Trim());
			if (result.Succeeded)
			{
				IsEnrollingTotp = false;
				TotpSecret = string.Empty;
				TotpCode = string.Empty;
				totpSessionInfo = null;
				StatusMessage = "Two-factor authentication is on.";
				await RefreshSecurityStateAsync();
			}
			else
			{
				StatusMessage = result.ErrorMessage;
			}
		}
		catch (Exception exception)
		{
			logger.LogError(exception, "Could not finish TOTP enrollment.");
			StatusMessage = $"Could not finish two-factor setup: {exception.Message}";
		}
		finally
		{
			IsBusy = false;
		}
	}

	bool CanConfirmTotpEnrollment() => !IsBusy && TotpCode.Trim().Length > 0;

	[RelayCommand]
	void CancelTotpEnrollment()
	{
		IsEnrollingTotp = false;
		TotpSecret = string.Empty;
		TotpCode = string.Empty;
		totpSessionInfo = null;
	}

	[RelayCommand]
	async Task CopyTotpSecretAsync()
	{
		if (TotpSecret.Length > 0)
		{
			await Clipboard.Default.SetTextAsync(TotpSecret);
		}
	}

	[RelayCommand]
	async Task RemoveTotpEnrollmentAsync()
	{
		if (totpEnrollmentId is not { } enrollmentId)
		{
			return;
		}

		IsBusy = true;
		StatusMessage = null;
		try
		{
			FirebaseAuthResult result = await authService.RemoveTotpEnrollmentAsync(enrollmentId);
			if (result.Succeeded)
			{
				StatusMessage = "Two-factor authentication removed.";
				await RefreshSecurityStateAsync();
			}
			else
			{
				StatusMessage = result.ErrorMessage;
			}
		}
		catch (Exception exception)
		{
			logger.LogError(exception, "Could not remove TOTP enrollment.");
			StatusMessage = $"Could not remove two-factor authentication: {exception.Message}";
		}
		finally
		{
			IsBusy = false;
		}
	}
}