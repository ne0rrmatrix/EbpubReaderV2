using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DisplayBook.App.Models;
using DisplayBook.App.Services;
using Microsoft.Extensions.Logging;

namespace DisplayBook.App.ViewModels;

/// <summary>
/// Drives the OPDS servers page: the saved-servers list, the live-discovery
/// section, and manual URL entry.
/// </summary>
public sealed partial class OpdsServersViewModel : ObservableObject, IDisposable
{
	readonly IOpdsServerRepository servers;
	readonly IBonjourDiscoveryService discovery;
	readonly INavigationService navigation;
	readonly ILogger<OpdsServersViewModel> logger;
	readonly ObservableCollection<OpdsServerDisplayModel> saved = [];
	readonly ObservableCollection<OpdsServerDisplayModel> discovered = [];
	bool disposed;
	Task? pendingStop;

	public OpdsServersViewModel(
		IOpdsServerRepository servers,
		IBonjourDiscoveryService discovery,
		INavigationService navigation,
		ILogger<OpdsServersViewModel> logger)
	{
		this.servers = servers;
		this.discovery = discovery;
		this.navigation = navigation;
		this.logger = logger;

		this.discovery.ServerDiscovered += OnServerDiscovered;
		this.discovery.ServerRemoved += OnServerRemoved;
		this.discovery.DiscoveryError += OnDiscoveryError;
	}

	public ObservableCollection<OpdsServerDisplayModel> Saved => saved;

	public ObservableCollection<OpdsServerDisplayModel> Discovered => discovered;

	[ObservableProperty]
	[NotifyPropertyChangedFor(nameof(ToggleButtonText))]
	public partial bool IsDiscovering { get; set; }

	public string ToggleButtonText => IsDiscovering ? "Stop discovery" : "Start discovery";

	[RelayCommand]
	Task GoBackAsync() => navigation.GoBackAsync();

	[ObservableProperty]
	public partial string ManualUrl { get; set; } = string.Empty;

	[ObservableProperty]
	public partial string ManualName { get; set; } = string.Empty;

	[ObservableProperty]
	public partial string? StatusMessage { get; set; }

	[ObservableProperty]
	[NotifyCanExecuteChangedFor(nameof(AddManualCommand))]
	public partial bool IsBusy { get; set; }

	public async Task OnPageAppearingAsync()
	{
		disposed = false;
		if (pendingStop is not null)
		{
			await pendingStop;
			pendingStop = null;
		}

		await LoadSavedAsync();
	}

	public void OnPageDisappearing()
	{
		disposed = true;
		if (discovery.IsDiscovering)
		{
			pendingStop = StopDiscoveryIfRunningAsync();
		}
	}

	async Task LoadSavedAsync()
	{
		saved.Clear();
		IReadOnlyList<OpdsServer>servers1 = await servers.GetAllAsync();
		foreach (OpdsServer server in servers1)
		{
			saved.Add(new OpdsServerDisplayModel(server, this));
		}
	}

	async void OnServerDiscovered(object? sender, DiscoveredServerEventArgs e)
	{
		if (e.IsNew)
		{
			await PersistDiscoveredServerAsync(e.Server);
		}

		MainThread.BeginInvokeOnMainThread(() =>
		{
			if (disposed)
			{
				return;
			}

			if (discovered.Any(model => model.Server.Id == e.Server.Id))
			{
				return;
			}

			discovered.Add(new OpdsServerDisplayModel(e.Server, this));
		});
	}

	async Task PersistDiscoveredServerAsync(OpdsServer server)
	{
		try
		{
			if (servers.FindByUrl(server.Url) is null)
			{
				await servers.AddAsync(server).ConfigureAwait(false);
			}
		}
		catch (Exception ex)
		{
			logger.LogWarning(ex, "Could not save discovered server {ServerName}", server.Name);
		}
	}

