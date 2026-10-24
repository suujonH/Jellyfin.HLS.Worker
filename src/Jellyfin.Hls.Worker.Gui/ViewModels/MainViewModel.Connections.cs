using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Jellyfin.Hls.Worker.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Hls.Worker.Gui.ViewModels;

/// <summary>
/// The remote directories of the settings, and their tests.
/// </summary>
internal sealed partial class MainViewModel
{
    private static readonly TimeSpan _testTimeout = TimeSpan.FromMinutes(2);

    private readonly CancellationTokenSource _tests = new();
    private readonly List<Task> _testing = [];

    public ObservableCollection<ConnectionViewModel> Connections { get; } = [];

    /// <summary>Gets the choices of the lines of the schemes, made from the remote directories.</summary>
    public TargetOptions TargetOptions { get; } = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CloneConnectionCommand), nameof(RemoveConnectionCommand), nameof(TestConnectionCommand))]
    public partial ConnectionViewModel? SelectedConnection { get; set; }

    private ConnectionViewModel AddConnectionViewModel(ConnectionSettings settings)
    {
        var connection = new ConnectionViewModel(settings);
        connection.PropertyChanged += OnConnectionChanged;
        Connections.Add(connection);
        return connection;
    }

    // The schemes show the directories by name.
    private void OnConnectionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ConnectionViewModel.Name))
        {
            TargetOptions.Rebuild(Connections.Select(c => c.Model));
        }
    }

    [RelayCommand]
    private void NewConnection(ConnectionType type)
    {
        var number = Connections.Count + 1;
        while (IsNameTaken("连接" + number))
        {
            number++;
        }

        AddConnection(new ConnectionSettings { Id = Guid.NewGuid(), Name = "连接" + number, Type = type });
    }

    [RelayCommand(CanExecute = nameof(HasSelectedConnection))]
    private void CloneConnection()
    {
        if (SelectedConnection is not { } source)
        {
            return;
        }

        var name = source.Name + " 副本";
        for (var number = 2; IsNameTaken(name); number++)
        {
            name = source.Name + " 副本" + number;
        }

        // A round trip through JSON copies every field, including those added later.
        var clone = JsonSerializer.Deserialize<ConnectionSettings>(JsonSerializer.Serialize(source.Model))!;
        clone.Id = Guid.NewGuid();
        clone.Name = name;
        AddConnection(clone);
    }

    private bool HasSelectedConnection() => SelectedConnection is not null;

    private bool IsNameTaken(string name) => Connections.Any(c => c.Name == name);

    private void AddConnection(ConnectionSettings settings)
    {
        SelectedConnection = AddConnectionViewModel(settings);
        TargetOptions.Rebuild(Connections.Select(c => c.Model));
    }

    [RelayCommand(CanExecute = nameof(HasSelectedConnection))]
    private void RemoveConnection()
    {
        if (SelectedConnection is not { } connection)
        {
            return;
        }

        connection.PropertyChanged -= OnConnectionChanged;
        Connections.Remove(connection);
        SelectedConnection = Connections.FirstOrDefault();

        // A line of a scheme that used it saves or declares nowhere: its choice is gone.
        TargetOptions.Rebuild(Connections.Select(c => c.Model));
    }

    [RelayCommand(CanExecute = nameof(HasSelectedConnection))]
    private Task TestConnectionAsync() => SelectedConnection is { } connection ? TestAsync(connection) : Task.CompletedTask;

    // What the tests before found is dropped first, so each mark shown is of this round; one still running keeps its own.
    [RelayCommand]
    private Task TestAllAsync()
    {
        foreach (var connection in Connections.Where(c => c.Status != TestStatus.Testing))
        {
            connection.Status = TestStatus.None;
            connection.StatusMessage = null;
        }

        return Task.WhenAll(Connections.ToList().Select(TestAsync));
    }

    // The directory as it is shown, without the rest of the settings: they may not be complete yet.
    private async Task TestAsync(ConnectionViewModel connection)
    {
        if (connection.Status == TestStatus.Testing)
        {
            return;
        }

        ConnectionSettings settings;
        try
        {
            settings = JsonSerializer.Deserialize<ConnectionSettings>(JsonSerializer.Serialize(connection.Model))!;
            _store.Resolve(settings);
            settings.Validate();
        }
        catch (ArgumentException ex)
        {
            connection.Status = TestStatus.Failed;
            connection.StatusMessage = ex.Message;
            return;
        }

        connection.Status = TestStatus.Testing;
        connection.StatusMessage = "测试中……";
        var test = RunTestAsync(connection, settings);
        _testing.Add(test);
        try
        {
            await test;
        }
        finally
        {
            _testing.Remove(test);
        }
    }

    private async Task RunTestAsync(ConnectionViewModel connection, ConnectionSettings settings)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_tests.Token);
        timeout.CancelAfter(_testTimeout);
        try
        {
            await Task.Run(() => Preflight.CheckConnectionAsync(settings, _logger, timeout.Token), CancellationToken.None);
            (connection.Status, connection.StatusMessage) = (TestStatus.Ok, "测试通过");
            _logger.LogInformation("远程目录 {Name} 测试通过", settings.Name);
        }
        catch (NotSupportedException ex)
        {
            (connection.Status, connection.StatusMessage) = (TestStatus.NotTestable, ex.Message);
        }
        catch (OperationCanceledException)
        {
            (connection.Status, connection.StatusMessage) = (TestStatus.Failed, "测试超时");
        }
        catch (Exception ex)
        {
            (connection.Status, connection.StatusMessage) = (TestStatus.Failed, ex.Message);
            _logger.LogError("远程目录 {Name} 测试失败：{Message}", settings.Name, ex.Message);
        }
    }
}
