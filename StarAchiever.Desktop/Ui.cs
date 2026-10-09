using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace StarAchiever.Desktop;

// Pinceles de la paleta (definidos en Tema.xaml) para usarlos desde el código.
public static class Paleta
{
    private static Brush B(string clave) => (Brush)Application.Current.FindResource(clave);

    public static Brush Fondo => B("FondoBrush");
    public static Brush Navy => B("NavyBrush");
    public static Brush Teal => B("TealBrush");
    public static Brush TealOscuro => B("TealOscuroBrush");
    public static Brush Amarillo => B("AmarilloBrush");
    public static Brush AmarilloTexto => B("AmarilloTextoBrush");
    public static Brush Morado => B("MoradoBrush");
    public static Brush Coral => B("CoralBrush");
    public static Brush Verde => B("VerdeBrush");
    public static Brush Menta => B("MentaBrush");
    public static Brush Menta2 => B("Menta2Brush");
    public static Brush Apagado => B("ApagadoBrush");
    public static Brush Borde => B("BordeBrush");
    public static Brush LineaSuave => B("LineaSuaveBrush");
    public static Brush FilaHover => B("FilaHoverBrush");

    // Texto legible sobre fondos claros (los mismos tonos que usa la web).
    public static Brush CoralTexto => B("CoralTextoBrush");
    public static Brush VerdeTexto => B("VerdeTextoBrush");
    public static Brush MoradoTexto => B("MoradoTextoBrush");
    public static Brush AmarilloTextoSuave => B("AmarilloTextoSuaveBrush");

    // Versiones suaves para fondos de chips, botones e iconos.
    public static Brush MoradoSuave => B("MoradoSuaveBrush");
    public static Brush CoralSuave => B("CoralSuaveBrush");
    public static Brush VerdeSuave => B("VerdeSuaveBrush");
    public static Brush AmarilloSuave => B("AmarilloSuaveBrush");
    public static Brush NeutroSuave => B("NeutroSuaveBrush");
}

// Tonos de color para etiquetas y botones de acción.
public enum Tono { Teal, Morado, Coral, Verde, Amarillo, Neutro }

// Estado global para la interfaz: ¿hay peticiones a la API en curso? (lo usan los estados
// de carga de las listas en Tema.xaml mediante {Binding Path=(local:EstadoUi.Ocupado)}).
// Se enciende al instante y se apaga tras un breve margen sin peticiones, para que una serie
// de llamadas seguidas no haga parpadear los indicadores.
public static class EstadoUi
{
    private static bool _ocupado;
    private static DispatcherTimer? _apagar;

    static EstadoUi()
    {
        ApiService.OcupadoCambio += () => Application.Current?.Dispatcher.BeginInvoke(Actualizar);
        // Puede haber peticiones en curso desde antes de que alguien consulte este estado.
        _ocupado = ApiService.Ocupado;
    }

    public static bool Ocupado => _ocupado;
    public static event EventHandler<PropertyChangedEventArgs>? StaticPropertyChanged;

    private static void Actualizar()
    {
        if (ApiService.Ocupado)
        {
            _apagar?.Stop();
            Fijar(true);
            return;
        }
        if (_apagar is null)
        {
            _apagar = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
            _apagar.Tick += (_, _) =>
            {
                _apagar.Stop();
                if (!ApiService.Ocupado) Fijar(false);
            };
        }
        _apagar.Stop();
        _apagar.Start();
    }

    private static void Fijar(bool valor)
    {
        if (_ocupado == valor) return;
        _ocupado = valor;
        StaticPropertyChanged?.Invoke(null, new PropertyChangedEventArgs(nameof(Ocupado)));
    }
}

// Piezas de interfaz que se arman desde el código (filas de tablas, etiquetas, botones…).
public static class Ui
{
    // (color fuerte para texto/icono, color suave para el fondo) de cada tono.
    public static (Brush fuerte, Brush suave) Colores(Tono tono) => tono switch
    {
        Tono.Teal => (Paleta.TealOscuro, Paleta.Menta),
        Tono.Morado => (Paleta.MoradoTexto, Paleta.MoradoSuave),
        Tono.Coral => (Paleta.CoralTexto, Paleta.CoralSuave),
        Tono.Verde => (Paleta.VerdeTexto, Paleta.VerdeSuave),
        Tono.Amarillo => (Paleta.AmarilloTextoSuave, Paleta.AmarilloSuave),
        _ => (Paleta.Apagado, Paleta.NeutroSuave)
    };

