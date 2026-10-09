using System.Configuration;
using System.Data;
using System.Windows;
using System.Windows.Threading;

namespace StarAchiever.Desktop
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        public App()
        {
            // Red de seguridad: si algo no previsto falla (p. ej. la API devuelve un dato con un
            // formato inesperado), se avisa y la app sigue abierta en lugar de cerrarse de golpe.
            DispatcherUnhandledException += (_, e) =>
            {
                e.Handled = true;
                MessageBox.Show(
                    "Ocurrió un error inesperado y la última acción no se completó.\n\n" +
                    $"Detalle: {e.Exception.Message}\n\nPuedes seguir usando la aplicación.",
                    "Star-Achiever", MessageBoxButton.OK, MessageBoxImage.Warning);
            };
        }
    }

}
