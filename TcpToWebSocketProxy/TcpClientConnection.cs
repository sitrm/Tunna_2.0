using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using TcpToWebSocketProxy;
using UtilDataPacket;

namespace TcpToWebSocketProxy
{
    public class TcpClientConnection
    {
        private readonly TcpClient _tcpClient;
        private readonly NetworkStream _networkStream;
        private readonly int _listenPort;
        private readonly string _targetIp;
        private readonly int _targetPort;
        private readonly WebSocketConnection _webSocket;
        private readonly ClientManager _clientManager;
        private readonly Guid _id = Guid.NewGuid();
        private ConsoleColor _color;

        //свойства только для чтения 
        public string TargetIp => _targetIp;
        public int TargetPort => _targetPort;

        public TcpClientConnection(
            TcpClient tcpClient,
            int listenPort,
            string targetIp,
            int targetPort,
            WebSocketConnection webSocket,
            ClientManager clientManager)
        {
            _tcpClient = tcpClient;
            _networkStream = tcpClient.GetStream();
            _listenPort = listenPort;
            _targetIp = targetIp;
            _targetPort = targetPort;
            _webSocket = webSocket;
            _clientManager = clientManager;
        }

        public void SetColor(ConsoleColor color) => _color = color;

        public async Task HandleConnectionAsync(CancellationToken ct)
        {
            _clientManager.AddClient(_id, this);
            Log($"[{DateTime.Now:HH:mm:ss.fff}] Client connected from {GetClientEndpoint()}");

            try
            {
                await ReceiveFromTcpAsync(ct);
            }
            catch (Exception ex)
            {
                Log($"Error: {ex.Message}");
            }
            finally
            {
                // Отправляем пакет отключения перед закрытием соединения
                await _webSocket.SendDisconnectPacketAsync(_id, _targetIp, _targetPort);

                _clientManager.RemoveClient(_id);
                _tcpClient.Close();
                Log($"[{DateTime.Now:HH:mm:ss.fff}] Connection closed");
            }
        }

        private async Task ReceiveFromTcpAsync(CancellationToken ct)
        {
            var buffer = new byte[4096];

            while (!ct.IsCancellationRequested)
            {
                var bytesRead = await _networkStream.ReadAsync(buffer, 0, buffer.Length, ct);
                if (bytesRead == 0) break;

                var packet = new DataPacket(
                    _id,
                    MessageType.Binary,
                    buffer.AsSpan(0, bytesRead).ToArray(),
                    _targetIp,
                    _targetPort);

                await _webSocket.SendAsync(packet, ct);
                Log($"[{DateTime.Now:HH:mm:ss.fff}] Forwarded {bytesRead} bytes to WebSocket");
            }
        }

        public async Task ForwardToTcpAsync(byte[] data)
        {
            await _networkStream.WriteAsync(data, 0, data.Length);
            await _networkStream.FlushAsync();
            Log($"[{DateTime.Now:HH:mm:ss.fff}] Forwarded {data.Length} bytes to TCP client");
        }

        private void Log(string message)
        {
            Console.ForegroundColor = _color;
            Console.WriteLine(message);
            Console.ResetColor();
        }

        private string GetClientEndpoint()
        {
            return ((IPEndPoint)_tcpClient.Client.RemoteEndPoint).ToString();
        }
    }
}