    // Borde de los botones de acción de cada tono (como .btn.ghost / .danger-btn de la web).
    private static Brush BordeDe(Tono tono) => (Brush)Application.Current.FindResource(tono switch
    {
        Tono.Teal => "TealBordeBrush",
        Tono.Morado => "MoradoBordeBrush",
        Tono.Coral => "CoralBordeBrush",
        Tono.Verde => "VerdeBordeBrush",
        Tono.Amarillo => "AvisoBordeBrush",
        _ => "BordeBrush"
    });

    // Icono de trazo de Tema.xaml (IcoInicio, IcoEditar…). Sin color, hereda el del texto.
    public static ContentControl Icono(string clave, double tam = 18, Brush? color = null)
    {
        var icono = new ContentControl
        {
            Style = (Style)Application.Current.FindResource("Icono"),
            Tag = Application.Current.FindResource(clave),
            Width = tam,
            Height = tam,
            VerticalAlignment = VerticalAlignment.Center
        };
        if (color != null) icono.Foreground = color;
        return icono;
    }

    // Fila de tabla: [número | información | acciones], plana, con separador y realce al pasar el mouse.
    public static Border Fila(int num, UIElement info, params UIElement[] acciones)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var numero = Numero(num);
        Grid.SetColumn(numero, 0);
        grid.Children.Add(numero);

        Grid.SetColumn(info, 1);
        grid.Children.Add(info);

