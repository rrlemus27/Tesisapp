using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace StarAchiever.Desktop;

// Capa visual del panel: menú lateral y títulos de sección, ventana redimensionable y
// menú compacto, indicador de carga, notificaciones y paginación de las tablas.
// No contiene lógica de datos: solo decide qué se ve y cómo.
public partial class DashboardWindow
{
    // Menú compacto (solo iconos) cuando la ventana es angosta. Lo usa el estilo ItemMenu.
    public static readonly DependencyProperty CompactoProperty = DependencyProperty.Register(
        nameof(Compacto), typeof(bool), typeof(DashboardWindow), new PropertyMetadata(false));

    public bool Compacto
    {
        get => (bool)GetValue(CompactoProperty);
        set => SetValue(CompactoProperty, value);
    }

    private const double AnchoMenuCompacto = 1240;

    private (RadioButton nav, TabItem tab, string titulo, string descripcion)[] _secciones =
        Array.Empty<(RadioButton, TabItem, string, string)>();

    private DispatcherTimer? _demoraCarga;

    private void ConfigurarVista()
    {
        AjustarTamanoInicial();

        // Cada opción del menú lateral abre una sección (pestaña oculta de panelAdmin).
        _secciones = new[]
        {
            (navInicio, tabInicio, "Inicio", "Resumen de la plataforma con datos en vivo de la API."),
            (navUsuarios, tabUsuarios, "Usuarios", "Crea cuentas, edita datos y roles, y gestiona el acceso."),
            (navEstudiantes, tabEstudiantes, "Estudiantes", "Altas, bajas y sección de cada estudiante."),
            (navDocentes, tabDocentes, "Docentes", "Altas, bajas y clases asignadas a cada docente."),
            (navMaterias, tabMaterias, "Materias", "Catálogo de materias de la institución."),
            (navGrados, tabGrados, "Grados y secciones", "La estructura académica: grados y sus secciones."),
            (navPeriodos, tabPeriodos, "Períodos académicos", "Fechas de inicio y fin de cada período."),
            (navAsignaciones, tabAsignaciones, "Asignaciones", "Clases de los docentes y secciones de los estudiantes.")
        };
        foreach (var (nav, tab, _, _) in _secciones)
            nav.Checked += (_, _) =>
            {
                if (!ReferenceEquals(panelAdmin.SelectedItem, tab)) panelAdmin.SelectedItem = tab;
            };
        panelAdmin.SelectionChanged += SeccionCambiada;

        // El menú de secciones es del ADMIN; los demás roles solo tienen «Inicio».
        bool admin = ApiService.Rol == "ADMIN";
        menuAdmin.Visibility = admin ? Visibility.Visible : Visibility.Collapsed;
        if (admin) MostrarSeccion(tabInicio, animar: false);
        else
        {
            lblSeccionTitulo.Text = "Inicio";
            lblSeccionDescripcion.Text = ApiService.Rol switch
            {
                "DOCENTE" => "Tus materias, temas, preguntas y actividades.",
                "ESTUDIANTE" => "Tu progreso por tema.",
                _ => ""
            };
        }

        // Ventana: tamaño adaptable, menú compacto y ajuste al maximizar.
        SizeChanged += (_, _) => AdaptarTamano();
        StateChanged += (_, _) => AjustarMaximizado();
        Loaded += (_, _) => { AdaptarTamano(); AjustarMaximizado(); };

        // Indicador discreto de carga mientras hay peticiones a la API.
        EventHandler<PropertyChangedEventArgs> manejador = (_, _) => Dispatcher.BeginInvoke(ActualizarCarga);
        EstadoUi.StaticPropertyChanged += manejador;
        Closed += (_, _) => EstadoUi.StaticPropertyChanged -= manejador;
        ActualizarCarga();

        // Paginación automática de las tablas que pueden crecer mucho.
        Ui.Paginar(listaUsuarios, pagUsuarios);
        Ui.Paginar(listaEstAdmin, pagEstAdmin);
        Ui.Paginar(listaDocentes, pagDocentes);
        Ui.Paginar(listaMaterias, pagMaterias);
        Ui.Paginar(listaPeriodos, pagPeriodos);
        Ui.Paginar(listaAsignaciones, pagAsignaciones);
        Ui.Paginar(listaEstudiantesSeccion, pagEstudiantesSeccion);
    }

    // ===== Navegación =====