	void OnServerRemoved(object? sender, ServerRemovedEventArgs e)
	{
		MainThread.BeginInvokeOnMainThread(() =>
		{
			if (disposed)
			{
				return;
			}

			OpdsServerDisplayModel? match = discovered.FirstOrDefault(model =>
				model.Url.Contains(e.ServerUrl, StringComparison.OrdinalIgnoreCase));
			if (match is not null)
			{
				discovered.Remove(match);
			}
		});
	}

	void OnDiscoveryError(object? sender, DiscoveryErrorEventArgs e)
	{
		MainThread.BeginInvokeOnMainThread(() =>
		{
			if (disposed)
			{
				return;
			}

			StatusMessage = e.Message;
		});
	}

	[RelayCommand]
	async Task ToggleDiscoveryAsync()
	{
		if (IsDiscovering)
		{
			await StopDiscoveryIfRunningAsync();
			return;
		}

		try
		{
			await discovery.StartDiscoveryAsync();
			IsDiscovering = true;
			StatusMessage = "Scanning the local network for Calibre OPDS servers...";
		}
		catch (Exception ex)
		{
			logger.LogWarning(ex, "Could not start mDNS discovery");
			StatusMessage = "Could not start discovery: " + ex.Message;
		}
	}

	async Task StopDiscoveryIfRunningAsync()
	{
		try
		{
			if (discovery.IsDiscovering)
			{
				await discovery.StopDiscoveryAsync();
			}
		}
		catch (Exception ex)
		{
			logger.LogWarning(ex, "Could not stop mDNS discovery");
		}
		finally
		{
			IsDiscovering = false;
		}
	}

	[RelayCommand(CanExecute = nameof(CanAddManual))]
	async Task AddManualAsync()
	{
		string url = ManualUrl.Trim();
		if (!IsValidHttpUrl(url))
		{
			StatusMessage = "Enter a valid http(s) URL, for example http://192.168.1.10:8080/";
			return;
		}

		IsBusy = true;
		try
		{
			string name = ManualName.Trim();
			OpdsServer server = new()
			{
				Name = string.IsNullOrWhiteSpace(name) ? new Uri(url).Host : name,
				Url = url,
				Type = ServerType.Manual
			};

			if (servers.FindByUrl(url) is null)
			{
				await servers.AddAsync(server);
			}

			StatusMessage = null;
			ManualUrl = string.Empty;
			ManualName = string.Empty;
			await navigation.ShowOpdsCatalogAsync(url, server.Name);
		}
		finally
		{
			IsBusy = false;
		}
	}

	bool CanAddManual() => !IsBusy;

	internal Task OpenServerAsync(OpdsServerDisplayModel model)
	{
		return navigation.ShowOpdsCatalogAsync(model.Url, model.Name);
	}

	internal async Task SetEnabledAsync(OpdsServerDisplayModel model, bool enabled)
	{
		try
		{
			model.Server.IsEnabled = enabled;
			await servers.UpdateAsync(model.Server.Id, model.Server);
		}
		catch (Exception ex)
		{
			logger.LogWarning(ex, "Could not save enabled state for server {Server}", model.Name);
		}
	}

	[RelayCommand]
	async Task RemoveServerAsync(OpdsServerDisplayModel? model)
	{
		if (model is null)
		{
			return;
		}

		await servers.RemoveAsync(model.Server.Id);
		saved.Remove(model);
		discovered.Remove(model);
		StatusMessage = $"Removed {model.Name}.";
	}

	[RelayCommand]
	Task GoToDownloadsAsync() => navigation.ShowDownloadsAsync();

	static bool IsValidHttpUrl(string url)
	{
		return Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) &&
			(uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
	}

	public void Dispose()
	{
		if (disposed)
		{
			return;
		}

		disposed = true;
		discovery.ServerDiscovered -= OnServerDiscovered;
		discovery.ServerRemoved -= OnServerRemoved;
		discovery.DiscoveryError -= OnDiscoveryError;

		// Dispose is synchronous; track the stop so nothing is silently dropped.
		pendingStop ??= StopDiscoveryIfRunningAsync();
	}
}
