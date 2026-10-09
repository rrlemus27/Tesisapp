using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace StarAchiever.Desktop;

// Pinceles de la paleta clara (definidos en Tema.xaml) para usarlos desde el código.
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
    public static Brush Apagado => B("ApagadoBrush");
    public static Brush Borde => B("BordeBrush");

    // Versiones suaves (el mismo color con transparencia) para fondos de pills y botones.
    public static Brush MoradoSuave => B("MoradoSuaveBrush");
    public static Brush CoralSuave => B("CoralSuaveBrush");
    public static Brush VerdeSuave => B("VerdeSuaveBrush");
    public static Brush AmarilloSuave => B("AmarilloSuaveBrush");
    public static Brush NeutroSuave => B("NeutroSuaveBrush");
}

// Tonos de color para etiquetas y botones de acción.
public enum Tono { Teal, Morado, Coral, Verde, Amarillo, Neutro }

// Piezas de interfaz que se arman desde el código (filas de listas, etiquetas, botones).
public static class Ui
{
    // (color fuerte para texto/borde, color suave para el fondo) de cada tono.
    public static (Brush fuerte, Brush suave) Colores(Tono tono) => tono switch
    {
        Tono.Teal => (Paleta.TealOscuro, Paleta.Menta),
        Tono.Morado => (Paleta.Morado, Paleta.MoradoSuave),
        Tono.Coral => (Paleta.Coral, Paleta.CoralSuave),
        Tono.Verde => (Paleta.Verde, Paleta.VerdeSuave),
        Tono.Amarillo => (Paleta.AmarilloTexto, Paleta.AmarilloSuave),
        _ => (Paleta.Apagado, Paleta.NeutroSuave)
    };

    // Fila de lista: tarjeta blanca redondeada con [número | información | acciones].
    public static Border Fila(int num, UIElement info, params UIElement[] acciones)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
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
                Margin = new Thickness(10, 0, 0, 0)
            };
            foreach (var a in acciones) panel.Children.Add(a);
            Grid.SetColumn(panel, 2);
            grid.Children.Add(panel);
        }

        return new Border
        {
            Background = Brushes.White,
            BorderBrush = Paleta.Borde,
            BorderThickness = new Thickness(1.5),
            CornerRadius = new CornerRadius(18),
            Padding = new Thickness(12, 10, 12, 10),
            Margin = new Thickness(0, 0, 0, 8),
            Child = grid
        };
    }

    // Círculo con el número de la fila; el color va rotando para darle alegría a la lista.
    public static Border Numero(int n)
    {
        var tonos = new[] { Tono.Teal, Tono.Morado, Tono.Coral, Tono.Amarillo, Tono.Verde };
        var (fuerte, suave) = Colores(tonos[(Math.Max(n, 1) - 1) % tonos.Length]);
        return new Border
        {
            Width = 34,
            Height = 34,
            CornerRadius = new CornerRadius(17),
            Background = suave,
            Margin = new Thickness(0, 0, 12, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = n.ToString(),
                FontSize = 13,
                FontWeight = FontWeights.ExtraBold,
                Foreground = fuerte,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };
    }

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
                Margin = new Thickness(0, 2, 0, 0)
            });
        return info;
    }

    public static TextBlock TextoPrincipal(string texto, double tam = 14.5) => new()
    {
        Text = texto,
        FontSize = tam,
        FontWeight = FontWeights.Bold,
        Foreground = Paleta.Navy,
        TextWrapping = TextWrapping.Wrap,
        VerticalAlignment = VerticalAlignment.Center
    };

    // Etiqueta redondeada (pill) de estado: ACTIVO, INACTIVA, PUBLICADA…
    public static Border Pill(string texto, Tono tono)
    {
        var (fuerte, suave) = Colores(tono);
        return new Border
        {
            CornerRadius = new CornerRadius(11),
            Background = suave,
            Padding = new Thickness(10, 3, 10, 3),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = texto,
                FontSize = 11,
                FontWeight = FontWeights.ExtraBold,
                Foreground = fuerte
            }
        };
    }

    // Botón de acción de una fila: fondo suave y texto de color; al pasar el mouse se
    // rellena con el color fuerte (ver estilo BtnAccion en Tema.xaml).
    public static Button Accion(string texto, Tono tono, string? ayuda = null)
    {
        var (fuerte, suave) = Colores(tono);
        var btn = new Button
        {
            Content = texto,
            Style = (Style)Application.Current.FindResource("BtnAccion"),
            Background = suave,
            Foreground = fuerte,
            BorderBrush = fuerte
        };
        if (ayuda != null) btn.ToolTip = ayuda;
        return btn;
    }

    // Indicador de carga para el contenido de un botón: un arco que gira + el texto.
    public static UIElement Cargando(string texto, Brush color)
    {
        var giro = new RotateTransform();
        var arco = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse("M 8,1 A 7,7 0 1 1 1,8"),
            Stroke = color,
            StrokeThickness = 2.4,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            Width = 17,
            Height = 17,
            VerticalAlignment = VerticalAlignment.Center,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = giro
        };
        giro.BeginAnimation(RotateTransform.AngleProperty, new System.Windows.Media.Animation.DoubleAnimation
        {
            From = 0,
            To = 360,
            Duration = TimeSpan.FromSeconds(0.9),
            RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever
        });

        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(arco);
        panel.Children.Add(new TextBlock
        {
            Text = texto,
            Foreground = color,
            Margin = new Thickness(10, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        });
        return panel;
    }

    // Hace que una fila se pueda elegir con un clic (resaltándola al pasar el mouse).
    // Los clics sobre los botones de la fila no cuentan como selección.
    public static void HacerClicable(Border fila, Action alElegir)
    {
        fila.Cursor = Cursors.Hand;
        fila.MouseEnter += (_, _) => { fila.Background = Paleta.Menta; fila.BorderBrush = Paleta.Teal; };
        fila.MouseLeave += (_, _) => { fila.Background = Brushes.White; fila.BorderBrush = Paleta.Borde; };
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
}
