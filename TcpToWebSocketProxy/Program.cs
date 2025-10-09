using System;
using System.Threading.Tasks;

namespace TcpToWebSocketProxy
{
    public class Program
    {
        public static async Task Main(string[] args)
        {
            try
            {
                Console.WriteLine("TCP to WebSocket Proxy starting...");
                Console.WriteLine("Loading configuration...");

                // Пытаемся загрузить конфигурацию из разных мест
                ProxyConfig config = null;
                string configFile = "proxyconfig.json";

                // Проверяем аргументы командной строки
                if (args.Length > 0 && !string.IsNullOrWhiteSpace(args[0]))
                {
                    configFile = args[0];
                    Console.WriteLine($"Using config file from command line: {configFile}");
                }

                try
                {
                    config = ProxyConfig.LoadFromFile(configFile);
                }
                catch (FileNotFoundException)
                {
                    Console.WriteLine($"Configuration file '{configFile}' not found.");
                    Console.WriteLine("Please create a proxyconfig.json file with your configuration.");
                    Console.WriteLine("Press any key to exit...");
                    Console.ReadKey();
                    return;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error loading configuration: {ex.Message}");
                    Console.WriteLine("Press any key to exit...");
                    Console.ReadKey();
                    return;
                }

                // Выводим информацию о конфигурации
                Console.WriteLine($"WebSocket URL: {config.WebSocketUrl}");
                Console.WriteLine($"Buffer size: {config.BufferSize}");
                Console.WriteLine("Port mappings:");
                foreach (var mapping in config.PortMappings)
                {
                    Console.WriteLine($"  {mapping}");
                }

                // Создаем и запускаем прокси-сервер
                using (var proxy = new ProxyServer(config))
                {
                    await proxy.Start();

                    Console.WriteLine("\nProxy server is running. Press 'Q' to stop...");

                    // Ожидаем команду остановки
                    while (true)
                    {
                        var key = Console.ReadKey(true);
                        if (key.Key == ConsoleKey.Q)
                        {
                            break;
                        }
                    }

                    Console.WriteLine("\nStopping proxy server...");
                    await proxy.Stop();
                }

                Console.WriteLine("Proxy server stopped.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Fatal error: {ex.Message}");
                Console.WriteLine("Press any key to exit...");
                Console.ReadKey();
            }
        }
    }
}