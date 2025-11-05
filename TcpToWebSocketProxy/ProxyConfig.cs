using System.Text.Json;
using System.Text.Json.Serialization;

namespace TcpToWebSocketProxy
{
    public class ProxyConfig
    {
        [JsonPropertyName("webSocketUrl")]
        public string WebSocketUrl { get; set; } = "ws://localhost/WebsiteDeployment/webSocketProxyMult/main.aspx";

        [JsonPropertyName("portMappings")]
        public List<PortMapping> PortMappings { get; set; } = new List<PortMapping>();

        [JsonPropertyName("tcpBufferSize")]
        public int TcpBufferSize { get; set; } = 64 * 1024; // 64KB для TCP

        [JsonPropertyName("webSocketBufferSize")]
        public int WebSocketBufferSize { get; set; } = 4 * 1024; // 4KB для WebSocket

        [JsonPropertyName("maxWebSocketMessageSize")]
        public int MaxWebSocketMessageSize { get; set; } = 1024 * 1024; // 1MB максимум

        public static ProxyConfig LoadFromFile(string filePath = "proxyconfig.json")
        {
            if (!File.Exists(filePath))
            {
                throw new FileNotFoundException($"Configuration file not found: {filePath}");
            }

            try
            {
                var json = File.ReadAllText(filePath);
                var config = JsonSerializer.Deserialize<ProxyConfig>(json, new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                    WriteIndented = true
                });

                if (config == null)
                {
                    throw new InvalidOperationException("Failed to deserialize configuration file");
                }

                if (config.PortMappings == null || config.PortMappings.Count == 0)
                {
                    throw new InvalidOperationException("No port mappings defined in configuration");
                }

                // Валидация портов
                foreach (var mapping in config.PortMappings)
                {
                    if (mapping.ListenPort < 1 || mapping.ListenPort > 65535)
                    {
                        throw new InvalidOperationException($"Invalid listen port: {mapping.ListenPort}");
                    }
                    if (mapping.TargetPort < 1 || mapping.TargetPort > 65535)
                    {
                        throw new InvalidOperationException($"Invalid target port: {mapping.TargetPort}");
                    }
                    if (string.IsNullOrWhiteSpace(mapping.TargetIp))
                    {
                        throw new InvalidOperationException("Target IP cannot be empty");
                    }
                }
                // Дополнительная валидация размеров буферов
                //if (config.TcpBufferSize < 1024 || config.TcpBufferSize > 1024 * 1024) // 1KB - 1MB
                //{
                //    throw new InvalidOperationException($"TCP buffer size must be between 1KB and 1MB");
                //}

                //if (config.WebSocketBufferSize < 1024 || config.WebSocketBufferSize > 64 * 1024) // 1KB - 64KB
                //{
                //    throw new InvalidOperationException($"WebSocket buffer size must be between 1KB and 64KB");
                //}

                Console.WriteLine($"Configuration loaded successfully from: {filePath}");
                return config;
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException($"Invalid JSON configuration: {ex.Message}");
            }
        }
    }

    public class PortMapping
    {
        [JsonPropertyName("listenPort")]
        public int ListenPort { get; set; }

        [JsonPropertyName("targetIp")]
        public string TargetIp { get; set; } = string.Empty;

        [JsonPropertyName("targetPort")]
        public int TargetPort { get; set; }

        public override string ToString()
        {
            return $"Port {ListenPort} -> {TargetIp}:{TargetPort}";
        }
    }
}