using TcpToWebSocketProxy;            // ProxyServer
using System;
using System.Threading.Tasks;


namespace TcpToWebSocketProxy
{
    public class Program
    {
        public static async Task Main()
        {
            var proxy = new ProxyServer();
            await proxy.Start();

            Console.WriteLine("Press any key to stop the server...");
            Console.ReadKey();

            await proxy.Stop();
        }
    }
}