using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace TcpToWebSocketProxyGUI.Converters
{
    public class StatusToColorConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is string status)
            {
                return status.ToLower() switch
                {
                    "connected" => Brushes.Green,
                    "disconnected" => Brushes.Red,
                    "error" => Brushes.OrangeRed,
                    _ => Brushes.Gray
                };
            }
            return Brushes.Gray;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}