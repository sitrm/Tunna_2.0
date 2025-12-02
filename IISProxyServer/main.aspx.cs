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

        // connectionId → TcpClient
        private static readonly ConcurrentDictionary<Guid, TcpClient> _tcpClients =
            new ConcurrentDictionary<Guid, TcpClient>();

        // connectionId → WebSocket
        private static readonly ConcurrentDictionary<Guid, WebSocket> _webSockets =
            new ConcurrentDictionary<Guid, WebSocket>();

        // connectionId → (TargetIp, TargetPort) — для уведомлений
        private static readonly ConcurrentDictionary<Guid, ConnectionTarget> _connectionTargets =
            new ConcurrentDictionary<Guid, ConnectionTarget>();

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

            try
            {
                while (_webSocket.State == WebSocketState.Open)
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
                                DataPacket packet = DataPacket.Deserialize(completeMessage);
                                await ProcessPacketAsync(_webSocket, packet, connectionId);
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
                // Очистка ресурсов
                WebSocket tempWebSocket = null;
                TcpClient tcpClient = null;
                ConnectionTarget conntarget = null;

                _webSockets.TryRemove(connectionId, out tempWebSocket);

                if (_tcpClients.TryRemove(connectionId, out tcpClient))
                {
                    try { tcpClient.Close(); }
                    catch { }
                }

                _connectionTargets.TryRemove(connectionId, out conntarget);

                if (_webSocket.State == WebSocketState.Open)
                {
                    try
                    {
                         _webSocket.CloseAsync(
                            WebSocketCloseStatus.NormalClosure,
                            "Closed",
                            CancellationToken.None);
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
                        await ForwardToTcpServer(connectionId, packet);
                        break;

                    case MessageType.Error:
                        await ProcessError(connectionId, packet);
                        break;

                    case MessageType.Disconnect:
                        await ProcessDisconnect(connectionId, packet);
                        break;
                }
            }
            catch (Exception ex)
            {
                SendErrorResponse(connectionId, packet, ex);
            }
        }

        //---------------------------------------------------------------------------------------------------
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
        private async Task ReadFromTcpServer(Guid connectionId, TcpClient tcpClient, DataPacket packet)
        {
            Guid userId = packet.UserId;
            var stream = tcpClient.GetStream();

            // Используем стандартный массив вместо ArrayPool
            byte[] buffer = new byte[SIZE_BUF];

            try
            {
                WebSocket ws = null;
                ConnectionTarget target = null;

                while (tcpClient.Connected)
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

                    if (!_webSockets.TryGetValue(connectionId, out ws) || ws.State != WebSocketState.Open)
                        break;

                    if (!_connectionTargets.TryGetValue(connectionId, out target))
                        break;

                    // Копируем данные в новый массив нужного размера
                    byte[] receivedData = new byte[bytesRead];
                    Array.Copy(buffer, 0, receivedData, 0, bytesRead);

                    var responsePacket = new DataPacket(
                        userId,
                        MessageType.Binary,
                        receivedData,
                        target.Ip,
                        target.Port
                    );

                    var serialized = responsePacket.Serialize();

                    try
                    {
                        await ws.SendAsync(
                            new ArraySegment<byte>(serialized),
                            WebSocketMessageType.Binary,
                            true,
                            CancellationToken.None
                        );
                    }
                    catch
                    {
                        break; // WebSocket мёртв
                    }
                }
            }
            finally
            {
                RemoveTcpClient(connectionId);
                // Массив buffer будет автоматически удален сборщиком мусора
            }
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

                    _connectionTargets[connectionId] = new ConnectionTarget
                    {
                        Ip = packet.TargetIp,
                        Port = packet.TargetPort
                    };

                    // Запускаем читатель
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
        private async Task ProcessError(Guid connectionId, DataPacket packet)
        {
            string errorMessage = Encoding.UTF8.GetString(packet.Data);
            //System.Diagnostics.Trace.TraceError($"Error from client {packet.UserId}: {errorMessage}");
        }

        //----------------------------------------------------------------------------------------------------
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
                await ws.SendAsync(
                    new ArraySegment<byte>(data),
                    WebSocketMessageType.Binary,
                    true,
                    CancellationToken.None);
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
                await ws.SendAsync(
                    new ArraySegment<byte>(data),
                    WebSocketMessageType.Binary,
                    true,
                    CancellationToken.None);
            }
            catch { }
        }

        //----------------------------------------------------------------------------------------------------
        private async Task SendTcpDisconnectNotification(WebSocket ws, string ip, int port)
        {
            try
            {
                var packet = new DataPacket(
                    Guid.Empty,
                    MessageType.Disconnect,
                    Encoding.UTF8.GetBytes("TCP connection closed:"),
                    ip,
                    port
                );

                var data = packet.Serialize();
                await ws.SendAsync(
                    new ArraySegment<byte>(data),
                    WebSocketMessageType.Binary,
                    true,
                    CancellationToken.None);
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
                Task.Run(async () =>
                    await SendTcpDisconnectNotification(ws, conntarget.Ip, conntarget.Port));
            }

            _connectionTargets.TryRemove(connectionId, out conntarget);
        }

        //-----------------------------------------------------------------------
        private async Task CleanupWebSocketResources(WebSocket webSocket)
        {
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
                catch { }
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