using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Newtonsoft.Json.Linq;

namespace StarAchiever.Desktop;

public partial class MainWindow : Window
{
    // Evita dos intentos de login a la vez (doble clic, Enter repetido…).
    private bool _iniciando;

    public MainWindow() : this(null) { }

    // 'aviso': mensaje a mostrar al abrir (p. ej. «tu sesión expiró»).
    public MainWindow(string? aviso)
    {
        InitializeComponent();
        if (!string.IsNullOrWhiteSpace(aviso)) MostrarError(aviso);
        Loaded += async (_, _) =>
        {
            txtUsuario.Focus();
            await ComprobarServidor();
        };
    }

    // Permite arrastrar la ventana desde la barra superior
    private void Barra_Mover(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }

    private void Cerrar_Click(object sender, RoutedEventArgs e) => Close();

    // ---- Mostrar / ocultar contraseña ----
    private bool ClaveVisible => txtClaveVisible.Visibility == Visibility.Visible;
    private string ClaveEscrita => ClaveVisible ? txtClaveVisible.Text : txtClave.Password;

    private void VerClave_Click(object sender, RoutedEventArgs e)
    {
        if (ClaveVisible)
        {
            txtClave.Password = txtClaveVisible.Text;
            txtClaveVisible.Clear(); // no dejamos la contraseña en texto plano en un control oculto
            txtClaveVisible.Visibility = Visibility.Collapsed;
            txtClave.Visibility = Visibility.Visible;
            icoVerClave.Text = ""; // ojo
            btnVerClave.ToolTip = "Mostrar contraseña";
            txtClave.Focus();
        }
        else
        {
            txtClaveVisible.Text = txtClave.Password;
            txtClave.Clear();
            txtClave.Visibility = Visibility.Collapsed;
            txtClaveVisible.Visibility = Visibility.Visible;
            icoVerClave.Text = ""; // ojo tachado
            btnVerClave.ToolTip = "Ocultar contraseña";
            txtClaveVisible.Focus();
            txtClaveVisible.CaretIndex = txtClaveVisible.Text.Length;
        }
    }

    private void EnfocarClave()
    {
        if (ClaveVisible) { txtClaveVisible.Focus(); txtClaveVisible.SelectAll(); }
        else { txtClave.Focus(); txtClave.SelectAll(); }
    }

    // ---- Login ---- (el botón es IsDefault: Enter en cualquier campo inicia sesión)
    private async void Login_Click(object sender, RoutedEventArgs e)
    {
        if (_iniciando) return;
        brdError.Visibility = Visibility.Collapsed;

        // Espacios sobrantes: se quitan del usuario (y se ve en el campo).
        var usuario = txtUsuario.Text.Trim();
        if (usuario != txtUsuario.Text) txtUsuario.Text = usuario;
        var clave = ClaveEscrita;

        var error = ValidarCampos(usuario, clave);
        if (error != null)
        {
            MostrarError(error.Value.mensaje);
            if (error.Value.enUsuario) { txtUsuario.Focus(); txtUsuario.SelectAll(); }
            else EnfocarClave();
            return;
        }

        _iniciando = true;
        BloquearFormulario(true);
        ApiResult? fallo = null;
        try
        {
            var res = await ApiService.LoginAsync(usuario, clave);

            if (res.Exito)
            {
                // Abre el dashboard y cierra el login
                var dash = new DashboardWindow();
                dash.Show();
                Close();
                return;
            }

            fallo = res;
            MostrarError(MensajeLogin(res, clave));
        }
        catch (Exception)
        {
            // No mostramos detalles técnicos: podrían incluir datos de la petición.
            MostrarError("Ocurrió un error inesperado al iniciar sesión. Inténtalo de nuevo.");
        }
        finally
        {
            _iniciando = false;
            BloquearFormulario(false);
        }

        // Ya con los campos habilitados: foco donde hay que corregir y estado real de la API.
        if (fallo?.Codigo == 401) EnfocarClave();
        else txtUsuario.Focus();
        if (fallo != null) await ComprobarServidor();
    }

