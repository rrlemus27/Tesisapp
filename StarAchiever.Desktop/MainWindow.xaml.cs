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
        brdError.Visibility = Visibility.Collapsed;
        btnLogin.Content = "Conectando...";
        btnLogin.IsEnabled = false;

        try
        {
            var res = await ApiService.LoginAsync(txtUsuario.Text.Trim(), txtClave.Password);

            if (res.Exito)
            {
                // Abre el dashboard y cierra el login
                var dash = new DashboardWindow();
                dash.Show();
                Close();
            }
            else
            {
                // Mostramos el error real de la API (código + mensaje).
                var detalle = string.IsNullOrWhiteSpace(res.Mensaje) ? res.Contenido : res.Mensaje;
                lblError.Text = res.Codigo == 401
                    ? $"Credenciales inválidas. Verifica usuario y contraseña. (Error 401: {detalle})"
                    : $"No se pudo iniciar sesión. (Error {res.Codigo}: {detalle})";
                brdError.Visibility = Visibility.Visible;
            }
        }
        catch (Exception ex)
        {
            lblError.Text = $"No se pudo conectar con la API. ¿Está corriendo en localhost:7173? ({ex.Message})";
            brdError.Visibility = Visibility.Visible;
        }
        finally
        {
            btnLogin.Content = "Ingresar";
            btnLogin.IsEnabled = true;
        }
    }
}
