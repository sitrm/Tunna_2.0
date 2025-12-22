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
using UtilDataPacket;
using System.Web.WebSockets;
using System.Collections.Concurrent;
using System.Linq;
using System.Security.Cryptography;
using System.IdentityModel.Protocols.WSTrust;
// Изменения:
// - Добавил статический RSACryptoServiceProvider в класс для генерации RSA пары.
// - В ProcessWebSocket: Добавил byte[] aesKey = null; per connection.
// - В receive loop: Deserialize(completeMessage, aesKey)
// - В ProcessPacketAsync: Добавил обработку handshake types.
//   Если aesKey == null и type не handshake, error.
//   Для HandShakeRequest: Отправить публичный ключ (null key).
//   Для EncryptedSymmetricKey: Расшифровать AES ключ RSA, установить aesKey, отправить HandShakeComplete (с новым aesKey).
// - DataPacket: Тот же, что и в клиенте (общий).
// - Добавил проверки, чтобы требовать handshake перед обычными пакетами.

namespace WSP
{
    public partial class WebSocketProxy : System.Web.UI.Page
    {
        int SIZE_BUF = 65400;

        private class ConnectionTarget
        {
            public string Ip { get; set; }
            public int Port { get; set; }
        }
        private class ClientConnection
        {
            public WebSocket WebSocket { get; set; }
            public TcpClient TcpClient { get; set; }
            public ConnectionTarget Target { get; set; }
            public Task ReadingTask { get; set; }
        }
        // clientId (packet.UserId) → ClientConnection
        private static readonly ConcurrentDictionary<Guid, ClientConnection> _clients =
            new ConcurrentDictionary<Guid, ClientConnection>();

        // WebSocket → List<clientId> (для очистки при отключении)
        private static readonly ConcurrentDictionary<WebSocket, List<Guid>> _webSocketClients =
            new ConcurrentDictionary<WebSocket, List<Guid>>();
        // Новый: AES ключ per WebSocket connection
        private static readonly ConcurrentDictionary<WebSocket, byte[]>     _aesKeys =
            new ConcurrentDictionary<WebSocket, byte[]>();
        // Новый: RSA провайдер для сервера (генерируется статически)
        private static readonly RSACryptoServiceProvider rsaProvider;
        static WebSocketProxy()
        {
            rsaProvider = new RSACryptoServiceProvider(2048);
        }

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
            WebSocket webSocket = context.WebSocket;
            List<Guid> clientIdsForThisSocket = new List<Guid>();
            _webSocketClients[webSocket] = clientIdsForThisSocket;
            // Новый: AES ключ per WebSocket connection
            _aesKeys[webSocket] = null;
            
            
            
