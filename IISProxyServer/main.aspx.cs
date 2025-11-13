using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using System.Web.WebSockets;
using Util;
using System.Buffers;
using System.Security.Cryptography;
using System.Collections.Concurrent;
using System.Web.UI.WebControls;


namespace WSP
{

    public partial class WebSocketProxy : System.Web.UI.Page
    {
        private class ConnectionTarget
        {
            public string Ip { get; set; }
            public int Port { get; set; }
        }
        // connectionId → TcpClient
        private static readonly ConcurrentDictionary<Guid, TcpClient> _tcpClients = new ConcurrentDictionary<Guid, TcpClient>();

        // connectionId → WebSocket
        private static readonly ConcurrentDictionary<Guid, WebSocket> _webSockets = new ConcurrentDictionary<Guid, WebSocket>();

        // connectionId → (TargetIp, TargetPort) — для уведомлений
        private static readonly ConcurrentDictionary<Guid, ConnectionTarget> _connectionTargets = 
            new ConcurrentDictionary<Guid, ConnectionTarget>();

        int SIZE_BUF = 64000;
        //----------------------------------------------------------------------------------------------------
        protected void Page_Load(object sender, EventArgs e)
        {
            if (Context.IsWebSocketRequest)
            {
                Context.AcceptWebSocketRequest(ProcessWebSocket);
            }
        }

