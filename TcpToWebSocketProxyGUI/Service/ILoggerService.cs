using System;
using System.Windows.Media;

namespace TcpToWebSocketProxyGUI.Services
{
    public interface ILoggerService
    {
        void LogMessage(string message, Color color);
        void LogInfo(string message);
        void LogWarning(string message);
        void LogError(string message);
        void LogSuccess(string message);
    }
}