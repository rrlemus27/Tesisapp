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
    // Grados, secciones (todas) y períodos ya cargados: para validar duplicados, el resumen y los combos.
    private JArray? _gradosData;
    private JArray? _seccionesData;
    private JArray? _periodosData;
    // Asignaciones docente ya cargadas (pestañas Asignaciones y Docentes, y el resumen).
    private JArray? _asignacionesData;
    // Roles reales de la API (GET api/roles): rolId -> (código, nombre). No se suponen los ids.
    private readonly Dictionary<int, (string codigo, string nombre)> _roles = new();

    // ¿Existen en la API los endpoints de asignaciones? null = aún no se sabe; false = la ruta
    // respondió 404 (no existe en la API conectada) y la función se marca como «no disponible».
    private bool? _asigDocenteDisponible;
    private bool? _estSeccionDisponible;

    // Operaciones en curso sobre una fila (baja, reactivación…), para no repetirlas con doble clic.
    private readonly HashSet<string> _enCurso = new();
    // El resumen del panel principal se pinta cuando termina la carga inicial.
    private bool _cargaInicialHecha;
    // Ya se volvió al login (cierre de sesión o sesión expirada).
    private bool _sesionTerminada;
    // Docente elegido en la pestaña Docentes para ver y gestionar sus clases.
    private int _docenteSeleccionadoId;

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

    public DashboardWindow()
    {
        InitializeComponent();
        ColorearMensajesDeEstado();
        ConfigurarVista(); // capa visual (DashboardWindow.Vista.cs)
        ApiService.SesionExpirada += AlExpirarSesion;
        Closed += (_, _) => ApiService.SesionExpirada -= AlExpirarSesion;
        Cargar();
    }

    private void Barra_Mover(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }
    private void Cerrar_Click(object sender, RoutedEventArgs e) => Application.Current.Shutdown();

    private void CerrarSesion_Click(object sender, RoutedEventArgs e) => VolverAlLogin(null);

    // El token venció o la API respondió 401: se vuelve al login con el aviso.
    // Se difiere para no cerrar la ventana en medio de la llamada que lo detectó.
    private void AlExpirarSesion() =>
        Dispatcher.BeginInvoke(() =>
            VolverAlLogin("Tu sesión expiró o ya no es válida. Vuelve a iniciar sesión para continuar."));

    private void VolverAlLogin(string? aviso)
    {
        if (_sesionTerminada) return;
        _sesionTerminada = true;
        ApiService.CerrarSesion();
        foreach (var dialogo in OwnedWindows.Cast<Window>().ToList()) dialogo.Close();
        new MainWindow(aviso).Show();
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
        // Las tarjetas de stats de los otros roles ocupan solo su ancho; las del ADMIN se reparten la fila.
        panelStats.HorizontalAlignment = HorizontalAlignment.Left;

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
                MostrarResumenCargando();
                // Roles primero: con ellos se sabe quién es estudiante o docente.
                await RecargarRoles();
                await RecargarUsuarios();
                await RecargarMaterias();
                await RecargarGrados();
                await RecargarSecciones();
                await RecargarPeriodos();
                // Con las listas cargadas, las asignaciones ya pueden mostrar nombres.
                MapearNombres();
                if (AsignacionesDisponibles)
                {
                    await RecargarAsignaciones();
                    await RecargarEstudiantesSeccion();
                }
                _cargaInicialHecha = true;
                ActualizarResumen();
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

    // ===== PANEL PRINCIPAL (ADMIN) =====
    // La API no tiene endpoint de estadísticas para el ADMIN: cada número se calcula con
    // las listas que ya devuelven los endpoints (Usuarios por rol, Materias, Secciones y
    // asignaciones-docente). Si una lista no se pudo cargar, la tarjeta lo dice; no se inventa.

    private static readonly (string titulo, Tono tono, string icono)[] TarjetasResumen =
    {
        ("Estudiantes", Tono.Teal, "IcoEstudiantes"),
        ("Docentes", Tono.Morado, "IcoDocentes"),
        ("Materias", Tono.Amarillo, "IcoLibro"),
        ("Secciones", Tono.Teal, "IcoCapas"),
        ("Clases", Tono.Verde, "IcoAsignaciones")
    };

    private void MostrarResumenCargando()
    {
        panelStats.HorizontalAlignment = HorizontalAlignment.Stretch;
        panelStats.Children.Clear();
        foreach (var (titulo, tono, icono) in TarjetasResumen)
            panelStats.Children.Add(TarjetaResumen(titulo, "…", "Cargando…", tono, icono));
    }

    // Recalcula las tarjetas con lo que hay cargado. Se llama tras cada recarga de listas.
    private void ActualizarResumen()
    {
        if (!_cargaInicialHecha || ApiService.Rol != "ADMIN") return;

        var valores = new (string valor, string detalle, string? ayuda)[]
        {
            ResumenUsuarios("ESTUDIANTE"),
            ResumenUsuarios("DOCENTE"),
            ResumenLista(_materiasData, "activa", "activa(s)"),
            ResumenLista(_seccionesData, "activa", "activa(s)"),
            ResumenClases()
        };

        panelStats.Children.Clear();
        for (int i = 0; i < TarjetasResumen.Length; i++)
        {
            var (titulo, tono, icono) = TarjetasResumen[i];
            var (valor, detalle, ayuda) = valores[i];
            panelStats.Children.Add(TarjetaResumen(titulo, valor, detalle, tono, icono, ayuda));
        }
    }

    // Estudiantes o docentes: usuarios de la lista con ese rol (todos) y cuántos están activos.
    private (string, string, string?) ResumenUsuarios(string rol)
    {
        if (_usuariosData is null) return ("—", "Error al cargar", "No se pudo cargar GET api/Usuarios.");
        var lista = _usuariosData.Where(u => EsRol(u, rol)).ToList();
        int activos = lista.Count(u => u["activo"]?.Value<bool?>() ?? false);
        return (lista.Count.ToString(), $"{activos} activo(s)",
            $"Usuarios con rol {rol} en GET api/Usuarios: {lista.Count} ({activos} activos, {lista.Count - activos} de baja).");
    }

    private static (string, string, string?) ResumenLista(JArray? datos, string campoActivo, string textoActivos)
    {
        if (datos is null) return ("—", "Error al cargar", "No se pudo cargar la lista de la API.");
        int activos = datos.Count(d => d[campoActivo]?.Value<bool?>() ?? false);
        return (datos.Count.ToString(), $"{activos} {textoActivos}", null);
    }

    // Clases = asignaciones docente → materia + sección + período (api/admin/asignaciones-docente).
    private (string, string, string?) ResumenClases()
    {
        if (_asigDocenteDisponible == false)
            return ("—", "No disponible",
                $"No disponible: la API conectada no tiene api/{Rutas.AsignacionesDocente} (responde 404), así que no hay datos de clases.");
        if (_asignacionesData is null) return ("—", "Error al cargar", "No se pudo cargar la lista de asignaciones.");
        // Si la API informa si cada clase está activa, se muestra; si no, solo el total.
        var conCampo = _asignacionesData.Where(a => a["activa"]?.Type == JTokenType.Boolean).ToList();
        var detalle = conCampo.Count == _asignacionesData.Count && conCampo.Count > 0
            ? $"{conCampo.Count(a => a["activa"]!.Value<bool>())} activa(s)"
            : "asignadas a docentes";
        return (_asignacionesData.Count.ToString(), detalle, null);
    }

    // Tarjeta del resumen (.summary-card de la web): icono en un recuadro de color, título,
    // número grande y detalle. Si el dato no se pudo cargar o no existe, el detalle va en coral.
    private ContentControl TarjetaResumen(string titulo, string valor, string detalle, Tono tono, string icono,
        string? ayuda = null)
    {
        var (fuerte, suave) = Ui.Colores(tono);
        var recuadro = new Border
        {
            Width = 46,
            Height = 46,
            CornerRadius = new CornerRadius(15),
            Background = suave,
            VerticalAlignment = VerticalAlignment.Center,
            Child = Ui.Icono(icono, 22, fuerte)
        };

        bool esNumero = valor.Length > 0 && valor.All(char.IsDigit);
        bool alerta = valor == "—"; // no se pudo cargar o no disponible
        var textos = new StackPanel { Margin = new Thickness(14, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        textos.Children.Add(new TextBlock
        {
            Text = titulo,
            FontSize = 13,
            FontWeight = FontWeights.Bold,
            Foreground = Paleta.Apagado,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        textos.Children.Add(new TextBlock
        {
            Text = valor,
            FontSize = 28,
            FontWeight = FontWeights.ExtraBold,
            Foreground = esNumero ? Paleta.Navy : Paleta.Apagado,
            Margin = new Thickness(0, -2, 0, -2),
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        textos.Children.Add(new TextBlock
        {
            Text = detalle,
            FontSize = 12,
            FontWeight = alerta ? FontWeights.Bold : FontWeights.SemiBold,
            Foreground = alerta ? Paleta.CoralTexto : Paleta.Apagado,
            TextTrimming = TextTrimming.CharacterEllipsis
        });

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(textos, 1);
        grid.Children.Add(recuadro);
        grid.Children.Add(textos);

        return new ContentControl
        {
            Style = (Style)FindResource("Tarjeta"),
            Padding = new Thickness(18, 16, 14, 16),
            Margin = new Thickness(0, 0, 14, 0),
            Content = grid,
            ToolTip = ayuda ?? $"{titulo}: {valor} · {detalle}"
        };
    }

    // Crea una tarjeta de stat arriba (vista de los otros roles): icono de color y el dato.
    private void Stat(string arriba, string abajo)
    {
        // Cada tarjeta toma un color distinto de la paleta (teal, morado, amarillo…).
        var tonos = new[] { Tono.Teal, Tono.Morado, Tono.Amarillo, Tono.Verde };
        var iconos = new[] { "IcoEstrella", "IcoLibro", "IcoChispa", "IcoUsuario" };
        int n = panelStats.Children.Count;
        var (fuerte, suave) = Ui.Colores(tonos[n % tonos.Length]);

        var icono = new Border
        {
            Width = 42,
            Height = 42,
            CornerRadius = new CornerRadius(14),
            Background = suave,
            VerticalAlignment = VerticalAlignment.Center,
            Child = Ui.Icono(iconos[n % iconos.Length], 20, fuerte)
        };
        var textos = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
        textos.Children.Add(new TextBlock
        {
            Text = arriba,
            FontSize = 12.5,
            FontWeight = FontWeights.Bold,
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
            Padding = new Thickness(16, 12, 24, 12),
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
                estado.Text = res.Error($"cargar /{ruta}");
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
                lblMateriasEstado.Text = res.Error("listar materias");
                return;
            }
            _materiasData = JArray.Parse(res.Contenido);
            Mapear(_nombreMateria, _materiasData, it => it["nombre"]?.ToString());
            RenderMaterias(txtBuscarMateria?.Text ?? "");
        }
        catch (Exception ex)
        {
            _materiasData = null;
            lblMateriasEstado.Text = $"✗ No se pudieron cargar las materias: {ex.Message}";
        }
        finally
        {
            ActualizarResumen();
        }
    }

    // Pinta las materias ya cargadas, filtrando por nombre (en vivo, sin llamar a la API).
    private void RenderMaterias(string filtro)
    {
        if (_materiasData is null) return;
        filtro = (filtro ?? "").Trim();

        int estado = cmbFiltroEstadoMaterias?.SelectedIndex ?? 0; // 0 = todas, 1 = activas, 2 = inactivas

        listaMaterias.Items.Clear();
        int i = 1, mostrados = 0;
        foreach (var item in _materiasData)
        {
            var nombre = item["nombre"]?.ToString() ?? "-";
            if (filtro.Length > 0 && !nombre.Contains(filtro, StringComparison.OrdinalIgnoreCase)) continue;

            int id = item["id"]?.Value<int>() ?? 0;
            bool activa = item["activa"]?.Value<bool?>() ?? true;
            if ((estado == 1 && !activa) || (estado == 2 && activa)) continue;
            listaMaterias.Items.Add(CrearFila(i++, nombre, activa ? "Activa" : "Inactiva", id, activa, conAcciones: true));
            mostrados++;
        }
        lblMateriasEstado.Text = filtro.Length > 0 || estado > 0
            ? $"✓ {mostrados} de {_materiasData.Count} materia(s) con los filtros aplicados."
            : $"✓ {_materiasData.Count} materia(s).";
    }

    private void BuscarMateria_Changed(object sender, TextChangedEventArgs e) => RenderMaterias(txtBuscarMateria.Text);
    private void FiltroMaterias_Changed(object sender, SelectionChangedEventArgs e) =>
        RenderMaterias(txtBuscarMateria?.Text ?? "");

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
                listaEstAdmin.Items.Clear();
                listaDocentes.Items.Clear();
                lblUsuariosEstado.Text = lblEstAdminEstado.Text = lblDocentesEstado.Text = res.Error("listar usuarios");
                return;
            }

            _usuariosData = JArray.Parse(res.Contenido);
            if (_roles.Count == 0) RolesDesdeUsuarios();
            Mapear(_nombreUsuario, _usuariosData, NombreDeUsuario);
            RenderUsuarios(txtBuscarUsuario?.Text ?? "");
            RenderEstudiantesAdmin();
            RenderDocentes();
        }
        catch (Exception ex)
        {
            _usuariosData = null;
            lblUsuariosEstado.Text = $"✗ No se pudieron cargar los usuarios: {ex.Message}";
        }
        finally
        {
            ActualizarResumen();
        }
    }

    // Pinta los usuarios ya cargados, filtrando por nombre completo (en vivo).
    private void RenderUsuarios(string filtro)
    {
        if (_usuariosData is null) return;
        filtro = (filtro ?? "").Trim();

        var rolFiltro = (cmbFiltroRolUsuarios?.SelectedItem as ComboBoxItem)?.Tag as string; // null = todos
        int estado = cmbFiltroEstadoUsuarios?.SelectedIndex ?? 0; // 0 = todos, 1 = activos, 2 = de baja

        listaUsuarios.Items.Clear();
        int i = 1, mostrados = 0;
        foreach (var item in _usuariosData)
        {
            var nombreCompleto = item["nombreCompleto"]?.ToString() ?? "-";
            var correo = item["correoOUsuario"]?.ToString() ?? "";
            if (!Coincide(filtro, nombreCompleto, correo)) continue;
            if (rolFiltro != null && CodigoRol(item) != rolFiltro) continue;
            if ((estado == 1 && !Activo(item, "activo")) || (estado == 2 && Activo(item, "activo"))) continue;

            int id = item["id"]?.Value<int>() ?? 0;
            var rol = item["rol"]?.ToString() ?? "";
            // Si la lista no trae rolId, lo deducimos del código del rol para que
            // «Editar» no cambie el rol del usuario por accidente.
            int rolId = Entero(item, "rolId") ?? RolIdDeCodigo(CodigoRol(item));
            bool activo = item["activo"]?.Value<bool?>() ?? true;
            listaUsuarios.Items.Add(CrearFilaUsuario(i++, id, nombreCompleto, correo, rol, rolId, activo));
            mostrados++;
        }
        lblUsuariosEstado.Text = filtro.Length > 0 || rolFiltro != null || estado > 0
            ? $"✓ {mostrados} de {_usuariosData.Count} usuario(s) con los filtros aplicados."
            : $"✓ {_usuariosData.Count} usuario(s).";
    }

    private void BuscarUsuario_Changed(object sender, TextChangedEventArgs e) => RenderUsuarios(txtBuscarUsuario.Text);
    private void FiltroUsuarios_Changed(object sender, SelectionChangedEventArgs e) =>
        RenderUsuarios(txtBuscarUsuario?.Text ?? "");

    // ===== ROLES: ids y códigos reales de GET api/roles (no se suponen) =====

    private async System.Threading.Tasks.Task RecargarRoles()
    {
        var (datos, error) = await ObtenerListaAsync(Rutas.Roles);
        if (datos is null)
        {
            // Se reintenta con los roles que traiga la lista de usuarios (ver RolesDesdeUsuarios).
            lblUsuariosEstado.Text = error ?? "";
            return;
        }
        _roles.Clear();
        foreach (var r in datos)
        {
            var id = Entero(r, "id");
            var codigo = Texto(r, "codigo");
            if (id is null || codigo is null) continue;
            _roles[id.Value] = (codigo.Trim().ToUpperInvariant(), Texto(r, "nombre") ?? codigo);
        }
        LlenarComboRoles(cmbRol, TagCombo(cmbRol) is > 0 and var elegido ? elegido : RolIdDeCodigo("ESTUDIANTE"));
    }

    // Si GET api/roles falló, se arman los roles con los (rolId, rol) que trae GET api/Usuarios.
    private void RolesDesdeUsuarios()
    {
        if (_usuariosData is null) return;
        foreach (var u in _usuariosData)
        {
            var id = Entero(u, "rolId");
            var nombre = Texto(u, "rol");
            if (id is null || nombre is null || _roles.ContainsKey(id.Value)) continue;
            _roles[id.Value] = (NormalizarCodigoRol(nombre), nombre);
        }
        LlenarComboRoles(cmbRol, RolIdDeCodigo("ESTUDIANTE"));
    }

    private void LlenarComboRoles(ComboBox combo, int rolIdElegido)
    {
        combo.Items.Clear();
        foreach (var (id, (codigo, _)) in _roles.OrderBy(r => r.Key))
            combo.Items.Add(new ComboBoxItem { Content = codigo, Tag = id });
        // Un rol que no está en la lista se conserva tal cual, para no cambiarlo sin querer.
        if (rolIdElegido > 0 && !_roles.ContainsKey(rolIdElegido))
            combo.Items.Add(new ComboBoxItem { Content = $"Rol #{rolIdElegido}", Tag = rolIdElegido });
        if (!SeleccionarEnCombo(combo, rolIdElegido) && combo.Items.Count > 0) combo.SelectedIndex = 0;
    }

    // Código del rol (ADMIN / DOCENTE / ESTUDIANTE) de un usuario de la lista.
    private string CodigoRol(JToken usuario)
    {
        var rolId = Entero(usuario, "rolId");
        if (rolId.HasValue && _roles.TryGetValue(rolId.Value, out var rol)) return rol.codigo;
        return NormalizarCodigoRol(Texto(usuario, "rol") ?? "");
    }

    // "Docente" -> "DOCENTE", "Administrador" -> "ADMIN".
    private static string NormalizarCodigoRol(string nombre)
    {
        var codigo = nombre.Trim().ToUpperInvariant();
        return codigo.StartsWith("ADMIN") ? "ADMIN" : codigo;
    }

    // Id del rol con ese código, o 0 si no se conoce.
    private int RolIdDeCodigo(string codigo) =>
        _roles.FirstOrDefault(r => r.Value.codigo == codigo).Key;

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
        else if (ReferenceEquals(panelAdmin.SelectedItem, tabDocentes))
            LlenarCombosClaseDocente();
    }

    private async System.Threading.Tasks.Task CargarPestanaAsignaciones()
    {
        if (_cargandoAsignaciones) return;
        _cargandoAsignaciones = true;
        try
        {
            // Se refrescan las listas (y con ellas las demás pestañas y el resumen) para
            // incluir lo creado en otras pestañas; los combos se llenan con esas mismas listas.
            await RecargarUsuarios();
            await RecargarMaterias();
            await RecargarSecciones();
            await RecargarPeriodos();
            LlenarCombosAsignacion();
            MapearNombres();
            await RecargarAsignaciones();
            await RecargarEstudiantesSeccion();
            // Los errores de los combos se muestran después, para que la recarga de las listas no los tape.
            MostrarErroresCombos(lblAsignacionesEstado, new[]
            {
                ErrorLista(_usuariosData, "usuarios"), ErrorLista(_materiasData, "materias"),
                ErrorLista(_seccionesData, "secciones"), ErrorLista(_periodosData, "períodos")
            });
            MostrarErroresCombos(lblEstudiantesEstado, new[]
            {
                ErrorLista(_usuariosData, "usuarios"), ErrorLista(_seccionesData, "secciones")
            });
        }
        finally
        {
            _cargandoAsignaciones = false;
        }
    }

    private static string? ErrorLista(JArray? datos, string nombre) =>
        datos is null ? $"✗ No se pudo cargar la lista de {nombre}; revisa su pestaña." : null;

    // Llena los combos de las dos asignaciones con las listas ya cargadas. Para asignar
    // solo se ofrecen docentes, materias y secciones activos; los períodos inactivos se marcan.
    private void LlenarCombosAsignacion()
    {
        LlenarCombo(cmbAsigDocente, _usuariosData,
            it => EsRol(it, "DOCENTE") && Activo(it, "activo") ? NombreDeUsuario(it) : null);
        LlenarCombo(cmbAsigMateria, _materiasData, it => Activo(it, "activa") ? it["nombre"]?.ToString() : null);
        LlenarCombo(cmbAsigSeccion, _seccionesData, it => Activo(it, "activa") ? TextoSeccion(it) : null);
        LlenarCombo(cmbAsigPeriodo, _periodosData, TextoPeriodo);
        LlenarCombo(cmbEstEstudiante, _usuariosData, it => EsRol(it, "ESTUDIANTE")
            ? NombreDeUsuario(it) + (Activo(it, "activo") ? "" : " (de baja)")
            : null);
        LlenarCombo(cmbEstSeccion, _seccionesData, it => Activo(it, "activa") ? TextoSeccion(it) : null);
    }

    // Mapas id -> nombre para mostrar las asignaciones aunque la API solo devuelva ids.
    private void MapearNombres()
    {
        Mapear(_nombreUsuario, _usuariosData, NombreDeUsuario);
        Mapear(_nombreMateria, _materiasData, it => it["nombre"]?.ToString());
        Mapear(_seccionesNombre, _seccionesData, TextoSeccion);
        Mapear(_nombrePeriodo, _periodosData, it => it["nombre"]?.ToString());
    }

    // Campo booleano de estado (activo / activa); si no viene, se considera activo.
    private static bool Activo(JToken it, string campo) => it[campo]?.Value<bool?>() ?? true;

    private static string? TextoPeriodo(JToken it)
    {
        var nombre = it["nombre"]?.ToString();
        if (string.IsNullOrWhiteSpace(nombre)) return null;
        return Activo(it, "activo") ? nombre : $"{nombre} (inactivo)";
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

    // ¿El usuario de la lista tiene este rol (código ADMIN / DOCENTE / ESTUDIANTE)?
    private bool EsRol(JToken usuario, string rol) => CodigoRol(usuario) == rol;

    // GET a una lista de la API. Devuelve (datos, null) o (null, error real con código + mensaje).
    private static async System.Threading.Tasks.Task<(JArray? datos, string? error)> ObtenerListaAsync(string ruta)
    {
        try
        {
            var res = await ApiService.GetResultAsync(ruta);
            if (!res.Exito)
            {
                return (null, res.Error($"cargar /{ruta}"));
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

    // Mensaje cuando la API conectada no tiene el endpoint (responde 404 sin cuerpo).
    private static string NoDisponible(string ruta, string que) =>
        $"✗ No disponible: la API conectada no tiene el servicio api/{ruta} (responde 404), " +
        $"así que no se pueden ver ni guardar {que}. Hace falta agregarlo en la API.";

    // Habilita o deshabilita lo que depende de los endpoints de asignaciones.
    private void ActualizarDisponibilidadAsignaciones()
    {
        bool docente = _asigDocenteDisponible != false;
        bool estudiante = _estSeccionDisponible != false;
        btnCrearAsignacion.IsEnabled = docente;
        btnAgregarClase.IsEnabled = docente;
        btnAsignarEstudiante.IsEnabled = estudiante;
        pillAsignaciones.Visibility = docente && estudiante ? Visibility.Collapsed : Visibility.Visible;
        pillAsignaciones.ToolTip = (docente, estudiante) switch
        {
            (false, false) => "No disponible: la API conectada no tiene los servicios de asignaciones (docentes y estudiantes).",
            (false, true) => "No disponible: la API conectada no tiene el servicio de asignaciones de docentes.",
            _ => "No disponible: la API conectada no tiene el servicio de secciones de estudiantes."
        };
    }

    private async System.Threading.Tasks.Task RecargarAsignaciones()
    {
        listaAsignaciones.Items.Clear();
        try
        {
            var res = await ApiService.GetResultAsync(Rutas.AsignacionesDocente);
            if (!res.Exito)
            {
                _asignacionesData = null;
                if (res.EndpointInexistente) _asigDocenteDisponible = false;
                lblAsignacionesEstado.Text = res.EndpointInexistente
                    ? NoDisponible(Rutas.AsignacionesDocente, "las clases asignadas a los docentes")
                    : res.Error("cargar las asignaciones de docentes");
                return;
            }

            _asignacionesData = JArray.Parse(res.Contenido);
            _asigDocenteDisponible = true;
            int i = 1;
            foreach (var item in _asignacionesData)
            {
                var (id, docente, materia, seccion, periodo) = DatosAsignacion(item);
                listaAsignaciones.Items.Add(
                    CrearFilaAsignacion(i++, id, docente, materia, seccion, periodo, lblAsignacionesEstado));
            }
            lblAsignacionesEstado.Text = _asignacionesData.Count == 0
                ? "Todavía no hay docentes asignados."
                : $"✓ {_asignacionesData.Count} asignación(es).";
        }
        catch (Exception ex)
        {
            _asignacionesData = null;
            lblAsignacionesEstado.Text = $"✗ No se pudieron leer las asignaciones de docentes: {ex.Message}";
        }
        finally
        {
            ActualizarDisponibilidadAsignaciones();
            RenderDocentes();
            RenderClasesDocente();
            ActualizarResumen();
        }
    }

    // Datos legibles de una asignación: se aceptan nombres en el propio JSON o, si solo
    // vienen ids, se buscan en las listas ya cargadas.
    private (int id, string docente, string materia, string seccion, string periodo) DatosAsignacion(JToken item)
    {
        int id = Entero(item, "id", "asignacionId", "asignacionDocenteId") ?? 0;
        var docente = Texto(item, "docente", "docenteNombre", "nombreDocente", "docenteNombreCompleto")
                      ?? Nombre(_nombreUsuario, DocenteDe(item));
        var materia = Texto(item, "materia", "materiaNombre", "nombreMateria")
                      ?? Nombre(_nombreMateria, Entero(item, "materiaId"));
        var seccion = TextoSeccionDe(item) ?? Nombre(_seccionesNombre, Entero(item, "seccionId"));
        var periodo = Texto(item, "periodo", "periodoAcademico", "periodoNombre", "periodoAcademicoNombre")
                      ?? Nombre(_nombrePeriodo, Entero(item, "periodoAcademicoId", "periodoId"));
        return (id, docente, materia, seccion, periodo);
    }

    private static int? DocenteDe(JToken item) =>
        Entero(item, "docenteUsuarioId", "docenteId", "usuarioId", "docente");

    // ¿Ya existe esa misma clase (docente + materia + sección + período)? La base no la admite dos veces.
    private bool ExisteClase(int docenteId, int materiaId, int seccionId, int periodoId) =>
        _asignacionesData?.Any(a =>
            DocenteDe(a) == docenteId
            && Entero(a, "materiaId", "materia") == materiaId
            && Entero(a, "seccionId", "seccion") == seccionId
            && Entero(a, "periodoAcademicoId", "periodoId", "periodoAcademico", "periodo") == periodoId) == true;

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
        btnCrearAsignacion.IsEnabled = false;
        try
        {
            await CrearClaseDocenteAsync(TagCombo(cmbAsigDocente), TagCombo(cmbAsigMateria),
                TagCombo(cmbAsigSeccion), TagCombo(cmbAsigPeriodo), lblAsignacionesEstado);
        }
        finally
        {
            btnCrearAsignacion.IsEnabled = _asigDocenteDisponible != false;
        }
    }

    // POST api/admin/asignaciones-docente: asigna al docente una materia en una sección y período.
    // Lo usan las pestañas Asignaciones y Docentes. Devuelve true si se guardó.
    private async System.Threading.Tasks.Task<bool> CrearClaseDocenteAsync(int docenteId, int materiaId,
        int seccionId, int periodoId, TextBlock estado)
    {
        if (_asigDocenteDisponible == false)
        {
            estado.Text = NoDisponible(Rutas.AsignacionesDocente, "las clases asignadas a los docentes");
            return false;
        }
        if (docenteId <= 0 || materiaId <= 0 || seccionId <= 0 || periodoId <= 0)
        {
            estado.Text = "✗ Elige docente, materia, sección y período.";
            return false;
        }
        var docente = _usuariosData?.FirstOrDefault(u => Entero(u, "id") == docenteId);
        if (docente != null && !Activo(docente, "activo"))
        {
            estado.Text = "✗ El docente está de baja. Reactívalo antes de asignarle clases.";
            return false;
        }
        if (ExisteClase(docenteId, materiaId, seccionId, periodoId))
        {
            estado.Text = "✗ Ese docente ya tiene esa materia en esa sección y período.";
            return false;
        }

        try
        {
            var res = await ApiService.PostAsync(Rutas.AsignacionesDocente, new
            {
                docenteUsuarioId = docenteId,
                materiaId = materiaId,
                seccionId = seccionId,
                periodoAcademicoId = periodoId
            });

            if (!res.Exito)
            {
                if (res.EndpointInexistente)
                {
                    _asigDocenteDisponible = false;
                    ActualizarDisponibilidadAsignaciones();
                    estado.Text = NoDisponible(Rutas.AsignacionesDocente, "las clases asignadas a los docentes");
                }
                else estado.Text = res.Error("asignar la clase");
                return false;
            }

            await RecargarAsignaciones();
            estado.Text = res.Ok("Clase asignada al docente.");
            return true;
        }
        catch (Exception ex)
        {
            estado.Text = $"✗ No se pudo crear la asignación: {ex.Message}";
            return false;
        }
    }

    private async void EliminarAsignacion(int id, string resumen, TextBlock estado)
    {
        if (!Confirmar("Eliminar asignación", $"¿Eliminar la asignación:\n{resumen}?")) return;

        try
        {
            var res = await ApiService.DeleteAsync($"{Rutas.AsignacionesDocente}/{id}");
            if (res.Exito)
            {
                await RecargarAsignaciones();
                estado.Text = res.Ok("Asignación eliminada.");
            }
            else
            {
                estado.Text = res.Error("eliminar la asignación");
            }
        }
        catch (Exception ex)
        {
            estado.Text = $"✗ No se pudo eliminar la asignación: {ex.Message}";
        }
    }

    // Fila de asignación. En la pestaña Docentes ya se sabe de qué docente es, así que el
    // título es la materia.
    private Border CrearFilaAsignacion(int num, int id, string docente, string materia, string seccion,
        string periodo, TextBlock estado, bool mostrarDocente = true)
    {
        var info = mostrarDocente
            ? Ui.Info(docente, $"{materia}  ·  Sección {seccion}  ·  {periodo}")
            : Ui.Info(materia, $"Sección {seccion}  ·  {periodo}");

        var resumen = $"{docente} — {materia} / Sección {seccion} / {periodo}";
        var btnEliminar = Ui.Accion("🗑 Eliminar", Tono.Coral);
        btnEliminar.Click += (_, _) => EliminarAsignacion(id, resumen, estado);
        if (id <= 0)
        {
            // Sin id no hay a qué ruta mandar el DELETE.
            btnEliminar.IsEnabled = false;
            btnEliminar.ToolTip = "La API no devolvió el id de esta asignación.";
            ToolTipService.SetShowOnDisabled(btnEliminar, true);
        }

        return Ui.Fila(num, info, btnEliminar);
    }

    // ----- Estudiante -> sección: api/admin/estudiantes-seccion -----

    // Trae todos los estudiantes con su sección (también los que no tienen) y los pinta.
    private async System.Threading.Tasks.Task RecargarEstudiantesSeccion()
    {
        try
        {
            var res = await ApiService.GetResultAsync(Rutas.EstudiantesSeccion);
            if (!res.Exito)
            {
                _estudiantesData = null;
                listaEstudiantesSeccion.Items.Clear();
                if (res.EndpointInexistente) _estSeccionDisponible = false;
                lblEstudiantesEstado.Text = res.EndpointInexistente
                    ? NoDisponible(Rutas.EstudiantesSeccion, "las secciones de los estudiantes")
                    : res.Error("cargar los estudiantes con su sección");
                return;
            }
            _estudiantesData = JArray.Parse(res.Contenido);
            _estSeccionDisponible = true;
            RenderEstudiantesSeccion(txtBuscarEstudiante.Text);
        }
        catch (Exception ex)
        {
            _estudiantesData = null;
            listaEstudiantesSeccion.Items.Clear();
            lblEstudiantesEstado.Text = $"✗ No se pudieron leer las secciones de los estudiantes: {ex.Message}";
        }
        finally
        {
            ActualizarDisponibilidadAsignaciones();
            RenderEstudiantesAdmin();
        }
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
            var (seccionId, seccion) = SeccionDe(item);
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

    // (seccionId, "A · Primer grado") de un elemento de api/admin/estudiantes-seccion; null si no tiene.
    private (int? id, string? texto) SeccionDe(JToken item)
    {
        int? seccionId = Entero(item, "seccionId", "seccion");
        if (seccionId <= 0) seccionId = null; // 0 = sin sección
        var seccion = TextoSeccionDe(item) ?? (seccionId.HasValue ? Nombre(_seccionesNombre, seccionId) : null);
        return (seccionId, seccion);
    }

    // Sección actual de un estudiante; null si el estudiante no aparece en la lista (o no se cargó).
    private (int? id, string? texto)? SeccionActual(int usuarioId)
    {
        var item = _estudiantesData?.FirstOrDefault(e => Entero(e, "usuarioId", "estudianteId", "id") == usuarioId);
        return item is null ? null : SeccionDe(item);
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

    private async void AsignarEstudiante_Click(object sender, RoutedEventArgs e)
    {
        int usuarioId = TagCombo(cmbEstEstudiante);
        int seccionId = TagCombo(cmbEstSeccion);

        btnAsignarEstudiante.IsEnabled = false;
        try
        {
            var (_, mensaje) = await AsignarSeccionAsync(usuarioId, Nombre(_nombreUsuario, usuarioId), seccionId);
            if (mensaje != null) lblEstudiantesEstado.Text = mensaje;
        }
        finally
        {
            btnAsignarEstudiante.IsEnabled = _estSeccionDisponible != false;
        }
    }

    // POST api/admin/estudiantes-seccion con { usuarioId, seccionId }: asigna o cambia la sección.
    // Si el estudiante ya tiene otra sección, se confirma antes. Devuelve (guardado, mensaje);
    // mensaje = null cuando el usuario canceló la confirmación.
    private async System.Threading.Tasks.Task<(bool ok, string? mensaje)> AsignarSeccionAsync(
        int usuarioId, string nombre, int seccionId)
    {
        if (_estSeccionDisponible == false)
            return (false, NoDisponible(Rutas.EstudiantesSeccion, "las secciones de los estudiantes"));
        if (usuarioId <= 0 || seccionId <= 0) return (false, "✗ Elige estudiante y sección.");

        var nueva = Nombre(_seccionesNombre, seccionId);
        var actual = SeccionActual(usuarioId);
        if (actual?.id == seccionId) return (false, $"✗ \"{nombre}\" ya está en la sección {nueva}.");
        if (actual?.id is not null
            && !Confirmar("Cambiar de sección",
                $"¿Pasar a \"{nombre}\" de la sección {actual.Value.texto} a la sección {nueva}?",
                "Sí, cambiar", "Su sección actual se reemplazará por la nueva.", "BtnPrimario"))
            return (false, null);

        try
        {
            var res = await ApiService.PostAsync(Rutas.EstudiantesSeccion,
                new { usuarioId = usuarioId, seccionId = seccionId });

            if (!res.Exito)
            {
                if (!res.EndpointInexistente) return (false, res.Error("asignar la sección"));
                _estSeccionDisponible = false;
                ActualizarDisponibilidadAsignaciones();
                return (false, NoDisponible(Rutas.EstudiantesSeccion, "las secciones de los estudiantes"));
            }

            await RecargarEstudiantesSeccion();
            return (true, res.Ok($"\"{nombre}\" quedó en la sección {nueva}."));
        }
        catch (Exception ex)
        {
            return (false, $"✗ No se pudo asignar el estudiante: {ex.Message}");
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
                lblEstudiantesEstado.Text = res.Ok("Estudiante quitado de la sección.");
            }
            else
            {
                lblEstudiantesEstado.Text = res.Error("quitar el estudiante de la sección");
            }
        }
        catch (Exception ex)
        {
            lblEstudiantesEstado.Text = $"✗ No se pudo quitar el estudiante de la sección: {ex.Message}";
        }
    }

    // ===== ESTUDIANTES y DOCENTES (ADMIN): baja / reactivación =====
    // Las listas salen de GET api/Usuarios filtrando por rol. Dar de baja = PUT api/Usuarios/{id}
    // con Activo = false (NO se borra nada); reactivar = el mismo PUT con Activo = true.

    private static Border PillEstado(bool activo) =>
        activo ? Ui.Pill("ACTIVO", Tono.Verde) : Ui.Pill("DE BAJA", Tono.Coral);

    // ¿El texto buscado aparece en el nombre o en el usuario/correo?
    private static bool Coincide(string filtro, string nombre, string? correo) =>
        filtro.Length == 0
        || nombre.Contains(filtro, StringComparison.OrdinalIgnoreCase)
        || (correo?.Contains(filtro, StringComparison.OrdinalIgnoreCase) ?? false);

    // Botón «Dar de baja» / «Reactivar» de una fila de usuario.
    private Button BotonEstado(int id, string nombre, bool activo, TextBlock estado, string? avisoBaja = null)
    {
        var btn = activo
            ? Ui.Accion("Dar de baja", Tono.Coral, "Desactiva la cuenta: no podrá iniciar sesión (no se borra nada)")
            : Ui.Accion("Reactivar", Tono.Verde, "Vuelve a activar la cuenta");
        btn.Click += (_, _) => CambiarActivoUsuario(id, nombre, !activo, estado, avisoBaja);
        if (activo && id == ApiService.UsuarioId)
        {
            btn.IsEnabled = false;
            btn.ToolTip = "No puedes darte de baja a ti mismo: perderías el acceso.";
            ToolTipService.SetShowOnDisabled(btn, true);
        }
        return btn;
    }

    // Baja / reactivación con confirmación. Antes del PUT se leen los datos actuales del
    // usuario (GET api/Usuarios/{id}) para no pisar cambios hechos desde otro lado.
    private async void CambiarActivoUsuario(int id, string nombre, bool activar, TextBlock estado,
        string? avisoBaja = null)
    {
        if (id <= 0) return;
        if (!activar && id == ApiService.UsuarioId)
        {
            estado.Text = "✗ No puedes darte de baja a ti mismo: perderías el acceso.";
            return;
        }
        var clave = $"usuario:{id}";
        if (!_enCurso.Add(clave)) return; // ya hay una operación en marcha sobre este usuario

        try
        {
            bool confirmado = activar
                ? Confirmar("Reactivar cuenta", $"¿Reactivar a \"{nombre}\"?", "Sí, reactivar",
                    "Podrá volver a iniciar sesión.", "BtnExito", "✓", Tono.Verde)
                : Confirmar("Dar de baja", $"¿Dar de baja a \"{nombre}\"?", "Sí, dar de baja",
                    "No podrá iniciar sesión. No se borra ningún dato y puedes reactivarlo cuando quieras."
                    + (avisoBaja is null ? "" : $"\n{avisoBaja}"));
            if (!confirmado) return;

            var actual = await ApiService.GetResultAsync($"{Rutas.Usuarios}/{id}");
            if (!actual.Exito)
            {
                estado.Text = actual.Error("leer los datos actuales del usuario");
                return;
            }
            var u = JObject.Parse(actual.Contenido);
            var nombreActual = Texto(u, "nombreCompleto");
            var correoActual = Texto(u, "correoOUsuario");
            var rolId = Entero(u, "rolId");
            if (nombreActual is null || correoActual is null || rolId is null)
            {
                estado.Text = "✗ La API devolvió el usuario incompleto; no se cambió su estado.";
                return;
            }

            var res = await ApiService.PutAsync($"{Rutas.Usuarios}/{id}", new
            {
                nombreCompleto = nombreActual,
                correoOUsuario = correoActual,
                rolId = rolId.Value,
                activo = activar
            });
            if (!res.Exito)
            {
                estado.Text = res.Error(activar ? "reactivar la cuenta" : "dar de baja la cuenta");
                return;
            }

            await RecargarUsuarios();
            estado.Text = activar ? $"✓ \"{nombre}\" fue reactivado." : $"✓ \"{nombre}\" fue dado de baja.";
        }
        catch (Exception ex)
        {
            estado.Text = $"✗ No se pudo cambiar el estado de la cuenta: {ex.Message}";
        }
        finally
        {
            _enCurso.Remove(clave);
        }
    }

    // ----- Pestaña Estudiantes -----

    private void RenderEstudiantesAdmin()
    {
        if (_usuariosData is null || listaEstAdmin is null) return;
        var filtro = (txtBuscarEstAdmin.Text ?? "").Trim();
        int modo = cmbFiltroEstAdmin.SelectedIndex; // 0 = todos, 1 = activos, 2 = de baja

        var estudiantes = _usuariosData.Where(u => EsRol(u, "ESTUDIANTE")).ToList();
        listaEstAdmin.Items.Clear();
        int i = 1;
        foreach (var u in estudiantes)
        {
            bool activo = Activo(u, "activo");
            if ((modo == 1 && !activo) || (modo == 2 && activo)) continue;
            if (!Coincide(filtro, NombreDeUsuario(u), Texto(u, "correoOUsuario"))) continue;
            listaEstAdmin.Items.Add(CrearFilaEstudianteAdmin(i++, u));
        }

        int activos = estudiantes.Count(u => Activo(u, "activo"));
        var resumen = $"{estudiantes.Count} estudiante(s): {activos} activo(s), {estudiantes.Count - activos} de baja";
        lblEstAdminEstado.Text = i - 1 == estudiantes.Count ? $"✓ {resumen}." : $"✓ Mostrando {i - 1} · {resumen}.";
        if (_estSeccionDisponible == false)
            lblEstAdminEstado.Text += $"\nLa sección de cada estudiante no está disponible: la API no tiene api/{Rutas.EstudiantesSeccion}.";
    }

    private void BuscarEstAdmin_Changed(object sender, TextChangedEventArgs e) => RenderEstudiantesAdmin();
    private void FiltroEstAdmin_Changed(object sender, SelectionChangedEventArgs e) => RenderEstudiantesAdmin();

    private Border CrearFilaEstudianteAdmin(int num, JToken usuario)
    {
        int id = Entero(usuario, "id") ?? 0;
        var nombre = NombreDeUsuario(usuario);
        var correo = Texto(usuario, "correoOUsuario");
        bool activo = Activo(usuario, "activo");

        // La sección solo se muestra si la API la informa; si no, se dice que no está disponible.
        var seccion = _estSeccionDisponible switch
        {
            false => "Sección: no disponible",
            true when SeccionActual(id) is { } s => s.texto is null ? "Sin sección" : $"Sección {s.texto}",
            _ => "Sección: —"
        };
        var info = Ui.Info(nombre, correo is null ? seccion : $"{correo}  ·  {seccion}", PillEstado(activo));

        var btnSeccion = Ui.Accion("Sección", Tono.Teal, "Asignar o cambiar la sección del estudiante");
        btnSeccion.Click += (_, _) => DialogoSeccionEstudiante(id, nombre);
        if (_estSeccionDisponible == false)
        {
            btnSeccion.IsEnabled = false;
            btnSeccion.ToolTip = $"No disponible: la API conectada no tiene api/{Rutas.EstudiantesSeccion}.";
            ToolTipService.SetShowOnDisabled(btnSeccion, true);
        }

        return Ui.Fila(num, info, btnSeccion, BotonEstado(id, nombre, activo, lblEstAdminEstado));
    }

    // Diálogo para asignar o cambiar la sección de un estudiante (solo secciones activas).
    private void DialogoSeccionEstudiante(int usuarioId, string nombre)
    {
        if (_seccionesData is null)
        {
            lblEstAdminEstado.Text = "✗ No se pudo cargar la lista de secciones; revisa la pestaña Grados y Secciones.";
            return;
        }
        var actual = SeccionActual(usuarioId);
        string? mensaje = null;
        var guardado = DialogoSeleccion("Sección del estudiante",
            actual?.texto is null ? $"{nombre} no tiene sección." : $"{nombre} está en la sección {actual.Value.texto}.",
            "Sección", _seccionesData, it => Activo(it, "activa") ? TextoSeccion(it) : null, actual?.id ?? 0,
            async seccionId =>
            {
                var (ok, texto) = await AsignarSeccionAsync(usuarioId, nombre, seccionId);
                if (ok) { mensaje = texto; return null; }
                return texto ?? ""; // "" = canceló la confirmación: el diálogo sigue abierto, sin error
            });
        if (guardado && mensaje != null) lblEstAdminEstado.Text = mensaje;
    }

    // ----- Pestaña Docentes -----

    private void RenderDocentes()
    {
        if (_usuariosData is null || listaDocentes is null) return;
        var filtro = (txtBuscarDocente.Text ?? "").Trim();

        int estado = cmbFiltroEstadoDocentes?.SelectedIndex ?? 0; // 0 = todos, 1 = activos, 2 = de baja

        var docentes = _usuariosData.Where(u => EsRol(u, "DOCENTE")).ToList();
        listaDocentes.Items.Clear();
        int i = 1;
        foreach (var u in docentes)
        {
            if (!Coincide(filtro, NombreDeUsuario(u), Texto(u, "correoOUsuario"))) continue;
            if ((estado == 1 && !Activo(u, "activo")) || (estado == 2 && Activo(u, "activo"))) continue;
            listaDocentes.Items.Add(CrearFilaDocente(i++, u));
        }

        int activos = docentes.Count(u => Activo(u, "activo"));
        var resumen = $"{docentes.Count} docente(s): {activos} activo(s), {docentes.Count - activos} de baja";
        lblDocentesEstado.Text = i - 1 == docentes.Count ? $"✓ {resumen}." : $"✓ Mostrando {i - 1} · {resumen}.";

        // Si el docente elegido ya no está en la lista, se cierra su panel de clases.
        if (_docenteSeleccionadoId > 0 && !docentes.Any(d => Entero(d, "id") == _docenteSeleccionadoId))
        {
            _docenteSeleccionadoId = 0;
            RenderClasesDocente();
        }
    }

    private void BuscarDocente_Changed(object sender, TextChangedEventArgs e) => RenderDocentes();
    private void FiltroDocentes_Changed(object sender, SelectionChangedEventArgs e) => RenderDocentes();

    private List<JToken> ClasesDe(int docenteId) =>
        _asignacionesData?.Where(a => DocenteDe(a) == docenteId).ToList() ?? new List<JToken>();

    private Border CrearFilaDocente(int num, JToken usuario)
    {
        int id = Entero(usuario, "id") ?? 0;
        var nombre = NombreDeUsuario(usuario);
        var correo = Texto(usuario, "correoOUsuario");
        bool activo = Activo(usuario, "activo");

        int? cantidad = _asignacionesData is null ? null : ClasesDe(id).Count;
        var clases = _asigDocenteDisponible == false ? "Clases: no disponible"
            : cantidad is null ? "Clases: —"
            : $"{cantidad} clase(s)";
        var info = Ui.Info(nombre, correo is null ? clases : $"{correo}  ·  {clases}", PillEstado(activo));

        var btnClases = Ui.Accion("Clases", Tono.Teal, "Ver y gestionar las clases de este docente");
        btnClases.Click += (_, _) => SeleccionarDocente(id);
        var aviso = cantidad > 0 ? $"Sus {cantidad} clase(s) asignada(s) no se quitan." : null;

        var fila = Ui.Fila(num, info, btnClases, BotonEstado(id, nombre, activo, lblDocentesEstado, aviso));
        if (id == _docenteSeleccionadoId) Ui.MarcarSeleccion(fila);
        return fila;
    }

    private void SeleccionarDocente(int id)
    {
        _docenteSeleccionadoId = id;
        LlenarCombosClaseDocente();
        RenderDocentes(); // resalta la fila elegida
        RenderClasesDocente();
    }

    // Combos del formulario «Asignar clase»: solo materias y secciones activas; períodos marcados.
    private void LlenarCombosClaseDocente()
    {
        LlenarCombo(cmbClaseMateria, _materiasData, it => Activo(it, "activa") ? it["nombre"]?.ToString() : null);
        LlenarCombo(cmbClaseSeccion, _seccionesData, it => Activo(it, "activa") ? TextoSeccion(it) : null);
        LlenarCombo(cmbClasePeriodo, _periodosData, TextoPeriodo);
    }

    // Panel derecho: las clases del docente elegido y el formulario para asignarle otra.
    private void RenderClasesDocente()
    {
        if (listaClasesDocente is null) return;
        listaClasesDocente.Items.Clear();
        if (_docenteSeleccionadoId <= 0)
        {
            panelClasesDocente.Visibility = Visibility.Collapsed;
            lblClasesDocenteInfo.Text = "Elige un docente (botón «Clases») para ver y gestionar sus clases.";
            return;
        }

        lblClasesDocenteInfo.Text = $"Clases de {Nombre(_nombreUsuario, _docenteSeleccionadoId)}";
        panelClasesDocente.Visibility = Visibility.Visible;

        if (_asigDocenteDisponible == false)
        {
            lblClasesDocenteEstado.Text = NoDisponible(Rutas.AsignacionesDocente, "las clases asignadas a los docentes");
            return;
        }
        if (_asignacionesData is null)
        {
            lblClasesDocenteEstado.Text = "✗ No se pudieron cargar las asignaciones; revisa la pestaña Asignaciones.";
            return;
        }

        var clases = ClasesDe(_docenteSeleccionadoId);
        int i = 1;
        foreach (var c in clases)
        {
            var (id, docente, materia, seccion, periodo) = DatosAsignacion(c);
            listaClasesDocente.Items.Add(CrearFilaAsignacion(i++, id, docente, materia, seccion, periodo,
                lblClasesDocenteEstado, mostrarDocente: false));
        }
        lblClasesDocenteEstado.Text = clases.Count == 0
            ? "Este docente aún no tiene clases asignadas."
            : $"✓ {clases.Count} clase(s).";
    }

    private async void AgregarClase_Click(object sender, RoutedEventArgs e)
    {
        if (_docenteSeleccionadoId <= 0)
        {
            lblClasesDocenteEstado.Text = "✗ Elige primero un docente.";
            return;
        }
        btnAgregarClase.IsEnabled = false;
        try
        {
            await CrearClaseDocenteAsync(_docenteSeleccionadoId, TagCombo(cmbClaseMateria),
                TagCombo(cmbClaseSeccion), TagCombo(cmbClasePeriodo), lblClasesDocenteEstado);
        }
        finally
        {
            btnAgregarClase.IsEnabled = _asigDocenteDisponible != false;
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
                lblActividadesEstado.Text = res.Error("listar actividades");
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
                lblActividadesEstado.Text = res.Ok("Actividad creada.");
            }
            else
            {
                lblActividadesEstado.Text = res.Error("crear la actividad");
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
                lblPublicarEstado.Text = res.Error("leer la actividad");
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
                lblPublicarEstado.Text = res.Ok("Actividad publicada a la sección.");
            }
            else
            {
                lblPublicarEstado.Text = res.Error("publicar");
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
                lblPublicarEstado.Text = res.Ok($"Estado cambiado a {nuevoEstado}.");
            }
            else
            {
                lblPublicarEstado.Text = res.Error("cambiar el estado");
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
                lblAsignarEstado.Text = res.Error("listar preguntas");
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
                lblAsignarEstado.Text = res.Ok("Pregunta asignada.");
            }
            else
            {
                lblAsignarEstado.Text = res.Error("asignar la pregunta");
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
                _gradosData = null;
                listaGrados.Items.Clear();
                lblGradosEstado.Text = res.Error("listar grados");
                return;
            }

            _gradosData = JArray.Parse(res.Contenido);

            listaGrados.Items.Clear();
            int i = 1;
            foreach (var item in _gradosData)
            {
                int id = item["id"]?.Value<int>() ?? 0;
                var nombre = item["nombre"]?.ToString() ?? "-";
                var orden = item["orden"]?.ToString() ?? ""; // opcional: puede venir null
                bool activo = item["activo"]?.Value<bool?>() ?? true;
                listaGrados.Items.Add(CrearFilaGrado(i++, id, nombre, orden, activo));
            }

            lblGradosEstado.Text = $"✓ {_gradosData.Count} grado(s).";
        }
        catch (Exception ex)
        {
            _gradosData = null;
            lblGradosEstado.Text = $"✗ No se pudieron cargar los grados: {ex.Message}";
        }
    }

    // Nombre obligatorio, máx. 50 caracteres y único (UQ_Grado_Nombre; la API no lo revisa).
    private string? ErrorGrado(string nombre, int id) =>
        Validacion.Texto(nombre, "Nombre del grado", Validacion.MaxGrado)
        ?? (Validacion.Duplicado(_gradosData, "nombre", nombre, id)
            ? $"✗ Ya existe un grado llamado \"{nombre}\"."
            : null);

    private const string ErrorOrden = "✗ El orden debe ser un número entero entre 0 y 255 (o déjalo vacío).";

    private async void CrearGrado_Click(object sender, RoutedEventArgs e)
    {
        var nombre = Validacion.Limpiar(txtNuevoGrado.Text);
        txtNuevoGrado.Text = nombre;
        var error = ErrorGrado(nombre, 0);
        if (error != null)
        {
            lblGradosEstado.Text = error;
            txtNuevoGrado.Focus();
            return;
        }
        // Orden: byte opcional (0-255). Vacío = sin orden.
        if (!LeerOrden(txtOrdenGrado.Text, out var orden))
        {
            lblGradosEstado.Text = ErrorOrden;
            txtOrdenGrado.Focus();
            txtOrdenGrado.SelectAll();
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
                lblGradosEstado.Text = res.Ok($"Grado \"{nombre}\" creado.");
            }
            else
            {
                lblGradosEstado.Text = res.Error("crear el grado");
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

    // Secciones de un grado según la lista ya cargada (null si no se pudo cargar).
    private int? SeccionesDelGrado(int gradoId) =>
        _seccionesData?.Count(s => Entero(s, "gradoId") == gradoId);

    private async void EliminarGrado(int id, string nombre)
    {
        var secciones = SeccionesDelGrado(id);
        var aviso = secciones > 0
            ? $"Este grado tiene {secciones} sección(es): la base no permite borrarlo mientras existan. " +
              "Si ya no se usa, desactívalo con «✎»."
            : "Esta acción no se puede deshacer.";
        if (!Confirmar("Eliminar grado", $"¿Eliminar el grado \"{nombre}\"?", "Sí, eliminar", aviso)) return;

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
                lblGradosEstado.Text = res.Ok("Grado eliminado.");
            }
            else
            {
                lblGradosEstado.Text = res.Error("eliminar el grado");
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

    // Trae TODAS las secciones (para el resumen, los combos y los duplicados) y pinta las del
    // grado elegido.
    private async System.Threading.Tasks.Task RecargarSecciones()
    {
        try
        {
            var res = await ApiService.GetResultAsync(Rutas.Secciones);

            if (!res.Exito)
            {
                _seccionesData = null;
                listaSecciones.Items.Clear();
                lblSeccionesEstado.Text = res.Error("listar secciones");
                return;
            }

            _seccionesData = JArray.Parse(res.Contenido);
            Mapear(_seccionesNombre, _seccionesData, TextoSeccion);
            RenderSecciones();
        }
        catch (Exception ex)
        {
            _seccionesData = null;
            lblSeccionesEstado.Text = $"✗ No se pudieron cargar las secciones: {ex.Message}";
        }
        finally
        {
            ActualizarResumen();
        }
    }

    private void RenderSecciones()
    {
        if (_gradoSeleccionadoId <= 0 || _seccionesData is null) return;

        listaSecciones.Items.Clear();
        int i = 1;
        foreach (var item in _seccionesData)
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

    // Nombre obligatorio, máx. 30 caracteres y único dentro del grado (UQ_Seccion_GradoNombre).
    private string? ErrorSeccion(string nombre, int id, int gradoId) =>
        Validacion.Texto(nombre, "Nombre de la sección", Validacion.MaxSeccion)
        ?? (Validacion.Duplicado(_seccionesData, "nombre", nombre, id, s => Entero(s, "gradoId") == gradoId)
            ? $"✗ Este grado ya tiene una sección llamada \"{nombre}\"."
            : null);

    private async void CrearSeccion_Click(object sender, RoutedEventArgs e)
    {
        if (_gradoSeleccionadoId <= 0)
        {
            lblSeccionesEstado.Text = "✗ Elige primero un grado.";
            return;
        }

        var nombre = Validacion.Limpiar(txtNuevaSeccion.Text);
        txtNuevaSeccion.Text = nombre;
        var error = ErrorSeccion(nombre, 0, _gradoSeleccionadoId);
        if (error != null)
        {
            lblSeccionesEstado.Text = error;
            txtNuevaSeccion.Focus();
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
                lblSeccionesEstado.Text = res.Ok($"Sección \"{nombre}\" creada.");
            }
            else
            {
                lblSeccionesEstado.Text = res.Error("crear la sección");
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
        if (!Confirmar("Eliminar sección", $"¿Eliminar la sección \"{nombre}\"?", "Sí, eliminar",
                "Esta acción no se puede deshacer. Si la sección tiene estudiantes, clases o actividades, " +
                "la base no permitirá borrarla: en ese caso desactívala con «✎»."))
            return;

        try
        {
            var res = await ApiService.DeleteAsync($"{Rutas.Secciones}/{id}");

            if (res.Exito)
            {
                await RecargarSecciones();
                lblSeccionesEstado.Text = res.Ok("Sección eliminada.");
            }
            else
            {
                lblSeccionesEstado.Text = res.Error("eliminar la sección");
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

    // Editar grado: PUT api/Grados/{id} con { nombre, orden (opcional), activo }.
    // Valida y guarda dentro del diálogo (si falla, lo escrito se conserva).
    private async void EditarGrado(int id, string nombreActual, string ordenActual, bool activoActual)
    {
        string? mensaje = null;
        var datos = DialogoCampos("Editar grado",
            new[] { ("Nombre", nombreActual), ("Orden (0-255, opcional)", ordenActual) }, "Activo", activoActual,
            async (valores, activo) =>
            {
                var nombre = Validacion.Limpiar(valores[0]);
                var error = ErrorGrado(nombre, id);
                if (error != null) return error;
                if (!LeerOrden(valores[1], out var orden)) return ErrorOrden;
                if (activoActual && !activo
                    && !Confirmar("Desactivar grado", $"¿Desactivar el grado \"{nombre}\"?", "Sí, desactivar",
                        "Sus secciones no se borran ni se desactivan solas.", "BtnPrimario"))
                    return "";

                var res = await ApiService.PutAsync($"{Rutas.Grados}/{id}", CuerpoGrado(nombre, orden, activo));
                if (!res.Exito) return res.Error("guardar el grado");
                mensaje = res.Ok("Grado actualizado.");
                return null;
            }, new[] { Validacion.MaxGrado, 3 });
        if (datos is null) return;

        await RecargarGrados();
        await RecargarSecciones(); // las secciones muestran el nombre del grado
        lblGradosEstado.Text = mensaje ?? "✓ Grado actualizado.";
    }

    // Editar sección: PUT api/Secciones/{id} con { gradoId, nombre, activa }.
    private async void EditarSeccion(int id, string nombreActual, bool activaActual)
    {
        int gradoId = _gradoSeleccionadoId; // el gradoId se conserva: es el grado que se está gestionando
        string? mensaje = null;
        var datos = DialogoCampos("Editar sección",
            new[] { ("Nombre", nombreActual) }, "Activa", activaActual,
            async (valores, activa) =>
            {
                var nombre = Validacion.Limpiar(valores[0]);
                var error = ErrorSeccion(nombre, id, gradoId);
                if (error != null) return error;
                if (activaActual && !activa
                    && !Confirmar("Desactivar sección", $"¿Desactivar la sección \"{nombre}\"?", "Sí, desactivar",
                        "Dejará de ofrecerse para asignar clases y estudiantes. No se borra nada.", "BtnPrimario"))
                    return "";

                var res = await ApiService.PutAsync($"{Rutas.Secciones}/{id}",
                    new { gradoId = gradoId, nombre = nombre, activa = activa });
                if (!res.Exito) return res.Error("guardar la sección");
                mensaje = res.Ok("Sección actualizada.");
                return null;
            }, new[] { Validacion.MaxSeccion });
        if (datos is null) return;

        await RecargarSecciones();
        lblSeccionesEstado.Text = mensaje ?? "✓ Sección actualizada.";
    }

    // ===== PERÍODOS ACADÉMICOS =====

    private async System.Threading.Tasks.Task RecargarPeriodos()
    {
        try
        {
            var res = await ApiService.GetResultAsync(Rutas.PeriodosAcademicos);

            if (!res.Exito)
            {
                _periodosData = null;
                listaPeriodos.Items.Clear();
                lblPeriodosEstado.Text = res.Error("listar períodos");
                return;
            }

            _periodosData = JArray.Parse(res.Contenido);
            Mapear(_nombrePeriodo, _periodosData, it => it["nombre"]?.ToString());

            listaPeriodos.Items.Clear();
            int i = 1;
            foreach (var item in _periodosData)
            {
                int id = item["id"]?.Value<int>() ?? 0;
                var nombre = item["nombre"]?.ToString() ?? "-";
                // DateOnly en la API: llegan como "yyyy-MM-dd".
                var fInicio = LeerFecha(item["fechaInicio"]);
                var fFin = LeerFecha(item["fechaFin"]);
                bool activo = item["activo"]?.Value<bool?>() ?? true;
                listaPeriodos.Items.Add(CrearFilaPeriodo(i++, id, nombre, fInicio, fFin, activo));
            }

            lblPeriodosEstado.Text = $"✓ {_periodosData.Count} período(s).";
        }
        catch (Exception ex)
        {
            _periodosData = null;
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

    // Nombre obligatorio, máx. 50 caracteres y único (UQ_Periodo_Nombre; la API no lo revisa).
    private string? ErrorPeriodo(string nombre, int id) =>
        Validacion.Texto(nombre, "Nombre del período", Validacion.MaxPeriodo)
        ?? (Validacion.Duplicado(_periodosData, "nombre", nombre, id)
            ? $"✗ Ya existe un período llamado \"{nombre}\"."
            : null);

    private async void CrearPeriodo_Click(object sender, RoutedEventArgs e)
    {
        var nombre = Validacion.Limpiar(txtNombrePeriodo.Text);
        txtNombrePeriodo.Text = nombre;
        var error = ErrorPeriodo(nombre, 0);
        if (error != null)
        {
            lblPeriodosEstado.Text = error;
            txtNombrePeriodo.Focus();
            return;
        }
        var fInicio = NormalizarFecha(txtFechaInicio.Text);
        var fFin = NormalizarFecha(txtFechaFin.Text);
        var errorFechas = ValidarFechasPeriodo(fInicio, fFin);
        if (errorFechas != null)
        {
            lblPeriodosEstado.Text = errorFechas;
            (fInicio is null ? txtFechaInicio : txtFechaFin).Focus();
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
                lblPeriodosEstado.Text = res.Ok($"Período \"{nombre}\" creado.");
            }
            else
            {
                lblPeriodosEstado.Text = res.Error("crear el período");
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

    // Editar período: PUT api/PeriodosAcademicos/{id} con { nombre, fechaInicio, fechaFin, activo }.
    // Valida y guarda dentro del diálogo (si falla, lo escrito se conserva).
    private async void EditarPeriodo(int id, string nombreActual, string fInicioActual, string fFinActual, bool activoActual)
    {
        string? mensaje = null;
        var datos = DialogoCampos("Editar período",
            new[]
            {
                ("Nombre", nombreActual),
                ("Fecha inicio (AAAA-MM-DD)", fInicioActual),
                ("Fecha fin (AAAA-MM-DD)", fFinActual)
            }, "Activo", activoActual,
            async (valores, activo) =>
            {
                var nombre = Validacion.Limpiar(valores[0]);
                var error = ErrorPeriodo(nombre, id);
                if (error != null) return error;
                var fInicio = NormalizarFecha(valores[1]);
                var fFin = NormalizarFecha(valores[2]);
                var errorFechas = ValidarFechasPeriodo(fInicio, fFin);
                if (errorFechas != null) return errorFechas;
                if (activoActual && !activo
                    && !Confirmar("Desactivar período", $"¿Desactivar el período \"{nombre}\"?", "Sí, desactivar",
                        "No se borra nada; puedes volver a activarlo cuando quieras.", "BtnPrimario"))
                    return "";

                var res = await ApiService.PutAsync($"{Rutas.PeriodosAcademicos}/{id}", new
                {
                    nombre = nombre,
                    fechaInicio = fInicio,
                    fechaFin = fFin,
                    activo = activo
                });
                if (!res.Exito) return res.Error("guardar el período");
                mensaje = res.Ok("Período actualizado.");
                return null;
            }, new[] { Validacion.MaxPeriodo, 10, 10 });
        if (datos is null) return;

        await RecargarPeriodos();
        lblPeriodosEstado.Text = mensaje ?? "✓ Período actualizado.";
    }

    private async void EliminarPeriodo(int id, string nombre)
    {
        if (!Confirmar("Eliminar período", $"¿Eliminar el período \"{nombre}\"?", "Sí, eliminar",
                "Esta acción no se puede deshacer. Si el período ya tiene clases o actividades, la base " +
                "no permitirá borrarlo: en ese caso desactívalo con «Editar»."))
            return;

        try
        {
            var res = await ApiService.DeleteAsync($"{Rutas.PeriodosAcademicos}/{id}");
            if (res.Exito)
            {
                await RecargarPeriodos();
                lblPeriodosEstado.Text = res.Ok("Período eliminado.");
            }
            else
            {
                lblPeriodosEstado.Text = res.Error("eliminar el período");
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
    // Con 'alGuardar' el diálogo valida y guarda SIN cerrarse: recibe los valores (recortados)
    // y devuelve null si salió bien (se cierra) o el error a mostrar dentro del diálogo, que
    // sigue abierto con lo escrito. "" = seguir abierto sin mensaje (p. ej. canceló una confirmación).
    private (string[] valores, bool activo)? DialogoCampos(
        string titulo, (string etiqueta, string valor)[] campos, string activoLabel, bool activoInicial,
        Func<string[], bool, System.Threading.Tasks.Task<string?>>? alGuardar = null, int[]? maximos = null)
    {
        (string[], bool)? resultado = null;

        var cont = new StackPanel();
        var cajas = campos
            .Select((c, i) => CampoDialogo(cont, c.etiqueta, c.valor, maximos?.ElementAtOrDefault(i) ?? 0))
            .ToList();

        var chk = new CheckBox
        {
            Content = activoLabel,
            IsChecked = activoInicial,
            Margin = new Thickness(2, 4, 0, 0)
        };
        cont.Children.Add(chk);
        var error = CajaErrorDialogo(cont);

        var dlg = NuevoDialogo(titulo, "Modifica los datos y pulsa «Guardar».", cont,
            out var btnCancelar, out var btnGuardar, "Guardar", "BtnPrimario");
        ConfigurarGuardado(dlg, btnCancelar, btnGuardar, error, async () =>
        {
            var valores = cajas.Select(c => c.Text.Trim()).ToArray();
            bool activo = chk.IsChecked == true;
            var fallo = alGuardar is null ? null : await alGuardar(valores, activo);
            if (fallo is null) resultado = (valores, activo);
            return fallo;
        });
        if (cajas.Count > 0) dlg.Loaded += (_, _) => { cajas[0].Focus(); cajas[0].SelectAll(); };

        var ok = dlg.ShowDialog();
        return ok == true ? resultado : null;
    }

    // Diálogo con una lista desplegable. 'alGuardar' recibe el id elegido y devuelve null si
    // se guardó (se cierra) o el error a mostrar (sigue abierto). Devuelve true si se guardó.
    private bool DialogoSeleccion(string titulo, string subtitulo, string etiqueta, JArray datos,
        Func<JToken, string?> texto, int elegido, Func<int, System.Threading.Tasks.Task<string?>> alGuardar)
    {
        var cont = new StackPanel();
        cont.Children.Add(new TextBlock { Text = etiqueta, Style = (Style)FindResource("Etiqueta") });
        var combo = new ComboBox { Margin = new Thickness(0, 0, 0, 4) };
        LlenarCombo(combo, datos, texto);
        if (elegido > 0) SeleccionarEnCombo(combo, elegido);
        cont.Children.Add(combo);
        var error = CajaErrorDialogo(cont);
        if (combo.Items.Count == 0) error.Text = "✗ No hay opciones activas para elegir.";

        var dlg = NuevoDialogo(titulo, subtitulo, cont, out var btnCancelar, out var btnGuardar,
            "Guardar", "BtnPrimario");
        ConfigurarGuardado(dlg, btnCancelar, btnGuardar, error, () =>
        {
            int id = TagCombo(combo);
            return id <= 0
                ? System.Threading.Tasks.Task.FromResult<string?>("✗ Elige una opción de la lista.")
                : alGuardar(id);
        });
        dlg.Loaded += (_, _) => combo.Focus();
        return dlg.ShowDialog() == true;
    }

    // Botones Cancelar / Guardar de un diálogo. Mientras se guarda, todo queda bloqueado
    // (sin doble clic ni cierre con Esc) y el botón muestra un indicador de carga.
    private static void ConfigurarGuardado(Window dlg, Button btnCancelar, Button btnGuardar, TextBlock error,
        Func<System.Threading.Tasks.Task<string?>> guardar)
    {
        bool guardando = false, cerrado = false;
        var textoGuardar = btnGuardar.Content;
        dlg.Closed += (_, _) => cerrado = true;
        btnCancelar.Click += (_, _) => { if (!guardando) dlg.DialogResult = false; };
        dlg.KeyDown += (_, e) => { if (e.Key == Key.Escape && !guardando) dlg.DialogResult = false; };
        btnGuardar.Click += async (_, _) =>
        {
            if (guardando) return;
            guardando = true;
            error.Text = "";
            btnGuardar.IsEnabled = btnCancelar.IsEnabled = false;
            btnGuardar.Content = Ui.Cargando("Guardando…");

            string? fallo;
            try { fallo = await guardar(); }
            catch (Exception ex) { fallo = $"✗ Ocurrió un error inesperado: {ex.Message}"; }

            guardando = false;
            if (cerrado) return; // se cerró mientras guardaba (p. ej. sesión expirada)
            if (fallo is null)
            {
                dlg.DialogResult = true;
                return;
            }
            error.Text = fallo;
            btnGuardar.Content = textoGuardar;
            btnGuardar.IsEnabled = btnCancelar.IsEnabled = true;
        };
    }

    // Cajita de error dentro de un diálogo: la misma notificación con icono que el resto de
    // la app (oculta mientras no haya texto).
    private static TextBlock CajaErrorDialogo(StackPanel cont)
    {
        var texto = new TextBlock { Style = (Style)Application.Current.FindResource("TextoEstado") };
        var caja = new Border
        {
            Style = (Style)Application.Current.FindResource("CajaEstado"),
            Margin = new Thickness(0, 14, 0, 0),
            Child = texto
        };
        cont.Children.Add(caja);
        Ui.ComoNotificacion(texto);
        return texto;
    }

    // Diálogo de confirmación (eliminar, dar de baja, cambiar…). Devuelve true si el usuario acepta.
    private bool Confirmar(string titulo, string mensaje, string textoAceptar = "Sí, eliminar",
        string aviso = "Esta acción no se puede deshacer.", string estiloAceptar = "BtnPeligro",
        string icono = "!", Tono tono = Tono.Coral)
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
            out var btnCancelar, out var btnAceptar, textoAceptar, estiloAceptar, icono, tono);
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
        string? icono = null, Tono tonoIcono = Tono.Coral)
    {
        // Si ya hay un diálogo abierto (p. ej. una confirmación al guardar), el nuevo va encima de él.
        var dueno = OwnedWindows.Cast<Window>().LastOrDefault(w => w.IsActive) ?? (Window)this;
        var dlg = new Window
        {
            Title = titulo,
            Width = 500,
            SizeToContent = SizeToContent.Height,
            Owner = dueno,
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
            var (fuerte, suave) = Ui.Colores(tonoIcono);
            // «!» = aviso y «✓» = éxito se dibujan con los iconos de la web.
            var clave = icono switch { "!" => "IcoAviso", "✓" => "IcoOk", _ => null };
            cabecera.Children.Add(new Border
            {
                Width = 46,
                Height = 46,
                CornerRadius = new CornerRadius(15),
                Background = suave,
                Margin = new Thickness(0, 0, 14, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Child = clave != null
                    ? Ui.Icono(clave, 22, fuerte)
                    : new TextBlock
                    {
                        Text = icono,
                        FontSize = 20,
                        FontWeight = FontWeights.Black,
                        Foreground = fuerte,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center
                    }
            });
        }
        var textos = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        textos.Children.Add(new TextBlock { Text = titulo, FontSize = 21, FontWeight = FontWeights.ExtraBold });
        if (subtitulo != null)
            textos.Children.Add(new TextBlock
            {
                Text = subtitulo,
                FontSize = 13,
                Foreground = Paleta.Apagado,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 380,
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
        var marco = new Grid { Margin = new Thickness(26) };
        marco.Children.Add(new Border
        {
            CornerRadius = (CornerRadius)FindResource("RadioGrande"),
            Background = Brushes.White,
            Effect = (Effect)FindResource("SombraVentana")
        });
        marco.Children.Add(new Border
        {
            CornerRadius = (CornerRadius)FindResource("RadioGrande"),
            Background = Brushes.White,
            BorderBrush = Paleta.Borde,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(30, 28, 30, 28),
            Child = cuerpo
        });
        dlg.Content = marco;
        Ui.Aparecer(marco, 10, 200);
        return dlg;
    }

    // Agrega a 'cont' una etiqueta + caja de texto (con el estilo del tema) y devuelve la caja.
    // 'maximo' > 0 limita lo que se puede escribir (largo de la columna en la base).
    private TextBox CampoDialogo(StackPanel cont, string etiqueta, string valor, int maximo = 0)
    {
        cont.Children.Add(new TextBlock { Text = etiqueta, Style = (Style)FindResource("Etiqueta") });
        var caja = new TextBox { Text = valor, Margin = new Thickness(0, 0, 0, 14) };
        if (maximo > 0) caja.MaxLength = maximo;
        cont.Children.Add(caja);
        return caja;
    }

    // Los mensajes de estado (✓ / ✗) se muestran como notificaciones discretas con icono:
    // éxito, aviso (no disponible), error o información; ocultas si no hay texto.
    private void ColorearMensajesDeEstado()
    {
        var etiquetas = new[]
        {
            lblEstado, lblMateriasEstado, lblUsuariosEstado, lblGradosEstado, lblSeccionesEstado,
            lblPeriodosEstado, lblAsignacionesEstado, lblEstudiantesEstado, lblTemasEstado, lblSubtemasEstado,
            lblPreguntaEstado, lblPreguntasEstado, lblActividadesEstado, lblAsignarEstado, lblPublicarEstado,
            lblEstAdminEstado, lblDocentesEstado, lblClasesDocenteEstado
        };
        foreach (var lbl in etiquetas) Ui.ComoNotificacion(lbl);
    }

    // Crea una materia (POST api/Materias) y refresca la lista para verla al instante.
    // Si algo falla, lo escrito se conserva para poder corregirlo.
    private async void CrearMateria_Click(object sender, RoutedEventArgs e)
    {
        var nombre = Validacion.Limpiar(txtNuevaMateria.Text);
        txtNuevaMateria.Text = nombre; // espacios sobrantes fuera (y se ve en el campo)
        var error = ErrorMateria(nombre, 0);
        if (error != null)
        {
            lblMateriasEstado.Text = error;
            txtNuevaMateria.Focus();
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
                lblMateriasEstado.Text = res.Ok($"Materia \"{nombre}\" creada.");
            }
            else
            {
                lblMateriasEstado.Text = res.Error("crear la materia");
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

    // Nombre obligatorio, máx. 80 caracteres y único (la base tiene UQ_Materia_Nombre y la API
    // no lo revisa: un duplicado terminaría en error 500).
    private string? ErrorMateria(string nombre, int id) =>
        Validacion.Texto(nombre, "Nombre de la materia", Validacion.MaxMateria)
        ?? (Validacion.Duplicado(_materiasData, "nombre", nombre, id)
            ? $"✗ Ya existe una materia llamada \"{nombre}\"."
            : null);

    // Usuario o correo con formato válido y que no lo tenga otro usuario.
    private string? ErrorCorreoUsuario(string correo, int id) =>
        Validacion.CorreoOUsuario(correo)
        ?? (Validacion.Duplicado(_usuariosData, "correoOUsuario", correo, id)
            ? $"✗ Ya existe un usuario con el usuario o correo \"{correo}\"."
            : null);

    private string NombreRol(int rolId) => _roles.TryGetValue(rolId, out var r) ? r.codigo : $"rol #{rolId}";

    // Crea un usuario (POST api/Usuarios) y refresca la lista. Esa ruta de la API no valida
    // los datos, así que aquí se aplican las mismas reglas que usa la API en Auth/registro.
    private async void CrearUsuario_Click(object sender, RoutedEventArgs e)
    {
        // Espacios sobrantes fuera (y se ve en los campos). La contraseña no se recorta.
        var nombreCompleto = Validacion.Limpiar(txtNombreCompleto.Text);
        var correoOUsuario = txtCorreoUsuario.Text.Trim();
        txtNombreCompleto.Text = nombreCompleto;
        txtCorreoUsuario.Text = correoOUsuario;
        var clave = pwdClave.Password;
        int rolId = TagCombo(cmbRol);

        Control campo = txtNombreCompleto;
        var error = Validacion.NombreCompleto(nombreCompleto);
        if (error is null) { campo = txtCorreoUsuario; error = ErrorCorreoUsuario(correoOUsuario, 0); }
        if (error is null) { campo = pwdClave; error = Validacion.ClaveNueva(clave); }
        if (error is null && rolId <= 0)
        {
            campo = cmbRol;
            error = _roles.Count == 0 ? "✗ No se pudieron cargar los roles de la API; no se puede elegir rol." : "✗ Elige un rol.";
        }
        if (error != null)
        {
            lblUsuariosEstado.Text = error;
            campo.Focus();
            return;
        }

        // Una cuenta de administrador tiene acceso total: se confirma antes de crearla.
        if (NombreRol(rolId) == "ADMIN"
            && !Confirmar("Crear administrador", $"¿Crear a \"{nombreCompleto}\" con rol ADMIN?", "Sí, crear",
                "Tendrá acceso total a la gestión de la plataforma.", "BtnPrimario"))
            return;

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
                lblUsuariosEstado.Text = res.Ok($"Usuario \"{nombreCompleto}\" creado.");
            }
            else
            {
                lblUsuariosEstado.Text = res.Error("crear el usuario");
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

    // Fila de usuario con botones Editar / Dar de baja (o Reactivar) / Eliminar (solo ADMIN).
    private Border CrearFilaUsuario(int num, int id, string nombreCompleto, string correo,
        string rol, int rolId, bool activo)
    {
        // Nombre + etiqueta de estado (verde activo / coral de baja), y debajo correo · rol.
        var sub = string.IsNullOrWhiteSpace(correo) ? rol : $"{correo}  ·  {rol}";
        var info = Ui.Info(nombreCompleto, sub, PillEstado(activo));

        var btnEditar = Ui.Accion("✎ Editar", Tono.Morado);
        btnEditar.Click += (_, _) => EditarUsuario(id, nombreCompleto, correo, rolId, activo);
        var btnEliminar = Ui.Accion("🗑", Tono.Coral, "Eliminar usuario definitivamente");
        btnEliminar.Click += (_, _) => EliminarUsuario(id, nombreCompleto);
        if (id == ApiService.UsuarioId)
        {
            btnEliminar.IsEnabled = false;
            btnEliminar.ToolTip = "No puedes eliminar tu propia cuenta.";
            ToolTipService.SetShowOnDisabled(btnEliminar, true);
        }

        return Ui.Fila(num, info, btnEditar, BotonEstado(id, nombreCompleto, activo, lblUsuariosEstado), btnEliminar);
    }

    // Editar usuario: PUT api/Usuarios/{id} con { nombreCompleto, correoOUsuario, rolId, activo }.
    // NO cambia la contraseña. Valida y guarda dentro del diálogo (si falla, lo escrito se conserva).
    private async void EditarUsuario(int id, string nombreActual, string correoActual, int rolIdActual, bool activoActual)
    {
        string? mensaje = null;
        var guardado = PedirDatosUsuario(nombreActual, correoActual, rolIdActual, activoActual,
            async (nombre, correo, rolId, activo) =>
            {
                var error = Validacion.NombreCompleto(nombre) ?? ErrorCorreoUsuario(correo, id)
                            ?? (rolId <= 0 ? "✗ Elige un rol." : null);
                if (error != null) return error;

                // El ADMIN no puede quitarse a sí mismo el acceso.
                if (id == ApiService.UsuarioId && !activo)
                    return "✗ No puedes desactivar tu propia cuenta: perderías el acceso.";
                if (id == ApiService.UsuarioId && rolId != rolIdActual)
                    return "✗ No puedes cambiar tu propio rol: perderías el acceso de administrador.";

                // Cambios delicados: se confirman antes de guardar ("" = canceló, el diálogo sigue abierto).
                if (rolId != rolIdActual
                    && !Confirmar("Cambiar rol",
                        $"¿Cambiar el rol de \"{nombre}\" de {NombreRol(rolIdActual)} a {NombreRol(rolId)}?",
                        "Sí, cambiar", "Cambia lo que esta persona puede hacer en la plataforma.", "BtnPrimario"))
                    return "";
                if (activoActual && !activo
                    && !Confirmar("Dar de baja", $"¿Dar de baja a \"{nombre}\"?", "Sí, dar de baja",
                        "No podrá iniciar sesión. No se borra ningún dato y puedes reactivarlo cuando quieras."))
                    return "";

                var res = await ApiService.PutAsync($"{Rutas.Usuarios}/{id}", new
                {
                    nombreCompleto = nombre,
                    correoOUsuario = correo,
                    rolId = rolId,
                    activo = activo
                });
                if (!res.Exito) return res.Error("guardar los cambios del usuario");
                mensaje = res.Ok("Usuario actualizado.");
                return null;
            });
        if (!guardado) return; // canceló

        await RecargarUsuarios();
        lblUsuariosEstado.Text = mensaje ?? "✓ Usuario actualizado.";
    }

    // Eliminar usuario: DELETE api/Usuarios/{id} (con confirmación). Borra el registro; para
    // solo impedir el acceso está «Dar de baja».
    private async void EliminarUsuario(int id, string nombre)
    {
        if (id == ApiService.UsuarioId)
        {
            lblUsuariosEstado.Text = "✗ No puedes eliminar tu propia cuenta.";
            return;
        }
        if (!Confirmar("Eliminar usuario", $"¿Eliminar definitivamente al usuario \"{nombre}\"?", "Sí, eliminar",
                "Esta acción no se puede deshacer. Si solo quieres que no pueda entrar, usa «Dar de baja»: conserva sus datos."))
            return;

        try
        {
            var res = await ApiService.DeleteAsync($"{Rutas.Usuarios}/{id}");

            if (res.Exito)
            {
                await RecargarUsuarios();
                lblUsuariosEstado.Text = res.Ok("Usuario eliminado.");
            }
            else
            {
                lblUsuariosEstado.Text = res.Error("eliminar el usuario");
            }
        }
        catch (Exception ex)
        {
            lblUsuariosEstado.Text = $"✗ No se pudo eliminar el usuario: {ex.Message}";
        }
    }

    // Diálogo para editar un usuario (nombre, correo, rol, activo). No pide contraseña.
    // 'alGuardar' valida y guarda sin cerrar el diálogo (ver DialogoCampos). Devuelve true si se guardó.
    private bool PedirDatosUsuario(string nombreActual, string correoActual, int rolIdActual, bool activoActual,
        Func<string, string, int, bool, System.Threading.Tasks.Task<string?>> alGuardar)
    {
        var cont = new StackPanel();
        var cajaNombre = CampoDialogo(cont, "Nombre completo", nombreActual, Validacion.MaxNombreCompleto);
        var cajaCorreo = CampoDialogo(cont, "Usuario o correo", correoActual, Validacion.MaxCorreoOUsuario);

        // Rol: los roles reales de la API (si el actual no se conoce, se conserva igual).
        cont.Children.Add(new TextBlock { Text = "Rol", Style = (Style)FindResource("Etiqueta") });
        var combo = new ComboBox { Margin = new Thickness(0, 0, 0, 14) };
        LlenarComboRoles(combo, rolIdActual);
        cont.Children.Add(combo);

        var chkActivo = new CheckBox
        {
            Content = "Usuario activo",
            IsChecked = activoActual,
            Margin = new Thickness(2, 4, 0, 0)
        };
        cont.Children.Add(chkActivo);
        var error = CajaErrorDialogo(cont);

        var dlg = NuevoDialogo("Editar usuario", "La contraseña no se modifica desde aquí.", cont,
            out var btnCancelar, out var btnGuardar, "Guardar", "BtnPrimario");
        ConfigurarGuardado(dlg, btnCancelar, btnGuardar, error, () =>
        {
            // Espacios sobrantes fuera, y se ve en los campos.
            cajaNombre.Text = Validacion.Limpiar(cajaNombre.Text);
            cajaCorreo.Text = cajaCorreo.Text.Trim();
            return alGuardar(cajaNombre.Text, cajaCorreo.Text, TagCombo(combo), chkActivo.IsChecked == true);
        });
        dlg.Loaded += (_, _) => { cajaNombre.Focus(); cajaNombre.SelectAll(); };

        return dlg.ShowDialog() == true;
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
                lblTemasEstado.Text = res.Error("listar temas");
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
                lblTemasEstado.Text = res.Ok("Tema creado.");
            }
            else
            {
                lblTemasEstado.Text = res.Error("crear el tema");
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
                lblPreguntaEstado.Text = res.Ok("Pregunta creada.");
            }
            else
            {
                lblPreguntaEstado.Text = res.Error("crear la pregunta");
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
                lblTemasEstado.Text = res.Ok("Tema actualizado.");
            }
            else
            {
                lblTemasEstado.Text = res.Error("editar el tema");
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
                lblTemasEstado.Text = res.Ok("Tema eliminado.");
            }
            else
            {
                lblTemasEstado.Text = res.Error("eliminar el tema");
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
                lblSubtemasEstado.Text = res.Error("listar subtemas");
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
                lblSubtemasEstado.Text = res.Ok("Subtema creado.");
            }
            else
            {
                lblSubtemasEstado.Text = res.Error("crear el subtema");
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
                lblSubtemasEstado.Text = res.Ok("Subtema actualizado.");
            }
            else
            {
                lblSubtemasEstado.Text = res.Error("editar el subtema");
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
                lblSubtemasEstado.Text = res.Ok("Subtema eliminado.");
            }
            else
            {
                lblSubtemasEstado.Text = res.Error("eliminar el subtema");
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
                lblPreguntasEstado.Text = res.Error("listar preguntas");
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
                lblPreguntasEstado.Text = res.Ok("Pregunta aprobada.");
            }
            else
            {
                lblPreguntasEstado.Text = res.Error($"aprobar la pregunta (estadoId={EstadoAprobadaId})");
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
                lblPreguntasEstado.Text = det.Error("abrir la pregunta");
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
                lblPreguntasEstado.Text = res.Ok("Pregunta actualizada.");
            }
            else
            {
                lblPreguntasEstado.Text = res.Error("editar la pregunta");
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
                lblPreguntasEstado.Text = res.Ok("Pregunta eliminada.");
            }
            else
            {
                lblPreguntasEstado.Text = res.Error("eliminar la pregunta");
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

    // Editar materia: PUT api/Materias/{id} con { nombre, activa }. Valida y guarda dentro del
    // diálogo: si algo falla, el diálogo sigue abierto con lo escrito.
    private async void EditarMateria(int id, string nombreActual, bool activaActual)
    {
        string? mensaje = null;
        var datos = DialogoCampos("Editar materia",
            new[] { ("Nombre", nombreActual) }, "Materia activa", activaActual,
            async (valores, activa) =>
            {
                var nombre = Validacion.Limpiar(valores[0]);
                var error = ErrorMateria(nombre, id);
                if (error != null) return error;
                if (activaActual && !activa
                    && !Confirmar("Desactivar materia", $"¿Desactivar la materia \"{nombre}\"?", "Sí, desactivar",
                        "Dejará de ofrecerse para asignar clases nuevas. No se borra nada.", "BtnPrimario"))
                    return "";

                var res = await ApiService.PutAsync($"{Rutas.Materias}/{id}", new { nombre = nombre, activa = activa });
                if (!res.Exito) return res.Error("guardar la materia");
                mensaje = res.Ok("Materia actualizada.");
                return null;
            }, new[] { Validacion.MaxMateria });
        if (datos is null) return; // el usuario canceló

        await RecargarMaterias(); // refresca la lista
        lblMateriasEstado.Text = mensaje ?? "✓ Materia actualizada.";
    }

    // Eliminar materia: DELETE api/Materias/{id} (con confirmación) y refresco.
    private async void EliminarMateria(int id, string nombre)
    {
        if (!Confirmar("Eliminar materia", $"¿Eliminar la materia \"{nombre}\"?", "Sí, eliminar",
                "Esta acción no se puede deshacer. Si la materia ya tiene temas, actividades o clases, " +
                "la base no permitirá borrarla: en ese caso desactívala con «Editar»."))
            return;

        try
        {
            var res = await ApiService.DeleteAsync($"{Rutas.Materias}/{id}");

            if (res.Exito)
            {
                await RecargarMaterias(); // refresca la lista
                lblMateriasEstado.Text = res.Ok("Materia eliminada.");
            }
            else
            {
                lblMateriasEstado.Text = res.Error("eliminar la materia");
            }
        }
        catch (Exception ex)
        {
            lblMateriasEstado.Text = $"✗ No se pudo eliminar la materia: {ex.Message}";
        }
    }
}
