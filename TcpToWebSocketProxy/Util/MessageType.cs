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
        Disconnect = 5
        // close !!! когда от iis приходит пакет чтобы закрыть соединение в случае чего либо 80 стр 
    }
}
