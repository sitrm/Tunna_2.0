using System;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TcpToWebSocketProxy;
using TcpToWebSocketProxyGUI.Services;
using UtilDataPacket;

namespace TcpToWebSocketProxy
{
    public class WebSocketConnection
    {
        private readonly string _url;
        private readonly ClientManager _clientManager;
        private ClientWebSocket _webSocket;
        private readonly object _lock = new object();

        private readonly ILoggerService _logger;
        private readonly SimpleClientService _clientService;
        public WebSocketConnection(string url, 
                                    ClientManager clientManager, 
                                    ILoggerService logger = null, 
                                    SimpleClientService clientService = null)
        {
            _url = url;
            _clientManager = clientManager;
            _logger = logger;
            _clientService = clientService;
        }

        public async Task ConnectAsync()
        {
            lock (_lock)
            {
                _webSocket = new ClientWebSocket();
            }

            await _webSocket.ConnectAsync(new Uri(_url), CancellationToken.None);
            _logger.LogInfo("WebSocket connected successfully");
            _ = Task.Run(ReceiveMessagesAsync);
        }

        public async Task SendAsync(DataPacket packet, CancellationToken ct)
        {
            var data = packet.Serialize();
            await _webSocket.SendAsync(
                new ArraySegment<byte>(data),
                WebSocketMessageType.Binary,
                true,
                ct
            );
        }
        public async Task SendDisconnectPacketAsync(Guid clientId, string targetIp, int targetPort)
        {
            if (_webSocket?.State != WebSocketState.Open)
                return;

            try
            {
                var disconnectPacket = new DataPacket(
                    clientId,
                    MessageType.Disconnect,
                    Encoding.UTF8.GetBytes("Client disconnected"),
                    targetIp,
                    targetPort
                );

                await SendAsync(disconnectPacket, CancellationToken.None);
                _logger.LogInfo($"Disconnect packet sent for client: {clientId}");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error sending disconnect packet for client {clientId}: {ex.Message}");
            }
        }

        private async Task ReceiveMessagesAsync()
        {
            var buffer = new byte[4096];

            while (_webSocket?.State == WebSocketState.Open)
            {
                try
                {
                    var result = await _webSocket.ReceiveAsync(
                        new ArraySegment<byte>(buffer),
                        CancellationToken.None);
                    //!!!!!!!!!!!!!!!!!! да норм 
                    if (result.MessageType == WebSocketMessageType.Close)
                        break;

                    var packet = DataPacket.Deserialize(buffer.AsSpan(0, result.Count).ToArray());

                    if (_clientManager.TryGetClient(packet.UserId, out var client))
                    {
                        await client.ForwardToTcpAsync(packet.Data);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError($"WebSocket receive error: {ex.Message}");
                    break;
                }
            }
        }

        public async Task DisconnectAsync()
        {
            if (_webSocket?.State == WebSocketState.Open)
            {
                await _webSocket.CloseAsync(
                    WebSocketCloseStatus.NormalClosure,
                    "Closing",
                    CancellationToken.None);
            }
           _logger.LogError("WebSocket disconnected");
        }
    }
}