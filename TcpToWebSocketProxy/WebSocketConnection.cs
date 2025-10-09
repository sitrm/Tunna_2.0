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
        private readonly int _bufferSize;
        private ClientWebSocket _webSocket;
        private readonly object _lock = new object();

        public WebSocketConnection(string url, ClientManager clientManager, int bufferSize = 32768)
        {
            _url = url;
            _clientManager = clientManager;
            _bufferSize = bufferSize;
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
            var buffer = new byte[_bufferSize];   // побольше!!! 16 100

            while (_webSocket?.State == WebSocketState.Open)
            {
                try
                {
                    // Используем MemoryStream для сборки полного сообщения
                    using (var memoryStream = new MemoryStream())
                    {
                        WebSocketReceiveResult result;
                        do
                        {
                            result = await _webSocket.ReceiveAsync(
                                new ArraySegment<byte>(buffer),
                                CancellationToken.None);

                            if (result.MessageType == WebSocketMessageType.Close)
                                break;

                            memoryStream.Write(buffer, 0, result.Count);
                        }
                        while (!result.EndOfMessage);

                        //if (result.MessageType == WebSocketMessageType.Close)
                        //    break;

                        // Получили полное сообщение
                        var completeMessage = memoryStream.ToArray();

                        if (completeMessage.Length > 0)
                        {
                            await ProcessReceivedMessage(completeMessage);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"WebSocket receive error: {ex.Message}");  //!!!!!!! select * from sys.databases
                    break;
                }
            }
        }
        private async Task ProcessReceivedMessage(byte[] messageData)
        {
            try
            {
                var packet = DataPacket.Deserialize(messageData);

                if (_clientManager.TryGetClient(packet.UserId, out var client))
                {
                    await client.ForwardToTcpAsync(packet.Data);
                }
                else
                {
                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] Unknown client ID: {packet.UserId}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] Error processing received message: {ex.Message}");
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