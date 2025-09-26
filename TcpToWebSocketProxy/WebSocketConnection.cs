using System;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TcpToWebSocketProxy;
using UtilDataPacket;

namespace TcpToWebSocketProxy
{
    public class WebSocketConnection
    {
        private readonly string _url;
        private readonly ClientManager _clientManager;
        private ClientWebSocket _webSocket;
        private readonly object _lock = new object();

        public WebSocketConnection(string url, ClientManager clientManager)
        {
            _url = url;
            _clientManager = clientManager;
        }

        public async Task ConnectAsync()
        {
            lock (_lock)
            {
                _webSocket = new ClientWebSocket();
            }

            await _webSocket.ConnectAsync(new Uri(_url), CancellationToken.None);
            _ = Task.Run(ReceiveMessagesAsync);
        }

        public async Task SendAsync(DataPacket packet, CancellationToken ct)
        {
            var data = packet.Serialize();
            await _webSocket.SendAsync(
                new ArraySegment<byte>(data),
                WebSocketMessageType.Binary,
                true,                                // !!!!! что такое 
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
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] Disconnect packet sent for client: {clientId}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] Error sending disconnect packet for client {clientId}: {ex.Message}");
            }
        }

        private async Task ReceiveMessagesAsync()
        {
            var buffer = new byte[4096];   // побольше!!! 16 100

            while (_webSocket?.State == WebSocketState.Open)
            {
                try
                {
                    var result = await _webSocket.ReceiveAsync(
                        new ArraySegment<byte>(buffer),
                        CancellationToken.None);
                    //!!!!!!!!!!!!!!!!!!
                    //if (result.MessageType == WebSocketMessageType.Close)
                    //    break;

                    var packet = DataPacket.Deserialize(buffer.AsSpan(0, result.Count).ToArray());

                    if (_clientManager.TryGetClient(packet.UserId, out var client))
                    {
                        await client.ForwardToTcpAsync(packet.Data);
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"WebSocket receive error: {ex.Message}");  //!!!!!!! select * from sys.databases
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
        }
    }
}