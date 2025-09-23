using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using TcpToWebSocketProxy; // ClientManaget, WebSocketConnection, ProxyConfig

namespace TcpToWebSocketProxy
{
    public class ProxyServer
    {
        //private const string WebSocketUrl = "ws://localhost/WebsiteDeployment/webSocketProxyMult/main.aspx";
        private const string WebSocketUrl = "ws://192.168.181.134/WebsiteDeployment/webSocketProxyMult/main.aspx";
        private readonly ProxyConfig _config = new ProxyConfig();
        private readonly WebSocketConnection _webSocketConnection;
        private readonly ClientManager _clientManager;
        private readonly List<TcpListener> _listeners = new List<TcpListener>();
        private readonly CancellationTokenSource _globalCts = new CancellationTokenSource();

        public ProxyServer()
        {
            _clientManager = new ClientManager();
            _webSocketConnection = new WebSocketConnection(WebSocketUrl, _clientManager);
        }

        public async Task Start()
        {
            Console.WriteLine("Starting TCP listeners with the following port mapping:");
            foreach (var mapping in _config.PortMappings)
            {
                Console.WriteLine($"Port {mapping.Key} -> {mapping.Value.TargetIp}:{mapping.Value.TargetPort}");
            }

            await _webSocketConnection.ConnectAsync();

            foreach (var mapping in _config.PortMappings)
            {
                StartTcpListener(mapping.Key, mapping.Value.TargetIp, mapping.Value.TargetPort);
            }
        }

        private void StartTcpListener(int port, string targetIp, int targetPort)
        {
            var listener = new TcpListener(IPAddress.Any, port);
            listener.Start();
            _listeners.Add(listener);

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
                            _clientManager
                        );
                        _ = client.HandleConnectionAsync(_globalCts.Token);   // target ip and port // SendDisconnectPacket
                    }
                    catch (ObjectDisposedException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Error accepting client: {ex.Message}");
                    }
                }
            }, _globalCts.Token);
        }

        public async Task Stop()
        {
            _globalCts.Cancel();

            foreach (var listener in _listeners)
            {
                listener.Stop();
            }

            await _webSocketConnection.DisconnectAsync();
        }
    }
}