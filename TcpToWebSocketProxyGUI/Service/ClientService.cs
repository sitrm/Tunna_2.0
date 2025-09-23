using System;
using System.Collections.ObjectModel;
using System.Windows.Threading;
using TcpToWebSocketProxyGUI.Models;

namespace TcpToWebSocketProxyGUI.Services
{
    public class SimpleClientService
    {
        private readonly ObservableCollection<ClientInfo> _clients;
        private readonly Dispatcher _dispatcher;

        public ObservableCollection<ClientInfo> Clients => _clients;

        public SimpleClientService(Dispatcher dispatcher)
        {
            _dispatcher = dispatcher;
            _clients = new ObservableCollection<ClientInfo>();
        }

        public void AddClient(Guid clientId, string targetIp, int targetPort, string clientAddress)
        {
            _dispatcher.Invoke(() =>
            {
                var clientInfo = new ClientInfo
                {
                    Id = clientId.ToString().Substring(0, 8) + "...", // Сокращенный ID
                    Target = $"{targetIp}:{targetPort}",
                    Status = "Connected",
                    ClientAddress = clientAddress, // Сохраняем адрес клиента
                    ConnectionTime = DateTime.Now
                };

                _clients.Add(clientInfo);
            });
        }

        public void RemoveClient(Guid clientId)
        {
            _dispatcher.Invoke(() =>
            {
                var clientIdStr = clientId.ToString();
                for (int i = _clients.Count - 1; i >= 0; i--)
                {
                    // Сравниваем полные ID, но отображаем сокращенный
                    if (_clients[i].Id.StartsWith(clientIdStr.Substring(0, 8)))
                    {
                        _clients.RemoveAt(i);
                        break;
                    }
                }
            });
        }

        public void ClearAll()
        {
            _dispatcher.Invoke(() =>
            {
                _clients.Clear();
            });
        }
    }
}