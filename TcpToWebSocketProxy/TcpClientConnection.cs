using System;
using System.Buffers;
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
        private readonly int _bufferSize;
        private ArrayPool<byte> arrayPool = ArrayPool<byte>.Shared;

        //свойства только для чтения 
        public string TargetIp => _targetIp;
        public int TargetPort => _targetPort;

        public TcpClientConnection(
            TcpClient tcpClient,
            int listenPort,
            string targetIp,
            int targetPort,
            WebSocketConnection webSocket,
            ClientManager clientManager,
            int bufferSize)
        {
            _tcpClient = tcpClient;
            _networkStream = tcpClient.GetStream();
            _listenPort = listenPort;
            _targetIp = targetIp;
            _targetPort = targetPort;
            _webSocket = webSocket;
            _clientManager = clientManager;
            _bufferSize = bufferSize;
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
                Log($"[{DateTime.Now:HH:mm:ss.fff}] !!! (HandleConnectionAsync) Error: {ex.Message}");
            }
            finally
            {
                // Отправляем пакет отключения перед закрытием соединения
                await _webSocket.SendDisconnectPacketAsync(_id, _targetIp, _targetPort);


                _clientManager.RemoveClient(_id);
                _tcpClient.Close();
                Log($"[{DateTime.Now:HH:mm:ss.fff}] (HandleConnectionAsync - finally) Connection closed");
            }
        }
        //--------------------------------------------------------------------------
        private async Task ReceiveFromTcpAsync(CancellationToken ct)
        {
            //var buffer = ArrayPool<byte>.Shared.Rent(_bufferSize);
            var buffer = arrayPool.Rent(_bufferSize);
            try
            {
                while (!ct.IsCancellationRequested && _tcpClient.Connected)
                {
                    int bytesRead = await _networkStream.ReadAsync(buffer, 0, _bufferSize, ct);
                    if (bytesRead == 0) break;
                    var data = buffer.AsMemory(0, bytesRead);
                    // Отправляем КАЖДЫЙ кусок сразу!!!
                    var packet = new DataPacket(
                        _id,
                        MessageType.Binary,
                        data.ToArray(),
                        _targetIp,
                        _targetPort
                    );
                    await _webSocket.SendAsync(packet, ct);

                    Log($"[{DateTime.Now:HH:mm:ss.fff}] Forwarded {bytesRead} bytes to WebSocket");
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
        //------------------------------------------------------------------------------------------------
        public async Task ForwardToTcpAsync(byte[] data)
        {
            //await _networkStream.WriteAsync(data, 0, data.Length);
            //await _networkStream.FlushAsync();
            //Log($"[{DateTime.Now:HH:mm:ss.fff}] Forwarded {data.Length} bytes to TCP client");
            if (data == null || data.Length == 0 || !_tcpClient.Connected)
                return;
            try
            {
                if (_tcpClient.Connected )
                {
                    await _networkStream.WriteAsync(data, 0, data.Length);
                    //await _networkStream.WriteAsync(data);
                    await _networkStream.FlushAsync();
                    Log($"[{DateTime.Now:HH:mm:ss.fff}] Forwarded {data.Length} bytes to TCP client");
                    //Log($"[{DateTime.Now:HH:mm:ss.fff}] Forwarded ___ bytes to TCP client");
                }
                else
                {
                    Log($"[{DateTime.Now:HH:mm:ss.fff}] !!!(ForwardToTcpAsync)Cannot forward data - TCP connection is closed");
                }
            }
            catch (IOException ex)
            {
                Log($"[{DateTime.Now:HH:mm:ss.fff}] !!!(ForwardToTcpAsync) Error writing to TCP: {ex.Message}");
            }
            catch (Exception ex)
            {
                Log($"[{DateTime.Now:HH:mm:ss.fff}] Unexpected error writing to TCP: {ex.Message}");
            }
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