        if (acciones.Length > 0)
        {
            var panel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(12, 0, 0, 0)
            };
            foreach (var a in acciones) panel.Children.Add(a);
            Grid.SetColumn(panel, 2);
            grid.Children.Add(panel);
        }

        var fila = new Border
        {
            Background = Brushes.Transparent,
            BorderBrush = Paleta.LineaSuave,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(18, 12, 16, 12),
            Child = grid
        };
        fila.MouseEnter += (_, _) => fila.Background = Paleta.FilaHover;
        fila.MouseLeave += (_, _) => fila.Background = EstaSeleccionada(fila) ? Paleta.Menta2 : Brushes.Transparent;
        return fila;
    }

    // Número de la fila (columna «#» de la tabla), discreto.
    public static FrameworkElement Numero(int n) => new TextBlock
    {
        Text = n.ToString(),
        FontSize = 12.5,
        FontWeight = FontWeights.Bold,
        Foreground = Paleta.Apagado,
        VerticalAlignment = VerticalAlignment.Center
    };

    // Marca una fila como elegida (p. ej. el docente cuyas clases se están viendo).
    public static void MarcarSeleccion(Border fila)
    {
        fila.Tag = MarcaSeleccion;
        fila.Background = Paleta.Menta2;
    }

    private static readonly object MarcaSeleccion = new();
    private static bool EstaSeleccionada(Border fila) => ReferenceEquals(fila.Tag, MarcaSeleccion);

    // Bloque de información: título (+ etiqueta opcional al lado) y subtítulo apagado debajo.
    public static StackPanel Info(string titulo, string? subtitulo = null, UIElement? pill = null)
    {
        var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center };

        // La etiqueta va dentro del mismo texto, para que un título largo pueda hacer
        // salto de línea y la etiqueta quede justo después de la última palabra.
        var linea = TextoPrincipal(titulo);
        if (pill != null)
        {
            linea.Inlines.Add(new Run("  "));
            linea.Inlines.Add(new InlineUIContainer(pill) { BaselineAlignment = BaselineAlignment.Center });
        }
        info.Children.Add(linea);

        if (!string.IsNullOrWhiteSpace(subtitulo))
            info.Children.Add(new TextBlock
            {
                Text = subtitulo,
                FontSize = 12.5,
                Foreground = Paleta.Apagado,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 3, 0, 0)
            });
        return info;
    }

    public static TextBlock TextoPrincipal(string texto, double tam = 14) => new()
    {
        Text = texto,
        FontSize = tam,
        FontWeight = FontWeights.Bold,
        Foreground = Paleta.Navy,
        TextWrapping = TextWrapping.Wrap,
        VerticalAlignment = VerticalAlignment.Center
    };

    // Etiqueta redondeada (chip) de estado: ACTIVO, DE BAJA, INACTIVA…
    public static Border Pill(string texto, Tono tono)
    {
        var (fuerte, suave) = Colores(tono);
        return new Border
        {
            CornerRadius = new CornerRadius(10),
            Background = suave,
            Padding = new Thickness(9, 2, 9, 3),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = texto,
                FontSize = 10.5,
                FontWeight = FontWeights.ExtraBold,
                Foreground = fuerte
            }
        };
    }

    // Botón de acción de una fila: blanco con borde y texto del tono; al pasar el mouse se
    // tiñe con el color suave (estilo BtnAccion). Los símbolos ✎ 🗑 ＋ ✓ al inicio del
    // texto se cambian por los iconos de la web; si solo hay símbolo, el botón es de icono.
    public static Button Accion(string texto, Tono tono, string? ayuda = null)
    {
        var (fuerte, suave) = Colores(tono);
        var (icono, resto) = SepararIcono(texto);

        object contenido = resto;
        if (icono != null)
        {
            var panel = new StackPanel { Orientation = Orientation.Horizontal };
            panel.Children.Add(Icono(icono, 15));
            if (resto.Length > 0)
                panel.Children.Add(new TextBlock
                {
                    Text = resto,
                    Margin = new Thickness(6, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center
                });
            contenido = panel;
        }

        var btn = new Button
        {
            Content = contenido,
            Style = (Style)Application.Current.FindResource("BtnAccion"),
            Foreground = fuerte,
            BorderBrush = BordeDe(tono),
            Tag = suave
        };
        if (icono != null && resto.Length == 0) btn.Padding = new Thickness(0);
        if (ayuda != null) btn.ToolTip = ayuda;
        else if (resto.Length == 0) btn.ToolTip = icono switch
        {
            "IcoBorrar" => "Eliminar",
            "IcoEditar" => "Editar",
            _ => null
        };
        return btn;
    }

    private static (string? icono, string resto) SepararIcono(string texto)
    {
        var t = texto.Trim();
        foreach (var (simbolo, clave) in new[]
                 { ("✎", "IcoEditar"), ("🗑", "IcoBorrar"), ("＋", "IcoMas"), ("+", "IcoMas"), ("✓", "IcoCheck") })
            if (t.StartsWith(simbolo, StringComparison.Ordinal))
                return (clave, t[simbolo.Length..].Trim());
        return (null, t);
    }

    // Indicador de carga para el contenido de un botón: un arco que gira + el texto.
    // Sin color, toma el del texto del botón.
    public static UIElement Cargando(string texto, Brush? color = null)
    {
        var giro = new RotateTransform();
        var arco = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse("M 8,1 A 7,7 0 1 1 1,8"),
            StrokeThickness = 2.4,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            Width = 17,
            Height = 17,
            VerticalAlignment = VerticalAlignment.Center,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = giro
        };
        var etiqueta = new TextBlock
        {
            Text = texto,
            Margin = new Thickness(10, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        if (color != null)
        {
            arco.Stroke = color;
            etiqueta.Foreground = color;
        }
        else
        {
            arco.SetBinding(System.Windows.Shapes.Shape.StrokeProperty, new System.Windows.Data.Binding
            {
                Path = new PropertyPath(TextElement.ForegroundProperty),
                RelativeSource = System.Windows.Data.RelativeSource.Self
            });
        }
        giro.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation
        {
            From = 0,
            To = 360,
            Duration = TimeSpan.FromSeconds(0.9),
            RepeatBehavior = RepeatBehavior.Forever
        });

        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(arco);
        panel.Children.Add(etiqueta);
        return panel;
    }

    // Hace que una fila se pueda elegir con un clic (resaltándola al pasar el mouse).
    // Los clics sobre los botones de la fila no cuentan como selección.
    public static void HacerClicable(Border fila, Action alElegir)
    {
        fila.Cursor = Cursors.Hand;
        fila.MouseEnter += (_, _) => fila.Background = Paleta.Menta2;
        fila.MouseLeftButtonUp += (_, e) => { if (!ClickVieneDeBoton(e.OriginalSource)) alElegir(); };
    }

    // ¿El origen del clic es un Button (o algo dentro de un Button)?
    private static bool ClickVieneDeBoton(object? origen)
    {
        var d = origen as DependencyObject;
        while (d != null)
        {
            if (d is Button) return true;
            d = (d is Visual || d is System.Windows.Media.Media3D.Visual3D)
                ? VisualTreeHelper.GetParent(d)
                : LogicalTreeHelper.GetParent(d);
        }
        return false;
    }

    // Entrada suave (opacidad + leve desplazamiento) para secciones y notificaciones.
    public static void Aparecer(UIElement el, double desde = 8, int ms = 220)
    {
        var mover = new TranslateTransform(0, desde);
        el.RenderTransform = mover;
        var suave = new CubicEase { EasingMode = EasingMode.EaseOut };
        el.BeginAnimation(UIElement.OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(ms)) { EasingFunction = suave });
        mover.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(desde, 0, TimeSpan.FromMilliseconds(ms)) { EasingFunction = suave });
    }

    // ===== Notificaciones en línea =====
    // Convierte la cajita de estado de una etiqueta (Border > TextBlock) en una notificación
    // discreta con icono y color según el mensaje: ✓ = éxito, ✗ No disponible = aviso,
    // ✗ = error, otro texto = información. La etiqueta original sigue en su lugar (oculta)
    // y conserva su texto tal cual: el código la sigue leyendo y escribiendo igual.
    public enum TipoAviso { Exito, Aviso, Error, Info }

    public static void ComoNotificacion(TextBlock etiqueta)
    {
        if (etiqueta.Parent is not Border caja) return;

        caja.Child = null;
        var icono = Icono("IcoInfo", 18);
        icono.VerticalAlignment = VerticalAlignment.Top;
        icono.Margin = new Thickness(0, 0, 10, 0);
        var texto = new TextBlock { Style = (Style)Application.Current.FindResource("TextoEstado") };
        etiqueta.Visibility = Visibility.Collapsed;

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(texto, 1);
        Grid.SetColumn(etiqueta, 1);
        grid.Children.Add(icono);
        grid.Children.Add(texto);
        grid.Children.Add(etiqueta);
        caja.Child = grid;

        void Pintar()
        {
            var mensaje = etiqueta.Text ?? "";
            if (string.IsNullOrWhiteSpace(mensaje))
            {
                caja.Visibility = Visibility.Collapsed;
                return;
            }
            var (tipo, limpio) = Clasificar(mensaje);
            var (fondo, borde, color, ico) = tipo switch
            {
                TipoAviso.Exito => ("ExitoFondoBrush", "ExitoBordeBrush", "ExitoTextoBrush", "IcoOk"),
                TipoAviso.Aviso => ("AvisoFondoBrush", "AvisoBordeBrush", "AvisoTextoBrush", "IcoAviso"),
                TipoAviso.Error => ("ErrorFondoBrush", "ErrorBordeBrush", "ErrorTextoBrush", "IcoError"),
                _ => ("InfoFondoBrush", "InfoBordeBrush", "InfoTextoBrush", "IcoInfo")
            };
            var app = Application.Current;
            caja.Background = (Brush)app.FindResource(fondo);
            caja.BorderBrush = (Brush)app.FindResource(borde);
            texto.Foreground = (Brush)app.FindResource(color);
            icono.Foreground = texto.Foreground;
            icono.Tag = app.FindResource(ico);
            texto.Text = limpio;
            caja.Visibility = Visibility.Visible;
            Aparecer(caja, 4, 200);
        }

        DependencyPropertyDescriptor.FromProperty(TextBlock.TextProperty, typeof(TextBlock))
            .AddValueChanged(etiqueta, (_, _) => Pintar());
        Pintar();
    }

    public static (TipoAviso tipo, string texto) Clasificar(string mensaje)
    {
        var (tipo, texto) = ClasificarPrimeraLinea(mensaje.Trim());
        // En mensajes de varias líneas, el icono ya indica el tipo: se quitan los ✓ / ✗ sueltos.
        var lineas = texto.Split('\n').Select(l => l.TrimStart('✓', '✗', ' '));
        return (tipo, string.Join("\n", lineas));
    }

    private static (TipoAviso tipo, string texto) ClasificarPrimeraLinea(string t)
    {
        if (t.StartsWith('✓'))
        {
            var resto = t[1..].Trim();
            // Un resumen que avisa de algo no disponible se muestra como aviso.
            return (resto.Contains("no disponible", StringComparison.OrdinalIgnoreCase)
                    || resto.Contains("no está disponible", StringComparison.OrdinalIgnoreCase)
                ? TipoAviso.Aviso
                : TipoAviso.Exito, resto);
        }
        if (t.StartsWith('✗'))
        {
            var resto = t[1..].Trim();
            return (resto.StartsWith("No disponible", StringComparison.OrdinalIgnoreCase)
                ? TipoAviso.Aviso
                : TipoAviso.Error, resto);
        }
        return (TipoAviso.Info, t);
    }

    // ===== Paginación =====
    // Si la lista pasa de 'porPagina' filas, muestra solo una página y pone en 'barra' los
    // controles «‹ 1–25 de 80 ›». Trabaja sobre la vista de la lista (Items.Filter): el código
    // que llena la lista no cambia. Al rehacerse la lista (Clear) vuelve a la primera página.
    public static void Paginar(ItemsControl lista, Panel barra, int porPagina = 25)
    {
        int pagina = 0;
        bool aplicando = false, pendiente = false;

        var lblRango = new TextBlock
        {
            FontSize = 12.5,
            FontWeight = FontWeights.Bold,
            Foreground = Paleta.Apagado,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0)
        };
        Button Boton(string icono, string ayuda) => new()
        {
            Style = (Style)Application.Current.FindResource("BtnIcono"),
            Content = Icono(icono, 16),
            ToolTip = ayuda
        };
        var btnAnterior = Boton("IcoIzquierda", "Página anterior");
        var btnSiguiente = Boton("IcoDerecha", "Página siguiente");
        barra.Children.Clear();
        barra.Children.Add(lblRango);
        barra.Children.Add(btnAnterior);
        barra.Children.Add(btnSiguiente);
        barra.Visibility = Visibility.Collapsed;

        void Aplicar()
        {
            pendiente = false;
            aplicando = true;
            try
            {
                var fuente = lista.Items.SourceCollection.Cast<object>().ToList();
                int total = fuente.Count;
                if (total <= porPagina)
                {
                    pagina = 0;
                    if (lista.Items.Filter != null) lista.Items.Filter = null;
                    barra.Visibility = Visibility.Collapsed;
                    return;
                }
                int paginas = (total + porPagina - 1) / porPagina;
                pagina = Math.Clamp(pagina, 0, paginas - 1);
                var visibles = new HashSet<object>(fuente.Skip(pagina * porPagina).Take(porPagina));
                lista.Items.Filter = o => visibles.Contains(o);
                lblRango.Text = $"{pagina * porPagina + 1}–{Math.Min(total, (pagina + 1) * porPagina)} de {total}";
                btnAnterior.IsEnabled = pagina > 0;
                btnSiguiente.IsEnabled = pagina < paginas - 1;
                barra.Visibility = Visibility.Visible;
            }
            finally
            {
                aplicando = false;
            }
        }

        void Programar()
        {
            if (pendiente) return;
            pendiente = true;
            lista.Dispatcher.BeginInvoke(Aplicar, DispatcherPriority.Background);
        }

        ((INotifyCollectionChanged)lista.Items).CollectionChanged += (_, e) =>
        {
            if (aplicando) return;
            if (e.Action == NotifyCollectionChangedAction.Reset) pagina = 0;
            Programar();
        };
        btnAnterior.Click += (_, _) => { pagina--; Aplicar(); };
        btnSiguiente.Click += (_, _) => { pagina++; Aplicar(); };
    }
}
