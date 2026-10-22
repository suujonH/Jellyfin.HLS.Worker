using System.Collections.ObjectModel;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Hls.Worker.Gui;

/// <summary>
/// The log of the window: the lines of every logger, newest last, kept to <see cref="Capacity"/>.
/// </summary>
internal sealed class GuiLog : ILoggerProvider
{
    private const int Capacity = 2000;

    /// <summary>Gets the lines; changed on the UI thread only.</summary>
    public ObservableCollection<string> Lines { get; } = [];

    public ILogger CreateLogger(string categoryName) => new Logger(this);

    public void Dispose()
    {
    }

    private void Add(string line)
        => Dispatcher.UIThread.Post(() =>
        {
            Lines.Add(line);
            while (Lines.Count > Capacity)
            {
                Lines.RemoveAt(0);
            }
        });

    private sealed class Logger(GuiLog log) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
            => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            var level = logLevel switch
            {
                LogLevel.Warning => "警告 ",
                LogLevel.Error or LogLevel.Critical => "错误 ",
                _ => string.Empty
            };
            var message = formatter(state, exception);
            log.Add($"{DateTime.Now:HH:mm:ss} {level}{message}{(exception is null ? string.Empty : "：" + exception.Message)}");
        }
    }
}
