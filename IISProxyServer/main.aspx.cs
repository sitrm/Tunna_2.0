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
using System.Collections.Concurrent;

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
                                DataPacket packet = DataPacket.Deserialize(completeMessage);
                                await ProcessPacketAsync(webSocket, packet, clientIdsForThisSocket);
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
            }
        }

        //----------------------------------------------------------------------------------------------------
        private async Task ProcessPacketAsync(WebSocket webSocket, DataPacket packet, List<Guid> clientIdsForThisSocket)
        {
            try
            {
                switch (packet.Type)
                {
                    case MessageType.Binary:
                        await ForwardToTcpServer(webSocket, packet, clientIdsForThisSocket);
                        break;

                    case MessageType.Error:
                        await ProcessError(webSocket, packet);
                        break;

                    case MessageType.Disconnect:
                        await ProcessDisconnect(packet.UserId);
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
                    RemoveClient(packet.UserId);
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
                RemoveClient(packet.UserId);
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

                    byte[] serialized = responsePacket.Serialize();

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
                RemoveClient(clientId);
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
        private async Task ProcessDisconnect(Guid clientId)
        {
            // Получаем данные перед удалением
            ClientConnection clientConn;
            WebSocket ws = null;
            ConnectionTarget target = null;
            
            if (_clients.TryGetValue(clientId, out clientConn))
            {
                ws = clientConn.WebSocket;
                target = clientConn.Target;
            }
            
            RemoveClient(clientId);
            
            // Отправляем подтверждение отключения после удаления
            if (ws != null && ws.State == WebSocketState.Open && target != null)
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

                    byte[] data = response.Serialize();
                    if (ws.State == WebSocketState.Open)
                    {
                        await ws.SendAsync(
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

                byte[] data = errorPacket.Serialize();
                
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

                byte[] data = packet.Serialize();
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
        private void RemoveClient(Guid clientId)
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
                    _ = Task.Run(async () =>
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
                    RemoveClient(clientId);
                }
                clientIds.Clear();
            }
        }
    }
}

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

    [Serializable]
    public class DataPacket
    {
        private const uint MAGIC_NUMBER = 0xDEADBEEF;

        public Guid UserId { get; set; }
        public MessageType Type { get; set; }
        public byte[] Data { get; set; }
        public string TargetIp { get; set; }
        public int TargetPort { get; set; }

        public DataPacket() { }

        public DataPacket(Guid userId, MessageType type, byte[] data, string targetIp, int targetPort)
        {
            UserId = userId;
            Type = type;
            Data = data;
            TargetIp = targetIp;
            TargetPort = targetPort;
        }

        public DataPacket(Guid userId, MessageType type, string text, string targetIp, int targetPort)
            : this(userId, type, Encoding.UTF8.GetBytes(text), targetIp, targetPort)
        {
        }

        public byte[] Serialize()
        {
            using (var ms = new MemoryStream())
            using (var writer = new BinaryWriter(ms))
            {
                writer.Write(MAGIC_NUMBER);
                writer.Write(UserId.ToByteArray());
                writer.Write((int)Type);

                writer.Write(TargetIp != null ? TargetIp.Length : 0);
                if (!string.IsNullOrEmpty(TargetIp))
                    writer.Write(Encoding.UTF8.GetBytes(TargetIp));

                writer.Write(TargetPort);
                writer.Write(Data != null ? Data.Length : 0);

                if (Data != null && Data.Length > 0)
                    writer.Write(Data);

                return ms.ToArray();
            }
        }

        public static DataPacket Deserialize(byte[] data)
        {
            if (data == null || data.Length < 36)
                throw new InvalidDataException("Packet too short or null");

            using (var ms = new MemoryStream(data))
            using (var reader = new BinaryReader(ms))
            {
                var magic = reader.ReadUInt32();
                if (magic != MAGIC_NUMBER)
                    throw new InvalidDataException("Invalid packet format");

                var guidBytes = reader.ReadBytes(16);
                var userId = new Guid(guidBytes);

                var type = (MessageType)reader.ReadInt32();

                var ipLength = reader.ReadInt32();
                string targetIp = null;
                if (ipLength > 0)
                    targetIp = Encoding.UTF8.GetString(reader.ReadBytes(ipLength));

                var targetPort = reader.ReadInt32();

                var dataLength = reader.ReadInt32();
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

        public string GetDataAsString()
        {
            return Data != null ? Encoding.UTF8.GetString(Data) : null;
        }
    }
}

