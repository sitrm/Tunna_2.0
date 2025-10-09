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

        [JsonPropertyName("bufferSize")]
        public int BufferSize { get; set; } = 32768;

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