            try
            {
                while (webSocket.State == WebSocketState.Open)
                {
                    try
                    {
                        // Используем стандартный массив вместо ArrayPool
                        byte[] buffer = new byte[SIZE_BUF];
                        using (var memoryStream = new MemoryStream())
                        {
                            WebSocketReceiveResult result;
                            do
                            {
                                result = await webSocket.ReceiveAsync(
                                    new ArraySegment<byte>(buffer),
                                    CancellationToken.None);

                                if (result.MessageType == WebSocketMessageType.Close)
                                    break;

                                memoryStream.Write(buffer, 0, result.Count);
                            }
                            while (!result.EndOfMessage && webSocket.State == WebSocketState.Open);

                            if (result.MessageType == WebSocketMessageType.Close)
                                break;

                            byte[] completeMessage = memoryStream.ToArray();

                            if (completeMessage.Length > 0)
                            {
                                byte[] aesKey = _aesKeys[webSocket];
                                DataPacket packet = DataPacket.Deserialize(completeMessage, aesKey);
                                await ProcessPacketAsync(webSocket, packet, clientIdsForThisSocket);

                            }
                        }
                    }
                    catch (InvalidDataException ex)
                    {
                        System.Diagnostics.Trace.TraceError("Invalid packet: " + ex.Message);
                        break;
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
                // Очистка всех клиентов для этого WebSocket
                CleanupWebSocketClients(webSocket, clientIdsForThisSocket);

                if (webSocket.State == WebSocketState.Open)
                {
                    try
                    {
                        webSocket.CloseAsync(
                            WebSocketCloseStatus.NormalClosure,
                            "Closed",
                            CancellationToken.None);
                    }
                    catch { }
                }

                List<Guid> removedList;
                _webSocketClients.TryRemove(webSocket, out removedList);
                byte[] removedKey;
                _aesKeys.TryRemove(webSocket, out removedKey);
            }
        }

        //----------------------------------------------------------------------------------------------------
        private async Task ProcessPacketAsync(WebSocket webSocket, DataPacket packet, List<Guid> clientIdsForThisSocket)
        {
            byte[] aesKey = _aesKeys[webSocket];
            // Требуем handshake перед обычными пакетами
            if (aesKey == null && packet.Type != MessageType.HandShakeRequest && packet.Type != MessageType.EncryptedSymmetricKey)
            {
                throw new InvalidOperationException("Handshake required before processing this message type");
            }

            try
            {
                switch (packet.Type)
                {
                    case MessageType.HandShakeRequest:
                        if (aesKey != null) throw new InvalidOperationException("Handshake already completed");
                        // Отправляем публичный RSA ключ (без шифрования)
                        string publicKeyXml = rsaProvider.ToXmlString(false);
                        var pubKeyPacket = new DataPacket(Guid.Empty, MessageType.PublicKey, Encoding.UTF8.GetBytes(publicKeyXml), "", 0);
                        byte[] pubSerialized = pubKeyPacket.Serialize(null);
                        await webSocket.SendAsync(new ArraySegment<byte>(pubSerialized), WebSocketMessageType.Binary, true, CancellationToken.None);
                        break;

                    case MessageType.EncryptedSymmetricKey:
                        //if (aesKey != null) throw new InvalidOperationException("Handshake already completed");
                        // Расшифровываем AES ключ приватным RSA
                        byte[] encryptedKey = packet.Data;
                        byte[] decryptedKey = rsaProvider.Decrypt(encryptedKey, false);
                        if (decryptedKey.Length != 32) throw new InvalidDataException("Invalid AES key length");
                        _aesKeys[webSocket] = decryptedKey;  //!!!!!!!!!!!!!!!!!!!  Обновляем глобально
                        //aesKey = _aesKeys[webSocket];
                        // Отправляем подтверждение (зашифрованное новым AES)
                        var completePacket = new DataPacket(Guid.Empty, MessageType.HandShakeComplete, Encoding.UTF8.GetBytes("OK"), "", 0);
                        byte[] completeSer = completePacket.Serialize(decryptedKey);
                        await webSocket.SendAsync(new ArraySegment<byte>(completeSer), WebSocketMessageType.Binary, true, CancellationToken.None);
                        break;
                    case MessageType.Binary:
                        await ForwardToTcpServer(webSocket, packet, clientIdsForThisSocket);
                        break;

                    case MessageType.Error:
                        await ProcessError(webSocket, packet);
                        break;

                    case MessageType.Disconnect:
                        await ProcessDisconnect(packet.UserId, webSocket);
                        break;
                }
            }
            catch (Exception ex)
            {
                SendErrorResponse(webSocket, packet, ex);
            }
        }

        //---------------------------------------------------------------------------------------------------
        private async Task ForwardToTcpServer(WebSocket webSocket, DataPacket packet, List<Guid> clientIdsForThisSocket)
        {
            ClientConnection clientConn = GetOrCreateClientConnection(webSocket, packet, clientIdsForThisSocket);
            if (clientConn == null || clientConn.TcpClient == null) return;

            try
            {
                if (!clientConn.TcpClient.Connected)
                {
                    RemoveClient(packet.UserId, webSocket);
                    return;
                }

                NetworkStream stream = clientConn.TcpClient.GetStream();
                if (packet.Data != null && packet.Data.Length > 0)
                {
                    await stream.WriteAsync(packet.Data, 0, packet.Data.Length);
                    await stream.FlushAsync();
                }
            }
            catch (Exception ex)
            {
                SendErrorResponse(webSocket, packet, ex);
                RemoveClient(packet.UserId, webSocket);
            }
        }

        //---------------------------------------------------------------------------------------------------
        private async Task ReadFromTcpServer(Guid clientId, TcpClient tcpClient, WebSocket webSocket, ConnectionTarget target)
        {
            NetworkStream stream = null;
            try
            {
                stream = tcpClient.GetStream();

                // Используем стандартный массив вместо ArrayPool
                byte[] buffer = new byte[SIZE_BUF];

                while (tcpClient.Connected && webSocket.State == WebSocketState.Open)
                {
                    int bytesRead;
                    try
                    {
                        bytesRead = await stream.ReadAsync(buffer, 0, SIZE_BUF);
                    }
                    catch (IOException)
                    {
                        break; // TCP разорван
                    }

                    if (bytesRead == 0) break;

                    // Копируем данные
                    byte[] receivedData = new byte[bytesRead];
                    Array.Copy(buffer, 0, receivedData, 0, bytesRead);

                    DataPacket responsePacket = new DataPacket(
                        clientId,
                        MessageType.Binary,
                        receivedData,
                        target.Ip,
                        target.Port
                    );
                    byte[] aesKey = _aesKeys[webSocket];
                    byte[] serialized = responsePacket.Serialize(aesKey);

                    try
                    {
                        if (webSocket.State == WebSocketState.Open)
                        {
                            await webSocket.SendAsync(
                                new ArraySegment<byte>(serialized),
                                WebSocketMessageType.Binary,
                                true,
                                CancellationToken.None
                            );
                        }
                        else
                        {
                            break; // WebSocket закрыт
                        }
                    }
                    catch
                    {
                        break; // WebSocket мёртв
                    }
                }
            }
            finally
            {
                // RemoveClient вызывается только здесь, не дублируется
                RemoveClient(clientId, webSocket);
            }
        }

        //----------------------------------------------------------------------------------------------------
        private ClientConnection GetOrCreateClientConnection(WebSocket webSocket, DataPacket packet, List<Guid> clientIdsForThisSocket)
        {
            Guid clientId = packet.UserId;

            // Проверяем, существует ли уже соединение
            ClientConnection existingConn;
            if (_clients.TryGetValue(clientId, out existingConn))
            {
                return existingConn;
            }

            // Создаем новое соединение
            TcpClient client = new TcpClient();
            try
            {
                client.Connect(packet.TargetIp, packet.TargetPort);

                ConnectionTarget target = new ConnectionTarget
                {
                    Ip = packet.TargetIp,
                    Port = packet.TargetPort
                };

                // Создаем объект подключения
                ClientConnection clientConn = new ClientConnection
                {
                    WebSocket = webSocket,
                    TcpClient = client,
                    Target = target
                };

                // Добавляем clientId в список для этого WebSocket ПЕРЕД добавлением в словарь
                lock (clientIdsForThisSocket)
                {
                    clientIdsForThisSocket.Add(clientId);
                }

                // Добавляем в словарь и запускаем задачу чтения только если успешно добавили
                if (_clients.TryAdd(clientId, clientConn))
                {
                    // Передаем aesKey в ReadFromTcpServer
                    // Запускаем задачу чтения из TCP (без Task.Run, так как это IO-bound операция)
                    clientConn.ReadingTask = ReadFromTcpServer(clientId, client, webSocket, target);
                    return clientConn;
                }
                else
                {
                    // Кто-то другой уже создал соединение
                    client.Close();
                    client.Dispose();
                    return _clients.TryGetValue(clientId, out existingConn) ? existingConn : null;
                }
            }
            catch
            {
                // При ошибке удаляем из списка и закрываем клиент
                lock (clientIdsForThisSocket)
                {
                    clientIdsForThisSocket.Remove(clientId);
                }
                try
                {
                    client.Close();
                    client.Dispose();
                }
                catch { }
                throw;
            }
        }

        //----------------------------------------------------------------------------------------------------
        private async Task ProcessError(WebSocket webSocket, DataPacket packet)
        {
            string errorMessage = Encoding.UTF8.GetString(packet.Data);
            System.Diagnostics.Trace.TraceError("Error from client " + packet.UserId + ": " + errorMessage);
        }

        //----------------------------------------------------------------------------------------------------
        private async Task ProcessDisconnect(Guid clientId, WebSocket webSocket)
        {
            // Получаем данные перед удалением
            ClientConnection clientConn;
            ConnectionTarget target = null;
            byte[] aesKey = _aesKeys[webSocket];

            if (_clients.TryGetValue(clientId, out clientConn))
            {
                target = clientConn.Target;
                // aesKey per ws, but since ws known, but to simplify, assume aesKey available (from context)
            }

            RemoveClient(clientId, webSocket);

            // Отправляем подтверждение отключения после удаления
            if (webSocket != null && webSocket.State == WebSocketState.Open && target != null)
            {
                try
                {
                    DataPacket response = new DataPacket(
                        clientId,
                        MessageType.Disconnect,
                        Encoding.UTF8.GetBytes("TCP connection closed successfully!!!"),
                        target.Ip,
                        target.Port
                    );

                    byte[] data = response.Serialize(aesKey);
                    if (webSocket.State == WebSocketState.Open)
                    {
                        await webSocket.SendAsync(
                            new ArraySegment<byte>(data),
                            WebSocketMessageType.Binary,
                            true,
                            CancellationToken.None);
                    }
                }
                catch { }
            }
        }

        //-----------------------------------------------------------------------------------------------------
        private async Task SendErrorResponse(WebSocket webSocket, DataPacket packet, Exception ex)
        {
            if (webSocket == null || webSocket.State != WebSocketState.Open)
                return;

            try
            {
                DataPacket errorPacket = new DataPacket(
                    packet.UserId,
                    MessageType.Error,
                    Encoding.UTF8.GetBytes("Error: " + ex.Message),
                    packet.TargetIp,
                    packet.TargetPort
                );
                byte[] aesKey = _aesKeys[webSocket];
                byte[] data = errorPacket.Serialize(aesKey);

                // Двойная проверка состояния перед отправкой
                if (webSocket.State == WebSocketState.Open)
                {
                    await webSocket.SendAsync(
                        new ArraySegment<byte>(data),
                        WebSocketMessageType.Binary,
                        true,
                        CancellationToken.None);
                }
            }
            catch { }
        }

        //----------------------------------------------------------------------------------------------------
        private async Task SendTcpDisconnectNotification(Guid clientId, WebSocket webSocket, ConnectionTarget target)
        {
            if (webSocket == null || webSocket.State != WebSocketState.Open)
                return;

            try
            {
                DataPacket packet = new DataPacket(
                    clientId,
                    MessageType.Disconnect,
                    Encoding.UTF8.GetBytes("TCP connection closed"),
                    target.Ip,
                    target.Port
                );
                byte[] aesKey = _aesKeys[webSocket];
                byte[] data = packet.Serialize(aesKey);
                if (webSocket.State == WebSocketState.Open)
                {
                    await webSocket.SendAsync(
                        new ArraySegment<byte>(data),
                        WebSocketMessageType.Binary,
                        true,
                        CancellationToken.None);
                }
            }
            catch { }
        }

        //-----------------------------------------------------------------------
        private void RemoveClient(Guid clientId, WebSocket webSocket)
        {
            ClientConnection clientConn;
            if (_clients.TryRemove(clientId, out clientConn))
            {
                // Отправляем уведомление об отключении ПЕРЕД закрытием соединения
                if (clientConn.WebSocket != null && clientConn.WebSocket.State == WebSocketState.Open)
                {
                    // Используем fire-and-forget, но сохраняем ссылку на WebSocket
                    var ws = clientConn.WebSocket;
                    var target = clientConn.Target;
                    Task.Run(async () =>
                    {
                        try
                        {
                            await SendTcpDisconnectNotification(clientId, ws, target);
                        }
                        catch { }
                    });
                }

                try
                {
                    if (clientConn.TcpClient != null)
                    {
                        clientConn.TcpClient.Close();
                        clientConn.TcpClient.Dispose();
                    }
                }
                catch { }
            }
        }

        //-----------------------------------------------------------------------
        private async Task CleanupWebSocketClients(WebSocket webSocket, List<Guid> clientIds)
        {
            // Удаляем всех клиентов для этого WebSocket
            lock (clientIds)
            {
                foreach (Guid clientId in clientIds)
                {
                    RemoveClient(clientId, webSocket);
                }
                clientIds.Clear();
            }
        }
    }
}

//****************************************************************************************************************

//using System;
//using System.Collections.Generic;
//using System.Linq;
//using System.Net;
//using System.Net.Sockets;
//using System.Security.Cryptography;
//using System.Text;
//using System.Threading.Tasks;

namespace UtilDataPacket
{
    public enum MessageType : int
    {
        Text = 1,
        Binary = 2,
        File = 3,
        Error = 4,
        Disconnect = 5,
        // Новые типы для handshake
        HandShakeRequest = 10,
        PublicKey = 11,
        EncryptedSymmetricKey = 12,
        HandShakeComplete = 13
        // close !!! когда от iis приходит пакет чтобы закрыть соединение в случае чего либо 80 стр 
    }
    /// <summary>
    /// Версия IP-адреса
    /// </summary>
    public enum IpAddressType : byte
    {
        None = 0,
        IPv4 = 1,
        IPv6 = 2
    }
    /// <summary>
    /// Вспомогательный класс для хранения результата парсинга IP-адреса
    /// </summary>
    internal class IpParseResult
    {
        public IpAddressType Type { get; set; }
        public byte[] Bytes { get; set; }