        private async Task ProcessWebSocket(AspNetWebSocketContext context)
        {

            WebSocket _webSocket = context.WebSocket;
           
            Guid connectionId = Guid.NewGuid();
            _webSockets[connectionId] = _webSocket;

            var buffer = ArrayPool<byte>.Shared.Rent(SIZE_BUF);
            try
            {

                while (_webSocket.State == WebSocketState.Open)
                {
                    //byte[] buffer = new byte[SIZE_BUF];
                    try
                    {
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
                            while (!result.EndOfMessage && _webSocket.State == WebSocketState.Open);

                            if (result.MessageType == WebSocketMessageType.Close)
                                break;

                            var completeMessage = memoryStream.ToArray();

                            if (completeMessage.Length > 0)
                            {
                                // Десериализуем пакет
                                DataPacket packet = DataPacket.Deserialize(completeMessage);
                                //Добавить await (если нужно дождаться завершения):
                                Task.Run(() => ProcessPacketAsync(_webSocket, packet, connectionId));
                                //_ = ProcessPacketAsync(_webSocket, packet, connectionId);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Trace.TraceError("WebSocket error: " + ex.Message);
                        break;
                    }
                }
            }
            finally
            {
                WebSocket tempWebSocket = null;
                TcpClient tcpClient = null;

                ArrayPool<byte>.Shared.Return(buffer);
                _webSockets.TryRemove(connectionId, out tempWebSocket);
                // Закрываем TCP-
                if (_tcpClients.TryRemove(connectionId, out tcpClient))
                {
                    try { tcpClient.Close(); }
                    catch { }
                }
                ConnectionTarget conntarget; 
                // Удаляем цель
                _connectionTargets.TryRemove(connectionId, out conntarget);
                // Корректно закрываем WebSocket
                if (_webSocket.State == WebSocketState.Open)
                {
                    try
                    {
                            _webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closed", CancellationToken.None);
                    }
                    catch { }
                }
            }
        }

        //----------------------------------------------------------------------------------------------------
        private async Task ProcessPacketAsync(WebSocket webSocket, DataPacket packet, Guid connectionId)
        {
            try
            {
                switch (packet.Type)
                {

                    case MessageType.Binary:
                        // Перенаправляем данные на целевой TCP сервер
                        await ForwardToTcpServer(connectionId, packet);
                        break;

                    // case MessageType.Authentication:
                    //     // Обрабатываем аутентификацию
                    //     await ProcessAuthentication(webSocket, packet);
                    //     break;

                    case MessageType.Error:
                        // Обрабатываем ошибки
                        await ProcessError(connectionId, packet);
                        break;

                    case MessageType.Disconnect:
                        // Обрабатываем отключение - закрываем TCP соединение
                        await ProcessDisconnect(connectionId, packet);
                        break;
                }
            }
            catch (Exception ex)
            {
                 SendErrorResponse(connectionId, packet, ex);
            }
        }

        //----------------------------------------------------------------------------------------------------
        //private async Task ForwardToTcpServer(WebSocket webSocket, DataPacket packet)
        //{
        //    string connectionKey = GetConnectionKey(packet.TargetIp, packet.TargetPort);
        //    TcpClient tcpClient = null;
        //    NetworkStream networkStream = null;

        //    try
        //    {
        //        // Получаем или создаем TCP соединение
        //        tcpClient = GetOrCreateTcpClient(webSocket, connectionKey, packet.TargetIp, packet.TargetPort);
        //        networkStream = tcpClient.GetStream();

        //        // Отправляем данные на TCP сервер
        //        await networkStream.WriteAsync(packet.Data, 0, packet.Data.Length);

        //        // Устанавливаем таймаут для чтения
        //        using (var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(3)))
        //        {
        //            try
        //            {
        //                // Читаем ответ от TCP сервера асинхронно с таймаутом
        //                byte[] responseBuffer = new byte[SIZE_BUF];
        //                // Асинхронно читаем ответ
        //                int bytesRead = await networkStream.ReadAsync(responseBuffer, 0, responseBuffer.Length, timeoutCts.Token);

        //                if (bytesRead > 0)
        //                {
        //                    // Подготавливаем ответные данные
        //                    byte[] responseData = new byte[bytesRead];
        //                    Array.Copy(responseBuffer, 0, responseData, 0, bytesRead);

        //                    // Создаем пакет ответа
        //                    DataPacket responsePacket = new DataPacket(
        //                        packet.UserId,
        //                        packet.Type,
        //                        responseData,
        //                        packet.TargetIp,
        //                        packet.TargetPort
        //                    );

        //                    // Отправляем ответ обратно через WebSocket
        //                    byte[] serializedResponse = responsePacket.Serialize();
        //                    await webSocket.SendAsync(
        //                        new ArraySegment<byte>(serializedResponse),
        //                        WebSocketMessageType.Binary,
        //                        true,
        //                        CancellationToken.None
        //                    );
        //                }
        //            }
        //            catch (OperationCanceledException)
        //            {
        //                // Таймаут чтения - это нормально, не все серверы отправляют ответ
        //                System.Diagnostics.Trace.TraceWarning("Read timeout for TCP server: " + connectionKey);
        //            }
        //        }
        //    }
        //    catch (Exception ex)
        //    {
        //        // Если соединение разорвано, удаляем его из кэша
        //        if (tcpClient != null && !tcpClient.Connected)
        //        {
        //            RemoveTcpClient(webSocket, connectionKey);
        //        }

        //        // Отправляем ошибку клиенту
        //        SendErrorResponse(webSocket, packet, ex);
        //    }
        //}
        //---------------------------------------------------------------------------------------------------
        //
        private async Task ForwardToTcpServer(Guid connectionId, DataPacket packet)
        {
            var tcpClient = GetOrCreateTcpClient(connectionId, packet);
            if (tcpClient == null) return;

            try
            {
                var stream = tcpClient.GetStream();
                await stream.WriteAsync(packet.Data, 0, packet.Data.Length);
                await stream.FlushAsync();
            }
            catch (Exception ex)
            {
                SendErrorResponse(connectionId, packet, ex);
                RemoveTcpClient(connectionId);
            }
        }
        //---------------------------------------------------------------------------------------------------

        // Функция для чтения данных от TCP 
        //33333333333333
        private async Task ReadFromTcpServer(Guid connectionId, TcpClient tcpClient, DataPacket packet)
        {
            Guid userId = packet.UserId;
            var stream = tcpClient.GetStream();
            var buffer = ArrayPool<byte>.Shared.Rent(SIZE_BUF);
            var accumulated = new MemoryStream();

            try
            {
                WebSocket ws = null;
                ConnectionTarget target = null;
                while (tcpClient.Connected &&
                       _webSockets.TryGetValue(connectionId, out ws) &&
                       ws.State == WebSocketState.Open &&
                       _connectionTargets.TryGetValue(connectionId, out target))
                {
                    int bytesRead = 0;
                    try
                    {
                        bytesRead = await stream.ReadAsync(buffer, 0, SIZE_BUF);
                    }
                    catch
                    {
                        break; // Ошибка чтения
                    }

                    if (bytesRead == 0) break;

                    await accumulated.WriteAsync(buffer, 0, bytesRead);
                    await SendAccumulatedDataAsync(ws, userId, accumulated, target.Ip, target.Port);
                }
            }
            catch { }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
                accumulated.Dispose();
                RemoveTcpClient(connectionId);
            }
        }

