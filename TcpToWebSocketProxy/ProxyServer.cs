using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using TcpToWebSocketProxy; // ClientManaget, WebSocketConnection, ProxyConfig

namespace TcpToWebSocketProxy
{
    public class ProxyServer : IDisposable
    {
       
        
        //private const string WebSocketUrl = "ws://192.168.181.134/WebsiteDeployment/webSocketProxyMult/main.aspx";
        private readonly ProxyConfig _config;
        private readonly WebSocketConnection _webSocketConnection;
        private readonly ClientManager _clientManager;
        private readonly List<TcpListener> _listeners = new List<TcpListener>();
        private readonly CancellationTokenSource _globalCts = new CancellationTokenSource();
        private bool _disposed = false;
        public ProxyServer(ProxyConfig config)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _clientManager = new ClientManager();
            _webSocketConnection = new WebSocketConnection(_config.WebSocketUrl, 
                                                           _clientManager,     
                                                           _config.WebSocketBufferSize,                                                                     _config.MaxWebSocketMessageSize
                                                           );
                                                                
        }

        public async Task Start()
        {
            Console.WriteLine("Starting TCP listeners with the following port mapping:");
            
            await _webSocketConnection.ConnectAsync();

            foreach (var mapping in _config.PortMappings)
            {
                StartTcpListener(mapping.ListenPort, mapping.TargetIp, mapping.TargetPort);
            }
            Console.WriteLine($"Started {_config.PortMappings.Count} TCP listener(s)");
        }

        private void StartTcpListener(int port, string targetIp, int targetPort)
        {
            var listener = new TcpListener(IPAddress.Parse("127.0.0.2"), port);
            listener.Start();
            _listeners.Add(listener);
            Console.WriteLine($"Listening on port {port} -> {targetIp}:{targetPort}");
            _ = Task.Run(async () =>
            {
                while (!_globalCts.Token.IsCancellationRequested)
                {
                    try
                    {
                        var tcpClient = await listener.AcceptTcpClientAsync();
                        var client = new TcpClientConnection(
                            tcpClient,
                            port,
                            targetIp,
                            targetPort,
                            _webSocketConnection,
                            _clientManager,
                            _config.TcpBufferSize
                        );
                        _ = client.HandleConnectionAsync(_globalCts.Token);   // target ip and port // SendDisconnectPacket
                    }
                    catch (ObjectDisposedException)
                    { 
                        break;
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Error accepting client on port {port}:{ex.Message}");
                    }
                }
            }, _globalCts.Token);
        }

        public async Task Stop()
        {
            if (_disposed) return;

            Console.WriteLine("Stopping proxy server...");
            _globalCts.Cancel();

            // Останавливаем все слушатели
            foreach (var listener in _listeners)
            {
                try
                {
                    listener.Stop();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error stopping listener: {ex.Message}");
                }
            }
            _listeners.Clear();

            // Отключаем WebSocket
            await _webSocketConnection.DisconnectAsync();
        }

        // реализация интерфейса IDisposable.
        //корректного освобождения неуправляемых ресурсов
        public void Dispose()
        {
            if (!_disposed)
            {
                Stop().GetAwaiter().GetResult();    // 2. Остановка сервера -  Синхронное ожидание остановки
                _globalCts.Dispose();               // 3. Освобождение CancellationTokenSource
                _disposed = true;                    // 4. Помечаем как освобожденный
            }
        }
    }
}