        public IpParseResult(IpAddressType type, byte[] bytes)
        {
            Type = type;
            Bytes = bytes;
        }
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
        public DataPacket() { }
        public DataPacket(Guid userId, MessageType type, byte[] data, string targetIp, int targetPort) : this()
        {
            UserId = userId;    // 16 
            Type = type;        // 4
            TargetIp = targetIp;   // 4
            TargetPort = targetPort;   // N
            Data = data;        // N
        }
        public DataPacket(Guid userId, MessageType type, string text, string targetIp, int targetPort) : this(userId, type, Encoding.UTF8.GetBytes(text), targetIp, targetPort)
        {
        }

        // <summary>
        /// Определяет тип IP-адреса и преобразует его в массив байт
        /// </summary>
        private IpParseResult ParseIpAddress(string ip)
        {
            if (string.IsNullOrEmpty(ip))
                return new IpParseResult(IpAddressType.None, new byte[0]);
            IPAddress address = null;
            if (IPAddress.TryParse(ip, out address))
            {
                if (address.AddressFamily == AddressFamily.InterNetwork)
                {
                    // IPv4 - 4 байта
                    return new IpParseResult(IpAddressType.IPv4, address.GetAddressBytes());
                }
                else if (address.AddressFamily == AddressFamily.InterNetworkV6)
                {
                    // IPv6 - 16 байт
                    return new IpParseResult(IpAddressType.IPv6, address.GetAddressBytes());
                }
            }

            throw new ArgumentException("Invalid IP address format:" + ip);
        }
        /// <summary>
        /// Преобразует массив байт обратно в строку IP-адреса
        /// </summary>
        private string BytesToIpAddress(IpAddressType ipType, byte[] bytes)
        {
            if (ipType == IpAddressType.None || bytes == null || bytes.Length == 0)
                return null;

            try
            {
                IPAddress address = new IPAddress(bytes);
                return address.ToString();
            }
            catch
            {
                return null;
            }
        }
        /// <summary>
        /// Сериализация пакета в массив байт
        /// </summary>
        public byte[] Serialize(byte[] encryptionKey)
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

