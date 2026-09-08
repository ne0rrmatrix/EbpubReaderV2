using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DisplayBook.App.Models;
using DisplayBook.App.Services;
using DisplayBook.App.Services.Opds;
using Microsoft.Extensions.Logging;

namespace DisplayBook.App.ViewModels;

/// <summary>
/// Drives the OPDS servers page: the saved-servers list, the live-discovery
/// section, and manual URL entry.
/// </summary>
public sealed partial class OpdsServersViewModel : ObservableObject, IDisposable
{
    private readonly IOpdsServerRepository _servers;
    private readonly IBonjourDiscoveryService _discovery;
    private readonly INavigationService _navigation;
    private readonly ILogger<OpdsServersViewModel> _logger;
    private readonly ObservableCollection<OpdsServerDisplayModel> _saved = [];
    private readonly ObservableCollection<OpdsServerDisplayModel> _discovered = [];
    private bool _disposed;
    private Task? _pendingStop;

    public OpdsServersViewModel(
        IOpdsServerRepository servers,
        IBonjourDiscoveryService discovery,
        INavigationService navigation,
        ILogger<OpdsServersViewModel> logger)
    {
        _servers = servers;
        _discovery = discovery;
        _navigation = navigation;
        _logger = logger;

        _discovery.ServerDiscovered += OnServerDiscovered;
        _discovery.ServerRemoved += OnServerRemoved;
        _discovery.DiscoveryError += OnDiscoveryError;
    }

    public ObservableCollection<OpdsServerDisplayModel> Saved => _saved;

    public ObservableCollection<OpdsServerDisplayModel> Discovered => _discovered;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ToggleButtonText))]
    public partial bool IsDiscovering { get; set; }

    public string ToggleButtonText => IsDiscovering ? "Stop discovery" : "Start discovery";

    [RelayCommand]
    private Task GoBackAsync() => _navigation.GoBackAsync();

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
        _disposed = false;
        if (_pendingStop is not null)
        {
            await _pendingStop;
            _pendingStop = null;
        }

        await LoadSavedAsync();
    }

    public void OnPageDisappearing()
    {
        _disposed = true;
        if (_discovery.IsDiscovering)
        {
            _pendingStop = StopDiscoveryIfRunningAsync();
        }
    }

    private async Task LoadSavedAsync()
    {
        _saved.Clear();
        var servers = await _servers.GetAllAsync();
        foreach (var server in servers)
        {
            _saved.Add(new OpdsServerDisplayModel(server, this));
        }
    }

    private async void OnServerDiscovered(object? sender, DiscoveredServerEventArgs e)
    {
        if (e.IsNew)
        {
            await PersistDiscoveredServerAsync(e.Server);
        }

        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (_disposed)
            {
                return;
            }

            if (_discovered.Any(model => model.Server.Id == e.Server.Id))
            {
                return;
            }

            _discovered.Add(new OpdsServerDisplayModel(e.Server, this));
        });
    }

    private async Task PersistDiscoveredServerAsync(OpdsServer server)
    {
        try
        {
            if (_servers.FindByUrl(server.Url) is null)
            {
                await _servers.AddAsync(server).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not save discovered server {ServerName}", server.Name);
        }
    }

    private void OnServerRemoved(object? sender, ServerRemovedEventArgs e)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (_disposed)
            {
                return;
            }

            var match = _discovered.FirstOrDefault(model =>
                model.Url.Contains(e.ServerUrl, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                _discovered.Remove(match);
            }
        });
    }

    private void OnDiscoveryError(object? sender, DiscoveryErrorEventArgs e)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (_disposed)
            {
                return;
            }

            StatusMessage = e.Message;
        });
    }

    [RelayCommand]
    private async Task ToggleDiscoveryAsync()
    {
        if (IsDiscovering)
        {
            await StopDiscoveryIfRunningAsync();
            return;
        }

        try
        {
            await _discovery.StartDiscoveryAsync();
            IsDiscovering = true;
            StatusMessage = "Scanning the local network for Calibre OPDS servers...";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not start mDNS discovery");
            StatusMessage = "Could not start discovery: " + ex.Message;
        }
    }

    private async Task StopDiscoveryIfRunningAsync()
    {
        try
        {
            if (_discovery.IsDiscovering)
            {
                await _discovery.StopDiscoveryAsync();
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not stop mDNS discovery");
        }
        finally
        {
            IsDiscovering = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanAddManual))]
    private async Task AddManualAsync()
    {
        var url = ManualUrl.Trim();
        if (!IsValidHttpUrl(url))
        {
            StatusMessage = "Enter a valid http(s) URL, for example http://192.168.1.10:8080/";
            return;
        }

        IsBusy = true;
        try
        {
            var name = ManualName.Trim();
            var server = new OpdsServer
            {
                Name = string.IsNullOrWhiteSpace(name) ? new Uri(url).Host : name,
                Url = url,
                Type = ServerType.Manual
            };

            if (_servers.FindByUrl(url) is null)
            {
                await _servers.AddAsync(server);
            }

            StatusMessage = null;
            ManualUrl = string.Empty;
            ManualName = string.Empty;
            await _navigation.ShowOpdsCatalogAsync(url, server.Name);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanAddManual() => !IsBusy;

    internal Task OpenServerAsync(OpdsServerDisplayModel model)
    {
        return _navigation.ShowOpdsCatalogAsync(model.Url, model.Name);
    }

    internal async Task SetEnabledAsync(OpdsServerDisplayModel model, bool enabled)
    {
        try
        {
            model.Server.IsEnabled = enabled;
            await _servers.UpdateAsync(model.Server.Id, model.Server);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not save enabled state for server {Server}", model.Name);
        }
    }

    [RelayCommand]
    private async Task RemoveServerAsync(OpdsServerDisplayModel? model)
    {
        if (model is null)
        {
            return;
        }

        await _servers.RemoveAsync(model.Server.Id);
        _saved.Remove(model);
        _discovered.Remove(model);
        StatusMessage = $"Removed {model.Name}.";
    }

    [RelayCommand]
    private Task GoToDownloadsAsync() => _navigation.ShowDownloadsAsync();

    private static bool IsValidHttpUrl(string url)
    {
        return Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
            (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _discovery.ServerDiscovered -= OnServerDiscovered;
        _discovery.ServerRemoved -= OnServerRemoved;
        _discovery.DiscoveryError -= OnDiscoveryError;

        // Dispose is synchronous; track the stop so nothing is silently dropped.
        _pendingStop ??= StopDiscoveryIfRunningAsync();
    }
}
