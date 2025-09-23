using System;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Threading.Tasks;
using TcpToWebSocketProxy;
using TcpToWebSocketProxyGUI.Models;
using TcpToWebSocketProxyGUI.Services;

namespace TcpToWebSocketProxyGUI
{
    public partial class MainWindow : Window
    {
        private ProxyServer _proxyServer;
        private ObservableCollection<PortMapping> _portMappings;
        private ObservableCollection<ClientInfo> _connectedClients;

        private SimpleClientService _clientService;
        private ILoggerService _loggerService;

        public MainWindow()
        {
            InitializeComponent();            // класс MainWindow вызывает скомпилированный ранее код XAML, строит gui
            InitializeLogger();
            InitializeCollections();
            LoadDefaultConfig();
            InitializeServices();
        }

        private void InitializeServices()
        {

            _clientService = new SimpleClientService(Dispatcher);
            dgClients.ItemsSource = _clientService.Clients;

            // Подписываемся на изменение коллекции клиентов
            _clientService.Clients.CollectionChanged += (s, e) =>
            {
                UpdateConnectedCount();
            };
        }
        private void InitializeLogger()
        {
            _loggerService = new LoggerService(LogToRichTextBox, Dispatcher);
        }
        private void InitializeCollections()
        {
            _portMappings = new ObservableCollection<PortMapping>();
            _connectedClients = new ObservableCollection<ClientInfo>();
            

            dgPortMappings.ItemsSource = _portMappings;
            dgClients.ItemsSource = _connectedClients;
        }

        private void LoadDefaultConfig()
        {
            // Загрузка конфигурации по умолчанию
            _portMappings.Add(new PortMapping { ListenPort = 1234, TargetIp = "192.168.181.134", TargetPort = 1433 });
            _portMappings.Add(new PortMapping { ListenPort = 9999, TargetIp = "127.0.0.1", TargetPort = 8888 });
        }

        private void UpdateConnectedCount()
        {
            Dispatcher.Invoke(() =>
            {
                txtConnectedCount.Text = _clientService.Clients.Count.ToString();
            });
        }

        private void LogToRichTextBox(string message, Color color)
        {
            Dispatcher.Invoke(() =>
            {
                var paragraph = new Paragraph();
                paragraph.Inlines.Add(new Run(message));
                paragraph.Foreground = new SolidColorBrush(color);

                rtbLog.Document.Blocks.Add(paragraph);
                rtbLog.ScrollToEnd();
                
            });
        }
        private async void BtnStart_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // Обновление конфигурации
                //var config = new ProxyConfig();
                // Здесь нужно добавить метод для обновления конфигурации

                _proxyServer = new ProxyServer(_loggerService, _clientService);
                await _proxyServer.Start();

                UpdateStatus("Running", Colors.Green);
                btnStart.IsEnabled = false;
                btnStop.IsEnabled = true;

                _loggerService.LogSuccess("Server started successfully");
            }
            catch (Exception ex)
            {
                _loggerService.LogError($"Error starting server: {ex.Message}");
            }
        }

        private async void BtnStop_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_proxyServer != null)
                {
                    await _proxyServer.Stop();
                    _proxyServer = null;
                }

                // Очищаем список клиентов при остановке сервера
                _clientService.ClearAll();

                UpdateStatus("Stopped", Colors.Red);
                btnStart.IsEnabled = true;
                btnStop.IsEnabled = false;

                _loggerService.LogWarning("Server stopped");
            }
            catch (Exception ex)
            {
                _loggerService.LogError($"Error stopping server: {ex.Message}");
            }
        }
      
        private void UpdateStatus(string status, Color color)
        {
            Dispatcher.Invoke(() =>
            {
                txtStatus.Text = status;
                txtStatus.Foreground = new SolidColorBrush(color);
            });
        }


        private void BtnAddMapping_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new AddPortMappingDialog();
            if (dialog.ShowDialog() == true && dialog.Mapping != null)
            {
                // Проверяем, нет ли уже порта с таким номером
                foreach (var existingMapping in _portMappings)
                {
                    if (existingMapping.ListenPort == dialog.Mapping.ListenPort)
                    {
                        MessageBox.Show($"Port {dialog.Mapping.ListenPort} is already in use!",
                            "Duplicate Port", MessageBoxButton.OK, MessageBoxImage.Warning);
                        return;
                    }
                }

                _portMappings.Add(dialog.Mapping);
                _loggerService.LogMessage($"Added port mapping: {dialog.Mapping}", Colors.Blue);
            }
        }

        private void BtnRemoveMapping_Click(object sender, RoutedEventArgs e)
        {
            if (dgPortMappings.SelectedItem is PortMapping mapping)
            {
                var result = MessageBox.Show($"Remove port mapping: {mapping}?",
                    "Confirm Removal", MessageBoxButton.YesNo, MessageBoxImage.Question);

                if (result == MessageBoxResult.Yes)
                {
                    _portMappings.Remove(mapping);
                    _loggerService.LogMessage($"Removed port mapping: {mapping}", Colors.Orange);
                }
            }
            else
            {
                MessageBox.Show("Please select a port mapping to remove",
                    "No Selection", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        private void BtnClearLog_Click(object sender, RoutedEventArgs e)
        {
            rtbLog.Document.Blocks.Clear();
           
        }

        protected override async void OnClosed(EventArgs e)
        {
            if (_proxyServer != null)
            {
                await _proxyServer.Stop();
            }
            base.OnClosed(e);
        }
    }
}