                // Тип IP-адреса (1 байт)
                var IpParseResult = ParseIpAddress(TargetIp);

                writer.Write((byte)IpParseResult.Type); // 1
                // IP-адрес (4 байта для IPv4 или 16 байт для IPv6)
                if (IpParseResult.Type != IpAddressType.None && IpParseResult.Bytes.Length > 0)
                {
                    writer.Write(IpParseResult.Bytes);
                }
                // TargetPort (4 байта)
                writer.Write(TargetPort);

                // Длина данных (4 байта)
                writer.Write(Data != null ? Data.Length : 0);

                // Данные (переменная длина)
                if (Data != null && Data.Length > 0)
                    writer.Write(Data);

                var plainPacket = ms.ToArray();
                // Если ключ null, не шифруем (для handshake)
                if (encryptionKey == null)
                    return plainPacket;
                else
                    return EncryptPacket(plainPacket, encryptionKey);
            }
        }

        /// <summary>
        /// Десериализация пакета из массива байт
        /// </summary>
        public static DataPacket Deserialize(byte[] data, byte[] encryptionKey)
        {
            if (data == null || data.Length < 33)
                // Минимальный размер: 4(magic) + 16(Guid) + 4(type) + 1(ipver) + 4(port) + 4(dataLen) + 4(checksum)
                throw new InvalidDataException("Packet too short or null");

            byte[] decrypted;
            if (encryptionKey == null)
                decrypted = data;
            else
                decrypted = DecryptPacket(data, encryptionKey);

            using (var ms = new MemoryStream(decrypted))
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

                // Чтение типа IP-адреса
                var ipType = (IpAddressType)reader.ReadByte();
                // Чтение IP-адреса в зависимости от типа
                string targetIp = null;
                byte[] ipBytes = null;

                switch (ipType)
                {
                    case IpAddressType.IPv4:
                        ipBytes = reader.ReadBytes(4); // 4 байта для IPv4
                        break;
                    case IpAddressType.IPv6:
                        ipBytes = reader.ReadBytes(16); // 16 байт для IPv6
                        break;
                    case IpAddressType.None:
                        // Нет IP-адреса
                        break;
                    default:
                        throw new InvalidDataException("Unknown IP address type:" + ipType);
                }
                if (ipBytes != null && ipBytes.Length > 0)
                {
                    targetIp = new IPAddress(ipBytes).ToString();
                }
                // Чтение TargetPort 4 
                var targetPort = reader.ReadInt32();
                // Чтение длины данных
                var dataLength = reader.ReadInt32();
                // Чтение данных
                byte[] packetData = null;
                if (dataLength > 0)
                    packetData = reader.ReadBytes(dataLength);

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
        /// <summary>
        /// Получение данных как строки
        /// </summary>
        public string GetDataAsString()
        {
            return Data != null ? Encoding.UTF8.GetString(Data) : null;
        }

        /// <summary>
        /// Шифрует пакет целиком (AES-256 CBC + HMAC-SHA256)
        /// Формат: [1 байт длина IV][IV][cipher][HMAC(32)]
        /// </summary>
        private static byte[] EncryptPacket(byte[] plainPacket, byte[] key)
        {
            if (plainPacket == null) throw new ArgumentNullException("plainPacket");
            if (key == null || key.Length != 32) throw new ArgumentException("Invalid encryption key");

            using (var aes = Aes.Create())
            using (var hmac = new HMACSHA256(key))
            {
                aes.Key = key;
                aes.Mode = CipherMode.CBC;
                aes.Padding = PaddingMode.PKCS7;
                aes.GenerateIV();

                using (var encryptor = aes.CreateEncryptor())
                {
                    byte[] cipher = encryptor.TransformFinalBlock(plainPacket, 0, plainPacket.Length);
                    byte[] iv = aes.IV;

                    byte[] hmacData = Combine(iv, cipher);
                    byte[] tag = hmac.ComputeHash(hmacData);

                    byte[] result = new byte[1 + iv.Length + cipher.Length + tag.Length];
                    result[0] = (byte)iv.Length;
                    Array.Copy(iv, 0, result, 1, iv.Length);
                    Array.Copy(cipher, 0, result, 1 + iv.Length, cipher.Length);
                    Array.Copy(tag, 0, result, 1 + iv.Length + cipher.Length, tag.Length);
                    return result;
                }
            }
        }

        /// <summary>
        /// Расшифровывает пакет целиком (AES-256 CBC + HMAC-SHA256)
        /// Ожидаемый формат: [1 байт длина IV][IV][cipher][HMAC(32)]
        /// </summary>
        private static byte[] DecryptPacket(byte[] encryptedPacket, byte[] key)
        {
            if (encryptedPacket == null || encryptedPacket.Length < 1 + 16 + 32)
                throw new InvalidDataException("Encrypted packet too short");
            if (key == null || key.Length != 32) throw new ArgumentException("Invalid encryption key");

            int ivLength = encryptedPacket[0];
            if (ivLength <= 0 || encryptedPacket.Length < 1 + ivLength + 32)
                throw new InvalidDataException("Invalid IV length in encrypted packet");

            int cipherLength = encryptedPacket.Length - 1 - ivLength - 32;
            if (cipherLength <= 0)
                throw new InvalidDataException("Encrypted packet has no cipher data");

            byte[] iv = new byte[ivLength];
            Array.Copy(encryptedPacket, 1, iv, 0, ivLength);

            byte[] cipher = new byte[cipherLength];
            Array.Copy(encryptedPacket, 1 + ivLength, cipher, 0, cipherLength);

            byte[] tag = new byte[32];
            Array.Copy(encryptedPacket, 1 + ivLength + cipherLength, tag, 0, 32);

            using (var hmac = new HMACSHA256(key))
            {
                byte[] expectedTag = hmac.ComputeHash(Combine(iv, cipher));
                if (!expectedTag.SequenceEqual(tag))
                    throw new InvalidDataException("Encrypted packet HMAC mismatch");
            }

            using (var aes = Aes.Create())
            {
                aes.Key = key;
                aes.IV = iv;
                aes.Mode = CipherMode.CBC;
                aes.Padding = PaddingMode.PKCS7;

                using (var decryptor = aes.CreateDecryptor())
                {
                    return decryptor.TransformFinalBlock(cipher, 0, cipher.Length);
                }
            }
        }
        private static byte[] Combine(params byte[][] buffers)
        {
            int total = buffers.Where(b => b != null).Sum(b => b.Length);
            byte[] result = new byte[total];
            int offset = 0;
            foreach (var buffer in buffers.Where(b => b != null))
            {
                Array.Copy(buffer, 0, result, offset, buffer.Length);
                offset += buffer.Length;
            }

            return result;
        }
    }
}