    private void SeccionCambiada(object sender, SelectionChangedEventArgs e)
    {
        // SelectionChanged también "sube" desde los combos y sub-pestañas internas.
        if (!ReferenceEquals(e.OriginalSource, panelAdmin)) return;
        if (panelAdmin.SelectedItem is TabItem tab) MostrarSeccion(tab, animar: true);
    }

    private void MostrarSeccion(TabItem tab, bool animar)
    {
        foreach (var (nav, t, titulo, descripcion) in _secciones)
        {
            if (!ReferenceEquals(t, tab)) continue;
            if (nav.IsChecked != true) nav.IsChecked = true;
            lblSeccionTitulo.Text = titulo;
            lblSeccionDescripcion.Text = descripcion;
        }
        // Las tarjetas de conteos son el panel principal: se ven en «Inicio».
        panelStats.Visibility = ReferenceEquals(tab, tabInicio) ? Visibility.Visible : Visibility.Collapsed;
        if (animar && tab.Content is UIElement contenido) Ui.Aparecer(contenido);
    }

    // Accesos rápidos del Inicio: CommandParameter = opción del menú a abrir.
    private void Acceso_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { CommandParameter: RadioButton destino }) destino.IsChecked = true;
    }

    // ===== Ventana =====

    private void Minimizar_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Maximizar_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    // Tamaño inicial según la pantalla (sirve en portátiles de 1366x768 y en monitores grandes).
    private void AjustarTamanoInicial()
    {
        var area = SystemParameters.WorkArea;
        Width = Math.Min(Width, area.Width - 40);
        Height = Math.Min(Height, area.Height - 40);
        MinWidth = Math.Min(MinWidth, area.Width);
        MinHeight = Math.Min(MinHeight, area.Height);
    }

    // Maximizada, la ventana sin marco se sale unos píxeles de la pantalla: se compensa.
    private void AjustarMaximizado()
    {
        bool max = WindowState == WindowState.Maximized;
        raiz.Margin = max ? SystemParameters.WindowResizeBorderThickness : new Thickness(0);
        icoMaximizar.Tag = FindResource(max ? "IcoRestaurar" : "IcoMaximizar");
        btnMaximizar.ToolTip = max ? "Restaurar" : "Maximizar";
    }

    // Ventana angosta: menú solo con iconos; los accesos rápidos se reacomodan.
    private void AdaptarTamano()
    {
        bool compacto = ActualWidth < AnchoMenuCompacto;
        if (compacto != Compacto)
        {
            Compacto = compacto;
            colSidebar.Width = new GridLength(compacto ? 84 : 252);
            sidebar.Margin = compacto ? new Thickness(12, 0, 12, 16) : new Thickness(18, 0, 18, 18);
            var textos = compacto ? Visibility.Collapsed : Visibility.Visible;
            rotGeneral.Visibility = rotPersonas.Visibility = rotAcademico.Visibility = textos;
            panelUsuarioTextos.Visibility = lblBienvenida.Visibility = txtSalir.Visibility = textos;
            tarjetaUsuario.Padding = new Thickness(compacto ? 6 : 12);
            // El logo es apaisado: en el menú compacto quedaría ilegible, así que se oculta.
            imgLogo.Visibility = textos;
        }
        gridAccesos.Columns = ActualWidth >= 1560 ? 4 : ActualWidth >= 1000 ? 3 : 2;
    }

    // ===== Indicador de carga =====
    // Aparece solo si la espera pasa de ~0,25 s (así no se ve en respuestas rápidas).
    private void ActualizarCarga()
    {
        if (EstadoUi.Ocupado)
        {
            if (_demoraCarga != null || panelCargando.Visibility == Visibility.Visible) return;
            _demoraCarga = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            _demoraCarga.Tick += (_, _) =>
            {
                _demoraCarga?.Stop();
                _demoraCarga = null;
                if (!EstadoUi.Ocupado) return;
                panelCargando.Visibility = Visibility.Visible;
                Ui.Aparecer(panelCargando, 0, 160);
                if (estrellaCargando.RenderTransform is RotateTransform giro)
                    giro.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, 360, TimeSpan.FromSeconds(1.6))
                    {
                        RepeatBehavior = RepeatBehavior.Forever
                    });
            };
            _demoraCarga.Start();
        }
        else
        {
            _demoraCarga?.Stop();
            _demoraCarga = null;
            panelCargando.Visibility = Visibility.Collapsed;
            if (estrellaCargando.RenderTransform is RotateTransform giro)
                giro.BeginAnimation(RotateTransform.AngleProperty, null);
        }
    }
}
