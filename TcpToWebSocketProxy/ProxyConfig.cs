using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TcpToWebSocketProxy
{
    internal class ProxyConfig
    {
        public Dictionary<int, (string TargetIp, int TargetPort)> PortMappings { get; } =
           new Dictionary<int, (string, int)>
           {
                { 1234, ("127.0.0.1", 1433) },
                { 9999, ("127.0.0.1",    8888) }
           };
    }
}
