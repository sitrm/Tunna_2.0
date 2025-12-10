using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Net.Sockets;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace UtilDataPacket
{

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
        private const string EncryptionKeyBase64 = "hVgR/6pAo0PfrxGX2YeliYg+6TS//N/xGaxzwoMPmxk="; // 256-bit ключ
        private static readonly byte[] EncryptionKey = Convert.FromBase64String(EncryptionKeyBase64);

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
                return EncryptPacket(plainPacket);
            }
        }

        /// <summary>
        /// Десериализация пакета из массива байт
        /// </summary>
        public static DataPacket Deserialize(byte[] data)
        {
            if (data == null || data.Length < 33)
                // Минимальный размер: 4(magic) + 16(Guid) + 4(type) + 1(ipver) + 4(port) + 4(dataLen) + 4(checksum)
                throw new InvalidDataException("Packet too short or null");

            var decrypted = DecryptPacket(data);

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
        private static byte[] EncryptPacket(byte[] plainPacket)
        {
            if (plainPacket == null) throw new ArgumentNullException("plainPacket");

            using (var aes = Aes.Create())
            using (var hmac = new HMACSHA256(EncryptionKey))
            {
                aes.Key = EncryptionKey;
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
        private static byte[] DecryptPacket(byte[] encryptedPacket)
        {
            if (encryptedPacket == null || encryptedPacket.Length < 1 + 16 + 32)
                throw new InvalidDataException("Encrypted packet too short");

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

            using (var hmac = new HMACSHA256(EncryptionKey))
            {
                byte[] expectedTag = hmac.ComputeHash(Combine(iv, cipher));
                if (!expectedTag.SequenceEqual(tag))
                    throw new InvalidDataException("Encrypted packet HMAC mismatch");
            }

            using (var aes = Aes.Create())
            {
                aes.Key = EncryptionKey;
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
