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

                await Task.Delay(300); // !!!!!!!!!!!!!!!!

                _clientManager.RemoveClient(_id);
                _tcpClient.Close();
                Log($"[{DateTime.Now:HH:mm:ss.fff}] (HandleConnectionAsync - finally) Connection closed");
            }
        }
        //----------------------------------------------------------------------------------
        //private async Task ReceiveFromTcpAsync(CancellationToken ct)
        //{
        //    while (!ct.IsCancellationRequested && _tcpClient.Connected)
        //    {
        //        var buffer = ArrayPool<byte>.Shared.Rent(_bufferSize);
        //        try
        //        {
        //            int bytesRead = await _networkStream.ReadAsync(buffer, 0, _bufferSize, ct);
        //            if (bytesRead == 0) break; // Соединение закрыто удаленной стороной

        //            var packet = new DataPacket(
        //                _id,
        //                MessageType.Binary,
        //                buffer.AsSpan(0, bytesRead).ToArray(), // Создаем копию только нужных данных
        //                _targetIp,
        //                _targetPort);

        //            await _webSocket.SendAsync(packet, ct);
        //            Log($"[{DateTime.Now:HH:mm:ss.fff}] Forwarded {bytesRead} bytes to WebSocket");
        //        }
        //        finally
        //        {
        //            ArrayPool<byte>.Shared.Return(buffer);
        //        }
        //    }
        //}
        private async Task ReceiveFromTcpAsync(CancellationToken ct)
        {
            var buffer = ArrayPool<byte>.Shared.Rent(_bufferSize);
            var accumulatedData = new MemoryStream();

            try
            {
                while (!ct.IsCancellationRequested && _tcpClient.Connected)
                {
                    int bytesRead = await _networkStream.ReadAsync(buffer, 0, _bufferSize, ct);
                    if (bytesRead == 0)
                    {
                        // Соединение закрыто удаленной стороной - отправляем накопленные данные
                        if (accumulatedData.Length > 0)
                        {
                            await SendAccumulatedDataAsync(accumulatedData, ct);
                        }
                        break;
                    }

                    // Записываем прочитанные данные в поток
                    await accumulatedData.WriteAsync(buffer, 0, bytesRead, ct);
                    //await Task.Delay(10, ct);
                    // Если накопили достаточно данных или это последний пакет, отправляем
                    if (bytesRead < _bufferSize)
                    {
                        await SendAccumulatedDataAsync(accumulatedData, ct);
                    }

                    //Log($"[{DateTime.Now:HH:mm:ss.fff}] Accumulated {bytesRead} bytes, total: {accumulatedData.Length}");
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
                accumulatedData?.Dispose();
            }
        }

        private async Task SendAccumulatedDataAsync(MemoryStream accumulatedData, CancellationToken ct)
        {
            if (accumulatedData.Length == 0) return;

            var packet = new DataPacket(
                _id,
                MessageType.Binary,
                accumulatedData.ToArray(), // Получаем все накопленные данные
                _targetIp,
                _targetPort);

            await _webSocket.SendAsync(packet, ct);
            Log($"[{DateTime.Now:HH:mm:ss.fff}] Forwarded {accumulatedData.Length} accumulated bytes to WebSocket");

            // Сбрасываем поток для следующих данных
            accumulatedData.SetLength(0);
        }
        //------------------------------------------------------------------------------------------------
        public async Task ForwardToTcpAsync(byte[] data)
        {
            //await _networkStream.WriteAsync(data, 0, data.Length);
            //await _networkStream.FlushAsync();
            //Log($"[{DateTime.Now:HH:mm:ss.fff}] Forwarded {data.Length} bytes to TCP client");
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