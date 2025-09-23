using System;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using TcpToWebSocketProxyGUI.Models;

namespace TcpToWebSocketProxyGUI
{
    public partial class AddPortMappingDialog : Window
    {
        public PortMapping Mapping { get; private set; }

        public AddPortMappingDialog()
        {
            InitializeComponent();
            Mapping = new PortMapping();

            // Подписываемся на события изменения текста для обновления превью
            txtListenPort.TextChanged += UpdatePreview;
            txtTargetIp.TextChanged += UpdatePreview;
            txtTargetPort.TextChanged += UpdatePreview;
        }

        private void UpdatePreview(object sender, TextChangedEventArgs e)
        {
            // Обновляем превью маппинга
            string listenPort = string.IsNullOrEmpty(txtListenPort.Text) ? "Port" : txtListenPort.Text;
            string targetIp = string.IsNullOrEmpty(txtTargetIp.Text) ? "IP" : txtTargetIp.Text;
            string targetPort = string.IsNullOrEmpty(txtTargetPort.Text) ? "Port" : txtTargetPort.Text;

            txtPreview.Text = $"{listenPort} → {targetIp}:{targetPort}";
        }

        private void BtnOk_Click(object sender, RoutedEventArgs e)
        {
            // Валидация данных
            if (!IsValidPort(txtListenPort.Text, out int listenPort))
            {
                MessageBox.Show("Please enter a valid listen port (1-65535)", "Validation Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                txtListenPort.Focus();
                txtListenPort.SelectAll();
                return;
            }

            if (!IsValidIpAddress(txtTargetIp.Text))
            {
                MessageBox.Show("Please enter a valid IP address", "Validation Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                txtTargetIp.Focus();
                txtTargetIp.SelectAll();
                return;
            }

            if (!IsValidPort(txtTargetPort.Text, out int targetPort))
            {
                MessageBox.Show("Please enter a valid target port (1-65535)", "Validation Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                txtTargetPort.Focus();
                txtTargetPort.SelectAll();
                return;
            }

            // Создание объекта маппинга
            Mapping = new PortMapping
            {
                ListenPort = listenPort,
                TargetIp = txtTargetIp.Text.Trim(),
                TargetPort = targetPort
            };

            DialogResult = true;
            Close();
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private bool IsValidPort(string portText, out int port)
        {
            port = 0;
            if (string.IsNullOrWhiteSpace(portText))
                return false;

            if (int.TryParse(portText, out port))
            {
                return port > 0 && port <= 65535;
            }
            return false;
        }

        private bool IsValidIpAddress(string ip)
        {
            if (string.IsNullOrWhiteSpace(ip))
                return false;

            ip = ip.Trim();

            // Проверка IPv4 адреса
            var ipPattern = @"^((25[0-5]|2[0-4][0-9]|[01]?[0-9][0-9]?)\.){3}(25[0-5]|2[0-4][0-9]|[01]?[0-9][0-9]?)$";
            if (Regex.IsMatch(ip, ipPattern))
                return true;

            // Проверка localhost
            if (ip.Equals("localhost", StringComparison.OrdinalIgnoreCase))
                return true;

            // Проверка 127.0.0.1
            if (ip == "127.0.0.1")
                return true;

            return false;
        }

        // Разрешаем ввод только цифр в поля портов
        private void Numeric_PreviewTextInput(object sender, TextCompositionEventArgs e)
        {
            var regex = new Regex("[^0-9]+");
            e.Handled = regex.IsMatch(e.Text);
        }

        // Обработка Paste для числовых полей - альтернативный способ
        private void TextBox_Pasting(object sender, DataObjectPastingEventArgs e)
        {
            if (e.DataObject.GetDataPresent(typeof(string)))
            {
                string text = (string)e.DataObject.GetData(typeof(string));
                if (!IsNumeric(text))
                {
                    e.CancelCommand();
                }
            }
            else
            {
                e.CancelCommand();
            }
        }

        private bool IsNumeric(string text)
        {
            return int.TryParse(text, out _);
        }

        protected override void OnActivated(EventArgs e)
        {
            base.OnActivated(e);
            txtListenPort.Focus();
        }
    }
}