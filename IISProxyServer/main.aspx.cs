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

namespace WSP
{
    public partial class WebSocketProxy : System.Web.UI.Page
    {
        // Словарь для хранения активных TCP соединений по ключу (targetIp:targetPort)
        private static readonly Dictionary<string, TcpClient> _tcpClients = new Dictionary<string, TcpClient>();
        // Объект для синхронизации многопоточного доступа к словарю
        private static readonly object _lock = new object();
        //----------------------------------------------------------------------------------------------------
        protected void Page_Load(object sender, EventArgs e)
        {
            if (Context.IsWebSocketRequest)
            {
                Context.AcceptWebSocketRequest(ProcessWebSocket);
            }
        }
        //----------------------------------------------------------------------------------------------------
        private async Task ProcessWebSocket(AspNetWebSocketContext context)
        {
            WebSocket webSocket = context.WebSocket;
            Guid connectionId = Guid.NewGuid();          // Уникальный ID для этого WebSocket соединения

            try
            {
                byte[] buffer = new byte[4096];
                while (webSocket.State == WebSocketState.Open)
                {
                    // 🔄 Ожидаем данные от клиента асинхронно 
                    ArraySegment<byte> segment = new ArraySegment<byte>(buffer);
                    WebSocketReceiveResult result = await webSocket.ReceiveAsync(segment, CancellationToken.None);

                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure,
                            "Closed by client", CancellationToken.None);
                        break;
                    }

                    // Копируем ТОЛЬКО полученные данные
                    byte[] receivedData = new byte[result.Count];
                    Array.Copy(buffer, 0, receivedData, 0, result.Count);

                    // Десериализуем пакет
                    DataPacket packet = DataPacket.Deserialize(receivedData);

                    // Обрабатываем пакет в отдельном потоке
                    Task.Run(() => ProcessPacketAsync(webSocket, packet, connectionId));
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.TraceError("WebSocket error: " + ex.Message);
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
                        await ForwardToTcpServer(webSocket, packet);
                        break;

                    // case MessageType.Authentication:
                    //     // Обрабатываем аутентификацию
                    //     await ProcessAuthentication(webSocket, packet);
                    //     break;

                    case MessageType.Error:
                        // Обрабатываем ошибки
                        await ProcessError(webSocket, packet);
                        break;

                    case MessageType.Disconnect:
                        // Обрабатываем отключение - закрываем TCP соединение
                        await ProcessDisconnect(webSocket, packet);
                        break;
                }
            }
            catch (Exception ex)
            {
                SendErrorResponse(webSocket, packet, ex);
            }
        }

        //----------------------------------------------------------------------------------------------------
        private async Task ForwardToTcpServer(WebSocket webSocket, DataPacket packet)
        {
            string connectionKey = GetConnectionKey(packet.TargetIp, packet.TargetPort);
            TcpClient tcpClient = null;
            NetworkStream networkStream = null;

            try
            {
                // Получаем или создаем TCP соединение
                tcpClient = GetOrCreateTcpClient(webSocket, connectionKey, packet.TargetIp, packet.TargetPort);
                networkStream = tcpClient.GetStream();

                // Отправляем данные на TCP сервер
                await networkStream.WriteAsync(packet.Data, 0, packet.Data.Length);

                // Устанавливаем таймаут для чтения
                using (var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(3)))
                {
                    try
                    {
                        // Читаем ответ от TCP сервера асинхронно с таймаутом
                        byte[] responseBuffer = new byte[4096];
                        // Асинхронно читаем ответ
                        int bytesRead = await networkStream.ReadAsync(responseBuffer, 0, responseBuffer.Length, timeoutCts.Token);

                        if (bytesRead > 0)
                        {
                            // Подготавливаем ответные данные
                            byte[] responseData = new byte[bytesRead];
                            Array.Copy(responseBuffer, 0, responseData, 0, bytesRead);

                            // Создаем пакет ответа
                            DataPacket responsePacket = new DataPacket(
                                packet.UserId,
                                packet.Type,
                                responseData,
                                packet.TargetIp,
                                packet.TargetPort
                            );

                            // Отправляем ответ обратно через WebSocket
                            byte[] serializedResponse = responsePacket.Serialize();
                            await webSocket.SendAsync(
                                new ArraySegment<byte>(serializedResponse),
                                WebSocketMessageType.Binary,
                                true,
                                CancellationToken.None
                            );
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        // Таймаут чтения - это нормально, не все серверы отправляют ответ
                        System.Diagnostics.Trace.TraceWarning("Read timeout for TCP server: " + connectionKey);
                    }
                }
            }
            catch (Exception ex)
            {
                // Если соединение разорвано, удаляем его из кэша
                if (tcpClient != null && !tcpClient.Connected)
                {
                    RemoveTcpClient(webSocket, connectionKey);
                }

                // Отправляем ошибку клиенту
                SendErrorResponse(webSocket, packet, ex);
            }
        }

        private string GetConnectionKey(string targetIp, int targetPort)
        {
            return targetIp + ":" + targetPort;
        }
        //----------------------------------------------------------------------------------------------------
        private TcpClient GetOrCreateTcpClient(WebSocket webSocket, string connectionKey, string targetIp, int targetPort)
        {
            lock (_lock)
            {
                TcpClient tcpClient = null;

                // Пытаемся получить существующее соединение
                if (_tcpClients.TryGetValue(connectionKey, out tcpClient))
                {
                    // Проверяем, активно ли соединение
                    if (tcpClient != null && tcpClient.Connected)
                    {
                        return tcpClient;
                    }
                    else
                    {
                        // Удаляем неактивное соединение
                        if (tcpClient != null)
                        {
                            tcpClient.Close();
                        }
                        _tcpClients.Remove(connectionKey);
                    }
                }

                // Создаем новое соединение
                tcpClient = new TcpClient();
                tcpClient.Connect(targetIp, targetPort);
                _tcpClients[connectionKey] = tcpClient;

                // Запускаем мониторинг соединения
                Task.Run(() => MonitorTcpConnection(webSocket, connectionKey, tcpClient));

                return tcpClient;
            }
        }
        //----------------------------------------------------------------------------------------------------
        private async Task MonitorTcpConnection(WebSocket webSocket, string connectionKey, TcpClient tcpClient)
        {
            try
            {
                while (tcpClient.Connected)  // Пока соединение активно
                {
                    await Task.Delay(5000); // Проверяем каждые 5 секунд

                    // Проверяем соединение отправкой пустого пакета
                    if (!IsTcpClientConnected(tcpClient))
                    {
                        RemoveTcpClient(webSocket, connectionKey);    //Удаляем если отвалилось
                        break;
                    }
                }
            }
            catch
            {
                RemoveTcpClient(webSocket, connectionKey);    // При ошибки тоже удаляем 
            }
        }
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
        private void RemoveTcpClient(WebSocket webSocket, string connectionKey)
        {
            lock (_lock)
            {
                TcpClient tcpClient = null;
                if (_tcpClients.TryGetValue(connectionKey, out tcpClient))
                {
                    try
                    {
                        // Проверяем, было ли соединение активно перед закрытием
                        bool wasConnected = tcpClient.Connected;
                        tcpClient.Close();
                        // Отправляем уведомление на WebSocket о разрыве соединения
                        if (wasConnected)
                        {
                            SendTcpDisconnectNotification(webSocket, connectionKey);
                        }
                    }
                    catch
                    {
                        // Игнорируем ошибки при закрытии
                        SendTcpDisconnectNotification(webSocket, connectionKey);
                    }
                    finally
                    {
                        _tcpClients.Remove(connectionKey);
                    }
                }
            }
        }
        //----------------------------------------------------------------------------------------------------
        // private async Task ProcessAuthentication(WebSocket webSocket, DataPacket packet)
        // {
        //     DataPacket responsePacket = new DataPacket(
        //         packet.UserId,
        //         MessageType.Authentication,
        //         Encoding.UTF8.GetBytes("Authenticated"),
        //         packet.TargetIp,
        //         packet.TargetPort
        //     );

        //     byte[] responseData = responsePacket.Serialize();
        //     await webSocket.SendAsync(
        //         new ArraySegment<byte>(responseData),
        //         WebSocketMessageType.Binary,
        //         true,
        //         CancellationToken.None
        //     );
        // }
        //----------------------------------------------------------------------------------------------------
        private async Task ProcessError(WebSocket webSocket, DataPacket packet)
        {
            string errorMessage = Encoding.UTF8.GetString(packet.Data);
            System.Diagnostics.Trace.TraceError("Error from client " + packet.UserId + ": " + errorMessage);
        }
        //----------------------------------------------------------------------------------------------------
        // метод для обработки пакетов отключения
        private async Task ProcessDisconnect(WebSocket webSocket, DataPacket packet)
        {
            string connectionKey = GetConnectionKey(packet.TargetIp, packet.TargetPort);

            //System.Diagnostics.Trace.TraceInformation($"Received disconnect packet for {connectionKey}, UserId: {packet.UserId}");

            // Закрываем TCP соединение
            RemoveTcpClient(webSocket, connectionKey);

            // Отправляем подтверждение отключения
            DataPacket responsePacket = new DataPacket(
                packet.UserId,
                MessageType.Disconnect,
                Encoding.UTF8.GetBytes("TCP connection closed successfully!!!"),
                packet.TargetIp,
                packet.TargetPort
            );

            byte[] responseData = responsePacket.Serialize();
            await webSocket.SendAsync(
                new ArraySegment<byte>(responseData),
                WebSocketMessageType.Binary,
                true,
                CancellationToken.None
            );

            // System.Diagnostics.Trace.TraceInformation($"TCP connection closed for {connectionKey}");
        }
        //-----------------------------------------------------------------------------------------------------
        private async Task SendErrorResponse(WebSocket webSocket, DataPacket packet, Exception error)
        {
            try
            {
                DataPacket errorPacket = new DataPacket(
                    packet.UserId,
                    MessageType.Error,
                    Encoding.UTF8.GetBytes("Error forwarding data: " + error.Message),
                    packet.TargetIp,
                    packet.TargetPort
                );

                byte[] errorData = errorPacket.Serialize();
                await webSocket.SendAsync(
                    new ArraySegment<byte>(errorData),
                    WebSocketMessageType.Binary,
                    true,
                    CancellationToken.None
                );
            }
            catch (Exception sendError)
            {
                System.Diagnostics.Trace.TraceError("Error sending error response: " + sendError.Message);
            }
        }
        //----------------------------------------------------------------------------------------------------
        // Метод для отправки уведомления о разрыве TCP соединения
        private async void SendTcpDisconnectNotification(WebSocket webSocket, string connectionKey)
        {
            try
            {
                
                if (webSocket != null && webSocket.State == WebSocketState.Open)
                {
                    // Парсим connectionKey для получения IP и порта
                    var parts = connectionKey.Split(':');
                    int port = 0;
                    if (parts.Length == 2 && int.TryParse(parts[1], out port))
                    {
                        string ip = parts[0];

                        // Создаем пакет уведомления о разрыве соединения
                        DataPacket disconnectPacket = new DataPacket(
                            Guid.Empty, // Пустой GUID для системных уведомлений
                            MessageType.Disconnect,
                            Encoding.UTF8.GetBytes("TCP connection closed: " + connectionKey),
                            ip,
                            port
                        );

                        byte[] disconnectData = disconnectPacket.Serialize();

                        await webSocket.SendAsync(
                            new ArraySegment<byte>(disconnectData),
                            WebSocketMessageType.Binary,
                            true,
                            CancellationToken.None
                        );
                    }
                }
            }
            catch (Exception ex)
            {
                
                
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
                // Заголовок пакета (4 байта)
                //writer.Write(0xDEADBEEF); // Magic number для идентификации пакета 

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

                //// Контрольная сумма (4 байта)
                //var dataHash = ComputeChecksum(ms.ToArray(), 0, (int)ms.Length - 4);
                //writer.Write(dataHash);

                return ms.ToArray();
            }
        }

        /// <summary>
        /// Десериализация пакета из массива байт
        /// </summary>
        public static DataPacket Deserialize(byte[] data)
        {
            //if (data == null || data.Length < 28) // Минимальный размер: 4(magic) + 16(Guid) + 4(type) + 4(length)
            //    throw new InvalidDataException("Packet too short or null");

            using (var ms = new MemoryStream(data))
            using (var reader = new BinaryReader(ms))
            {
                // Проверка magic number
                //var magic = reader.ReadInt32();
                //if (magic != 0xDEADBEEF)
                //    throw new InvalidDataException("Invalid packet format

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
                //var actualChecksum = ComputeChecksum(data, 0, data.Length - 4);

                //if (storedChecksum != actualChecksum)
                //    throw new InvalidDataException("Data corruption detected");

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
    }
}