using System.Windows;
using System.Windows.Input;

namespace StarAchiever.Desktop;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    // Permite arrastrar la ventana desde la barra superior
    private void Barra_Mover(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }

    private void Cerrar_Click(object sender, RoutedEventArgs e) => Close();

    private async void Login_Click(object sender, RoutedEventArgs e)
    {
        lblError.Visibility = Visibility.Collapsed;
        btnLogin.Content = "CONECTANDO...";
        btnLogin.IsEnabled = false;

        try
        {
            var ok = await ApiService.LoginAsync(txtUsuario.Text.Trim(), txtClave.Password);

            if (ok)
            {
                // Abre el dashboard y cierra el login
                var dash = new DashboardWindow();
                dash.Show();
                Close();
            }
            else
            {
                lblError.Text = "Credenciales inválidas. Verifica usuario y contraseña.";
                lblError.Visibility = Visibility.Visible;
            }
        }
        catch
        {
            lblError.Text = "No se pudo conectar con la API. ¿Está corriendo en localhost:7173?";
            lblError.Visibility = Visibility.Visible;
        }
        finally
        {
            btnLogin.Content = "INGRESAR";
            btnLogin.IsEnabled = true;
        }
    }
}