        private async Task SendAccumulatedDataAsync(WebSocket ws, Guid userId, MemoryStream accumulated,
            string targetIp, int targetPort)
        {
            if (accumulated.Length == 0) return;

            var data = accumulated.ToArray();
            var responsePacket = new DataPacket(userId, MessageType.Binary, data, targetIp, targetPort);
            var serialized = responsePacket.Serialize();

            try
            {
                await ws.SendAsync(new ArraySegment<byte>(serialized), 
                                    WebSocketMessageType.Binary, 
                                    true, 
                                    CancellationToken.None);
            }
            catch { }

            accumulated.SetLength(0);
        }

        //---------------------------------------------------------------------------------------------------
        private string GetConnectionKey(Guid userId)
        {
            return userId.ToString();
        }
        //----------------------------------------------------------------------------------------------------
        private TcpClient GetOrCreateTcpClient(Guid connectionId, DataPacket packet)
        {
            return _tcpClients.GetOrAdd(connectionId, _ =>
            {
                var client = new TcpClient();
                try
                {
                    client.Connect(packet.TargetIp, packet.TargetPort);
                    // Сохраняем цель для уведомлений 
                    _connectionTargets[connectionId] = new ConnectionTarget { Ip = packet.TargetIp, Port = packet.TargetPort };

                    // Запускаем **один** читатель
                    Task.Run(async () =>
                    {
                        try
                        {
                            await ReadFromTcpServer(connectionId, client, packet);
                        }
                        finally
                        {
                            RemoveTcpClient(connectionId);
                        }
                    });

                    return client;
                }
                catch
                {
                    TcpClient tcpClient = null;
                    _tcpClients.TryRemove(connectionId, out tcpClient);
                    throw;
                }
            });
        }
        //----------------------------------------------------------------------------------------------------
        //private async Task MonitorTcpConnection(WebSocket webSocket, string connectionKey, TcpClient tcpClient)
        //{
        //    try
        //    {
        //        while (tcpClient.Connected)  // Пока соединение активно
        //        {
        //            await Task.Delay(5000); // Проверяем каждые 5 секунд

