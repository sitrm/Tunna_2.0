using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace UtilDataPacket
{

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
            TargetIp = targetIp;   // 4
            TargetPort = targetPort;   // N
            Data = data;        // N
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
                writer.Write(TargetIp?.Length ?? 0);
                if (!string.IsNullOrEmpty(TargetIp))
                    writer.Write(Encoding.UTF8.GetBytes(TargetIp));

                // TargetPort (4 байта)
                writer.Write(TargetPort);

                // Длина данных (4 байта)
                writer.Write(Data?.Length ?? 0);

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
                if (ipLength > 0 || ipLength < 1024)
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
        /// <summary>
        /// Получение данных как строки
        /// </summary>
        public string GetDataAsString()
        {
            return Data != null ? Encoding.UTF8.GetString(Data) : null;
        }
        /// <summary>
        /// Вычисление контрольной суммы с использованием MD5
        /// </summary>
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
