using System;
using System.Windows.Media;
using System.Windows.Threading;

namespace TcpToWebSocketProxyGUI.Services
{
    public class LoggerService : ILoggerService
    {
        private readonly Action<string, Color> _logAction;
        private readonly Dispatcher _dispatcher;

        public LoggerService(Action<string, Color> logAction, Dispatcher dispatcher = null)
        {
            _logAction = logAction;
            _dispatcher = dispatcher ?? Dispatcher.CurrentDispatcher;
        }

        public void LogMessage(string message, Color color)
        {
            _dispatcher.Invoke(() => _logAction?.Invoke($"[{DateTime.Now:HH:mm:ss}] {message}", color));
        }

        public void LogInfo(string message)
        {
            LogMessage(message, Colors.White);
        }

        public void LogWarning(string message)
        {
            LogMessage(message, Colors.Orange);
        }

        public void LogError(string message)
        {
            LogMessage(message, Colors.Red);
        }

        public void LogSuccess(string message)
        {
            LogMessage(message, Colors.Green);
        }
    }
}