        //            // Проверяем соединение отправкой пустого пакета
        //            if (!IsTcpClientConnected(tcpClient))
        //            {
        //                RemoveTcpClient(connectionKey);    //Удаляем если отвалилось
        //                break;
        //            }
        //        }
        //    }
        //    catch
        //    {
        //        RemoveTcpClient(webSocket, connectionKey);    // При ошибки тоже удаляем 
        //    }
        //}
        //----------------------------------------------------------------------------------------------------
        private bool IsTcpClientConnected(TcpClient tcpClient)
        {
            try
            {
                if (tcpClient == null || !tcpClient.Connected)
                    return false;

                // Проверяем соединение попыткой чтения/записи
                var stream = tcpClient.GetStream();
                return stream.CanRead && stream.CanWrite;
            }
            catch
            {
                return false;
            }
        }
        //----------------------------------------------------------------------------------------------------
        private async Task ProcessError(Guid connectionId, DataPacket packet)
        {
            string errorMessage = Encoding.UTF8.GetString(packet.Data);
            System.Diagnostics.Trace.TraceError("Error from client " + packet.UserId + ": " + errorMessage);
        }
        //----------------------------------------------------------------------------------------------------
        // метод для обработки пакетов отключения
        private async Task ProcessDisconnect(Guid connectionId, DataPacket packet)
        {
            RemoveTcpClient(connectionId);
            WebSocket ws = null;
            if (_webSockets.TryGetValue(connectionId, out ws) && ws.State == WebSocketState.Open)
            {
                var response = new DataPacket(
                    packet.UserId,
                    MessageType.Disconnect,
                    Encoding.UTF8.GetBytes("TCP connection closed successfully!!!"),
                    packet.TargetIp,
                    packet.TargetPort
                );

                var data = response.Serialize();
                await ws.SendAsync(new ArraySegment<byte>(data), WebSocketMessageType.Binary, true, CancellationToken.None);
            }
        }
        //-----------------------------------------------------------------------------------------------------
        private async Task SendErrorResponse(Guid connectionId, DataPacket packet, Exception ex)
        {
            WebSocket ws = null;
            if (!_webSockets.TryGetValue(connectionId, out ws) || ws.State != WebSocketState.Open)
                return;

            var errorPacket = new DataPacket(
                packet.UserId,
                MessageType.Error,
                Encoding.UTF8.GetBytes("Error: " + ex.Message),
                packet.TargetIp,
                packet.TargetPort
            );

            var data = errorPacket.Serialize();
            try
            {
                await ws.SendAsync(new ArraySegment<byte>(data), WebSocketMessageType.Binary, true, CancellationToken.None);
            }
            catch { }
        }
        //----------------------------------------------------------------------------------------------------
        // Метод для отправки уведомления о разрыве TCP соединения
        private async Task SendTcpDisconnectNotification(WebSocket ws, string ip, int port)
        {
            try
            {
                var packet = new DataPacket(
                    Guid.Empty,
                    MessageType.Disconnect,
                    Encoding.UTF8.GetBytes("TCP connection closed:" + ip + ":" + port),
                    ip,
                    port
                );

                var data = packet.Serialize();
                await ws.SendAsync(new ArraySegment<byte>(data), WebSocketMessageType.Binary, true, CancellationToken.None);
            }
            catch { }
        }
        //-----------------------------------------------------------------------
        private void RemoveTcpClient(Guid connectionId)
        {
            TcpClient tcpClient = null;
            WebSocket ws = null;
            ConnectionTarget conntarget = null;

            if (_tcpClients.TryRemove(connectionId, out tcpClient))
            {
                try { tcpClient.Close(); }
                catch { }
            }
            if (_connectionTargets.TryGetValue(connectionId, out conntarget) &&
                _webSockets.TryGetValue(connectionId, out ws) &&
                ws.State == WebSocketState.Open)
            {
                SendTcpDisconnectNotification(ws, conntarget.Ip, conntarget.Port);
            }

            _connectionTargets.TryRemove(connectionId, out conntarget);
        }
        //-----------------------------------------------------------------------
        private async Task CleanupWebSocketResources(WebSocket webSocket)
        {
            // Закрываем все TCP соединения для этого WebSocket
            foreach (var connectionKey in _tcpClients.Keys)
            {
                RemoveTcpClient(connectionKey);
            }

            if (webSocket.State == WebSocketState.Open)
            {
                try
                {
                    await webSocket.CloseAsync(
                        WebSocketCloseStatus.NormalClosure,
                        "Connection closed",
                        CancellationToken.None);
                }
                catch
                {
                    // Игнорируем ошибки закрытия
                }
            }
        }

    }
}

//****************************************************************************************************************
//****************************************************************************************************************
//****************************************************************************************************************

namespace Util
{
    public enum MessageType : int
    {
        Text = 1,
        Binary = 2,
        File = 3,
        Error = 4,
        Disconnect = 5
    }

    /// <summary>
    /// Класс для сериализации/десериализации пакетов данных
    /// </summary>
    [Serializable]
    public class DataPacket
    {
        private const uint MAGIC_NUMBER = 0xDEADBEEF; //(4 байта)
        public Guid UserId { get; set; }
        public MessageType Type { get; set; }
        public byte[] Data { get; set; }
        public string TargetIp { get; set; }
        public int TargetPort { get; set; }

        public DataPacket()
        {

        }

        public DataPacket(Guid userId, MessageType type, byte[] data, string targetIp, int targetPort) : this()
        {
            UserId = userId;    // 16 
            Type = type;        // 4
            Data = data;        // N
            TargetIp = targetIp;   // 4
            TargetPort = targetPort;   // N
        }

        public DataPacket(Guid userId, MessageType type, string text, string targetIp, int targetPort) : this(userId, type, Encoding.UTF8.GetBytes(text), targetIp, targetPort)
        {
        }

