# Tunna_2.0


Краткое описание
-----------------

Tunna_2.0 — набор простых прокси-сервисов для перенаправления TCP-трафика через WebSocket (и обратно) на основе Microsoft IIS. Проект содержит два основных компонента:

- `TcpToWebSocketProxy` — консольное приложение на .NET, которое слушает один или несколько TCP-портов и пересылает данные через WebSocket на веб-сервер/прокси.
- `IISProxyServer` — простая веб-обёртка (ASPX) для приёма WebSocket-соединений со стороны сервера (используется как приёмник сообщений и шлюз в целевые TCP-сервисы на стороне сервера).

Общая схема передачи данных:
------------------------

```
+------------+      +---------------------+      +-------------------+      +---------------------+
| TCP Client | ---> | TcpToWebSocketProxy | ---> |   WebSocket/IIS   | ---> | Target TCP Service  |
+------------+      +---------------------+      +-------------------+      +---------------------+
```

Конфигурация
-------------------------

Файл конфигурации для консольного прокси находится в

	`TcpToWebSocketProxy/proxyconfig.json`

Пример (включён в репозиторий):

```
{
	"webSocketUrl": "ws://localhost/WebsiteDeployment/webSocketProxyMult/main.aspx",
	"bufferSize": 16000,
	"portMappings": [
		{ "listenPort": 1234, "targetIp": "127.0.0.1", "targetPort": 1433 },
		{ "listenPort": 9999, "targetIp": "127.0.0.1", "targetPort": 8888 },
		{ "listenPort": 5222, "targetIp": "127.0.0.1", "targetPort": 5201 }
	]
}
```

Краткая инструкция по запуску
----------------------------

Требования:
- .NET SDK/Runtime (проект TargetFramework: net8.0)

Запуск из исходников (рекомендуется для отладки):

1. Откройте командную строку в папке `TcpToWebSocketProxy`.
2. При желании измените `proxyconfig.json` (например, `webSocketUrl` и сопоставления портов).
3. Запустите приложение через `dotnet run` или соберите и запустите исполняемый файл:

```cmd
cd TcpToWebSocketProxy
dotnet run --project TcpToWebSocketProxy.csproj
```

Вы также можете указать альтернативный путь к файлу конфигурации при запуске:

```cmd
dotnet run --project TcpToWebSocketProxy.csproj -- "C:\path\to\your\proxyconfig.json"
```

После успешного старта консоль выведет информацию о WebSocket URL, размере буфера и списке портов.
Нажмите `Q` в консоли, чтобы корректно остановить сервер.

Развёртывание IISProxyServer
----------------------------

`IISProxyServer` — это простая веб-страница/endpoint (ASPX). Разверните содержимое папки `IISProxyServer` в приложении IIS (или в виртуальной директории вашего веб-сервера). Убедитесь, что WebSocket поддерживается и включён в IIS (Windows feature).

