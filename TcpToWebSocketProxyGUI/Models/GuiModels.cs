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
        private string _clientAddress; // Новое поле: адрес клиента
        private DateTime _connectionTime;

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
        public string ClientAddress
        {
            get => _clientAddress;
            set
            {
                _clientAddress = value;
                OnPropertyChanged(nameof(ClientAddress));
            }
        }

        public DateTime ConnectionTime
        {
            get => _connectionTime;
            set
            {
                _connectionTime = value;
                OnPropertyChanged(nameof(ConnectionTime));
            }
        }

        public string ConnectionInfo => $"Connected at {ConnectionTime:HH:mm:ss}";

        public event PropertyChangedEventHandler PropertyChanged;

        protected virtual void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
        
    }

    
}