using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

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
    /// Тип IP-адреса
    /// </summary>
    public enum IpAddressType : byte
    {
        None = 0,
        IPv4 = 1,
        IPv6 = 2
    }
}