        /// <summary>
        /// Сериализация пакета в массив байт
        /// </summary>
        public byte[] Serialize()
        {
            using (var ms = new MemoryStream())
            using (var writer = new BinaryWriter(ms))
            {
                //Заголовок пакета(4 байта)
                writer.Write(MAGIC_NUMBER); // Magic number для идентификации пакета 

                // UserId (16 байт)
                writer.Write(UserId.ToByteArray());

                // Тип сообщения (4 байта)
                writer.Write((int)Type);

                // TargetIp (длина(4 байта и данные)
                writer.Write(TargetIp != null ? TargetIp.Length : 0);
                if (!string.IsNullOrEmpty(TargetIp))
                    writer.Write(Encoding.UTF8.GetBytes(TargetIp));

                // TargetPort (4 байта)
                writer.Write(TargetPort);

                // Длина данных (4 байта)
                writer.Write(Data != null ? Data.Length : 0);

                // Данные (переменная длина)
                if (Data != null && Data.Length > 0)
                    writer.Write(Data);

                ////// Контрольная сумма (4 байта)
                //var checksum = ComputeChecksum(ms.ToArray());
                //writer.Write(checksum);

                return ms.ToArray();
            }
        }

        /// <summary>
        /// Десериализация пакета из массива байт
        /// </summary>
        public static DataPacket Deserialize(byte[] data)
        {
            if (data == null || data.Length < 36)
                // Минимальный размер: 4(magic) + 16(Guid) + 4(type) + 4(ipLen) + 4(port) + 4(dataLen) + 4(checksum)
                throw new InvalidDataException("Packet too short or null");

            using (var ms = new MemoryStream(data))
            using (var reader = new BinaryReader(ms))
            {
                // Проверка magic number
                var magic = reader.ReadUInt32();
                if (magic != MAGIC_NUMBER)
                    throw new InvalidDataException("Invalid packet format");

                // Чтение UserId
                var guidBytes = reader.ReadBytes(16);
                var userId = new Guid(guidBytes);

                // Чтение типа сообщения
                var type = (MessageType)reader.ReadInt32();

                // Чтение длинны TargetIp 
                var ipLength = reader.ReadInt32();
                // чтение TargetIp N байт 
                string targetIp = null;
                if (ipLength > 0)
                    targetIp = Encoding.UTF8.GetString(reader.ReadBytes(ipLength));

                // Чтение TargetPort 4 
                var targetPort = reader.ReadInt32();

                // Чтение длины данных
                var dataLength = reader.ReadInt32();

                // Чтение данных
                byte[] packetData = null;
                if (dataLength > 0)
                    packetData = reader.ReadBytes(dataLength);

                //// Чтение и проверка контрольной суммы
                //var storedChecksum = reader.ReadInt32();
                //var dataForChecksum = new byte[data.Length - 4];
                //Buffer.BlockCopy(data, 0, dataForChecksum, 0, dataForChecksum.Length);
                //var calculatedChecksum = ComputeChecksum(dataForChecksum);

                //if (storedChecksum != calculatedChecksum)
                //    throw new InvalidDataException("Data corruption detected: checksum mismatch");

                return new DataPacket
                {
                    UserId = userId,
                    Type = type,
                    TargetIp = targetIp,
                    TargetPort = targetPort,
                    Data = packetData,
                };
            }
        }

        ///// <summary>
        ///// Вычисление контрольной суммы
        ///// </summary>
        //private static int ComputeChecksum(byte[] data, int offset, int count)
        //{
        //    using (var md5 = MD5.Create())
        //    {
        //        var hash = md5.ComputeHash(data, offset, count);
        //        return BitConverter.ToInt32(hash, 0);
        //    }
        //}

        /// <summary>
        /// Получение данных как строки
        /// </summary>
        public string GetDataAsString()
        {
            return Data != null ? Encoding.UTF8.GetString(Data) : null;
        }
        private static int ComputeChecksum(byte[] data)
        {
            using (var md5 = MD5.Create())
            {
                var hash = md5.ComputeHash(data);
                return BitConverter.ToInt32(hash, 0);
            }
        }
    }
}