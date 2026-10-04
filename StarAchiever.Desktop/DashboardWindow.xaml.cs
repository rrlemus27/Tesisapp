using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using Newtonsoft.Json.Linq;

namespace StarAchiever.Desktop;

public partial class DashboardWindow : Window
{
    // Materia y tema elegidos por el DOCENTE para gestionar temas / preguntas.
    private int _materiaSeleccionadaId;
    private int _temaSeleccionadoId;
    // Grado elegido por el ADMIN para gestionar sus secciones.
    private int _gradoSeleccionadoId;

    // Actividad elegida por el DOCENTE para asignarle preguntas (y su tema).
    private int _actividadSeleccionadaId;
    private int _actividadTemaId;
    // Mapa seccionId -> nombre, para mostrar las secciones donde ya se publicó.
    private readonly Dictionary<int, string> _seccionesNombre = new();

    // Datos ya cargados de usuarios y materias, para filtrar en vivo sin re-llamar a la API.
    private JArray? _usuariosData;
    private JArray? _materiasData;

    // ⚠️ ID del estado "aprobada/publicada" del catálogo EstadoContenido.
    // No pude confirmarlo contra la base (no hay seed en el código). Ajusta este
    // valor al id real del estado aprobado si el botón «Aprobar» da error.
    private const int EstadoAprobadaId = 2;

    // La pestaña «Asignaciones» usa api/admin/asignaciones-docente y api/admin/estudiantes-seccion.
    // Si hiciera falta ocultarla de nuevo, poner esto en false muestra la nota de «no disponible»
    // y deja de llamar a esos endpoints.
    private static readonly bool AsignacionesDisponibles = true;

    // Mapas id -> nombre para mostrar las asignaciones aunque la API solo devuelva ids.
    private readonly Dictionary<int, string> _nombreUsuario = new();
    private readonly Dictionary<int, string> _nombreMateria = new();
    private readonly Dictionary<int, string> _nombrePeriodo = new();
    // Lista de estudiantes con su sección, para filtrar en vivo sin re-llamar a la API.
    private JArray? _estudiantesData;
    // Evita lanzar dos cargas de la pestaña Asignaciones a la vez.
    private bool _cargandoAsignaciones;

    // Roles del sistema: la posición + 1 es el rolId (ADMIN=1, DOCENTE=2, ESTUDIANTE=3).
    private static readonly string[] Roles = { "ADMIN", "DOCENTE", "ESTUDIANTE" };

    public DashboardWindow()
    {
        InitializeComponent();
        ColorearMensajesDeEstado();
        Cargar();
    }

    private void Barra_Mover(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }
    private void Cerrar_Click(object sender, RoutedEventArgs e) => Application.Current.Shutdown();

    private void CerrarSesion_Click(object sender, RoutedEventArgs e)
    {
        
        ApiService.Token = null;
        ApiService.Rol = null;
        ApiService.NombreUsuario = null;
        ApiService.UsuarioId = 0;

        var login = new MainWindow();
        login.Show();
        Close();
    }

    private async void Cargar()
    {
        // Datos del usuario logueado
        var nombre = ApiService.NombreUsuario ?? "Usuario";
        lblNombre.Text = nombre;
        lblInicial.Text = string.IsNullOrWhiteSpace(nombre) ? "?" : nombre.Trim()[..1].ToUpper();
        lblRolBadge.Text = ApiService.Rol ?? "ROL";
        lblBienvenida.Text = $"Sesión iniciada como {ApiService.Rol}. Datos en vivo desde la API.";

        // Por defecto se muestra la vista genérica; el ADMIN usa su propia vista.
        panelGenerico.Visibility = Visibility.Visible;
        panelAdmin.Visibility = Visibility.Collapsed;

        // Asignaciones: formulario oculto y nota visible mientras la API no tenga el endpoint.
        panelAsignacionesForm.Visibility = AsignacionesDisponibles ? Visibility.Visible : Visibility.Collapsed;
        panelAsignacionesNota.Visibility = AsignacionesDisponibles ? Visibility.Collapsed : Visibility.Visible;
        pillAsignaciones.Visibility = AsignacionesDisponibles ? Visibility.Collapsed : Visibility.Visible;

        // Según el rol, mostramos un panel distinto
        switch (ApiService.Rol)
        {
            case "ADMIN":
                lblTitulo.Text = "Panel del Administrador";
                // El ADMIN gestiona materias y usuarios en su vista con secciones.
                panelGenerico.Visibility = Visibility.Collapsed;
                panelAdmin.Visibility = Visibility.Visible;
                await RecargarMaterias();
                await RecargarUsuarios();
                await RecargarGrados();
                await RecargarPeriodos();
                // Asignaciones se carga al abrir su pestaña (ver PanelAdmin_SelectionChanged).
                Stat("Gestión", AsignacionesDisponibles
                    ? "Usuarios · Materias · Grados · Períodos · Asignaciones"
                    : "Usuarios · Materias · Grados · Períodos");
                Stat("Rol", "ADMIN");
                break;

            case "DOCENTE":
                lblTitulo.Text = "Panel del Docente";
                lblSeccion.Text = "Materias disponibles (elige una)";
                // onSeleccion: al hacer clic en una materia se abre el panel de temas.
                await CargarLista(listaDatos, lblEstado, Rutas.Materias, "nombre", "activa", onSeleccion: SeleccionarMateria);
                // Módulo de actividades (crear, listar, asignar preguntas).
                panelActividades.Visibility = Visibility.Visible;
                await CargarModuloActividades();
                Stat("Contenido", "Materias · Actividades");
                Stat("Rol", "DOCENTE");
                break;

            case "ESTUDIANTE":
                lblTitulo.Text = "Panel del Estudiante";
                lblSeccion.Text = "Tu progreso por tema";
                await CargarLista(listaDatos, lblEstado, $"estudiante/{ApiService.UsuarioId}/progreso", "tema", "nivelDominio");
                Stat("Aventura", "En curso");
                Stat("Rol", "ESTUDIANTE");
                break;

            default:
                lblTitulo.Text = "Panel";
                lblSeccion.Text = "Roles del sistema";
                await CargarLista(listaDatos, lblEstado, "roles", "nombre", "codigo");
                break;
        }
    }

    // Crea una tarjeta de stat arriba: blanca, con un círculo de color y el dato.
    private void Stat(string arriba, string abajo)
    {
        // Cada tarjeta toma un color distinto de la paleta (teal, morado, amarillo…).
        var tonos = new[] { Tono.Teal, Tono.Morado, Tono.Amarillo, Tono.Coral };
        var iconos = new[] { "★", "◆", "●", "▲" };
        int n = panelStats.Children.Count;
        var (fuerte, suave) = Ui.Colores(tonos[n % tonos.Length]);

        var icono = new Border
        {
            Width = 38,
            Height = 38,
            CornerRadius = new CornerRadius(19),
            Background = suave,
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = iconos[n % iconos.Length],
                FontSize = 16,
                Foreground = fuerte,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };
        var textos = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
        textos.Children.Add(new TextBlock
        {
            Text = arriba.ToUpper(),
            FontSize = 11,
            FontWeight = FontWeights.ExtraBold,
            Foreground = Paleta.Apagado
        });
        textos.Children.Add(new TextBlock
        {
            Text = abajo,
            FontSize = 15,
            FontWeight = FontWeights.ExtraBold,
            Foreground = Paleta.Navy,
            Margin = new Thickness(0, 1, 0, 0)
        });

        var fila = new StackPanel { Orientation = Orientation.Horizontal };
        fila.Children.Add(icono);
        fila.Children.Add(textos);

        panelStats.Children.Add(new ContentControl
        {
            Style = (Style)FindResource("Tarjeta"),
            Padding = new Thickness(14, 10, 22, 10),
            Margin = new Thickness(0, 0, 14, 0),
            Content = fila
        });
    }

    // Trae una lista de la API y la pinta como filas.
    // Si la llamada falla, muestra el error real (código + mensaje) en pantalla.
    private async System.Threading.Tasks.Task CargarLista(ItemsControl destino, TextBlock estado,
        string ruta, string campo1, string campo2,
        bool conAcciones = false, Action<int, string>? onSeleccion = null)
    {
        try
        {
            var res = await ApiService.GetResultAsync(ruta);

            if (!res.Exito)
            {
                destino.Items.Clear();
                var detalle = string.IsNullOrWhiteSpace(res.Mensaje) ? res.Contenido : res.Mensaje;
                estado.Text = $"✗ Error {res.Codigo} al llamar /{ruta}: {detalle}";
                return;
            }

            var array = JArray.Parse(res.Contenido);

            destino.Items.Clear();
            int i = 1;
            foreach (var item in array)
            {
                var val1 = item[campo1]?.ToString() ?? "-";
                // Los campos booleanos (p. ej. "activa") se muestran como Activa / Inactiva.
                var token2 = item[campo2];
                var val2 = token2?.Type == JTokenType.Boolean
                    ? (token2.Value<bool>() ? "Activa" : "Inactiva")
                    : token2?.ToString() ?? "";
                // Solo cuando hay acciones (materias del ADMIN) leemos id y activa
                // para poder editar/eliminar esa materia concreta.
                int id = item["id"]?.Value<int>() ?? 0;
                bool activa = item["activa"]?.Value<bool>() ?? true;
                destino.Items.Add(CrearFila(i++, val1, val2, id, activa, conAcciones, onSeleccion));
            }

            estado.Text = $"✓ {array.Count} registros traídos de la API en vivo.";
        }
        catch (Exception ex)
        {
            estado.Text = $"✗ No se pudo procesar la respuesta de /{ruta}: {ex.Message}";
        }
    }

    // Atajos para recargar las listas del panel ADMIN.
    // Carga materias (con botones Editar/Eliminar), guardando los datos para poder filtrar.
    private async System.Threading.Tasks.Task RecargarMaterias()
    {
        try
        {
            var res = await ApiService.GetResultAsync(Rutas.Materias);
            if (!res.Exito)
            {
                _materiasData = null;
                listaMaterias.Items.Clear();
                var detalle = string.IsNullOrWhiteSpace(res.Mensaje) ? res.Contenido : res.Mensaje;
                lblMateriasEstado.Text = $"✗ Error {res.Codigo} al listar materias: {detalle}";
                return;
            }
            _materiasData = JArray.Parse(res.Contenido);
            RenderMaterias(txtBuscarMateria?.Text ?? "");
        }
        catch (Exception ex)
        {
            lblMateriasEstado.Text = $"✗ No se pudieron cargar las materias: {ex.Message}";
        }
    }

    // Pinta las materias ya cargadas, filtrando por nombre (en vivo, sin llamar a la API).
    private void RenderMaterias(string filtro)
    {
        if (_materiasData is null) return;
        filtro = (filtro ?? "").Trim();

        listaMaterias.Items.Clear();
        int i = 1, mostrados = 0;
        foreach (var item in _materiasData)
        {
            var nombre = item["nombre"]?.ToString() ?? "-";
            if (filtro.Length > 0 && !nombre.Contains(filtro, StringComparison.OrdinalIgnoreCase)) continue;

            int id = item["id"]?.Value<int>() ?? 0;
            bool activa = item["activa"]?.Value<bool?>() ?? true;
            listaMaterias.Items.Add(CrearFila(i++, nombre, activa ? "Activa" : "Inactiva", id, activa, conAcciones: true));
            mostrados++;
        }
        lblMateriasEstado.Text = filtro.Length > 0
            ? $"✓ {mostrados} de {_materiasData.Count} materia(s) (filtro: \"{filtro}\")."
            : $"✓ {_materiasData.Count} materia(s).";
    }

    private void BuscarMateria_Changed(object sender, TextChangedEventArgs e) => RenderMaterias(txtBuscarMateria.Text);

    // Carga usuarios (con botones Editar/Eliminar), guardando los datos para poder filtrar.
    private async System.Threading.Tasks.Task RecargarUsuarios()
    {
        try
        {
            var res = await ApiService.GetResultAsync(Rutas.Usuarios);

            if (!res.Exito)
            {
                _usuariosData = null;
                listaUsuarios.Items.Clear();
                var detalle = string.IsNullOrWhiteSpace(res.Mensaje) ? res.Contenido : res.Mensaje;
                lblUsuariosEstado.Text = $"✗ Error {res.Codigo} al listar usuarios: {detalle}";
                return;
            }

            _usuariosData = JArray.Parse(res.Contenido);
            RenderUsuarios(txtBuscarUsuario?.Text ?? "");
        }
        catch (Exception ex)
        {
            lblUsuariosEstado.Text = $"✗ No se pudieron cargar los usuarios: {ex.Message}";
        }
    }

    // Pinta los usuarios ya cargados, filtrando por nombre completo (en vivo).
    private void RenderUsuarios(string filtro)
    {
        if (_usuariosData is null) return;
        filtro = (filtro ?? "").Trim();

        listaUsuarios.Items.Clear();
        int i = 1, mostrados = 0;
        foreach (var item in _usuariosData)
        {
            var nombreCompleto = item["nombreCompleto"]?.ToString() ?? "-";
            if (filtro.Length > 0 && !nombreCompleto.Contains(filtro, StringComparison.OrdinalIgnoreCase)) continue;

            int id = item["id"]?.Value<int>() ?? 0;
            var correo = item["correoOUsuario"]?.ToString() ?? "";
            var rol = item["rol"]?.ToString() ?? "";
            // Si la lista no trae rolId, lo deducimos del nombre del rol para que
            // «Editar» no cambie el rol del usuario por accidente.
            int rolId = item["rolId"]?.Value<int?>() ?? RolIdDesdeNombre(rol);
            bool activo = item["activo"]?.Value<bool?>() ?? true;
            listaUsuarios.Items.Add(CrearFilaUsuario(i++, id, nombreCompleto, correo, rol, rolId, activo));
            mostrados++;
        }
        lblUsuariosEstado.Text = filtro.Length > 0
            ? $"✓ {mostrados} de {_usuariosData.Count} usuario(s) (filtro: \"{filtro}\")."
            : $"✓ {_usuariosData.Count} usuario(s).";
    }

    private void BuscarUsuario_Changed(object sender, TextChangedEventArgs e) => RenderUsuarios(txtBuscarUsuario.Text);

    // "DOCENTE" -> 2, etc. Devuelve 0 si el nombre no coincide con ningún rol conocido.
    private static int RolIdDesdeNombre(string rol) =>
        Array.FindIndex(Roles, r => r.Equals(rol.Trim(), StringComparison.OrdinalIgnoreCase)) + 1;

    // ===== ASIGNACIONES (ADMIN): docente -> materia + sección, estudiante -> sección =====

