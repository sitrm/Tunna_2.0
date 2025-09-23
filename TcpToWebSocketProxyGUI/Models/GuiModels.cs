using System.ComponentModel;

namespace TcpToWebSocketProxyGUI.Models
{
    public class PortMapping : INotifyPropertyChanged
    {
        private int _listenPort;
        private string _targetIp;
        private int _targetPort;

        public int ListenPort
        {
            get => _listenPort;
            set
            {
                _listenPort = value;
                OnPropertyChanged(nameof(ListenPort));
            }
        }

        public string TargetIp
        {
            get => _targetIp;
            set
            {
                _targetIp = value;
                OnPropertyChanged(nameof(TargetIp));
            }
        }

        public int TargetPort
        {
            get => _targetPort;
            set
            {
                _targetPort = value;
                OnPropertyChanged(nameof(TargetPort));
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;

        protected virtual void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        public override string ToString()
        {
            return $"{ListenPort} → {TargetIp}:{TargetPort}";
        }
    }

    public class ClientInfo : INotifyPropertyChanged
    {
        private string _id;
        private string _target;
        private string _status;
        private System.ConsoleColor _color;
        private string _localEndpoint;
        private string _remoteEndpoint;
        private DateTime _connectionTime;
        private long _bytesSent;
        private long _bytesReceived;

        public string Id
        {
            get => _id;
            set
            {
                _id = value;
                OnPropertyChanged(nameof(Id));
            }
        }

        public string Target
        {
            get => _target;
            set
            {
                _target = value;
                OnPropertyChanged(nameof(Target));
            }
        }

        public string Status
        {
            get => _status;
            set
            {
                _status = value;
                OnPropertyChanged(nameof(Status));
            }
        }

        public System.ConsoleColor Color
        {
            get => _color;
            set
            {
                _color = value;
                OnPropertyChanged(nameof(Color));
            }
        }
        public string LocalEndpoint
        {
            get => _localEndpoint;
            set
            {
                _localEndpoint = value;
                OnPropertyChanged(nameof(LocalEndpoint));
            }
        }

        public string RemoteEndpoint
        {
            get => _remoteEndpoint;
            set
            {
                _remoteEndpoint = value;
                OnPropertyChanged(nameof(RemoteEndpoint));
            }
        }

        public DateTime ConnectionTime
        {
            get => _connectionTime;
            set
            {
                _connectionTime = value;
                OnPropertyChanged(nameof(ConnectionTime));
                OnPropertyChanged(nameof(ConnectionDuration));
            }
        }

        public string ConnectionDuration
        {
            get
            {
                if (ConnectionTime == DateTime.MinValue) return "N/A";
                var duration = DateTime.Now - ConnectionTime;
                return $"{(int)duration.TotalMinutes}m {duration.Seconds}s";
            }
        }

        public long BytesSent
        {
            get => _bytesSent;
            set
            {
                _bytesSent = value;
                OnPropertyChanged(nameof(BytesSent));
                OnPropertyChanged(nameof(BytesSentFormatted));
            }
        }

        public long BytesReceived
        {
            get => _bytesReceived;
            set
            {
                _bytesReceived = value;
                OnPropertyChanged(nameof(BytesReceived));
                OnPropertyChanged(nameof(BytesReceivedFormatted));
            }
        }
        public string BytesSentFormatted => FormatBytes(BytesSent);
        public string BytesReceivedFormatted => FormatBytes(BytesReceived);
        public string TotalBytesFormatted => FormatBytes(BytesSent + BytesReceived);
        private string FormatBytes(long bytes)
        {
            string[] suffixes = { "B", "KB", "MB", "GB" };
            int counter = 0;
            decimal number = bytes;
            while (Math.Round(number / 1024) >= 1)
            {
                number /= 1024;
                counter++;
            }
            return $"{number:n1} {suffixes[counter]}";
        }

        public event PropertyChangedEventHandler PropertyChanged;

        protected virtual void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
        public void UpdateDuration()
        {
            OnPropertyChanged(nameof(ConnectionDuration));
        }
    }

    
}