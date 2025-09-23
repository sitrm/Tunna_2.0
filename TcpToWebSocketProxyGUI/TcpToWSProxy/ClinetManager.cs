using System;
using System.Collections.Concurrent;
using TcpToWebSocketProxy;

namespace TcpToWebSocketProxy
{
    public class ClientManager
    {
        private readonly ConcurrentDictionary<Guid, TcpClientConnection> _clients = new ConcurrentDictionary<Guid, TcpClientConnection>();
        private readonly ConsoleColor[] _availableColors = {
            ConsoleColor.Yellow, ConsoleColor.Magenta, ConsoleColor.Cyan,
            ConsoleColor.Red, ConsoleColor.Blue
        };
        private int _colorIndex = 0;

        public void AddClient(Guid id, TcpClientConnection client)
        {
            _clients[id] = client;
            client.SetColor(_availableColors[_colorIndex++ % _availableColors.Length]);
        }

        public bool TryGetClient(Guid id, out TcpClientConnection client)
        {
            return _clients.TryGetValue(id, out client);
        }

        public void RemoveClient(Guid id)
        {
            _clients.TryRemove(id, out _);
        }

        public (string targetIp, int targetPort) GetClientTargetInfo(Guid id)
        {
            if (_clients.TryGetValue(id, out var client))
            {
                return (client.TargetIp, client.TargetPort);
            }
            return (null, 0);
        }
    }
}