    // Revisa los campos antes de llamar a la API. Devuelve el mensaje y qué campo enfocar.
    private static (string mensaje, bool enUsuario)? ValidarCampos(string usuario, string clave)
    {
        if (usuario.Length == 0 && clave.Length == 0)
            return ("Escribe tu usuario o correo y tu contraseña.", true);
        if (usuario.Length == 0)
            return ("Escribe tu usuario o correo.", true);
        if (usuario.Any(char.IsWhiteSpace))
            return ("El usuario o correo no puede tener espacios en medio.", true);
        if (usuario.Length < 2)
            return ("El usuario o correo es demasiado corto.", true);
        if (clave.Length == 0)
            return ("Escribe tu contraseña.", false);
        if (string.IsNullOrWhiteSpace(clave))
            return ("La contraseña no puede estar formada solo por espacios.", false);
        return null;
    }

    // Mensaje claro según la respuesta, sin repetir la contraseña ni mostrar el cuerpo crudo.
    private static string MensajeLogin(ApiResult res, string clave)
    {
        switch (res.Codigo)
        {
            case 0:
                return res.Mensaje; // sin conexión / tiempo agotado (ya viene explicado)
            case 401:
                var texto = "Usuario o contraseña incorrectos, o la cuenta está desactivada.";
                if (clave != clave.Trim())
                    texto += " Ojo: tu contraseña empieza o termina con un espacio; revisa si es intencional.";
                return texto;
            case 429:
                return "Demasiados intentos seguidos. Espera un momento y vuelve a intentarlo.";
            case >= 500:
                return "El servidor tuvo un problema al iniciar sesión. Inténtalo de nuevo en unos minutos.";
            case 200:
                return res.Mensaje; // respondió OK pero sin token o con un formato inesperado
            default:
                return $"No se pudo iniciar sesión (HTTP {res.Codigo}).";
        }
    }

    private void BloquearFormulario(bool ocupado)
    {
        txtUsuario.IsEnabled = !ocupado;
        txtClave.IsEnabled = !ocupado;
        txtClaveVisible.IsEnabled = !ocupado;
        btnVerClave.IsEnabled = !ocupado;
        btnLogin.IsEnabled = !ocupado;
        btnLogin.Content = ocupado ? Ui.Cargando("Verificando…", Paleta.AmarilloTexto) : "Ingresar";
    }

    private void MostrarError(string mensaje)
    {
        lblError.Text = mensaje;
        brdError.Visibility = Visibility.Visible;
    }

    // Consulta GET api/health y muestra el estado real del servidor y de la base de datos.
    private async System.Threading.Tasks.Task ComprobarServidor()
    {
        var res = await ApiService.ComprobarServidorAsync();
        string texto;
        System.Windows.Media.Brush color;

        if (res.SinRespuesta)
        {
            texto = $"Sin conexión con la API ({ApiService.Servidor})";
            color = Paleta.Coral;
        }
        else if (res.Exito)
        {
            texto = $"API conectada · {ApiService.Servidor}";
            color = Paleta.Verde;
        }
        else if (res.Codigo == 503 && LeerCampo(res.Contenido, "database") is { } db && db != "ok")
        {
            texto = "La API responde, pero su base de datos no está disponible";
            color = Paleta.Coral;
        }
        else
        {
            // Responde, pero sin el servicio de salud (o con otro código): al menos hay servidor.
            texto = $"API en {ApiService.Servidor} (estado sin confirmar, HTTP {res.Codigo})";
            color = Paleta.Amarillo;
        }

        dotApi.Fill = color;
        lblApi.Text = texto;
    }

    private static string? LeerCampo(string json, string campo)
    {
        try { return JObject.Parse(json)[campo]?.ToString(); }
        catch (Newtonsoft.Json.JsonException) { return null; }
    }
}