    // Al abrir la pestaña «Asignaciones» se recargan combos y listas, para que aparezcan los
    // docentes, estudiantes, materias o secciones recién creados en las otras pestañas.
    private async void PanelAdmin_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // SelectionChanged también "sube" desde los combos y pestañas internas:
        // solo nos interesa cuando cambia la pestaña principal del panel ADMIN.
        if (!ReferenceEquals(e.OriginalSource, panelAdmin) || tabAsignaciones is null) return;
        if (AsignacionesDisponibles && ReferenceEquals(panelAdmin.SelectedItem, tabAsignaciones))
            await CargarPestanaAsignaciones();
    }

    private async System.Threading.Tasks.Task CargarPestanaAsignaciones()
    {
        if (_cargandoAsignaciones) return;
        _cargandoAsignaciones = true;
        try
        {
            var (errUsuarios, errMaterias, errSecciones, errPeriodos) = await CargarCombosAsignacion();
            await RecargarAsignaciones();
            await RecargarEstudiantesSeccion();
            // Los errores de los combos se muestran después, para que la recarga de las listas no los tape.
            MostrarErroresCombos(lblAsignacionesEstado, new[] { errUsuarios, errMaterias, errSecciones, errPeriodos });
            MostrarErroresCombos(lblEstudiantesEstado, new[] { errUsuarios, errSecciones });
        }
        finally
        {
            _cargandoAsignaciones = false;
        }
    }

    // Trae usuarios, materias, secciones y períodos (una vez cada uno), llena los combos de
    // las dos asignaciones y los mapas id -> nombre. Devuelve el error de cada lista, si lo hubo.
    private async System.Threading.Tasks.Task<(string? usuarios, string? materias, string? secciones, string? periodos)>
        CargarCombosAsignacion()
    {
        var (usuarios, errUsuarios) = await ObtenerListaAsync(Rutas.Usuarios);
        var (materias, errMaterias) = await ObtenerListaAsync(Rutas.Materias);
        var (secciones, errSecciones) = await ObtenerListaAsync(Rutas.Secciones);
        var (periodos, errPeriodos) = await ObtenerListaAsync(Rutas.PeriodosAcademicos);

        LlenarCombo(cmbAsigDocente, usuarios, it => EsRol(it, "DOCENTE") ? NombreDeUsuario(it) : null);
        LlenarCombo(cmbAsigMateria, materias, it => it["nombre"]?.ToString());
        LlenarCombo(cmbAsigSeccion, secciones, TextoSeccion);
        LlenarCombo(cmbAsigPeriodo, periodos, it => it["nombre"]?.ToString());
        LlenarCombo(cmbEstEstudiante, usuarios, it => EsRol(it, "ESTUDIANTE") ? NombreDeUsuario(it) : null);
        LlenarCombo(cmbEstSeccion, secciones, TextoSeccion);

        Mapear(_nombreUsuario, usuarios, NombreDeUsuario);
        Mapear(_nombreMateria, materias, it => it["nombre"]?.ToString());
        Mapear(_seccionesNombre, secciones, TextoSeccion);
        Mapear(_nombrePeriodo, periodos, it => it["nombre"]?.ToString());

        return (errUsuarios, errMaterias, errSecciones, errPeriodos);
    }

    // "A · Primer grado": nombre de la sección con su grado, para los combos.
    private static string? TextoSeccion(JToken it)
    {
        var s = it["nombre"]?.ToString() ?? "";
        var g = it["grado"]?.ToString();
        return string.IsNullOrWhiteSpace(g) ? s : $"{s} · {g}";
    }

    private static string NombreDeUsuario(JToken it) =>
        Texto(it, "nombreCompleto", "nombre", "correoOUsuario") ?? $"Usuario #{Entero(it, "id") ?? 0}";

    // ¿El usuario de la lista tiene este rol? Mira "rol" (texto) y, si no viene, "rolId".
    private static bool EsRol(JToken usuario, string rol)
    {
        var texto = Texto(usuario, "rol");
        return texto != null
            ? texto.Trim().Equals(rol, StringComparison.OrdinalIgnoreCase)
            : Entero(usuario, "rolId") == RolIdDesdeNombre(rol);
    }

    // GET a una lista de la API. Devuelve (datos, null) o (null, error real con código + mensaje).
    private static async System.Threading.Tasks.Task<(JArray? datos, string? error)> ObtenerListaAsync(string ruta)
    {
        try
        {
            var res = await ApiService.GetResultAsync(ruta);
            if (!res.Exito)
            {
                var detalle = string.IsNullOrWhiteSpace(res.Mensaje) ? res.Contenido : res.Mensaje;
                return (null, $"✗ Error {res.Codigo} al cargar /{ruta}: {detalle}");
            }
            return (JArray.Parse(res.Contenido), null);
        }
        catch (Exception ex)
        {
            return (null, $"✗ No se pudo cargar /{ruta}: {ex.Message}");
        }
    }

    // GET a 'ruta' y llena el combo con ComboBoxItem(Content=texto, Tag=id).
    // 'texto' devuelve null para saltarse ese elemento (p. ej. usuarios que no son DOCENTE).
    // Devuelve null si todo fue bien, o el error real (código + mensaje) si la API falló.
    private async System.Threading.Tasks.Task<string?> LlenarComboAsync(ComboBox combo, string ruta, Func<JToken, string?> texto)
    {
        var (datos, error) = await ObtenerListaAsync(ruta);
        LlenarCombo(combo, datos, texto);
        return error;
    }

    // Llena el combo con los datos (o lo deja vacío si no hay). Si el elemento que estaba
    // elegido sigue en la lista, lo vuelve a elegir; si no, elige el primero.
    private static void LlenarCombo(ComboBox combo, JArray? datos, Func<JToken, string?> texto)
    {
        int elegido = TagCombo(combo);
        combo.Items.Clear();
        if (datos is null) return;
        foreach (var item in datos)
        {
            var t = texto(item);
            if (t is null) continue;
            int id = item["id"]?.Value<int>() ?? 0;
            combo.Items.Add(new ComboBoxItem { Content = t, Tag = id });
        }
        if (!(elegido > 0 && SeleccionarEnCombo(combo, elegido)) && combo.Items.Count > 0)
            combo.SelectedIndex = 0;
    }

    // Elige en el combo el elemento con ese id (Tag). Devuelve false si no está.
    private static bool SeleccionarEnCombo(ComboBox combo, int id)
    {
        var item = combo.Items.OfType<ComboBoxItem>().FirstOrDefault(i => i.Tag is int t && t == id);
        if (item != null) combo.SelectedItem = item;
        return item != null;
    }

    // Rellena un mapa id -> texto con una lista. Si la lista no se pudo cargar, deja el mapa como estaba.
    private static void Mapear(Dictionary<int, string> mapa, JArray? datos, Func<JToken, string?> texto)
    {
        if (datos is null) return;
        mapa.Clear();
        foreach (var item in datos)
        {
            var id = Entero(item, "id");
            var t = texto(item);
            if (id.HasValue && !string.IsNullOrWhiteSpace(t)) mapa[id.Value] = t;
        }
    }

    // Nombre de un id según el mapa; si no está, muestra el id.
    private static string Nombre(Dictionary<int, string> mapa, int? id) =>
        id is null ? "-" : mapa.TryGetValue(id.Value, out var n) ? n : $"#{id}";

    // Primer valor de texto entre varios nombres de campo posibles (sin distinguir mayúsculas).
    // Si el campo es un objeto (p. ej. "docente": { "nombreCompleto": ... }), usa su nombre.
    // Los números se ignoran: en ese caso el nombre se busca por id en los mapas.
    private static string? Texto(JToken? item, params string[] campos)
    {
        if (item is not JObject obj) return null;
        foreach (var campo in campos)
        {
            var v = obj.GetValue(campo, StringComparison.OrdinalIgnoreCase);
            if (v is JObject sub)
                v = sub.GetValue("nombreCompleto", StringComparison.OrdinalIgnoreCase)
                    ?? sub.GetValue("nombre", StringComparison.OrdinalIgnoreCase);
            if (v?.Type == JTokenType.String && !string.IsNullOrWhiteSpace(v.ToString()))
                return v.ToString();
        }
        return null;
    }

    // Primer número entero entre varios nombres de campo posibles (o el "id" si el campo es un objeto).
    private static int? Entero(JToken? item, params string[] campos)
    {
        if (item is not JObject obj) return null;
        foreach (var campo in campos)
        {
            var v = obj.GetValue(campo, StringComparison.OrdinalIgnoreCase);
            if (v is JObject sub) v = sub.GetValue("id", StringComparison.OrdinalIgnoreCase);
            if (v?.Type == JTokenType.Integer) return v.Value<int>();
            if (v?.Type == JTokenType.String && int.TryParse(v.ToString(), out var n)) return n;
        }
        return null;
    }

    // Si algún combo no se pudo llenar, antepone el/los errores al texto de la etiqueta de estado.
    private static void MostrarErroresCombos(TextBlock estado, IEnumerable<string?> errores)
    {
        // (sin repetir un error que ya se está mostrando)
        var texto = string.Join("\n", errores.Where(e => e != null && !(estado.Text ?? "").Contains(e)));
        if (texto.Length == 0) return;
        estado.Text = string.IsNullOrWhiteSpace(estado.Text) ? texto : texto + "\n" + estado.Text;
    }

    private static int TagCombo(ComboBox combo) =>
        (combo.SelectedItem as ComboBoxItem)?.Tag is int id ? id : 0;

    // ----- Docente -> materia + sección + período: api/admin/asignaciones-docente -----

    private async System.Threading.Tasks.Task RecargarAsignaciones()
    {
        var (datos, error) = await ObtenerListaAsync(Rutas.AsignacionesDocente);
        listaAsignaciones.Items.Clear();
        if (datos is null)
        {
            lblAsignacionesEstado.Text = error ?? "";
            return;
        }

        int i = 1;
        foreach (var item in datos)
        {
            // Se aceptan nombres en el propio JSON o, si solo vienen ids, se buscan en los combos.
            int id = Entero(item, "id", "asignacionId", "asignacionDocenteId") ?? 0;
            var docente = Texto(item, "docente", "docenteNombre", "nombreDocente", "docenteNombreCompleto")
                          ?? Nombre(_nombreUsuario, Entero(item, "docenteUsuarioId", "docenteId", "usuarioId"));
            var materia = Texto(item, "materia", "materiaNombre", "nombreMateria")
                          ?? Nombre(_nombreMateria, Entero(item, "materiaId"));
            var seccion = TextoSeccionDe(item) ?? Nombre(_seccionesNombre, Entero(item, "seccionId"));
            var periodo = Texto(item, "periodo", "periodoAcademico", "periodoNombre", "periodoAcademicoNombre")
                          ?? Nombre(_nombrePeriodo, Entero(item, "periodoAcademicoId", "periodoId"));
            listaAsignaciones.Items.Add(CrearFilaAsignacion(i++, id, docente, materia, seccion, periodo));
        }
        lblAsignacionesEstado.Text = datos.Count == 0
            ? "Todavía no hay docentes asignados."
            : $"✓ {datos.Count} asignación(es).";
    }

    // Nombre de la sección (con su grado si viene) leído del JSON de una asignación; null si no viene.
    private static string? TextoSeccionDe(JToken item)
    {
        var seccion = Texto(item, "seccion", "seccionNombre", "nombreSeccion");
        if (seccion is null) return null;
        var grado = Texto(item, "grado", "gradoNombre", "nombreGrado");
        return string.IsNullOrWhiteSpace(grado) ? seccion : $"{seccion} · {grado}";
    }

    private async void CrearAsignacion_Click(object sender, RoutedEventArgs e)
    {
        int docenteId = TagCombo(cmbAsigDocente);
        int materiaId = TagCombo(cmbAsigMateria);
        int seccionId = TagCombo(cmbAsigSeccion);
        int periodoId = TagCombo(cmbAsigPeriodo);

        if (docenteId <= 0 || materiaId <= 0 || seccionId <= 0 || periodoId <= 0)
        {
            lblAsignacionesEstado.Text = "✗ Elige docente, materia, sección y período.";
            return;
        }

        btnCrearAsignacion.IsEnabled = false;
        try
        {
            var res = await ApiService.PostAsync(Rutas.AsignacionesDocente, new
            {
                docenteUsuarioId = docenteId,
                materiaId = materiaId,
                seccionId = seccionId,
                periodoAcademicoId = periodoId
            });

            if (res.Exito)
            {
                await RecargarAsignaciones();
                lblAsignacionesEstado.Text = $"✓ {(string.IsNullOrWhiteSpace(res.Mensaje) ? "Asignación creada." : res.Mensaje)}";
            }
            else
            {
                var detalle = string.IsNullOrWhiteSpace(res.Mensaje) ? res.Contenido : res.Mensaje;
                lblAsignacionesEstado.Text = $"✗ Error {res.Codigo} al asignar: {detalle}";
            }
        }
        catch (Exception ex)
        {
            lblAsignacionesEstado.Text = $"✗ No se pudo crear la asignación: {ex.Message}";
        }
        finally
        {
            btnCrearAsignacion.IsEnabled = true;
        }
    }

    private async void EliminarAsignacion(int id, string resumen)
    {
        if (!Confirmar("Eliminar asignación", $"¿Eliminar la asignación:\n{resumen}?")) return;

        try
        {
            var res = await ApiService.DeleteAsync($"{Rutas.AsignacionesDocente}/{id}");
            if (res.Exito)
            {
                await RecargarAsignaciones();
                lblAsignacionesEstado.Text = $"✓ {(string.IsNullOrWhiteSpace(res.Mensaje) ? "Asignación eliminada." : res.Mensaje)}";
            }
            else
            {
                var detalle = string.IsNullOrWhiteSpace(res.Mensaje) ? res.Contenido : res.Mensaje;
                lblAsignacionesEstado.Text = $"✗ Error {res.Codigo} al eliminar la asignación: {detalle}";
            }
        }
        catch (Exception ex)
        {
            lblAsignacionesEstado.Text = $"✗ No se pudo eliminar la asignación: {ex.Message}";
        }
    }

    private Border CrearFilaAsignacion(int num, int id, string docente, string materia, string seccion, string periodo)
    {
        var info = Ui.Info(docente, $"{materia}  ·  Sección {seccion}  ·  {periodo}");

        var resumen = $"{docente} — {materia} / Sección {seccion} / {periodo}";
        var btnEliminar = Ui.Accion("🗑 Eliminar", Tono.Coral);
        btnEliminar.Click += (_, _) => EliminarAsignacion(id, resumen);
        if (id <= 0)
        {
            // Sin id no hay a qué ruta mandar el DELETE.
            btnEliminar.IsEnabled = false;
            btnEliminar.ToolTip = "La API no devolvió el id de esta asignación.";
        }

        return Ui.Fila(num, info, btnEliminar);
    }

    // ----- Estudiante -> sección: api/admin/estudiantes-seccion -----

    // Trae todos los estudiantes con su sección (también los que no tienen) y los pinta.
    private async System.Threading.Tasks.Task RecargarEstudiantesSeccion()
    {
        var (datos, error) = await ObtenerListaAsync(Rutas.EstudiantesSeccion);
        _estudiantesData = datos;
        if (datos is null)
        {
            listaEstudiantesSeccion.Items.Clear();
            lblEstudiantesEstado.Text = error ?? "";
            return;
        }
        RenderEstudiantesSeccion(txtBuscarEstudiante.Text);
    }

    // Pinta los estudiantes ya cargados, filtrando por nombre (en vivo, sin llamar a la API).
    private void RenderEstudiantesSeccion(string filtro)
    {
        if (_estudiantesData is null) return;
        filtro = (filtro ?? "").Trim();

        listaEstudiantesSeccion.Items.Clear();
        int i = 1, mostrados = 0, sinSeccion = 0;
        foreach (var item in _estudiantesData)
        {
            int usuarioId = Entero(item, "usuarioId", "estudianteId", "id") ?? 0;
            var nombre = Texto(item, "nombreCompleto", "estudiante", "estudianteNombre", "nombre")
                         ?? Nombre(_nombreUsuario, usuarioId);
            var correo = Texto(item, "correoOUsuario", "correo", "usuario");
            int? seccionId = Entero(item, "seccionId", "seccion");
            if (seccionId <= 0) seccionId = null; // 0 = sin sección
            var seccion = TextoSeccionDe(item) ?? (seccionId.HasValue ? Nombre(_seccionesNombre, seccionId) : null);
            if (seccion is null) sinSeccion++;

            if (filtro.Length > 0 && !nombre.Contains(filtro, StringComparison.OrdinalIgnoreCase)) continue;
            listaEstudiantesSeccion.Items.Add(CrearFilaEstudianteSeccion(i++, usuarioId, nombre, correo, seccionId, seccion));
            mostrados++;
        }

        var resumen = $"{_estudiantesData.Count} estudiante(s) · {sinSeccion} sin sección";
        lblEstudiantesEstado.Text = filtro.Length > 0
            ? $"✓ {mostrados} de {resumen} (filtro: \"{filtro}\")."
            : $"✓ {resumen}.";
    }

    private void BuscarEstudiante_Changed(object sender, TextChangedEventArgs e) =>
        RenderEstudiantesSeccion(txtBuscarEstudiante.Text);

    // Fila de estudiante: nombre + sección (o «Sin sección»), y botones Asignar/Cambiar y Quitar.
    private Border CrearFilaEstudianteSeccion(int num, int usuarioId, string nombre, string? correo,
        int? seccionId, string? seccion)
    {
        bool tieneSeccion = seccion != null;
        var info = Ui.Info(nombre, correo,
            tieneSeccion ? Ui.Pill(seccion!, Tono.Teal) : Ui.Pill("Sin sección", Tono.Amarillo));

        // «Asignar» / «Cambiar» solo prepara el formulario; se confirma con el botón del formulario.
        var btnPreparar = tieneSeccion
            ? Ui.Accion("✎ Cambiar", Tono.Morado, "Elegir otra sección para este estudiante")
            : Ui.Accion("＋ Asignar", Tono.Teal, "Asignar este estudiante a una sección");
        btnPreparar.Click += (_, _) => PrepararAsignacionEstudiante(usuarioId, nombre, seccionId);

        if (!tieneSeccion) return Ui.Fila(num, info, btnPreparar);

        var btnQuitar = Ui.Accion("Quitar", Tono.Coral, "Quitar al estudiante de su sección");
        btnQuitar.Click += (_, _) => QuitarEstudianteSeccion(usuarioId, nombre);
        return Ui.Fila(num, info, btnPreparar, btnQuitar);
    }

    // Deja elegidos el estudiante (y su sección actual, si tiene) en el formulario.
    private void PrepararAsignacionEstudiante(int usuarioId, string nombre, int? seccionId)
    {
        if (!SeleccionarEnCombo(cmbEstEstudiante, usuarioId))
        {
            lblEstudiantesEstado.Text = $"✗ \"{nombre}\" no aparece entre los usuarios con rol ESTUDIANTE.";
            return;
        }
        if (seccionId.HasValue) SeleccionarEnCombo(cmbEstSeccion, seccionId.Value);
        cmbEstSeccion.Focus();
    }

    // POST api/admin/estudiantes-seccion con { usuarioId, seccionId }: asigna o reasigna.
    private async void AsignarEstudiante_Click(object sender, RoutedEventArgs e)
    {
        int usuarioId = TagCombo(cmbEstEstudiante);
        int seccionId = TagCombo(cmbEstSeccion);
        if (usuarioId <= 0 || seccionId <= 0)
        {
            lblEstudiantesEstado.Text = "✗ Elige estudiante y sección.";
            return;
        }

        btnAsignarEstudiante.IsEnabled = false;
        try
        {
            var res = await ApiService.PostAsync(Rutas.EstudiantesSeccion,
                new { usuarioId = usuarioId, seccionId = seccionId });

            if (res.Exito)
            {
                await RecargarEstudiantesSeccion();
                lblEstudiantesEstado.Text = $"✓ {(string.IsNullOrWhiteSpace(res.Mensaje) ? "Estudiante asignado a la sección." : res.Mensaje)}";
            }
            else
            {
                var detalle = string.IsNullOrWhiteSpace(res.Mensaje) ? res.Contenido : res.Mensaje;
                lblEstudiantesEstado.Text = $"✗ Error {res.Codigo} al asignar el estudiante: {detalle}";
            }
        }
        catch (Exception ex)
        {
            lblEstudiantesEstado.Text = $"✗ No se pudo asignar el estudiante: {ex.Message}";
        }
        finally
        {
            btnAsignarEstudiante.IsEnabled = true;
        }
    }

    // DELETE api/admin/estudiantes-seccion/{usuarioId}: el estudiante queda sin sección.
    private async void QuitarEstudianteSeccion(int usuarioId, string nombre)
    {
        if (!Confirmar("Quitar de la sección", $"¿Quitar a \"{nombre}\" de su sección?",
                "Sí, quitar", "El estudiante quedará sin sección; puedes volver a asignarlo cuando quieras."))
            return;

        try
        {
            var res = await ApiService.DeleteAsync($"{Rutas.EstudiantesSeccion}/{usuarioId}");
            if (res.Exito)
            {
                await RecargarEstudiantesSeccion();
                lblEstudiantesEstado.Text = $"✓ {(string.IsNullOrWhiteSpace(res.Mensaje) ? "Estudiante quitado de la sección." : res.Mensaje)}";
            }
            else
            {
                var detalle = string.IsNullOrWhiteSpace(res.Mensaje) ? res.Contenido : res.Mensaje;
                lblEstudiantesEstado.Text = $"✗ Error {res.Codigo} al quitar el estudiante de la sección: {detalle}";
            }
        }
        catch (Exception ex)
        {
            lblEstudiantesEstado.Text = $"✗ No se pudo quitar el estudiante de la sección: {ex.Message}";
        }
    }

    // ===== ACTIVIDADES (DOCENTE) =====

    // Llena el combo de materias y carga la lista de actividades del docente.
    private async System.Threading.Tasks.Task CargarModuloActividades()
    {
        var error = await LlenarComboAsync(cmbActMateria, Rutas.Materias, it => it["nombre"]?.ToString());
        // Al tener materia seleccionada, ActMateria_Changed carga sus temas.
        await RecargarActividades();
        MostrarErroresCombos(lblActividadesEstado, new[] { error });
    }

    // Cuando cambia la materia elegida, recargamos los temas de esa materia en el combo.
    private async void ActMateria_Changed(object sender, SelectionChangedEventArgs e)
    {
        int materiaId = TagCombo(cmbActMateria);
        if (materiaId <= 0) { cmbActTema.Items.Clear(); return; }
        var error = await LlenarComboAsync(cmbActTema, $"temas?materiaId={materiaId}", it => it["nombre"]?.ToString());
        MostrarErroresCombos(lblActividadesEstado, new[] { error });
    }

    private async System.Threading.Tasks.Task RecargarActividades()
    {
        try
        {
            var res = await ApiService.GetResultAsync($"actividades?docenteId={ApiService.UsuarioId}");
            if (!res.Exito)
            {
                listaActividades.Items.Clear();
                var detalle = string.IsNullOrWhiteSpace(res.Mensaje) ? res.Contenido : res.Mensaje;
                lblActividadesEstado.Text = $"✗ Error {res.Codigo} al listar actividades: {detalle}";
                return;
            }
            var array = JArray.Parse(res.Contenido);
            listaActividades.Items.Clear();
            int i = 1;
            foreach (var item in array)
            {
                int id = item["id"]?.Value<int>() ?? 0;
                var titulo = item["titulo"]?.ToString() ?? "-";
                var estado = item["estado"]?.ToString() ?? "";
                int cant = item["cantidadPreguntas"]?.Value<int>() ?? 0;
                int temaId = item["temaId"]?.Value<int>() ?? 0;
                var tema = item["tema"]?.ToString() ?? "";
                listaActividades.Items.Add(CrearFilaActividad(i++, id, titulo, estado, cant, temaId, tema));
            }
            lblActividadesEstado.Text = $"✓ {array.Count} actividad(es).";
        }
        catch (Exception ex)
        {
            lblActividadesEstado.Text = $"✗ No se pudieron cargar las actividades: {ex.Message}";
        }
    }

    private async void CrearActividad_Click(object sender, RoutedEventArgs e)
    {
        var titulo = txtActTitulo.Text.Trim();
        int materiaId = TagCombo(cmbActMateria);
        int temaId = TagCombo(cmbActTema);
        var modo = (cmbActModo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "MANUAL";

        if (string.IsNullOrWhiteSpace(titulo)) { lblActividadesEstado.Text = "✗ Escribe un título."; return; }
        if (materiaId <= 0 || temaId <= 0) { lblActividadesEstado.Text = "✗ Elige materia y tema."; return; }
        if (!int.TryParse(txtActTipoId.Text.Trim(), out var tipoId)) tipoId = 1;
        if (!int.TryParse(txtActNivelId.Text.Trim(), out var nivelId)) nivelId = 1;

        var descripcion = txtActDescripcion.Text.Trim();

        btnCrearActividad.IsEnabled = false;
        try
        {
            var res = await ApiService.PostAsync("actividades", new
            {
                tipoActividadId = tipoId,
                materiaId = materiaId,
                temaId = temaId,
                minijuegoId = (int?)null,
                titulo = titulo,
                descripcion = string.IsNullOrWhiteSpace(descripcion) ? null : descripcion,
                modoDificultad = modo,
                nivelInicialId = nivelId,
                recuperacionActiva = chkRecuperacion.IsChecked == true,
                recompensasActivas = chkRecompensas.IsChecked == true,
                creadaPorUsuarioId = ApiService.UsuarioId
            });

            if (res.Exito)
            {
                txtActTitulo.Clear();
                txtActDescripcion.Clear();
                await RecargarActividades();
                lblActividadesEstado.Text = $"✓ {(string.IsNullOrWhiteSpace(res.Mensaje) ? "Actividad creada." : res.Mensaje)}";
            }
            else
            {
                var detalle = string.IsNullOrWhiteSpace(res.Mensaje) ? res.Contenido : res.Mensaje;
                lblActividadesEstado.Text = $"✗ Error {res.Codigo} al crear la actividad: {detalle}";
            }
        }
        catch (Exception ex)
        {
            lblActividadesEstado.Text = $"✗ No se pudo crear la actividad: {ex.Message}";
        }
        finally
        {
            btnCrearActividad.IsEnabled = true;
        }
    }

    // El docente eligió una actividad: muestra banco de preguntas, estado y publicación.
    private async void SeleccionarActividad(int id, string titulo, int temaId, string estado)
    {
        _actividadSeleccionadaId = id;
        _actividadTemaId = temaId;
        lblAsignarTitulo.Text = $"Asignar preguntas a: {titulo}";
        MostrarEstadoActividad(estado);
        panelAsignarPreguntas.Visibility = Visibility.Visible;

        // Combos de publicación (sección con su grado, y período) + mapa de nombres de sección.
        // Ojo: en la nueva API Secciones y PeriodosAcademicos son solo ADMIN; si el DOCENTE
        // no tiene acceso, el error real (p. ej. 403) se muestra bajo «Publicada en».
        await CargarMapaSecciones();
        var errores = new[]
        {
            await LlenarComboAsync(cmbPubSeccion, Rutas.Secciones, TextoSeccion),
            await LlenarComboAsync(cmbPubPeriodo, Rutas.PeriodosAcademicos, it => it["nombre"]?.ToString())
        };

        await RecargarBancoPreguntas();
        await RecargarPubSecciones();
        MostrarErroresCombos(lblPublicarEstado, errores);
    }

    private void MostrarEstadoActividad(string estado)
    {
        lblEstadoActual.Text = string.IsNullOrWhiteSpace(estado) ? "—" : estado;
        var (texto, fondo) = Ui.Colores(TonoEstado(estado));
        brdEstadoActual.Background = fondo;
        lblEstadoActual.Foreground = texto;
    }

    // Carga (una vez por selección) el mapa seccionId -> nombre para las secciones publicadas.
    private async System.Threading.Tasks.Task CargarMapaSecciones()
    {
        try
        {
            var res = await ApiService.GetResultAsync(Rutas.Secciones);
            if (!res.Exito) return;
            _seccionesNombre.Clear();
            foreach (var s in JArray.Parse(res.Contenido))
            {
                int sid = s["id"]?.Value<int>() ?? 0;
                var nombre = s["nombre"]?.ToString() ?? $"#{sid}";
                var grado = s["grado"]?.ToString();
                _seccionesNombre[sid] = string.IsNullOrWhiteSpace(grado) ? nombre : $"{nombre} · {grado}";
            }
        }
        catch { /* si falla, se mostrará el id */ }
    }

    // Lista las secciones donde ya se publicó la actividad (desde GET /api/actividades/{id}).
    private async System.Threading.Tasks.Task RecargarPubSecciones()
    {
        if (_actividadSeleccionadaId <= 0) return;
        try
        {
            var res = await ApiService.GetResultAsync($"actividades/{_actividadSeleccionadaId}");
            if (!res.Exito)
            {
                listaPubSecciones.Items.Clear();
                var detalle = string.IsNullOrWhiteSpace(res.Mensaje) ? res.Contenido : res.Mensaje;
                lblPublicarEstado.Text = $"✗ Error {res.Codigo} al leer la actividad: {detalle}";
                return;
            }
            var obj = JObject.Parse(res.Contenido);
            // Mantenemos sincronizado el estado mostrado con el real.
            MostrarEstadoActividad(obj["estado"]?.ToString() ?? "");

            var secciones = obj["secciones"] as JArray ?? new JArray();
            listaPubSecciones.Items.Clear();
            int i = 1;
            foreach (var s in secciones)
            {
                int sid = s["seccionId"]?.Value<int>() ?? 0;
                var nombre = _seccionesNombre.TryGetValue(sid, out var n) ? n : $"Sección #{sid}";
                var fa = RecortarFecha(s["fechaApertura"]?.ToString() ?? "");
                var fc = RecortarFecha(s["fechaCierre"]?.ToString() ?? "");
                var intentos = s["maxIntentos"]?.ToString() ?? "";
                bool activa = s["activa"]?.Value<bool>() ?? true;
                listaPubSecciones.Items.Add(CrearFilaPubSeccion(i++, nombre, fa, fc, intentos, activa));
            }
            lblPublicarEstado.Text = secciones.Count == 0
                ? "Esta actividad aún no está publicada en ninguna sección."
                : $"✓ Publicada en {secciones.Count} sección(es).";
        }
        catch (Exception ex)
        {
            lblPublicarEstado.Text = $"✗ No se pudieron leer las secciones publicadas: {ex.Message}";
        }
    }

    // Normaliza una fecha/hora a ISO (yyyy-MM-ddTHH:mm:ss); null si no es válida.
    private static string? NormalizarFechaHora(string valor)
    {
        if (DateTime.TryParse(valor.Trim(), System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var f))
            return f.ToString("yyyy-MM-ddTHH:mm:ss");
        return null;
    }

    private async void PublicarSeccion_Click(object sender, RoutedEventArgs e)
    {
        if (_actividadSeleccionadaId <= 0) { lblPublicarEstado.Text = "✗ Elige primero una actividad."; return; }
        int seccionId = TagCombo(cmbPubSeccion);
        int periodoId = TagCombo(cmbPubPeriodo);
        if (seccionId <= 0 || periodoId <= 0) { lblPublicarEstado.Text = "✗ Elige sección y período."; return; }

        var apertura = NormalizarFechaHora(txtPubApertura.Text);
        var cierre = NormalizarFechaHora(txtPubCierre.Text);
        if (apertura is null || cierre is null)
        {
            lblPublicarEstado.Text = "✗ Fechas inválidas. Usa yyyy-MM-dd o yyyy-MM-dd HH:mm.";
            return;
        }
        if (!byte.TryParse(txtPubIntentos.Text.Trim(), out var maxIntentos)) maxIntentos = 1;

        int? duracion = null;
        if (int.TryParse(txtPubDuracion.Text.Trim(), out var d)) duracion = d;

        btnPublicarSeccion.IsEnabled = false;
        try
        {
            var res = await ApiService.PostAsync($"actividades/{_actividadSeleccionadaId}/secciones", new
            {
                seccionId = seccionId,
                periodoAcademicoId = periodoId,
                fechaApertura = apertura,
                fechaCierre = cierre,
                maxIntentos = (int)maxIntentos,
                duracionMaxMinutos = duracion
            });

            if (res.Exito)
            {
                await RecargarPubSecciones();
                lblPublicarEstado.Text = $"✓ {(string.IsNullOrWhiteSpace(res.Mensaje) ? "Actividad publicada a la sección." : res.Mensaje)}";
            }
            else
            {
                var detalle = string.IsNullOrWhiteSpace(res.Mensaje) ? res.Contenido : res.Mensaje;
                lblPublicarEstado.Text = $"✗ Error {res.Codigo} al publicar: {detalle}";
            }
        }
        catch (Exception ex)
        {
            lblPublicarEstado.Text = $"✗ No se pudo publicar: {ex.Message}";
        }
        finally
        {
            btnPublicarSeccion.IsEnabled = true;
        }
    }

    private void EstadoPublicar_Click(object sender, RoutedEventArgs e) => CambiarEstadoActividad("PUBLICADA");
    private void EstadoCerrar_Click(object sender, RoutedEventArgs e) => CambiarEstadoActividad("CERRADA");

    private async void CambiarEstadoActividad(string nuevoEstado)
    {
        if (_actividadSeleccionadaId <= 0) { lblPublicarEstado.Text = "✗ Elige primero una actividad."; return; }
        try
        {
            var res = await ApiService.PatchAsync($"actividades/{_actividadSeleccionadaId}/estado?estado={nuevoEstado}");
            if (res.Exito)
            {
                MostrarEstadoActividad(nuevoEstado);
                await RecargarActividades(); // refleja el nuevo estado en la lista
                lblPublicarEstado.Text = $"✓ {(string.IsNullOrWhiteSpace(res.Mensaje) ? $"Estado cambiado a {nuevoEstado}." : res.Mensaje)}";
            }
            else
            {
                var detalle = string.IsNullOrWhiteSpace(res.Mensaje) ? res.Contenido : res.Mensaje;
                lblPublicarEstado.Text = $"✗ Error {res.Codigo} al cambiar el estado: {detalle}";
            }
        }
        catch (Exception ex)
        {
            lblPublicarEstado.Text = $"✗ No se pudo cambiar el estado: {ex.Message}";
        }
    }

    private Border CrearFilaPubSeccion(int num, string seccion, string apertura, string cierre, string intentos, bool activa)
    {
        var info = Ui.Info(seccion, $"{apertura}  →  {cierre}   ·   máx. intentos: {intentos}",
            activa ? null : Ui.Pill("INACTIVA", Tono.Coral));
        return Ui.Fila(num, info);
    }

    private async System.Threading.Tasks.Task RecargarBancoPreguntas()
    {
        if (_actividadTemaId <= 0)
        {
            listaBancoPreguntas.Items.Clear();
            lblAsignarEstado.Text = "La actividad no tiene tema asociado.";
            return;
        }
        try
        {
            var res = await ApiService.GetResultAsync($"preguntas?temaId={_actividadTemaId}");
            if (!res.Exito)
            {
                listaBancoPreguntas.Items.Clear();
                var detalle = string.IsNullOrWhiteSpace(res.Mensaje) ? res.Contenido : res.Mensaje;
                lblAsignarEstado.Text = $"✗ Error {res.Codigo} al listar preguntas: {detalle}";
                return;
            }
            var array = JArray.Parse(res.Contenido);
            listaBancoPreguntas.Items.Clear();
            int i = 1;
            foreach (var item in array)
            {
                int pid = item["id"]?.Value<int>() ?? 0;
                var enunciado = item["enunciado"]?.ToString() ?? "-";
                var estado = item["estado"]?.ToString() ?? "";
                listaBancoPreguntas.Items.Add(CrearFilaBancoPregunta(i++, pid, enunciado, estado));
            }
            lblAsignarEstado.Text = array.Count == 0 ? "El tema no tiene preguntas en el banco." : $"✓ {array.Count} pregunta(s) en el banco.";
        }
        catch (Exception ex)
        {
            lblAsignarEstado.Text = $"✗ No se pudieron cargar las preguntas: {ex.Message}";
        }
    }

    // Asigna una pregunta del banco a la actividad: POST /api/actividades/{id}/preguntas
    private async void AsignarPreguntaActividad(int preguntaId)
    {
        if (_actividadSeleccionadaId <= 0) { lblAsignarEstado.Text = "✗ Elige primero una actividad."; return; }
        try
        {
            // orden 1 y puntaje 1 por defecto; el docente puede reordenar/puntuar luego.
            var res = await ApiService.PostAsync($"actividades/{_actividadSeleccionadaId}/preguntas",
                new { preguntaId = preguntaId, orden = 1, puntaje = 1.0 });
            if (res.Exito)
            {
                await RecargarActividades(); // actualiza el contador de preguntas
                lblAsignarEstado.Text = $"✓ {(string.IsNullOrWhiteSpace(res.Mensaje) ? "Pregunta asignada." : res.Mensaje)}";
            }
            else
            {
                var detalle = string.IsNullOrWhiteSpace(res.Mensaje) ? res.Contenido : res.Mensaje;
                lblAsignarEstado.Text = $"✗ Error {res.Codigo} al asignar la pregunta: {detalle}";
            }
        }
        catch (Exception ex)
        {
            lblAsignarEstado.Text = $"✗ No se pudo asignar la pregunta: {ex.Message}";
        }
    }

    private Border CrearFilaActividad(int num, int id, string titulo, string estado, int cantPreguntas, int temaId, string tema)
    {
        var info = Ui.Info(titulo, $"Tema: {tema}  ·  {cantPreguntas} pregunta(s)",
            Ui.Pill(string.IsNullOrWhiteSpace(estado) ? "—" : estado, TonoEstado(estado)));

        var btnAsignar = Ui.Accion("＋ Preguntas", Tono.Morado);
        btnAsignar.Click += (_, _) => SeleccionarActividad(id, titulo, temaId, estado);

        return Ui.Fila(num, info, btnAsignar);
    }

    private Border CrearFilaBancoPregunta(int num, int preguntaId, string enunciado, string estado)
    {
        var info = Ui.Info(enunciado, string.IsNullOrWhiteSpace(estado) ? null : estado);

        var btnAgregar = Ui.Accion("＋ Agregar", Tono.Verde);
        btnAgregar.Click += (_, _) => AsignarPreguntaActividad(preguntaId);

        return Ui.Fila(num, info, btnAgregar);
    }

    // ===== GRADOS =====

    private async System.Threading.Tasks.Task RecargarGrados()
    {
        try
        {
            var res = await ApiService.GetResultAsync(Rutas.Grados);

            if (!res.Exito)
            {
                listaGrados.Items.Clear();
                var detalle = string.IsNullOrWhiteSpace(res.Mensaje) ? res.Contenido : res.Mensaje;
                lblGradosEstado.Text = $"✗ Error {res.Codigo} al listar grados: {detalle}";
                return;
            }

            var array = JArray.Parse(res.Contenido);

            listaGrados.Items.Clear();
            int i = 1;
            foreach (var item in array)
            {
                int id = item["id"]?.Value<int>() ?? 0;
                var nombre = item["nombre"]?.ToString() ?? "-";
                var orden = item["orden"]?.ToString() ?? ""; // opcional: puede venir null
                bool activo = item["activo"]?.Value<bool?>() ?? true;
                listaGrados.Items.Add(CrearFilaGrado(i++, id, nombre, orden, activo));
            }

            lblGradosEstado.Text = $"✓ {array.Count} grado(s).";
        }
        catch (Exception ex)
        {
            lblGradosEstado.Text = $"✗ No se pudieron cargar los grados: {ex.Message}";
        }
    }

    private async void CrearGrado_Click(object sender, RoutedEventArgs e)
    {
        var nombre = txtNuevoGrado.Text.Trim();
        if (string.IsNullOrWhiteSpace(nombre))
        {
            lblGradosEstado.Text = "✗ Escribe un nombre para el grado.";
            return;
        }
        // Orden: byte opcional (0-255). Vacío = sin orden.
        if (!LeerOrden(txtOrdenGrado.Text, out var orden))
        {
            lblGradosEstado.Text = "✗ El orden debe ser un número entero entre 0 y 255 (o déjalo vacío).";
            return;
        }

        btnCrearGrado.IsEnabled = false;
        try
        {
            var res = await ApiService.PostAsync(Rutas.Grados, CuerpoGrado(nombre, orden, activo: true));

            if (res.Exito)
            {
                txtNuevoGrado.Clear();
                txtOrdenGrado.Text = "1";
                await RecargarGrados();
                lblGradosEstado.Text = $"✓ {(string.IsNullOrWhiteSpace(res.Mensaje) ? "Grado creado." : res.Mensaje)}";
            }
            else
            {
                var detalle = string.IsNullOrWhiteSpace(res.Mensaje) ? res.Contenido : res.Mensaje;
                lblGradosEstado.Text = $"✗ Error {res.Codigo} al crear el grado: {detalle}";
            }
        }
        catch (Exception ex)
        {
            lblGradosEstado.Text = $"✗ No se pudo crear el grado: {ex.Message}";
        }
        finally
        {
            btnCrearGrado.IsEnabled = true;
        }
    }

    // Lee el orden del grado: vacío => null; un número 0-255 => ese valor; otra cosa => inválido.
    private static bool LeerOrden(string texto, out byte? orden)
    {
        orden = null;
        texto = texto.Trim();
        if (texto.Length == 0) return true;
        if (!byte.TryParse(texto, NumberStyles.None, CultureInfo.InvariantCulture, out var valor)) return false;
        orden = valor;
        return true;
    }

    // Cuerpo de { nombre, orden, activo } para api/Grados. Si no hay orden, el campo
    // no se envía (es opcional en la API).
    private static Dictionary<string, object?> CuerpoGrado(string nombre, byte? orden, bool activo)
    {
        var cuerpo = new Dictionary<string, object?> { ["nombre"] = nombre, ["activo"] = activo };
        if (orden.HasValue) cuerpo["orden"] = orden.Value;
        return cuerpo;
    }

    private async void EliminarGrado(int id, string nombre)
    {
        if (!Confirmar("Eliminar grado", $"¿Eliminar el grado \"{nombre}\"?")) return;

        try
        {
            var res = await ApiService.DeleteAsync($"{Rutas.Grados}/{id}");

            if (res.Exito)
            {
                // Si borramos el grado que estaba seleccionado, cerramos su panel de secciones.
                if (_gradoSeleccionadoId == id)
                {
                    _gradoSeleccionadoId = 0;
                    panelSecciones.Visibility = Visibility.Collapsed;
                    lblSeccionesInfo.Text = "Elige un grado (botón «Secciones») para gestionar sus secciones.";
                }
                await RecargarGrados();
                lblGradosEstado.Text = $"✓ {(string.IsNullOrWhiteSpace(res.Mensaje) ? "Grado eliminado." : res.Mensaje)}";
            }
            else
            {
                var detalle = string.IsNullOrWhiteSpace(res.Mensaje) ? res.Contenido : res.Mensaje;
                lblGradosEstado.Text = $"✗ Error {res.Codigo} al eliminar el grado: {detalle}";
            }
        }
        catch (Exception ex)
        {
            lblGradosEstado.Text = $"✗ No se pudo eliminar el grado: {ex.Message}";
        }
    }

    // El ADMIN eligió un grado: abre el panel de secciones de ese grado.
    private async void SeleccionarGrado(int id, string nombre)
    {
        _gradoSeleccionadoId = id;
        lblSeccionesInfo.Text = $"Secciones del grado: {nombre}";
        lblSeccionesTitulo.Text = $"Nueva sección en {nombre}";
        txtNuevaSeccion.Clear();
        panelSecciones.Visibility = Visibility.Visible;
        await RecargarSecciones();
    }

    // ===== SECCIONES =====

    private async System.Threading.Tasks.Task RecargarSecciones()
    {
        if (_gradoSeleccionadoId <= 0) return;

        try
        {
            var res = await ApiService.GetResultAsync(Rutas.Secciones);

            if (!res.Exito)
            {
                listaSecciones.Items.Clear();
                var detalle = string.IsNullOrWhiteSpace(res.Mensaje) ? res.Contenido : res.Mensaje;
                lblSeccionesEstado.Text = $"✗ Error {res.Codigo} al listar secciones: {detalle}";
                return;
            }

            var array = JArray.Parse(res.Contenido);

            listaSecciones.Items.Clear();
            int i = 1;
            foreach (var item in array)
            {
                int gradoId = item["gradoId"]?.Value<int>() ?? 0;
                if (gradoId != _gradoSeleccionadoId) continue; // filtramos en la app

                int id = item["id"]?.Value<int>() ?? 0;
                var nombre = item["nombre"]?.ToString() ?? "-";
                var grado = item["grado"]?.ToString() ?? "";
                bool activa = item["activa"]?.Value<bool>() ?? true;
                listaSecciones.Items.Add(CrearFilaSeccion(i++, id, nombre, grado, activa));
            }

            lblSeccionesEstado.Text = (i == 1)
                ? "Este grado aún no tiene secciones."
                : $"✓ {i - 1} sección(es).";
        }
        catch (Exception ex)
        {
            lblSeccionesEstado.Text = $"✗ No se pudieron cargar las secciones: {ex.Message}";
        }
    }

    private async void CrearSeccion_Click(object sender, RoutedEventArgs e)
    {
        if (_gradoSeleccionadoId <= 0)
        {
            lblSeccionesEstado.Text = "✗ Elige primero un grado.";
            return;
        }

        var nombre = txtNuevaSeccion.Text.Trim();
        if (string.IsNullOrWhiteSpace(nombre))
        {
            lblSeccionesEstado.Text = "✗ Escribe un nombre para la sección.";
            return;
        }

        btnCrearSeccion.IsEnabled = false;
        try
        {
            var res = await ApiService.PostAsync(Rutas.Secciones,
                new { gradoId = _gradoSeleccionadoId, nombre = nombre, activa = true });

            if (res.Exito)
            {
                txtNuevaSeccion.Clear();
                await RecargarSecciones();
                lblSeccionesEstado.Text = $"✓ {(string.IsNullOrWhiteSpace(res.Mensaje) ? "Sección creada." : res.Mensaje)}";
            }
            else
            {
                var detalle = string.IsNullOrWhiteSpace(res.Mensaje) ? res.Contenido : res.Mensaje;
                lblSeccionesEstado.Text = $"✗ Error {res.Codigo} al crear la sección: {detalle}";
            }
        }
        catch (Exception ex)
        {
            lblSeccionesEstado.Text = $"✗ No se pudo crear la sección: {ex.Message}";
        }
        finally
        {
            btnCrearSeccion.IsEnabled = true;
        }
    }

    private async void EliminarSeccion(int id, string nombre)
    {
        if (!Confirmar("Eliminar sección", $"¿Eliminar la sección \"{nombre}\"?")) return;

        try
        {
            var res = await ApiService.DeleteAsync($"{Rutas.Secciones}/{id}");

            if (res.Exito)
            {
                await RecargarSecciones();
                lblSeccionesEstado.Text = $"✓ {(string.IsNullOrWhiteSpace(res.Mensaje) ? "Sección eliminada." : res.Mensaje)}";
            }
            else
            {
                var detalle = string.IsNullOrWhiteSpace(res.Mensaje) ? res.Contenido : res.Mensaje;
                lblSeccionesEstado.Text = $"✗ Error {res.Codigo} al eliminar la sección: {detalle}";
            }
        }
        catch (Exception ex)
        {
            lblSeccionesEstado.Text = $"✗ No se pudo eliminar la sección: {ex.Message}";
        }
    }

    // Fila de grado: nombre + orden, botón «Secciones» (elige), «Editar» y «Eliminar».
    private Border CrearFilaGrado(int num, int id, string nombre, string orden, bool activo)
    {
        var info = Ui.Info(nombre, string.IsNullOrWhiteSpace(orden) ? "Sin orden" : $"Orden {orden}",
            activo ? null : Ui.Pill("INACTIVO", Tono.Coral));

        var btnSecciones = Ui.Accion("Secciones", Tono.Teal);
        btnSecciones.Click += (_, _) => SeleccionarGrado(id, nombre);
        var btnEditar = Ui.Accion("✎", Tono.Morado, "Editar grado");
        btnEditar.Click += (_, _) => EditarGrado(id, nombre, orden, activo);
        var btnEliminar = Ui.Accion("🗑", Tono.Coral, "Eliminar grado");
        btnEliminar.Click += (_, _) => EliminarGrado(id, nombre);

        return Ui.Fila(num, info, btnSecciones, btnEditar, btnEliminar);
    }

    // Fila de sección: nombre + grado, botones «Editar» y «Eliminar».
    private Border CrearFilaSeccion(int num, int id, string nombre, string grado, bool activa)
    {
        var info = Ui.Info(nombre, string.IsNullOrWhiteSpace(grado) ? null : $"Grado: {grado}",
            activa ? null : Ui.Pill("INACTIVA", Tono.Coral));

        var btnEditar = Ui.Accion("✎", Tono.Morado, "Editar sección");
        btnEditar.Click += (_, _) => EditarSeccion(id, nombre, activa);
        var btnEliminar = Ui.Accion("🗑", Tono.Coral, "Eliminar sección");
        btnEliminar.Click += (_, _) => EliminarSeccion(id, nombre);

        return Ui.Fila(num, info, btnEditar, btnEliminar);
    }

    // Editar grado: PUT /api/Grados/{id} con { nombre, orden (opcional), activo }.
    private async void EditarGrado(int id, string nombreActual, string ordenActual, bool activoActual)
    {
        var datos = DialogoCampos("Editar grado",
            new[] { ("Nombre", nombreActual), ("Orden (0-255, opcional)", ordenActual) }, "Activo", activoActual);
        if (datos is null) return;

        var (valores, activo) = datos.Value;
        var nombre = valores[0];
        if (string.IsNullOrWhiteSpace(nombre))
        {
            lblGradosEstado.Text = "✗ El nombre del grado no puede quedar vacío.";
            return;
        }
        if (!LeerOrden(valores[1], out var orden))
        {
            lblGradosEstado.Text = "✗ El orden debe ser un número entero entre 0 y 255 (o déjalo vacío).";
            return;
        }

        try
        {
            var res = await ApiService.PutAsync($"{Rutas.Grados}/{id}", CuerpoGrado(nombre, orden, activo));
            if (res.Exito)
            {
                await RecargarGrados();
                lblGradosEstado.Text = $"✓ {(string.IsNullOrWhiteSpace(res.Mensaje) ? "Grado actualizado." : res.Mensaje)}";
            }
            else
            {
                var detalle = string.IsNullOrWhiteSpace(res.Mensaje) ? res.Contenido : res.Mensaje;
                lblGradosEstado.Text = $"✗ Error {res.Codigo} al editar el grado: {detalle}";
            }
        }
        catch (Exception ex)
        {
            lblGradosEstado.Text = $"✗ No se pudo editar el grado: {ex.Message}";
        }
    }

    // Editar sección: PUT /api/Secciones/{id} con { gradoId, nombre, activa }.
    private async void EditarSeccion(int id, string nombreActual, bool activaActual)
    {
        var datos = DialogoCampos("Editar sección",
            new[] { ("Nombre", nombreActual) }, "Activa", activaActual);
        if (datos is null) return;

        var (valores, activa) = datos.Value;
        var nombre = valores[0];
        if (string.IsNullOrWhiteSpace(nombre))
        {
            lblSeccionesEstado.Text = "✗ El nombre de la sección no puede quedar vacío.";
            return;
        }

        try
        {
            // El gradoId se conserva: es el grado seleccionado que estamos gestionando.
            var res = await ApiService.PutAsync($"{Rutas.Secciones}/{id}",
                new { gradoId = _gradoSeleccionadoId, nombre = nombre, activa = activa });
            if (res.Exito)
            {
                await RecargarSecciones();
                lblSeccionesEstado.Text = $"✓ {(string.IsNullOrWhiteSpace(res.Mensaje) ? "Sección actualizada." : res.Mensaje)}";
            }
            else
            {
                var detalle = string.IsNullOrWhiteSpace(res.Mensaje) ? res.Contenido : res.Mensaje;
                lblSeccionesEstado.Text = $"✗ Error {res.Codigo} al editar la sección: {detalle}";
            }
        }
        catch (Exception ex)
        {
            lblSeccionesEstado.Text = $"✗ No se pudo editar la sección: {ex.Message}";
        }
    }

    // ===== PERÍODOS ACADÉMICOS =====

    private async System.Threading.Tasks.Task RecargarPeriodos()
    {
        try
        {
            var res = await ApiService.GetResultAsync(Rutas.PeriodosAcademicos);

            if (!res.Exito)
            {
                listaPeriodos.Items.Clear();
                var detalle = string.IsNullOrWhiteSpace(res.Mensaje) ? res.Contenido : res.Mensaje;
                lblPeriodosEstado.Text = $"✗ Error {res.Codigo} al listar períodos: {detalle}";
                return;
            }

            var array = JArray.Parse(res.Contenido);

            listaPeriodos.Items.Clear();
            int i = 1;
            foreach (var item in array)
            {
                int id = item["id"]?.Value<int>() ?? 0;
                var nombre = item["nombre"]?.ToString() ?? "-";
                // DateOnly en la API: llegan como "yyyy-MM-dd".
                var fInicio = LeerFecha(item["fechaInicio"]);
                var fFin = LeerFecha(item["fechaFin"]);
                bool activo = item["activo"]?.Value<bool?>() ?? true;
                listaPeriodos.Items.Add(CrearFilaPeriodo(i++, id, nombre, fInicio, fFin, activo));
            }

            lblPeriodosEstado.Text = $"✓ {array.Count} período(s).";
        }
        catch (Exception ex)
        {
            lblPeriodosEstado.Text = $"✗ No se pudieron cargar los períodos: {ex.Message}";
        }
    }

    private static string RecortarFecha(string valor)
    {
        if (string.IsNullOrEmpty(valor)) return valor;
        var t = valor.IndexOf('T');
        return t > 0 ? valor.Substring(0, t) : valor;
    }

    // Una fecha de la API como "yyyy-MM-dd". Si llegara con hora (Newtonsoft la convierte
    // en DateTime), también la dejamos solo como fecha.
    private static string LeerFecha(JToken? valor) =>
        valor?.Type == JTokenType.Date
            ? valor.Value<DateTime>().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : RecortarFecha(valor?.ToString() ?? "");

    // Formatos aceptados al escribir una fecha (la API usa DateOnly = solo fecha).
    private static readonly string[] FormatosFecha = { "yyyy-MM-dd", "yyyy-M-d", "dd/MM/yyyy", "d/M/yyyy" };

    // Valida la fecha y la normaliza a "yyyy-MM-dd" (lo que espera DateOnly en la API);
    // devuelve null si no es una fecha válida.
    private static string? NormalizarFecha(string valor)
    {
        if (DateOnly.TryParseExact(valor.Trim(), FormatosFecha, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var fecha))
            return fecha.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        return null;
    }

    // Revisa que las dos fechas sean válidas y que el fin no sea anterior al inicio.
    // Devuelve el mensaje de error, o null si todo está bien.
    private static string? ValidarFechasPeriodo(string? fInicio, string? fFin)
    {
        if (fInicio is null || fFin is null)
            return "✗ Fechas inválidas. Usa el formato AAAA-MM-DD (p. ej. 2026-02-15).";
        if (string.CompareOrdinal(fFin, fInicio) < 0)
            return "✗ La fecha fin no puede ser anterior a la fecha inicio.";
        return null;
    }

    private async void CrearPeriodo_Click(object sender, RoutedEventArgs e)
    {
        var nombre = txtNombrePeriodo.Text.Trim();
        if (string.IsNullOrWhiteSpace(nombre))
        {
            lblPeriodosEstado.Text = "✗ Escribe un nombre para el período.";
            return;
        }
        var fInicio = NormalizarFecha(txtFechaInicio.Text);
        var fFin = NormalizarFecha(txtFechaFin.Text);
        var errorFechas = ValidarFechasPeriodo(fInicio, fFin);
        if (errorFechas != null)
        {
            lblPeriodosEstado.Text = errorFechas;
            return;
        }

        btnCrearPeriodo.IsEnabled = false;
        try
        {
            var res = await ApiService.PostAsync(Rutas.PeriodosAcademicos, new
            {
                nombre = nombre,
                fechaInicio = fInicio,
                fechaFin = fFin,
                activo = chkActivoPeriodo.IsChecked == true
            });

            if (res.Exito)
            {
                txtNombrePeriodo.Clear();
                txtFechaInicio.Clear();
                txtFechaFin.Clear();
                chkActivoPeriodo.IsChecked = true;
                await RecargarPeriodos();
                lblPeriodosEstado.Text = $"✓ {(string.IsNullOrWhiteSpace(res.Mensaje) ? "Período creado." : res.Mensaje)}";
            }
            else
            {
                var detalle = string.IsNullOrWhiteSpace(res.Mensaje) ? res.Contenido : res.Mensaje;
                lblPeriodosEstado.Text = $"✗ Error {res.Codigo} al crear el período: {detalle}";
            }
        }
        catch (Exception ex)
        {
            lblPeriodosEstado.Text = $"✗ No se pudo crear el período: {ex.Message}";
        }
        finally
        {
            btnCrearPeriodo.IsEnabled = true;
        }
    }

    // Editar período: PUT /api/PeriodosAcademicos/{id} con { nombre, fechaInicio, fechaFin, activo }.
    private async void EditarPeriodo(int id, string nombreActual, string fInicioActual, string fFinActual, bool activoActual)
    {
        var datos = DialogoCampos("Editar período",
            new[]
            {
                ("Nombre", nombreActual),
                ("Fecha inicio (AAAA-MM-DD)", fInicioActual),
                ("Fecha fin (AAAA-MM-DD)", fFinActual)
            }, "Activo", activoActual);
        if (datos is null) return;

        var (valores, activo) = datos.Value;
        var nombre = valores[0];
        var fInicio = NormalizarFecha(valores[1]);
        var fFin = NormalizarFecha(valores[2]);
        if (string.IsNullOrWhiteSpace(nombre))
        {
            lblPeriodosEstado.Text = "✗ El nombre del período no puede quedar vacío.";
            return;
        }
        var errorFechas = ValidarFechasPeriodo(fInicio, fFin);
        if (errorFechas != null)
        {
            lblPeriodosEstado.Text = errorFechas;
            return;
        }

        try
        {
            var res = await ApiService.PutAsync($"{Rutas.PeriodosAcademicos}/{id}", new
            {
                nombre = nombre,
                fechaInicio = fInicio,
                fechaFin = fFin,
                activo = activo
            });
            if (res.Exito)
            {
                await RecargarPeriodos();
                lblPeriodosEstado.Text = $"✓ {(string.IsNullOrWhiteSpace(res.Mensaje) ? "Período actualizado." : res.Mensaje)}";
            }
            else
            {
                var detalle = string.IsNullOrWhiteSpace(res.Mensaje) ? res.Contenido : res.Mensaje;
                lblPeriodosEstado.Text = $"✗ Error {res.Codigo} al editar el período: {detalle}";
            }
        }
        catch (Exception ex)
        {
            lblPeriodosEstado.Text = $"✗ No se pudo editar el período: {ex.Message}";
        }
    }

    private async void EliminarPeriodo(int id, string nombre)
    {
        if (!Confirmar("Eliminar período", $"¿Eliminar el período \"{nombre}\"?")) return;

        try
        {
            var res = await ApiService.DeleteAsync($"{Rutas.PeriodosAcademicos}/{id}");
            if (res.Exito)
            {
                await RecargarPeriodos();
                lblPeriodosEstado.Text = $"✓ {(string.IsNullOrWhiteSpace(res.Mensaje) ? "Período eliminado." : res.Mensaje)}";
            }
            else
            {
                var detalle = string.IsNullOrWhiteSpace(res.Mensaje) ? res.Contenido : res.Mensaje;
                lblPeriodosEstado.Text = $"✗ Error {res.Codigo} al eliminar el período: {detalle}";
            }
        }
        catch (Exception ex)
        {
            lblPeriodosEstado.Text = $"✗ No se pudo eliminar el período: {ex.Message}";
        }
    }

    // Fila de período: nombre + fechas, botones Editar / Eliminar.
    private Border CrearFilaPeriodo(int num, int id, string nombre, string fInicio, string fFin, bool activo)
    {
        var info = Ui.Info(nombre, $"📅  {fInicio}  →  {fFin}",
            activo ? Ui.Pill("ACTIVO", Tono.Verde) : Ui.Pill("INACTIVO", Tono.Coral));

        var btnEditar = Ui.Accion("✎ Editar", Tono.Morado);
        btnEditar.Click += (_, _) => EditarPeriodo(id, nombre, fInicio, fFin, activo);
        var btnEliminar = Ui.Accion("🗑 Eliminar", Tono.Coral);
        btnEliminar.Click += (_, _) => EliminarPeriodo(id, nombre);

        return Ui.Fila(num, info, btnEditar, btnEliminar);
    }

    // Diálogo genérico: N campos de texto + un check. Devuelve (valores, activo) o null.
    private (string[] valores, bool activo)? DialogoCampos(
        string titulo, (string etiqueta, string valor)[] campos, string activoLabel, bool activoInicial)
    {
        (string[], bool)? resultado = null;

        var cont = new StackPanel();
        var cajas = campos.Select(c => CampoDialogo(cont, c.etiqueta, c.valor)).ToList();

        var chk = new CheckBox
        {
            Content = activoLabel,
            IsChecked = activoInicial,
            Margin = new Thickness(2, 4, 0, 0)
        };
        cont.Children.Add(chk);

        var dlg = NuevoDialogo(titulo, "Modifica los datos y pulsa «Guardar».", cont,
            out var btnCancelar, out var btnGuardar, "Guardar", "BtnPrimario");
        btnCancelar.Click += (_, _) => { dlg.DialogResult = false; };
        btnGuardar.Click += (_, _) =>
        {
            resultado = (cajas.Select(c => c.Text.Trim()).ToArray(), chk.IsChecked == true);
            dlg.DialogResult = true;
        };
        dlg.KeyDown += (_, e) => { if (e.Key == Key.Escape) dlg.DialogResult = false; };
        if (cajas.Count > 0) dlg.Loaded += (_, _) => { cajas[0].Focus(); cajas[0].SelectAll(); };

        var ok = dlg.ShowDialog();
        return ok == true ? resultado : null;
    }

    // Diálogo de confirmación (para eliminar o quitar). Devuelve true si el usuario acepta.
    private bool Confirmar(string titulo, string mensaje, string textoAceptar = "Sí, eliminar",
        string aviso = "Esta acción no se puede deshacer.")
    {
        var cont = new StackPanel();
        cont.Children.Add(new TextBlock
        {
            Text = mensaje,
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap
        });
        cont.Children.Add(new TextBlock
        {
            Text = aviso,
            FontSize = 13,
            Foreground = Paleta.Apagado,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 0)
        });

        var dlg = NuevoDialogo(titulo, null, cont,
            out var btnCancelar, out var btnAceptar, textoAceptar, "BtnPeligro", icono: "!");
        btnCancelar.Click += (_, _) => { dlg.DialogResult = false; };
        btnAceptar.Click += (_, _) => { dlg.DialogResult = true; };
        dlg.KeyDown += (_, e) => { if (e.Key == Key.Escape) dlg.DialogResult = false; };
        dlg.Loaded += (_, _) => btnCancelar.Focus(); // por seguridad, el foco empieza en «Cancelar»

        return dlg.ShowDialog() == true;
    }

    // Arma una ventana de diálogo con el estilo claro: tarjeta blanca redondeada con sombra,
    // título, el contenido recibido y los botones Cancelar / Aceptar abajo a la derecha.
    private Window NuevoDialogo(string titulo, string? subtitulo, StackPanel contenido,
        out Button btnCancelar, out Button btnAceptar, string textoAceptar, string estiloAceptar,
        string? icono = null)
    {
        var dlg = new Window
        {
            Title = titulo,
            Width = 500,
            SizeToContent = SizeToContent.Height,
            Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            FontFamily = (FontFamily)FindResource("FuenteApp"),
            Foreground = Paleta.Navy
        };

        var cuerpo = new StackPanel();

        // Cabecera: (icono) + título + subtítulo opcional.
        var cabecera = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 18) };
        if (icono != null)
        {
            var (fuerte, suave) = Ui.Colores(Tono.Coral);
            cabecera.Children.Add(new Border
            {
                Width = 44,
                Height = 44,
                CornerRadius = new CornerRadius(22),
                Background = suave,
                Margin = new Thickness(0, 0, 14, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = icono,
                    FontSize = 22,
                    FontWeight = FontWeights.Black,
                    Foreground = fuerte,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
            });
        }
        var textos = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        textos.Children.Add(new TextBlock { Text = titulo, FontSize = 22, FontWeight = FontWeights.ExtraBold });
        if (subtitulo != null)
            textos.Children.Add(new TextBlock
            {
                Text = subtitulo,
                FontSize = 13,
                Foreground = Paleta.Apagado,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 3, 0, 0)
            });
        cabecera.Children.Add(textos);
        cuerpo.Children.Add(cabecera);
        cuerpo.Children.Add(contenido);

        var fila = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 24, 0, 0)
        };
        btnCancelar = new Button
        {
            Content = "Cancelar",
            Style = (Style)FindResource("BtnSecundario"),
            MinWidth = 120,
            Margin = new Thickness(0, 0, 10, 0)
        };
        btnAceptar = new Button
        {
            Content = textoAceptar,
            Style = (Style)FindResource(estiloAceptar),
            MinWidth = 140
        };
        fila.Children.Add(btnCancelar);
        fila.Children.Add(btnAceptar);
        cuerpo.Children.Add(fila);

        // Sombra en un borde aparte (detrás) para no re-renderizar el contenido con el efecto.
        var raiz = new Grid { Margin = new Thickness(24) };
        raiz.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(26),
            Background = Brushes.White,
            Effect = (Effect)FindResource("SombraSuave")
        });
        raiz.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(26),
            Background = Brushes.White,
            BorderBrush = Paleta.Borde,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(28, 26, 28, 26),
            Child = cuerpo
        });
        dlg.Content = raiz;
        return dlg;
    }

    // Agrega a 'cont' una etiqueta + caja de texto (con el estilo del tema) y devuelve la caja.
    private TextBox CampoDialogo(StackPanel cont, string etiqueta, string valor)
    {
        cont.Children.Add(new TextBlock { Text = etiqueta, Style = (Style)FindResource("Etiqueta") });
        var caja = new TextBox { Text = valor, Margin = new Thickness(0, 0, 0, 14) };
        cont.Children.Add(caja);
        return caja;
    }

    // Los mensajes de estado (✓ / ✗) se muestran en una cajita de color:
    // verde si salió bien, coral si hubo error, neutra en otro caso; oculta si no hay texto.
    private void ColorearMensajesDeEstado()
    {
        var etiquetas = new[]
        {
            lblEstado, lblMateriasEstado, lblUsuariosEstado, lblGradosEstado, lblSeccionesEstado,
            lblPeriodosEstado, lblAsignacionesEstado, lblEstudiantesEstado, lblTemasEstado, lblSubtemasEstado,
            lblPreguntaEstado, lblPreguntasEstado, lblActividadesEstado, lblAsignarEstado, lblPublicarEstado
        };
        var descriptor = DependencyPropertyDescriptor.FromProperty(TextBlock.TextProperty, typeof(TextBlock));
        foreach (var lbl in etiquetas)
        {
            if (lbl.Parent is not Border caja) continue;
            void Pintar()
            {
                var texto = lbl.Text ?? "";
                caja.Visibility = string.IsNullOrWhiteSpace(texto) ? Visibility.Collapsed : Visibility.Visible;
                var tono = texto.StartsWith("✗") ? Tono.Coral : texto.StartsWith("✓") ? Tono.Verde : Tono.Neutro;
                var (fuerte, suave) = Ui.Colores(tono);
                caja.Background = suave;
                caja.BorderBrush = tono == Tono.Neutro ? Paleta.Borde : fuerte;
            }
            descriptor.AddValueChanged(lbl, (_, _) => Pintar());
            Pintar();
        }
    }

    // Crea una materia (POST real a la API) y refresca la lista para verla al instante.
    private async void CrearMateria_Click(object sender, RoutedEventArgs e)
    {
        var nombre = txtNuevaMateria.Text.Trim();
        if (string.IsNullOrWhiteSpace(nombre))
        {
            lblMateriasEstado.Text = "✗ Escribe un nombre para la materia.";
            return;
        }

        btnCrear.IsEnabled = false;
        try
        {
            // Crear materias es acción de ADMIN (POST api/Materias).
            var res = await ApiService.PostAsync(Rutas.Materias, new { nombre = nombre, activa = true });

            if (res.Exito)
            {
                txtNuevaMateria.Clear();
                await RecargarMaterias(); // refresca => aparece la nueva
                lblMateriasEstado.Text = $"✓ {(string.IsNullOrWhiteSpace(res.Mensaje) ? "Materia creada." : res.Mensaje)}";
            }
            else
            {
                var detalle = string.IsNullOrWhiteSpace(res.Mensaje) ? res.Contenido : res.Mensaje;
                lblMateriasEstado.Text = $"✗ Error {res.Codigo} al crear la materia: {detalle}";
            }
        }
        catch (Exception ex)
        {
            lblMateriasEstado.Text = $"✗ No se pudo crear la materia: {ex.Message}";
        }
        finally
        {
            btnCrear.IsEnabled = true;
        }
    }

    // Crea un usuario (POST /api/Usuarios) y refresca la lista de usuarios.
    private async void CrearUsuario_Click(object sender, RoutedEventArgs e)
    {
        var nombreCompleto = txtNombreCompleto.Text.Trim();
        var correoOUsuario = txtCorreoUsuario.Text.Trim();
        var clave = pwdClave.Password;

        if (string.IsNullOrWhiteSpace(nombreCompleto) ||
            string.IsNullOrWhiteSpace(correoOUsuario) ||
            string.IsNullOrWhiteSpace(clave))
        {
            lblUsuariosEstado.Text = "✗ Completa nombre, usuario/correo y contraseña.";
            return;
        }

        if (cmbRol.SelectedIndex < 0)
        {
            lblUsuariosEstado.Text = "✗ Elige un rol.";
            return;
        }
        // ComboBox: 0=ADMIN, 1=DOCENTE, 2=ESTUDIANTE  =>  rolId 1, 2, 3
        int rolId = cmbRol.SelectedIndex + 1;

        btnCrearUsuario.IsEnabled = false;
        try
        {
            var res = await ApiService.PostAsync(Rutas.Usuarios, new
            {
                nombreCompleto = nombreCompleto,
                correoOUsuario = correoOUsuario,
                clave = clave,
                rolId = rolId
            });

            if (res.Exito)
            {
                txtNombreCompleto.Clear();
                txtCorreoUsuario.Clear();
                pwdClave.Clear();
                await RecargarUsuarios(); // refresca => aparece el nuevo usuario
                lblUsuariosEstado.Text = $"✓ {(string.IsNullOrWhiteSpace(res.Mensaje) ? "Usuario creado." : res.Mensaje)}";
            }
            else
            {
                var detalle = string.IsNullOrWhiteSpace(res.Mensaje) ? res.Contenido : res.Mensaje;
                lblUsuariosEstado.Text = $"✗ Error {res.Codigo} al crear el usuario: {detalle}";
            }
        }
        catch (Exception ex)
        {
            lblUsuariosEstado.Text = $"✗ No se pudo crear el usuario: {ex.Message}";
        }
        finally
        {
            btnCrearUsuario.IsEnabled = true;
        }
    }

    // Fila de usuario con botones Editar / Eliminar (solo ADMIN).
    private Border CrearFilaUsuario(int num, int id, string nombreCompleto, string correo,
        string rol, int rolId, bool activo)
    {
        // Nombre + etiqueta de estado (verde activo / coral inactivo), y debajo correo · rol.
        var sub = string.IsNullOrWhiteSpace(correo) ? rol : $"{correo}  ·  {rol}";
        var info = Ui.Info(nombreCompleto, sub,
            activo ? Ui.Pill("ACTIVO", Tono.Verde) : Ui.Pill("INACTIVO", Tono.Coral));

        var btnEditar = Ui.Accion("✎ Editar", Tono.Morado);
        btnEditar.Click += (_, _) => EditarUsuario(id, nombreCompleto, correo, rolId, activo);
        var btnEliminar = Ui.Accion("🗑", Tono.Coral, "Eliminar usuario");
        btnEliminar.Click += (_, _) => EliminarUsuario(id, nombreCompleto);

        return Ui.Fila(num, info, btnEditar, btnEliminar);
    }

    // Editar usuario: PUT /api/Usuarios/{id} con { nombreCompleto, correoOUsuario, rolId, activo }.
    // NO cambia la contraseña.
    private async void EditarUsuario(int id, string nombreActual, string correoActual, int rolIdActual, bool activoActual)
    {
        var datos = PedirDatosUsuario(nombreActual, correoActual, rolIdActual, activoActual);
        if (datos is null) return; // canceló

        var (nombre, correo, rolId, activo) = datos.Value;
        if (string.IsNullOrWhiteSpace(nombre) || string.IsNullOrWhiteSpace(correo))
        {
            lblUsuariosEstado.Text = "✗ Nombre y usuario/correo no pueden quedar vacíos.";
            return;
        }

        try
        {
            var res = await ApiService.PutAsync($"{Rutas.Usuarios}/{id}", new
            {
                nombreCompleto = nombre,
                correoOUsuario = correo,
                rolId = rolId,
                activo = activo
            });

            if (res.Exito)
            {
                await RecargarUsuarios();
                lblUsuariosEstado.Text = $"✓ {(string.IsNullOrWhiteSpace(res.Mensaje) ? "Usuario actualizado." : res.Mensaje)}";
            }
            else
            {
                var detalle = string.IsNullOrWhiteSpace(res.Mensaje) ? res.Contenido : res.Mensaje;
                lblUsuariosEstado.Text = $"✗ Error {res.Codigo} al editar el usuario: {detalle}";
            }
        }
        catch (Exception ex)
        {
            lblUsuariosEstado.Text = $"✗ No se pudo editar el usuario: {ex.Message}";
        }
    }

    // Eliminar usuario: DELETE /api/Usuarios/{id} (con confirmación).
    private async void EliminarUsuario(int id, string nombre)
    {
        if (!Confirmar("Eliminar usuario", $"¿Eliminar al usuario \"{nombre}\"?")) return;

        try
        {
            var res = await ApiService.DeleteAsync($"{Rutas.Usuarios}/{id}");

            if (res.Exito)
            {
                await RecargarUsuarios();
                lblUsuariosEstado.Text = $"✓ {(string.IsNullOrWhiteSpace(res.Mensaje) ? "Usuario eliminado." : res.Mensaje)}";
            }
            else
            {
                var detalle = string.IsNullOrWhiteSpace(res.Mensaje) ? res.Contenido : res.Mensaje;
                lblUsuariosEstado.Text = $"✗ Error {res.Codigo} al eliminar el usuario: {detalle}";
            }
        }
        catch (Exception ex)
        {
            lblUsuariosEstado.Text = $"✗ No se pudo eliminar el usuario: {ex.Message}";
        }
    }

    // Diálogo para editar un usuario (nombre, correo, rol, activo). No pide contraseña.
    // Devuelve (nombre, correo, rolId, activo) o null si se cancela.
    private (string nombre, string correo, int rolId, bool activo)? PedirDatosUsuario(
        string nombreActual, string correoActual, int rolIdActual, bool activoActual)
    {
        (string, string, int, bool)? resultado = null;

        var cont = new StackPanel();
        var cajaNombre = CampoDialogo(cont, "Nombre completo", nombreActual);
        var cajaCorreo = CampoDialogo(cont, "Usuario o correo", correoActual);

        // Rol
        cont.Children.Add(new TextBlock { Text = "Rol", Style = (Style)FindResource("Etiqueta") });
        var combo = new ComboBox { Margin = new Thickness(0, 0, 0, 14) };
        foreach (var rol in Roles) combo.Items.Add(new ComboBoxItem { Content = rol });
        // rolId 1..3 => índice 0..2; si viene fuera de rango, ESTUDIANTE por defecto.
        combo.SelectedIndex = (rolIdActual >= 1 && rolIdActual <= Roles.Length) ? rolIdActual - 1 : 2;
        cont.Children.Add(combo);

        var chkActivo = new CheckBox
        {
            Content = "Usuario activo",
            IsChecked = activoActual,
            Margin = new Thickness(2, 4, 0, 0)
        };
        cont.Children.Add(chkActivo);

        var dlg = NuevoDialogo("Editar usuario", "La contraseña no se modifica desde aquí.", cont,
            out var btnCancelar, out var btnGuardar, "Guardar", "BtnPrimario");
        btnCancelar.Click += (_, _) => { dlg.DialogResult = false; };
        btnGuardar.Click += (_, _) =>
        {
            int rolId = combo.SelectedIndex + 1; // 0..2 => 1..3
            resultado = (cajaNombre.Text.Trim(), cajaCorreo.Text.Trim(), rolId, chkActivo.IsChecked == true);
            dlg.DialogResult = true;
        };

        dlg.KeyDown += (_, e) => { if (e.Key == Key.Escape) dlg.DialogResult = false; };
        dlg.Loaded += (_, _) => { cajaNombre.Focus(); cajaNombre.SelectAll(); };

        var ok = dlg.ShowDialog();
        return ok == true ? resultado : null;
    }

    // El DOCENTE eligió una materia: abre el panel de temas y carga sus temas.
    private async void SeleccionarMateria(int id, string nombre)
    {
        _materiaSeleccionadaId = id;
        lblTemasTitulo.Text = $"Temas de: {nombre}";
        txtNuevoTema.Clear();
        tabsDocente.Visibility = Visibility.Visible;
        // Al cambiar de materia se ocultan subtemas y preguntas hasta elegir un tema.
        _temaSeleccionadoId = 0;
        panelSubtemas.Visibility = Visibility.Collapsed;
        panelPregunta.Visibility = Visibility.Collapsed;
        await CargarTemas();
    }

    // El DOCENTE eligió un tema: muestra subtemas (pestaña Temas) y preguntas (pestaña Preguntas).
    private async void SeleccionarTema(int id, string nombre)
    {
        _temaSeleccionadoId = id;
        lblTemaDetalleTitulo.Text = $"Tema: {nombre}";
        lblPreguntaTitulo.Text = $"Crear pregunta en: {nombre}";
        LimpiarFormularioPregunta();
        txtNuevoSubtema.Clear();
        panelSubtemas.Visibility = Visibility.Visible;
        panelPregunta.Visibility = Visibility.Visible;
        await RecargarSubtemas();
        await RecargarPreguntas();
    }

    private void LimpiarFormularioPregunta()
    {
        txtEnunciado.Clear();
        txtExplicacion.Clear();
        txtOp1.Clear();
        txtOp2.Clear();
        txtOp3.Clear();
        txtOp4.Clear();
        rbOp1.IsChecked = true; // por defecto, la primera opción es la correcta
    }

    // Lista los temas de la materia elegida: GET /api/temas?materiaId={id}
    private async System.Threading.Tasks.Task CargarTemas()
    {
        if (_materiaSeleccionadaId <= 0) return;

        try
        {
            var res = await ApiService.GetResultAsync($"temas?materiaId={_materiaSeleccionadaId}");

            if (!res.Exito)
            {
                listaTemas.Items.Clear();
                var detalle = string.IsNullOrWhiteSpace(res.Mensaje) ? res.Contenido : res.Mensaje;
                lblTemasEstado.Text = $"✗ Error {res.Codigo} al listar temas: {detalle}";
                return;
            }

            var array = JArray.Parse(res.Contenido);

            listaTemas.Items.Clear();
            int i = 1;
            foreach (var item in array)
            {
                var nombre = item["nombre"]?.ToString() ?? "-";
                int temaId = item["id"]?.Value<int>() ?? 0;
                var orden = item["orden"]?.ToString() ?? "";
                bool activo = item["activo"]?.Value<bool>() ?? true;
                listaTemas.Items.Add(CrearFilaTema(i++, nombre, temaId, orden, activo, SeleccionarTema));
            }

            lblTemasEstado.Text = array.Count == 0
                ? "Esta materia aún no tiene temas."
                : $"✓ {array.Count} tema(s).";
        }
        catch (Exception ex)
        {
            lblTemasEstado.Text = $"✗ No se pudieron cargar los temas: {ex.Message}";
        }
    }

    // Crea un tema para la materia elegida: POST /api/temas y refresca la lista.
    private async void CrearTema_Click(object sender, RoutedEventArgs e)
    {
        if (_materiaSeleccionadaId <= 0)
        {
            lblTemasEstado.Text = "✗ Selecciona primero una materia de la izquierda.";
            return;
        }

        var nombre = txtNuevoTema.Text.Trim();
        if (string.IsNullOrWhiteSpace(nombre))
        {
            lblTemasEstado.Text = "✗ Escribe un nombre para el tema.";
            return;
        }

        btnCrearTema.IsEnabled = false;
        try
        {
            var res = await ApiService.PostAsync("temas",
                new { materiaId = _materiaSeleccionadaId, nombre = nombre, orden = 1, activo = true });

            if (res.Exito)
            {
                txtNuevoTema.Clear();
                await CargarTemas(); // refresca => aparece el nuevo tema
                lblTemasEstado.Text = $"✓ {(string.IsNullOrWhiteSpace(res.Mensaje) ? "Tema creado." : res.Mensaje)}";
            }
            else
            {
                var detalle = string.IsNullOrWhiteSpace(res.Mensaje) ? res.Contenido : res.Mensaje;
                lblTemasEstado.Text = $"✗ Error {res.Codigo} al crear el tema: {detalle}";
            }
        }
        catch (Exception ex)
        {
            lblTemasEstado.Text = $"✗ No se pudo crear el tema: {ex.Message}";
        }
        finally
        {
            btnCrearTema.IsEnabled = true;
        }
    }

    // Crea una pregunta de opción múltiple en el tema elegido: POST /api/preguntas
    private async void CrearPregunta_Click(object sender, RoutedEventArgs e)
    {
        if (_temaSeleccionadoId <= 0)
        {
            lblPreguntaEstado.Text = "✗ Selecciona primero un tema.";
            return;
        }

        var enunciado = txtEnunciado.Text.Trim();
        var op1 = txtOp1.Text.Trim();
        var op2 = txtOp2.Text.Trim();
        var op3 = txtOp3.Text.Trim();
        var op4 = txtOp4.Text.Trim();

        if (string.IsNullOrWhiteSpace(enunciado))
        {
            lblPreguntaEstado.Text = "✗ Escribe el enunciado de la pregunta.";
            return;
        }
        if (string.IsNullOrWhiteSpace(op1) || string.IsNullOrWhiteSpace(op2) ||
            string.IsNullOrWhiteSpace(op3) || string.IsNullOrWhiteSpace(op4))
        {
            lblPreguntaEstado.Text = "✗ Completa las cuatro opciones de respuesta.";
            return;
        }

        var explicacion = txtExplicacion.Text.Trim();

        var cuerpo = new
        {
            temaId = _temaSeleccionadoId,
            subtemaId = (int?)null,
            tipoPreguntaId = 1,
            nivelDificultadId = 2,
            enunciado = enunciado,
            explicacion = string.IsNullOrWhiteSpace(explicacion) ? null : explicacion,
            respuestaReferencia = (string?)null,
            disponiblePractica = true,
            estadoContenidoId = 1,
            creadaPorUsuarioId = ApiService.UsuarioId,
            opciones = new[]
            {
                new { texto = op1, esCorrecta = rbOp1.IsChecked == true, retroalimentacion = (string?)null, orden = 1 },
                new { texto = op2, esCorrecta = rbOp2.IsChecked == true, retroalimentacion = (string?)null, orden = 2 },
                new { texto = op3, esCorrecta = rbOp3.IsChecked == true, retroalimentacion = (string?)null, orden = 3 },
                new { texto = op4, esCorrecta = rbOp4.IsChecked == true, retroalimentacion = (string?)null, orden = 4 },
            }
        };

        btnCrearPregunta.IsEnabled = false;
        try
        {
            var res = await ApiService.PostAsync("preguntas", cuerpo);

            if (res.Exito)
            {
                LimpiarFormularioPregunta();
                lblPreguntaEstado.Text = $"✓ {(string.IsNullOrWhiteSpace(res.Mensaje) ? "Pregunta creada." : res.Mensaje)}";
            }
            else
            {
                var detalle = string.IsNullOrWhiteSpace(res.Mensaje) ? res.Contenido : res.Mensaje;
                lblPreguntaEstado.Text = $"✗ Error {res.Codigo} al crear la pregunta: {detalle}";
            }
        }
        catch (Exception ex)
        {
            lblPreguntaEstado.Text = $"✗ No se pudo crear la pregunta: {ex.Message}";
        }
        finally
        {
            btnCrearPregunta.IsEnabled = true;
        }
    }

    // Fila de tema (clicable: al pulsar se selecciona) con botones Editar / Eliminar.
    private Border CrearFilaTema(int num, string nombre, int temaId, string orden, bool activo, Action<int, string> onClick)
    {
        var info = Ui.Info(nombre, null, activo ? null : Ui.Pill("INACTIVO", Tono.Coral));

        var btnEditar = Ui.Accion("✎", Tono.Morado, "Editar tema");
        btnEditar.Click += (_, _) => EditarTema(temaId, nombre, orden, activo);
        var btnEliminar = Ui.Accion("🗑", Tono.Coral, "Eliminar tema");
        btnEliminar.Click += (_, _) => EliminarTema(temaId, nombre);

        var fila = Ui.Fila(num, info, btnEditar, btnEliminar);
        // Seleccionar el tema, salvo que el clic venga de un botón de acción de la fila.
        Ui.HacerClicable(fila, () => onClick(temaId, nombre));
        return fila;
    }

    // ===== TEMAS: editar / eliminar =====

    private async void EditarTema(int id, string nombreActual, string ordenActual, bool activoActual)
    {
        var datos = DialogoCampos("Editar tema",
            new[] { ("Nombre", nombreActual), ("Orden", ordenActual) }, "Activo", activoActual);
        if (datos is null) return;

        var (valores, activo) = datos.Value;
        var nombre = valores[0];
        if (string.IsNullOrWhiteSpace(nombre))
        {
            lblTemasEstado.Text = "✗ El nombre del tema no puede quedar vacío.";
            return;
        }
        if (!int.TryParse(valores[1], out var orden)) orden = 1;

        try
        {
            var res = await ApiService.PutAsync($"temas/{id}",
                new { materiaId = _materiaSeleccionadaId, nombre = nombre, orden = orden, activo = activo });
            if (res.Exito)
            {
                await CargarTemas();
                lblTemasEstado.Text = $"✓ {(string.IsNullOrWhiteSpace(res.Mensaje) ? "Tema actualizado." : res.Mensaje)}";
            }
            else
            {
                var detalle = string.IsNullOrWhiteSpace(res.Mensaje) ? res.Contenido : res.Mensaje;
                lblTemasEstado.Text = $"✗ Error {res.Codigo} al editar el tema: {detalle}";
            }
        }
        catch (Exception ex) { lblTemasEstado.Text = $"✗ No se pudo editar el tema: {ex.Message}"; }
    }

    private async void EliminarTema(int id, string nombre)
    {
        if (!Confirmar("Eliminar tema", $"¿Eliminar el tema \"{nombre}\"?")) return;

        try
        {
            var res = await ApiService.DeleteAsync($"temas/{id}");
            if (res.Exito)
            {
                // Si borramos el tema seleccionado, cerramos su panel de detalle.
                if (_temaSeleccionadoId == id)
                {
                    _temaSeleccionadoId = 0;
                    panelSubtemas.Visibility = Visibility.Collapsed;
                    panelPregunta.Visibility = Visibility.Collapsed;
                }
                await CargarTemas();
                lblTemasEstado.Text = $"✓ {(string.IsNullOrWhiteSpace(res.Mensaje) ? "Tema eliminado." : res.Mensaje)}";
            }
            else
            {
                var detalle = string.IsNullOrWhiteSpace(res.Mensaje) ? res.Contenido : res.Mensaje;
                lblTemasEstado.Text = $"✗ Error {res.Codigo} al eliminar el tema: {detalle}";
            }
        }
        catch (Exception ex) { lblTemasEstado.Text = $"✗ No se pudo eliminar el tema: {ex.Message}"; }
    }

    // ===== SUBTEMAS =====

    private async System.Threading.Tasks.Task RecargarSubtemas()
    {
        if (_temaSeleccionadoId <= 0) return;
        try
        {
            var res = await ApiService.GetResultAsync($"subtemas?temaId={_temaSeleccionadoId}");
            if (!res.Exito)
            {
                listaSubtemas.Items.Clear();
                var detalle = string.IsNullOrWhiteSpace(res.Mensaje) ? res.Contenido : res.Mensaje;
                lblSubtemasEstado.Text = $"✗ Error {res.Codigo} al listar subtemas: {detalle}";
                return;
            }
            var array = JArray.Parse(res.Contenido);
            listaSubtemas.Items.Clear();
            int i = 1;
            foreach (var item in array)
            {
                int id = item["id"]?.Value<int>() ?? 0;
                var nombre = item["nombre"]?.ToString() ?? "-";
                var orden = item["orden"]?.ToString() ?? "";
                bool activo = item["activo"]?.Value<bool>() ?? true;
                listaSubtemas.Items.Add(CrearFilaSubtema(i++, id, nombre, orden, activo));
            }
            lblSubtemasEstado.Text = array.Count == 0 ? "Este tema aún no tiene subtemas." : $"✓ {array.Count} subtema(s).";
        }
        catch (Exception ex) { lblSubtemasEstado.Text = $"✗ No se pudieron cargar los subtemas: {ex.Message}"; }
    }

    private async void CrearSubtema_Click(object sender, RoutedEventArgs e)
    {
        if (_temaSeleccionadoId <= 0) { lblSubtemasEstado.Text = "✗ Selecciona primero un tema."; return; }
        var nombre = txtNuevoSubtema.Text.Trim();
        if (string.IsNullOrWhiteSpace(nombre)) { lblSubtemasEstado.Text = "✗ Escribe un nombre para el subtema."; return; }

        btnCrearSubtema.IsEnabled = false;
        try
        {
            var res = await ApiService.PostAsync("subtemas",
                new { temaId = _temaSeleccionadoId, nombre = nombre, orden = 1, activo = true });
            if (res.Exito)
            {
                txtNuevoSubtema.Clear();
                await RecargarSubtemas();
                lblSubtemasEstado.Text = $"✓ {(string.IsNullOrWhiteSpace(res.Mensaje) ? "Subtema creado." : res.Mensaje)}";
            }
            else
            {
                var detalle = string.IsNullOrWhiteSpace(res.Mensaje) ? res.Contenido : res.Mensaje;
                lblSubtemasEstado.Text = $"✗ Error {res.Codigo} al crear el subtema: {detalle}";
            }
        }
        catch (Exception ex) { lblSubtemasEstado.Text = $"✗ No se pudo crear el subtema: {ex.Message}"; }
        finally { btnCrearSubtema.IsEnabled = true; }
    }

    private async void EditarSubtema(int id, string nombreActual, string ordenActual, bool activoActual)
    {
        var datos = DialogoCampos("Editar subtema",
            new[] { ("Nombre", nombreActual), ("Orden", ordenActual) }, "Activo", activoActual);
        if (datos is null) return;

        var (valores, activo) = datos.Value;
        var nombre = valores[0];
        if (string.IsNullOrWhiteSpace(nombre)) { lblSubtemasEstado.Text = "✗ El nombre del subtema no puede quedar vacío."; return; }
        if (!int.TryParse(valores[1], out var orden)) orden = 1;

        try
        {
            var res = await ApiService.PutAsync($"subtemas/{id}",
                new { temaId = _temaSeleccionadoId, nombre = nombre, orden = orden, activo = activo });
            if (res.Exito)
            {
                await RecargarSubtemas();
                lblSubtemasEstado.Text = $"✓ {(string.IsNullOrWhiteSpace(res.Mensaje) ? "Subtema actualizado." : res.Mensaje)}";
            }
            else
            {
                var detalle = string.IsNullOrWhiteSpace(res.Mensaje) ? res.Contenido : res.Mensaje;
                lblSubtemasEstado.Text = $"✗ Error {res.Codigo} al editar el subtema: {detalle}";
            }
        }
        catch (Exception ex) { lblSubtemasEstado.Text = $"✗ No se pudo editar el subtema: {ex.Message}"; }
    }

    private async void EliminarSubtema(int id, string nombre)
    {
        if (!Confirmar("Eliminar subtema", $"¿Eliminar el subtema \"{nombre}\"?")) return;
        try
        {
            var res = await ApiService.DeleteAsync($"subtemas/{id}");
            if (res.Exito)
            {
                await RecargarSubtemas();
                lblSubtemasEstado.Text = $"✓ {(string.IsNullOrWhiteSpace(res.Mensaje) ? "Subtema eliminado." : res.Mensaje)}";
            }
            else
            {
                var detalle = string.IsNullOrWhiteSpace(res.Mensaje) ? res.Contenido : res.Mensaje;
                lblSubtemasEstado.Text = $"✗ Error {res.Codigo} al eliminar el subtema: {detalle}";
            }
        }
        catch (Exception ex) { lblSubtemasEstado.Text = $"✗ No se pudo eliminar el subtema: {ex.Message}"; }
    }

    private Border CrearFilaSubtema(int num, int id, string nombre, string orden, bool activo)
    {
        var info = Ui.Info(nombre, null, activo ? null : Ui.Pill("INACTIVO", Tono.Coral));

        var btnEditar = Ui.Accion("✎", Tono.Morado, "Editar subtema");
        btnEditar.Click += (_, _) => EditarSubtema(id, nombre, orden, activo);
        var btnEliminar = Ui.Accion("🗑", Tono.Coral, "Eliminar subtema");
        btnEliminar.Click += (_, _) => EliminarSubtema(id, nombre);

        return Ui.Fila(num, info, btnEditar, btnEliminar);
    }

    // ===== PREGUNTAS (lista, aprobar, editar, eliminar) =====

    private async System.Threading.Tasks.Task RecargarPreguntas()
    {
        if (_temaSeleccionadoId <= 0) return;
        try
        {
            var res = await ApiService.GetResultAsync($"preguntas?temaId={_temaSeleccionadoId}");
            if (!res.Exito)
            {
                listaPreguntas.Items.Clear();
                var detalle = string.IsNullOrWhiteSpace(res.Mensaje) ? res.Contenido : res.Mensaje;
                lblPreguntasEstado.Text = $"✗ Error {res.Codigo} al listar preguntas: {detalle}";
                return;
            }
            var array = JArray.Parse(res.Contenido);
            listaPreguntas.Items.Clear();
            int i = 1;
            foreach (var item in array)
            {
                int id = item["id"]?.Value<int>() ?? 0;
                var enunciado = item["enunciado"]?.ToString() ?? "-";
                var estado = item["estado"]?.ToString() ?? "";
                listaPreguntas.Items.Add(CrearFilaPregunta(i++, id, enunciado, estado));
            }
            lblPreguntasEstado.Text = array.Count == 0 ? "Este tema aún no tiene preguntas." : $"✓ {array.Count} pregunta(s).";
        }
        catch (Exception ex) { lblPreguntasEstado.Text = $"✗ No se pudieron cargar las preguntas: {ex.Message}"; }
    }

    // Aprobar: PATCH /api/preguntas/{id}/estado?estadoId={EstadoAprobadaId}
    private async void AprobarPregunta(int id)
    {
        try
        {
            var res = await ApiService.PatchAsync($"preguntas/{id}/estado?estadoId={EstadoAprobadaId}");
            if (res.Exito)
            {
                await RecargarPreguntas();
                lblPreguntasEstado.Text = $"✓ {(string.IsNullOrWhiteSpace(res.Mensaje) ? "Pregunta aprobada." : res.Mensaje)}";
            }
            else
            {
                var detalle = string.IsNullOrWhiteSpace(res.Mensaje) ? res.Contenido : res.Mensaje;
                lblPreguntasEstado.Text = $"✗ Error {res.Codigo} al aprobar (estadoId={EstadoAprobadaId}): {detalle}";
            }
        }
        catch (Exception ex) { lblPreguntasEstado.Text = $"✗ No se pudo aprobar la pregunta: {ex.Message}"; }
    }

    // Editar enunciado/explicación: primero traemos el detalle para conservar los demás campos.
    private async void EditarPregunta(int id, string enunciadoActual)
    {
        try
        {
            var det = await ApiService.GetResultAsync($"preguntas/{id}");
            if (!det.Exito)
            {
                var d = string.IsNullOrWhiteSpace(det.Mensaje) ? det.Contenido : det.Mensaje;
                lblPreguntasEstado.Text = $"✗ Error {det.Codigo} al abrir la pregunta: {d}";
                return;
            }
            var obj = JObject.Parse(det.Contenido);
            var explicacionActual = obj["explicacion"]?.ToString() ?? "";
            bool disponibleActual = obj["disponiblePractica"]?.Value<bool>() ?? true;

            var datos = DialogoCampos("Editar pregunta",
                new[] { ("Enunciado", enunciadoActual), ("Explicación (opcional)", explicacionActual) },
                "Disponible en práctica", disponibleActual);
            if (datos is null) return;

            var (valores, disponible) = datos.Value;
            var enunciado = valores[0];
            if (string.IsNullOrWhiteSpace(enunciado)) { lblPreguntasEstado.Text = "✗ El enunciado no puede quedar vacío."; return; }
            var explicacion = valores[1];

            // Conservamos los campos que no editamos aquí (tipo, dificultad, subtema, estado...).
            var res = await ApiService.PutAsync($"preguntas/{id}", new
            {
                temaId = obj["temaId"]?.Value<int>() ?? _temaSeleccionadoId,
                subtemaId = obj["subtemaId"]?.Type == JTokenType.Integer ? obj["subtemaId"]!.Value<int>() : (int?)null,
                tipoPreguntaId = obj["tipoPreguntaId"]?.Value<int>() ?? 1,
                nivelDificultadId = obj["nivelDificultadId"]?.Value<int>() ?? 2,
                enunciado = enunciado,
                explicacion = string.IsNullOrWhiteSpace(explicacion) ? null : explicacion,
                respuestaReferencia = obj["respuestaReferencia"]?.ToString(),
                disponiblePractica = disponible,
                estadoContenidoId = obj["estadoContenidoId"]?.Value<int>() ?? 1,
                creadaPorUsuarioId = ApiService.UsuarioId
            });

            if (res.Exito)
            {
                await RecargarPreguntas();
                lblPreguntasEstado.Text = $"✓ {(string.IsNullOrWhiteSpace(res.Mensaje) ? "Pregunta actualizada." : res.Mensaje)}";
            }
            else
            {
                var detalle = string.IsNullOrWhiteSpace(res.Mensaje) ? res.Contenido : res.Mensaje;
                lblPreguntasEstado.Text = $"✗ Error {res.Codigo} al editar la pregunta: {detalle}";
            }
        }
        catch (Exception ex) { lblPreguntasEstado.Text = $"✗ No se pudo editar la pregunta: {ex.Message}"; }
    }

    private async void EliminarPregunta(int id, string enunciado)
    {
        var recorte = enunciado.Length > 60 ? enunciado.Substring(0, 60) + "…" : enunciado;
        if (!Confirmar("Eliminar pregunta", $"¿Eliminar la pregunta:\n\"{recorte}\"?")) return;
        try
        {
            var res = await ApiService.DeleteAsync($"preguntas/{id}");
            if (res.Exito)
            {
                await RecargarPreguntas();
                lblPreguntasEstado.Text = $"✓ {(string.IsNullOrWhiteSpace(res.Mensaje) ? "Pregunta eliminada." : res.Mensaje)}";
            }
            else
            {
                var detalle = string.IsNullOrWhiteSpace(res.Mensaje) ? res.Contenido : res.Mensaje;
                lblPreguntasEstado.Text = $"✗ Error {res.Codigo} al eliminar la pregunta: {detalle}";
            }
        }
        catch (Exception ex) { lblPreguntasEstado.Text = $"✗ No se pudo eliminar la pregunta: {ex.Message}"; }
    }

    // Devuelve el color de la etiqueta de estado según su nombre.
    private static Tono TonoEstado(string estado)
    {
        var e = estado.ToLowerInvariant();
        if (e.Contains("aprob") || e.Contains("public")) return Tono.Verde;
        if (e.Contains("rechaz")) return Tono.Coral;
        if (e.Contains("revis")) return Tono.Amarillo;
        return Tono.Neutro; // borrador / otros
    }

    private Border CrearFilaPregunta(int num, int id, string enunciado, string estado)
    {
        // Línea 1: enunciado · Línea 2: etiqueta de estado + botones
        var contenido = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        contenido.Children.Add(Ui.TextoPrincipal(enunciado, 13.5));

        var fila2 = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        var etiqueta = Ui.Pill(string.IsNullOrWhiteSpace(estado) ? "—" : estado, TonoEstado(estado));
        etiqueta.Margin = new Thickness(0, 0, 4, 0);
        fila2.Children.Add(etiqueta);

        var btnAprobar = Ui.Accion("✓ Aprobar", Tono.Verde);
        btnAprobar.Click += (_, _) => AprobarPregunta(id);
        var btnEditar = Ui.Accion("✎", Tono.Morado, "Editar pregunta");
        btnEditar.Click += (_, _) => EditarPregunta(id, enunciado);
        var btnEliminar = Ui.Accion("🗑", Tono.Coral, "Eliminar pregunta");
        btnEliminar.Click += (_, _) => EliminarPregunta(id, enunciado);
        fila2.Children.Add(btnAprobar);
        fila2.Children.Add(btnEditar);
        fila2.Children.Add(btnEliminar);
        contenido.Children.Add(fila2);

        return Ui.Fila(num, contenido);
    }

    private Border CrearFila(int num, string texto, string extra, int id = 0, bool activa = true,
        bool conAcciones = false, Action<int, string>? onSeleccion = null)
    {
        // El dato extra va como etiqueta: coral si es «Inactiva», teal en otro caso.
        var pill = string.IsNullOrWhiteSpace(extra)
            ? null
            : Ui.Pill(extra, extra.StartsWith("Inactiv", StringComparison.OrdinalIgnoreCase) ? Tono.Coral : Tono.Teal);
        var info = Ui.Info(texto, null, pill);

        Border fila;
        if (conAcciones)
        {
            var btnEditar = Ui.Accion("✎ Editar", Tono.Morado);
            btnEditar.Click += (_, _) => EditarMateria(id, texto, activa);
            var btnEliminar = Ui.Accion("🗑 Eliminar", Tono.Coral);
            btnEliminar.Click += (_, _) => EliminarMateria(id, texto);
            fila = Ui.Fila(num, info, btnEditar, btnEliminar);
        }
        else
        {
            fila = Ui.Fila(num, info);
        }

        // Fila clicable (p. ej. materias del DOCENTE): al pulsar, se selecciona.
        if (onSeleccion != null)
            Ui.HacerClicable(fila, () => onSeleccion(id, texto));

        return fila;
    }

    // Editar materia: PUT /api/Materias/{id} con { nombre, activa } y refresco.
    private async void EditarMateria(int id, string nombreActual, bool activaActual)
    {
        var datos = DialogoCampos("Editar materia",
            new[] { ("Nombre", nombreActual) }, "Materia activa", activaActual);
        if (datos is null) return;            // el usuario canceló

        var (valores, activa) = datos.Value;
        var nuevo = valores[0];
        if (string.IsNullOrWhiteSpace(nuevo))
        {
            lblMateriasEstado.Text = "✗ El nombre de la materia no puede quedar vacío.";
            return;
        }

        try
        {
            var res = await ApiService.PutAsync($"{Rutas.Materias}/{id}", new { nombre = nuevo, activa = activa });

            if (res.Exito)
            {
                await RecargarMaterias(); // refresca la lista
                lblMateriasEstado.Text = $"✓ {(string.IsNullOrWhiteSpace(res.Mensaje) ? "Materia actualizada." : res.Mensaje)}";
            }
            else
            {
                var detalle = string.IsNullOrWhiteSpace(res.Mensaje) ? res.Contenido : res.Mensaje;
                lblMateriasEstado.Text = $"✗ Error {res.Codigo} al editar la materia: {detalle}";
            }
        }
        catch (Exception ex)
        {
            lblMateriasEstado.Text = $"✗ No se pudo editar la materia: {ex.Message}";
        }
    }

    // Eliminar materia: DELETE /api/Materias/{id} (con confirmación) y refresco.
    private async void EliminarMateria(int id, string nombre)
    {
        if (!Confirmar("Eliminar materia", $"¿Eliminar la materia \"{nombre}\"?")) return;

        try
        {
            var res = await ApiService.DeleteAsync($"{Rutas.Materias}/{id}");

            if (res.Exito)
            {
                await RecargarMaterias(); // refresca la lista
                lblMateriasEstado.Text = $"✓ {(string.IsNullOrWhiteSpace(res.Mensaje) ? "Materia eliminada." : res.Mensaje)}";
            }
            else
            {
                var detalle = string.IsNullOrWhiteSpace(res.Mensaje) ? res.Contenido : res.Mensaje;
                lblMateriasEstado.Text = $"✗ Error {res.Codigo} al eliminar la materia: {detalle}";
            }
        }
        catch (Exception ex)
        {
            lblMateriasEstado.Text = $"✗ No se pudo eliminar la materia: {ex.Message}";
        }
    }
}
