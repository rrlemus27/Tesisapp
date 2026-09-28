using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
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

    public DashboardWindow()
    {
        InitializeComponent();
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
        lblNombre.Text = ApiService.NombreUsuario ?? "Usuario";
        lblRolBadge.Text = ApiService.Rol ?? "ROL";
        lblBienvenida.Text = $"Sesión iniciada como {ApiService.Rol}. Datos en vivo desde la API.";

        // Por defecto se muestra la vista genérica; el ADMIN usa su propia vista.
        panelGenerico.Visibility = Visibility.Visible;
        panelAdmin.Visibility = Visibility.Collapsed;

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
                await CargarCombosAsignacion();
                await RecargarAsignaciones();
                Stat("Gestión", "Materias · Usuarios · Grados · Períodos · Asignaciones");
                Stat("Rol", "ADMIN");
                break;

            case "DOCENTE":
                lblTitulo.Text = "Panel del Docente";
                lblSeccion.Text = "Materias disponibles (elige una)";
                // onSeleccion: al hacer clic en una materia se abre el panel de temas.
                await CargarLista(listaDatos, lblEstado, "materias", "nombre", "activa", onSeleccion: SeleccionarMateria);
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

    // Crea una tarjeta de stat arriba
    private void Stat(string arriba, string abajo)
    {
        var border = new Border
        {
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(22, 16, 22, 16),
            Margin = new Thickness(0, 0, 14, 0),
            Background = new LinearGradientBrush(
                (Color)ColorConverter.ConvertFromString("#3D195B"),
                (Color)ColorConverter.ConvertFromString("#963CBD"), 45)
        };
        var sp = new StackPanel();
        sp.Children.Add(new TextBlock
        {
            Text = arriba,
            FontSize = 11,
            FontWeight = FontWeights.Bold,
            Foreground = (Brush)new BrushConverter().ConvertFrom("#00FF87")!
        });
        sp.Children.Add(new TextBlock
        {
            Text = abajo,
            FontSize = 18,
            FontWeight = FontWeights.Bold,
            Foreground = Brushes.White,
            Margin = new Thickness(0, 4, 0, 0)
        });
        border.Child = sp;
        panelStats.Children.Add(border);
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
                var val2 = item[campo2]?.ToString() ?? "";
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
            var res = await ApiService.GetResultAsync("materias");
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
            var activaStr = item["activa"]?.ToString() ?? "";
            bool activa = item["activa"]?.Value<bool>() ?? true;
            listaMaterias.Items.Add(CrearFila(i++, nombre, activaStr, id, activa, conAcciones: true));
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
            var res = await ApiService.GetResultAsync("usuarios");

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
            int rolId = item["rolId"]?.Value<int>() ?? 0;
            bool activo = item["activo"]?.Value<bool>() ?? true;
            listaUsuarios.Items.Add(CrearFilaUsuario(i++, id, nombreCompleto, correo, rol, rolId, activo));
            mostrados++;
        }
        lblUsuariosEstado.Text = filtro.Length > 0
            ? $"✓ {mostrados} de {_usuariosData.Count} usuario(s) (filtro: \"{filtro}\")."
            : $"✓ {_usuariosData.Count} usuario(s).";
    }

    private void BuscarUsuario_Changed(object sender, TextChangedEventArgs e) => RenderUsuarios(txtBuscarUsuario.Text);

    // ===== ASIGNACIONES DOCENTE-CLASE =====

    // Llena los 4 combos (docentes, materias, secciones, períodos) desde la API.
    private async System.Threading.Tasks.Task CargarCombosAsignacion()
    {
        await LlenarComboAsync(cmbAsigDocente, "usuarios", it =>
            string.Equals(it["rol"]?.ToString(), "DOCENTE", StringComparison.OrdinalIgnoreCase)
                ? it["nombreCompleto"]?.ToString() : null);

        await LlenarComboAsync(cmbAsigMateria, "materias", it => it["nombre"]?.ToString());

        await LlenarComboAsync(cmbAsigSeccion, "secciones", it =>
        {
            var s = it["nombre"]?.ToString() ?? "";
            var g = it["grado"]?.ToString();
            return string.IsNullOrWhiteSpace(g) ? s : $"{s} · {g}";
        });

        await LlenarComboAsync(cmbAsigPeriodo, "periodosacademicos", it => it["nombre"]?.ToString());
    }

    // GET a 'ruta' y llena el combo con ComboBoxItem(Content=texto, Tag=id).
    // 'texto' devuelve null para saltarse ese elemento (p. ej. usuarios que no son DOCENTE).
    private async System.Threading.Tasks.Task LlenarComboAsync(ComboBox combo, string ruta, Func<JToken, string?> texto)
    {
        combo.Items.Clear();
        try
        {
            var res = await ApiService.GetResultAsync(ruta);
            if (!res.Exito) return;
            var array = JArray.Parse(res.Contenido);
            foreach (var item in array)
            {
                var t = texto(item);
                if (t is null) continue;
                int id = item["id"]?.Value<int>() ?? 0;
                combo.Items.Add(new ComboBoxItem { Content = t, Tag = id });
            }
            if (combo.Items.Count > 0) combo.SelectedIndex = 0;
        }
        catch { /* si falla, el combo queda vacío; el error se verá al asignar */ }
    }

    private static int TagCombo(ComboBox combo) =>
        (combo.SelectedItem as ComboBoxItem)?.Tag is int id ? id : 0;

    private async System.Threading.Tasks.Task RecargarAsignaciones()
    {
        try
        {
            var res = await ApiService.GetResultAsync("docenteclases");
            if (!res.Exito)
            {
                listaAsignaciones.Items.Clear();
                var detalle = string.IsNullOrWhiteSpace(res.Mensaje) ? res.Contenido : res.Mensaje;
                lblAsignacionesEstado.Text = $"✗ Error {res.Codigo} al listar asignaciones: {detalle}";
                return;
            }
            var array = JArray.Parse(res.Contenido);
            listaAsignaciones.Items.Clear();
            int i = 1;
            foreach (var item in array)
            {
                int id = item["id"]?.Value<int>() ?? 0;
                var docente = item["docente"]?.ToString() ?? "-";
                var materia = item["materia"]?.ToString() ?? "-";
                var seccion = item["seccion"]?.ToString() ?? "-";
                var periodo = item["periodo"]?.ToString() ?? "-";
                listaAsignaciones.Items.Add(CrearFilaAsignacion(i++, id, docente, materia, seccion, periodo));
            }
            lblAsignacionesEstado.Text = $"✓ {array.Count} asignación(es).";
        }
        catch (Exception ex)
        {
            lblAsignacionesEstado.Text = $"✗ No se pudieron cargar las asignaciones: {ex.Message}";
        }
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
            var res = await ApiService.PostAsync("docenteclases", new
            {
                docenteUsuarioId = docenteId,
                materiaId = materiaId,
                seccionId = seccionId,
                periodoAcademicoId = periodoId,
                activa = true
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
        var confirmar = MessageBox.Show($"¿Eliminar la asignación:\n{resumen}?",
            "Confirmar eliminación", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirmar != MessageBoxResult.Yes) return;

        try
        {
            var res = await ApiService.DeleteAsync($"docenteclases/{id}");
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
        var border = new Border
        {
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(16, 12, 16, 12),
            Margin = new Thickness(6, 5, 6, 5),
            Background = (Brush)new BrushConverter().ConvertFrom("#1A2142")!
        };
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var numBlock = new TextBlock
        {
            Text = num.ToString(),
            FontWeight = FontWeights.Bold,
            Foreground = (Brush)new BrushConverter().ConvertFrom("#00FF87")!,
            VerticalAlignment = VerticalAlignment.Center
        };
        var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
        info.Children.Add(new TextBlock
        {
            Text = docente,
            Foreground = Brushes.White,
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap
        });
        info.Children.Add(new TextBlock
        {
            Text = $"{materia}  ·  Sección {seccion}  ·  {periodo}",
            Foreground = (Brush)new BrushConverter().ConvertFrom("#8891B0")!,
            FontSize = 12,
            Margin = new Thickness(0, 2, 0, 0),
            TextWrapping = TextWrapping.Wrap
        });

        var resumen = $"{docente} — {materia} / Sección {seccion} / {periodo}";
        var btnEliminar = new Button
        {
            Content = "🗑 Eliminar",
            Style = (Style)FindResource("BtnAccion"),
            Background = (Brush)new BrushConverter().ConvertFrom("#FF3B5C")!,
            VerticalAlignment = VerticalAlignment.Center
        };
        btnEliminar.Click += (_, _) => EliminarAsignacion(id, resumen);

        Grid.SetColumn(numBlock, 0);
        Grid.SetColumn(info, 1);
        Grid.SetColumn(btnEliminar, 2);
        grid.Children.Add(numBlock);
        grid.Children.Add(info);
        grid.Children.Add(btnEliminar);
        border.Child = grid;
        return border;
    }

    // ===== ACTIVIDADES (DOCENTE) =====

    // Llena el combo de materias y carga la lista de actividades del docente.
    private async System.Threading.Tasks.Task CargarModuloActividades()
    {
        await LlenarComboAsync(cmbActMateria, "materias", it => it["nombre"]?.ToString());
        // Al tener materia seleccionada, ActMateria_Changed carga sus temas.
        await RecargarActividades();
    }

    // Cuando cambia la materia elegida, recargamos los temas de esa materia en el combo.
    private async void ActMateria_Changed(object sender, SelectionChangedEventArgs e)
    {
        int materiaId = TagCombo(cmbActMateria);
        if (materiaId <= 0) { cmbActTema.Items.Clear(); return; }
        await LlenarComboAsync(cmbActTema, $"temas?materiaId={materiaId}", it => it["nombre"]?.ToString());
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
        await CargarMapaSecciones();
        await LlenarComboAsync(cmbPubSeccion, "secciones", it =>
        {
            var s = it["nombre"]?.ToString() ?? "";
            var g = it["grado"]?.ToString();
            return string.IsNullOrWhiteSpace(g) ? s : $"{s} · {g}";
        });
        await LlenarComboAsync(cmbPubPeriodo, "periodosacademicos", it => it["nombre"]?.ToString());

        await RecargarBancoPreguntas();
        await RecargarPubSecciones();
    }

    private void MostrarEstadoActividad(string estado)
    {
        lblEstadoActual.Text = string.IsNullOrWhiteSpace(estado) ? "—" : estado;
        var (fondo, texto) = ColorEstado(estado);
        brdEstadoActual.Background = (Brush)new BrushConverter().ConvertFrom(fondo)!;
        lblEstadoActual.Foreground = (Brush)new BrushConverter().ConvertFrom(texto)!;
    }

    // Carga (una vez por selección) el mapa seccionId -> nombre para las secciones publicadas.
    private async System.Threading.Tasks.Task CargarMapaSecciones()
    {
        try
        {
            var res = await ApiService.GetResultAsync("secciones");
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
        var border = new Border
        {
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 9, 12, 9),
            Margin = new Thickness(4, 4, 4, 4),
            Background = (Brush)new BrushConverter().ConvertFrom("#1A2142")!
        };
        var sp = new StackPanel();
        sp.Children.Add(new TextBlock
        {
            Text = num + ".  " + (activa ? seccion : $"{seccion}  ·  INACTIVA"),
            Foreground = activa ? Brushes.White : (Brush)new BrushConverter().ConvertFrom("#FF7A90")!,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap
        });
        sp.Children.Add(new TextBlock
        {
            Text = $"{apertura}  →  {cierre}   ·   máx. intentos: {intentos}",
            Foreground = (Brush)new BrushConverter().ConvertFrom("#8891B0")!,
            FontSize = 11,
            Margin = new Thickness(0, 2, 0, 0),
            TextWrapping = TextWrapping.Wrap
        });
        border.Child = sp;
        return border;
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
        var border = new Border
        {
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(16, 12, 16, 12),
            Margin = new Thickness(6, 5, 6, 5),
            Background = (Brush)new BrushConverter().ConvertFrom("#1A2142")!
        };
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var numBlock = new TextBlock
        {
            Text = num.ToString(),
            FontWeight = FontWeights.Bold,
            Foreground = (Brush)new BrushConverter().ConvertFrom("#00FF87")!,
            VerticalAlignment = VerticalAlignment.Center
        };
        var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
        var linea = new StackPanel { Orientation = Orientation.Horizontal };
        linea.Children.Add(new TextBlock
        {
            Text = titulo,
            Foreground = Brushes.White,
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap
        });
        var (fondo, textoColor) = ColorEstado(estado);
        linea.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(6),
            Background = (Brush)new BrushConverter().ConvertFrom(fondo)!,
            Padding = new Thickness(8, 2, 8, 2),
            Margin = new Thickness(10, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(estado) ? "—" : estado,
                Foreground = (Brush)new BrushConverter().ConvertFrom(textoColor)!,
                FontSize = 10,
                FontWeight = FontWeights.Bold
            }
        });
        info.Children.Add(linea);
        info.Children.Add(new TextBlock
        {
            Text = $"Tema: {tema}  ·  {cantPreguntas} pregunta(s)",
            Foreground = (Brush)new BrushConverter().ConvertFrom("#8891B0")!,
            FontSize = 12,
            Margin = new Thickness(0, 3, 0, 0),
            TextWrapping = TextWrapping.Wrap
        });

        var btnAsignar = new Button
        {
            Content = "＋ Preguntas",
            Style = (Style)FindResource("BtnAccion"),
            MinWidth = 110,
            Background = (Brush)new BrushConverter().ConvertFrom("#963CBD")!,
            VerticalAlignment = VerticalAlignment.Center
        };
        btnAsignar.Click += (_, _) => SeleccionarActividad(id, titulo, temaId, estado);

        Grid.SetColumn(numBlock, 0);
        Grid.SetColumn(info, 1);
        Grid.SetColumn(btnAsignar, 2);
        grid.Children.Add(numBlock);
        grid.Children.Add(info);
        grid.Children.Add(btnAsignar);
        border.Child = grid;
        return border;
    }

    private Border CrearFilaBancoPregunta(int num, int preguntaId, string enunciado, string estado)
    {
        var border = new Border
        {
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 9, 12, 9),
            Margin = new Thickness(4, 4, 4, 4),
            Background = (Brush)new BrushConverter().ConvertFrom("#1A2142")!
        };
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(26) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var numBlock = new TextBlock
        {
            Text = num.ToString(),
            FontWeight = FontWeights.Bold,
            Foreground = (Brush)new BrushConverter().ConvertFrom("#00FF87")!,
            VerticalAlignment = VerticalAlignment.Center
        };
        var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
        info.Children.Add(new TextBlock
        {
            Text = enunciado,
            Foreground = Brushes.White,
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap
        });
        if (!string.IsNullOrWhiteSpace(estado))
            info.Children.Add(new TextBlock
            {
                Text = estado,
                Foreground = (Brush)new BrushConverter().ConvertFrom("#8891B0")!,
                FontSize = 11,
                Margin = new Thickness(0, 2, 0, 0)
            });

        var btnAgregar = new Button
        {
            Content = "＋ Agregar",
            Style = (Style)FindResource("BtnAccion"),
            MinWidth = 96,
            Background = (Brush)new BrushConverter().ConvertFrom("#1E7A4D")!,
            VerticalAlignment = VerticalAlignment.Center
        };
        btnAgregar.Click += (_, _) => AsignarPreguntaActividad(preguntaId);

        Grid.SetColumn(numBlock, 0);
        Grid.SetColumn(info, 1);
        Grid.SetColumn(btnAgregar, 2);
        grid.Children.Add(numBlock);
        grid.Children.Add(info);
        grid.Children.Add(btnAgregar);
        border.Child = grid;
        return border;
    }

    // ===== GRADOS =====

    private async System.Threading.Tasks.Task RecargarGrados()
    {
        try
        {
            var res = await ApiService.GetResultAsync("grados");

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
                var orden = item["orden"]?.ToString() ?? "";
                bool activo = item["activo"]?.Value<bool>() ?? true;
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
        // Orden: número; si está vacío o no es válido, usamos 1.
        if (!int.TryParse(txtOrdenGrado.Text.Trim(), out var orden)) orden = 1;

        btnCrearGrado.IsEnabled = false;
        try
        {
            var res = await ApiService.PostAsync("grados", new { nombre = nombre, orden = orden, activo = true });

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

    private async void EliminarGrado(int id, string nombre)
    {
        var confirmar = MessageBox.Show(
            $"¿Eliminar el grado \"{nombre}\"?",
            "Confirmar eliminación", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirmar != MessageBoxResult.Yes) return;

        try
        {
            var res = await ApiService.DeleteAsync($"grados/{id}");

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
        lblSeccionesTitulo.Text = $"CREAR SECCIÓN EN {nombre.ToUpper()}";
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
            var res = await ApiService.GetResultAsync("secciones");

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
            var res = await ApiService.PostAsync("secciones",
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
        var confirmar = MessageBox.Show(
            $"¿Eliminar la sección \"{nombre}\"?",
            "Confirmar eliminación", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirmar != MessageBoxResult.Yes) return;

        try
        {
            var res = await ApiService.DeleteAsync($"secciones/{id}");

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

    // Fila de grado: nombre + orden, botón «Secciones» (elige) y «Eliminar».
    private Border CrearFilaGrado(int num, int id, string nombre, string orden, bool activo)
    {
        var border = new Border
        {
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(16, 12, 16, 12),
            Margin = new Thickness(6, 5, 6, 5),
            Background = (Brush)new BrushConverter().ConvertFrom("#1A2142")!
        };
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var numBlock = new TextBlock
        {
            Text = num.ToString(),
            FontWeight = FontWeights.Bold,
            Foreground = (Brush)new BrushConverter().ConvertFrom("#00FF87")!,
            VerticalAlignment = VerticalAlignment.Center
        };
        var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        info.Children.Add(new TextBlock
        {
            Text = nombre,
            Foreground = Brushes.White,
            FontSize = 14,
            TextWrapping = TextWrapping.Wrap
        });
        var sub = $"orden {orden}";
        if (!activo) sub += "  ·  INACTIVO";
        info.Children.Add(new TextBlock
        {
            Text = sub,
            Foreground = (Brush)new BrushConverter().ConvertFrom(activo ? "#8891B0" : "#FF7A90")!,
            FontSize = 12,
            Margin = new Thickness(0, 2, 12, 0)
        });

        var acciones = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center
        };
        var btnSecciones = new Button
        {
            Content = "◉ Secciones",
            Style = (Style)FindResource("BtnAccion"),
            Background = (Brush)new BrushConverter().ConvertFrom("#963CBD")!
        };
        btnSecciones.Click += (_, _) => SeleccionarGrado(id, nombre);
        var btnEditar = new Button
        {
            Content = "✎ Editar",
            Style = (Style)FindResource("BtnAccion"),
            Background = (Brush)new BrushConverter().ConvertFrom("#4A5578")!
        };
        btnEditar.Click += (_, _) => EditarGrado(id, nombre, orden, activo);
        var btnEliminar = new Button
        {
            Content = "🗑 Eliminar",
            Style = (Style)FindResource("BtnAccion"),
            Background = (Brush)new BrushConverter().ConvertFrom("#FF3B5C")!
        };
        btnEliminar.Click += (_, _) => EliminarGrado(id, nombre);
        acciones.Children.Add(btnSecciones);
        acciones.Children.Add(btnEditar);
        acciones.Children.Add(btnEliminar);

        Grid.SetColumn(numBlock, 0);
        Grid.SetColumn(info, 1);
        Grid.SetColumn(acciones, 2);
        grid.Children.Add(numBlock);
        grid.Children.Add(info);
        grid.Children.Add(acciones);
        border.Child = grid;
        return border;
    }

    // Fila de sección: nombre + botón «Eliminar».
    private Border CrearFilaSeccion(int num, int id, string nombre, string grado, bool activa)
    {
        var border = new Border
        {
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(16, 10, 16, 10),
            Margin = new Thickness(6, 5, 6, 5),
            Background = (Brush)new BrushConverter().ConvertFrom("#1A2142")!
        };
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(32) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var numBlock = new TextBlock
        {
            Text = num.ToString(),
            FontWeight = FontWeights.Bold,
            Foreground = (Brush)new BrushConverter().ConvertFrom("#00FF87")!,
            VerticalAlignment = VerticalAlignment.Center
        };
        // Nombre de la sección + a qué grado pertenece.
        var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
        info.Children.Add(new TextBlock
        {
            Text = activa ? nombre : $"{nombre}  ·  INACTIVA",
            Foreground = activa ? Brushes.White : (Brush)new BrushConverter().ConvertFrom("#FF7A90")!,
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap
        });
        info.Children.Add(new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(grado) ? "" : $"Grado: {grado}",
            Foreground = (Brush)new BrushConverter().ConvertFrom("#8891B0")!,
            FontSize = 11,
            Margin = new Thickness(0, 2, 0, 0)
        });
        var textoBlock = info;
        var acciones = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center
        };
        var btnEditar = new Button
        {
            Content = "✎ Editar",
            Style = (Style)FindResource("BtnAccion"),
            Background = (Brush)new BrushConverter().ConvertFrom("#963CBD")!
        };
        btnEditar.Click += (_, _) => EditarSeccion(id, nombre, activa);
        var btnEliminar = new Button
        {
            Content = "🗑 Eliminar",
            Style = (Style)FindResource("BtnAccion"),
            Background = (Brush)new BrushConverter().ConvertFrom("#FF3B5C")!
        };
        btnEliminar.Click += (_, _) => EliminarSeccion(id, nombre);
        acciones.Children.Add(btnEditar);
        acciones.Children.Add(btnEliminar);

        Grid.SetColumn(numBlock, 0);
        Grid.SetColumn(textoBlock, 1);
        Grid.SetColumn(acciones, 2);
        grid.Children.Add(numBlock);
        grid.Children.Add(textoBlock);
        grid.Children.Add(acciones);
        border.Child = grid;
        return border;
    }

    // Editar grado: PUT /api/grados/{id} con { nombre, orden, activo }.
    private async void EditarGrado(int id, string nombreActual, string ordenActual, bool activoActual)
    {
        var datos = DialogoCampos("Editar grado",
            new[] { ("Nombre", nombreActual), ("Orden", ordenActual) }, "Activo", activoActual);
        if (datos is null) return;

        var (valores, activo) = datos.Value;
        var nombre = valores[0];
        if (string.IsNullOrWhiteSpace(nombre))
        {
            lblGradosEstado.Text = "✗ El nombre del grado no puede quedar vacío.";
            return;
        }
        if (!int.TryParse(valores[1], out var orden)) orden = 1;

        try
        {
            var res = await ApiService.PutAsync($"grados/{id}", new { nombre = nombre, orden = orden, activo = activo });
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

    // Editar sección: PUT /api/secciones/{id} con { gradoId, nombre, activa }.
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
            var res = await ApiService.PutAsync($"secciones/{id}",
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
            var res = await ApiService.GetResultAsync("periodosacademicos");

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
                var fInicio = item["fechaInicio"]?.ToString() ?? "";
                var fFin = item["fechaFin"]?.ToString() ?? "";
                bool activo = item["activo"]?.Value<bool>() ?? true;
                // Las fechas vienen como "yyyy-MM-ddT..."; recortamos a la parte de fecha.
                fInicio = RecortarFecha(fInicio);
                fFin = RecortarFecha(fFin);
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

    // Valida "yyyy-MM-dd" y lo normaliza; devuelve null si no es una fecha válida.
    private static string? NormalizarFecha(string valor)
    {
        if (DateOnly.TryParse(valor.Trim(), System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var fecha))
            return fecha.ToString("yyyy-MM-dd");
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
        if (fInicio is null || fFin is null)
        {
            lblPeriodosEstado.Text = "✗ Fechas inválidas. Usa el formato yyyy-MM-dd.";
            return;
        }

        btnCrearPeriodo.IsEnabled = false;
        try
        {
            var res = await ApiService.PostAsync("periodosacademicos", new
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

    // Editar período: PUT /api/periodosacademicos/{id} con { nombre, fechaInicio, fechaFin, activo }.
    private async void EditarPeriodo(int id, string nombreActual, string fInicioActual, string fFinActual, bool activoActual)
    {
        var datos = DialogoCampos("Editar período",
            new[]
            {
                ("Nombre", nombreActual),
                ("Fecha inicio (yyyy-MM-dd)", fInicioActual),
                ("Fecha fin (yyyy-MM-dd)", fFinActual)
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
        if (fInicio is null || fFin is null)
        {
            lblPeriodosEstado.Text = "✗ Fechas inválidas. Usa el formato yyyy-MM-dd.";
            return;
        }

        try
        {
            var res = await ApiService.PutAsync($"periodosacademicos/{id}", new
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
        var confirmar = MessageBox.Show(
            $"¿Eliminar el período \"{nombre}\"?",
            "Confirmar eliminación", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirmar != MessageBoxResult.Yes) return;

        try
        {
            var res = await ApiService.DeleteAsync($"periodosacademicos/{id}");
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
        var border = new Border
        {
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(16, 12, 16, 12),
            Margin = new Thickness(6, 5, 6, 5),
            Background = (Brush)new BrushConverter().ConvertFrom("#1A2142")!
        };
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var numBlock = new TextBlock
        {
            Text = num.ToString(),
            FontWeight = FontWeights.Bold,
            Foreground = (Brush)new BrushConverter().ConvertFrom("#00FF87")!,
            VerticalAlignment = VerticalAlignment.Center
        };
        var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        info.Children.Add(new TextBlock
        {
            Text = nombre,
            Foreground = Brushes.White,
            FontSize = 14,
            TextWrapping = TextWrapping.Wrap
        });
        var sub = $"{fInicio}  →  {fFin}";
        if (!activo) sub += "  ·  INACTIVO";
        info.Children.Add(new TextBlock
        {
            Text = sub,
            Foreground = (Brush)new BrushConverter().ConvertFrom(activo ? "#8891B0" : "#FF7A90")!,
            FontSize = 12,
            Margin = new Thickness(0, 2, 12, 0)
        });

        var acciones = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center
        };
        var btnEditar = new Button
        {
            Content = "✎ Editar",
            Style = (Style)FindResource("BtnAccion"),
            Background = (Brush)new BrushConverter().ConvertFrom("#963CBD")!
        };
        btnEditar.Click += (_, _) => EditarPeriodo(id, nombre, fInicio, fFin, activo);
        var btnEliminar = new Button
        {
            Content = "🗑 Eliminar",
            Style = (Style)FindResource("BtnAccion"),
            Background = (Brush)new BrushConverter().ConvertFrom("#FF3B5C")!
        };
        btnEliminar.Click += (_, _) => EliminarPeriodo(id, nombre);
        acciones.Children.Add(btnEditar);
        acciones.Children.Add(btnEliminar);

        Grid.SetColumn(numBlock, 0);
        Grid.SetColumn(info, 1);
        Grid.SetColumn(acciones, 2);
        grid.Children.Add(numBlock);
        grid.Children.Add(info);
        grid.Children.Add(acciones);
        border.Child = grid;
        return border;
    }

    // Diálogo genérico oscuro: N campos de texto + un check. Devuelve (valores, activo) o null.
    private (string[] valores, bool activo)? DialogoCampos(
        string titulo, (string etiqueta, string valor)[] campos, string activoLabel, bool activoInicial)
    {
        (string[], bool)? resultado = null;

        var dlg = new Window
        {
            Title = titulo,
            Width = 460,
            Height = 180 + campos.Length * 74,
            Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ResizeMode = ResizeMode.NoResize
        };

        var marco = new Border
        {
            CornerRadius = new CornerRadius(16),
            Background = (Brush)new BrushConverter().ConvertFrom("#0A0E27")!,
            Padding = new Thickness(22)
        };
        var cont = new StackPanel();
        cont.Children.Add(new TextBlock
        {
            Text = titulo.ToUpper(),
            FontSize = 11,
            FontWeight = FontWeights.Bold,
            Foreground = (Brush)new BrushConverter().ConvertFrom("#00FF87")!,
            Margin = new Thickness(0, 0, 0, 14)
        });

        var cajas = new List<TextBox>();
        foreach (var (etiqueta, valor) in campos)
        {
            cont.Children.Add(new TextBlock
            {
                Text = etiqueta,
                FontSize = 11,
                Foreground = (Brush)new BrushConverter().ConvertFrom("#8891B0")!,
                Margin = new Thickness(0, 0, 0, 4)
            });
            var borde = new Border
            {
                Background = (Brush)new BrushConverter().ConvertFrom("#0E1330")!,
                BorderBrush = (Brush)new BrushConverter().ConvertFrom("#3D2A54")!,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(12, 0, 12, 0),
                Margin = new Thickness(0, 0, 0, 12)
            };
            var caja = new TextBox
            {
                Text = valor,
                Height = 40,
                VerticalContentAlignment = VerticalAlignment.Center,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Foreground = Brushes.White,
                CaretBrush = (Brush)new BrushConverter().ConvertFrom("#00FF87")!,
                FontSize = 14
            };
            borde.Child = caja;
            cont.Children.Add(borde);
            cajas.Add(caja);
        }

        var chk = new CheckBox
        {
            Content = activoLabel,
            IsChecked = activoInicial,
            Foreground = Brushes.White,
            FontSize = 13,
            Margin = new Thickness(2, 2, 0, 0)
        };
        cont.Children.Add(chk);

        var fila = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 20, 0, 0)
        };
        var btnCancelar = new Button
        {
            Content = "Cancelar",
            Style = (Style)FindResource("BtnAccion"),
            Height = 38,
            MinWidth = 100,
            Background = (Brush)new BrushConverter().ConvertFrom("#2A3358")!
        };
        btnCancelar.Click += (_, _) => { dlg.DialogResult = false; };
        var btnGuardar = new Button
        {
            Content = "Guardar",
            Style = (Style)FindResource("BtnAccion"),
            Height = 38,
            MinWidth = 100,
            Foreground = (Brush)new BrushConverter().ConvertFrom("#0A0E27")!,
            Background = (Brush)new BrushConverter().ConvertFrom("#00FF87")!
        };
        btnGuardar.Click += (_, _) =>
        {
            resultado = (cajas.Select(c => c.Text.Trim()).ToArray(), chk.IsChecked == true);
            dlg.DialogResult = true;
        };
        fila.Children.Add(btnCancelar);
        fila.Children.Add(btnGuardar);
        cont.Children.Add(fila);

        marco.Child = cont;
        dlg.Content = marco;
        dlg.KeyDown += (_, e) => { if (e.Key == Key.Escape) dlg.DialogResult = false; };
        if (cajas.Count > 0) { cajas[0].Focus(); cajas[0].SelectAll(); }

        var ok = dlg.ShowDialog();
        return ok == true ? resultado : null;
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
            // Crear materias es acción de ADMIN (POST api/materias).
            var res = await ApiService.PostAsync("materias", new { nombre = nombre, activa = true });

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

    // Crea un usuario (POST /api/usuarios) y refresca la lista de usuarios.
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
            var res = await ApiService.PostAsync("usuarios", new
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
        var border = new Border
        {
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(16, 12, 16, 12),
            Margin = new Thickness(6, 5, 6, 5),
            Background = (Brush)new BrushConverter().ConvertFrom("#1A2142")!
        };
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var numBlock = new TextBlock
        {
            Text = num.ToString(),
            FontWeight = FontWeights.Bold,
            Foreground = (Brush)new BrushConverter().ConvertFrom("#00FF87")!,
            VerticalAlignment = VerticalAlignment.Center
        };

        // Nombre + etiqueta de estado (verde activo / gris inactivo), y debajo correo · rol.
        var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var lineaNombre = new StackPanel { Orientation = Orientation.Horizontal };
        lineaNombre.Children.Add(new TextBlock
        {
            Text = nombreCompleto,
            Foreground = Brushes.White,
            FontSize = 14,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center
        });
        lineaNombre.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(6),
            Background = (Brush)new BrushConverter().ConvertFrom(activo ? "#134E2A" : "#2A3358")!,
            Padding = new Thickness(8, 2, 8, 2),
            Margin = new Thickness(10, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = activo ? "ACTIVO" : "INACTIVO",
                Foreground = (Brush)new BrushConverter().ConvertFrom(activo ? "#00FF87" : "#8891B0")!,
                FontSize = 10,
                FontWeight = FontWeights.Bold
            }
        });
        info.Children.Add(lineaNombre);
        var sub = string.IsNullOrWhiteSpace(correo) ? rol : $"{correo}  ·  {rol}";
        info.Children.Add(new TextBlock
        {
            Text = sub,
            Foreground = (Brush)new BrushConverter().ConvertFrom("#8891B0")!,
            FontSize = 12,
            Margin = new Thickness(0, 3, 12, 0),
            TextWrapping = TextWrapping.Wrap
        });

        var acciones = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center
        };
        var btnEditar = new Button
        {
            Content = "✎ Editar",
            Style = (Style)FindResource("BtnAccion"),
            Background = (Brush)new BrushConverter().ConvertFrom("#963CBD")!
        };
        btnEditar.Click += (_, _) => EditarUsuario(id, nombreCompleto, correo, rolId, activo);
        var btnEliminar = new Button
        {
            Content = "🗑 Eliminar",
            Style = (Style)FindResource("BtnAccion"),
            Background = (Brush)new BrushConverter().ConvertFrom("#FF3B5C")!
        };
        btnEliminar.Click += (_, _) => EliminarUsuario(id, nombreCompleto);
        acciones.Children.Add(btnEditar);
        acciones.Children.Add(btnEliminar);

        Grid.SetColumn(numBlock, 0);
        Grid.SetColumn(info, 1);
        Grid.SetColumn(acciones, 2);
        grid.Children.Add(numBlock);
        grid.Children.Add(info);
        grid.Children.Add(acciones);
        border.Child = grid;
        return border;
    }

    // Editar usuario: PUT /api/usuarios/{id} con { nombreCompleto, correoOUsuario, rolId, activo }.
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
            var res = await ApiService.PutAsync($"usuarios/{id}", new
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

    // Eliminar usuario: DELETE /api/usuarios/{id} (con confirmación).
    private async void EliminarUsuario(int id, string nombre)
    {
        var confirmar = MessageBox.Show(
            $"¿Eliminar al usuario \"{nombre}\"?",
            "Confirmar eliminación",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (confirmar != MessageBoxResult.Yes) return;

        try
        {
            var res = await ApiService.DeleteAsync($"usuarios/{id}");

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

    // Diálogo oscuro para editar un usuario (nombre, correo, rol, activo). No pide contraseña.
    // Devuelve (nombre, correo, rolId, activo) o null si se cancela.
    private (string nombre, string correo, int rolId, bool activo)? PedirDatosUsuario(
        string nombreActual, string correoActual, int rolIdActual, bool activoActual)
    {
        (string, string, int, bool)? resultado = null;

        var dlg = new Window
        {
            Title = "Editar usuario",
            Width = 460,
            Height = 420,
            Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ResizeMode = ResizeMode.NoResize
        };

        var marco = new Border
        {
            CornerRadius = new CornerRadius(16),
            Background = (Brush)new BrushConverter().ConvertFrom("#0A0E27")!,
            Padding = new Thickness(22)
        };
        var cont = new StackPanel();
        cont.Children.Add(new TextBlock
        {
            Text = "EDITAR USUARIO",
            FontSize = 11,
            FontWeight = FontWeights.Bold,
            Foreground = (Brush)new BrushConverter().ConvertFrom("#00FF87")!,
            Margin = new Thickness(0, 0, 0, 14)
        });

        // Helper local para crear un campo de texto con etiqueta.
        TextBox CampoTexto(string etiqueta, string valor)
        {
            cont.Children.Add(new TextBlock
            {
                Text = etiqueta,
                FontSize = 11,
                Foreground = (Brush)new BrushConverter().ConvertFrom("#8891B0")!,
                Margin = new Thickness(0, 0, 0, 4)
            });
            var borde = new Border
            {
                Background = (Brush)new BrushConverter().ConvertFrom("#0E1330")!,
                BorderBrush = (Brush)new BrushConverter().ConvertFrom("#3D2A54")!,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(12, 0, 12, 0),
                Margin = new Thickness(0, 0, 0, 12)
            };
            var caja = new TextBox
            {
                Text = valor,
                Height = 40,
                VerticalContentAlignment = VerticalAlignment.Center,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Foreground = Brushes.White,
                CaretBrush = (Brush)new BrushConverter().ConvertFrom("#00FF87")!,
                FontSize = 14
            };
            borde.Child = caja;
            cont.Children.Add(borde);
            return caja;
        }

        var cajaNombre = CampoTexto("Nombre completo", nombreActual);
        var cajaCorreo = CampoTexto("Usuario o correo", correoActual);

        // Rol
        cont.Children.Add(new TextBlock
        {
            Text = "Rol",
            FontSize = 11,
            Foreground = (Brush)new BrushConverter().ConvertFrom("#8891B0")!,
            Margin = new Thickness(0, 0, 0, 4)
        });
        var combo = new ComboBox
        {
            Style = (Style)FindResource("ComboOscuro"),
            ItemContainerStyle = (Style)FindResource("ComboItemOscuro"),
            Margin = new Thickness(0, 0, 0, 12)
        };
        combo.Items.Add(new ComboBoxItem { Content = "ADMIN" });
        combo.Items.Add(new ComboBoxItem { Content = "DOCENTE" });
        combo.Items.Add(new ComboBoxItem { Content = "ESTUDIANTE" });
        // rolId 1..3 => índice 0..2; si viene fuera de rango, ESTUDIANTE por defecto.
        combo.SelectedIndex = (rolIdActual >= 1 && rolIdActual <= 3) ? rolIdActual - 1 : 2;

        var chkActivo = new CheckBox
        {
            Content = "Usuario activo",
            IsChecked = activoActual,
            Foreground = Brushes.White,
            FontSize = 13,
            Margin = new Thickness(2, 4, 0, 0)
        };
        cont.Children.Add(combo);
        cont.Children.Add(chkActivo);

        var fila = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 20, 0, 0)
        };
        var btnCancelar = new Button
        {
            Content = "Cancelar",
            Style = (Style)FindResource("BtnAccion"),
            Height = 38,
            MinWidth = 100,
            Background = (Brush)new BrushConverter().ConvertFrom("#2A3358")!
        };
        btnCancelar.Click += (_, _) => { dlg.DialogResult = false; };
        var btnGuardar = new Button
        {
            Content = "Guardar",
            Style = (Style)FindResource("BtnAccion"),
            Height = 38,
            MinWidth = 100,
            Foreground = (Brush)new BrushConverter().ConvertFrom("#0A0E27")!,
            Background = (Brush)new BrushConverter().ConvertFrom("#00FF87")!
        };
        btnGuardar.Click += (_, _) =>
        {
            int rolId = combo.SelectedIndex + 1; // 0..2 => 1..3
            resultado = (cajaNombre.Text.Trim(), cajaCorreo.Text.Trim(), rolId, chkActivo.IsChecked == true);
            dlg.DialogResult = true;
        };
        fila.Children.Add(btnCancelar);
        fila.Children.Add(btnGuardar);
        cont.Children.Add(fila);

        marco.Child = cont;
        dlg.Content = marco;

        dlg.KeyDown += (_, e) => { if (e.Key == Key.Escape) dlg.DialogResult = false; };
        cajaNombre.Focus();
        cajaNombre.SelectAll();

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
        var border = new Border
        {
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 9, 12, 9),
            Margin = new Thickness(4, 4, 4, 4),
            Cursor = Cursors.Hand,
            Background = (Brush)new BrushConverter().ConvertFrom("#1A2142")!
        };
        border.MouseEnter += (_, _) => border.Background = (Brush)new BrushConverter().ConvertFrom("#26305A")!;
        border.MouseLeave += (_, _) => border.Background = (Brush)new BrushConverter().ConvertFrom("#1A2142")!;
        // Seleccionar el tema, salvo que el clic venga de un botón de acción de la fila.
        border.MouseLeftButtonUp += (_, e) => { if (!ClickVieneDeBoton(e.OriginalSource)) onClick(temaId, nombre); };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var numBlock = new TextBlock
        {
            Text = num.ToString(),
            FontWeight = FontWeights.Bold,
            Foreground = (Brush)new BrushConverter().ConvertFrom("#00FF87")!,
            VerticalAlignment = VerticalAlignment.Center
        };
        var textoBlock = new TextBlock
        {
            Text = activo ? nombre : $"{nombre}  ·  INACTIVO",
            Foreground = activo ? Brushes.White : (Brush)new BrushConverter().ConvertFrom("#FF7A90")!,
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0)
        };

        var acciones = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center
        };
        var btnEditar = new Button
        {
            Content = "✎",
            Style = (Style)FindResource("BtnAccion"),
            MinWidth = 40,
            Background = (Brush)new BrushConverter().ConvertFrom("#963CBD")!
        };
        btnEditar.Click += (_, _) => EditarTema(temaId, nombre, orden, activo);
        var btnEliminar = new Button
        {
            Content = "🗑",
            Style = (Style)FindResource("BtnAccion"),
            MinWidth = 40,
            Background = (Brush)new BrushConverter().ConvertFrom("#FF3B5C")!
        };
        btnEliminar.Click += (_, _) => EliminarTema(temaId, nombre);
        acciones.Children.Add(btnEditar);
        acciones.Children.Add(btnEliminar);

        Grid.SetColumn(numBlock, 0);
        Grid.SetColumn(textoBlock, 1);
        Grid.SetColumn(acciones, 2);
        grid.Children.Add(numBlock);
        grid.Children.Add(textoBlock);
        grid.Children.Add(acciones);
        border.Child = grid;
        return border;
    }

    // ¿El origen del clic es un Button (o algo dentro de un Button)? Evita seleccionar la fila al pulsar acciones.
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
        var confirmar = MessageBox.Show($"¿Eliminar el tema \"{nombre}\"?",
            "Confirmar eliminación", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirmar != MessageBoxResult.Yes) return;

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
        var confirmar = MessageBox.Show($"¿Eliminar el subtema \"{nombre}\"?",
            "Confirmar eliminación", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirmar != MessageBoxResult.Yes) return;
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
        var border = new Border
        {
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 8, 12, 8),
            Margin = new Thickness(4, 4, 4, 4),
            Background = (Brush)new BrushConverter().ConvertFrom("#1A2142")!
        };
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(26) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var numBlock = new TextBlock
        {
            Text = num.ToString(),
            FontWeight = FontWeights.Bold,
            Foreground = (Brush)new BrushConverter().ConvertFrom("#00FF87")!,
            VerticalAlignment = VerticalAlignment.Center
        };
        var textoBlock = new TextBlock
        {
            Text = activo ? nombre : $"{nombre}  ·  INACTIVO",
            Foreground = activo ? Brushes.White : (Brush)new BrushConverter().ConvertFrom("#FF7A90")!,
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0)
        };
        var acciones = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var btnEditar = new Button { Content = "✎", Style = (Style)FindResource("BtnAccion"), MinWidth = 40, Background = (Brush)new BrushConverter().ConvertFrom("#963CBD")! };
        btnEditar.Click += (_, _) => EditarSubtema(id, nombre, orden, activo);
        var btnEliminar = new Button { Content = "🗑", Style = (Style)FindResource("BtnAccion"), MinWidth = 40, Background = (Brush)new BrushConverter().ConvertFrom("#FF3B5C")! };
        btnEliminar.Click += (_, _) => EliminarSubtema(id, nombre);
        acciones.Children.Add(btnEditar);
        acciones.Children.Add(btnEliminar);

        Grid.SetColumn(numBlock, 0);
        Grid.SetColumn(textoBlock, 1);
        Grid.SetColumn(acciones, 2);
        grid.Children.Add(numBlock);
        grid.Children.Add(textoBlock);
        grid.Children.Add(acciones);
        border.Child = grid;
        return border;
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
        var confirmar = MessageBox.Show($"¿Eliminar la pregunta:\n\"{recorte}\"?",
            "Confirmar eliminación", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirmar != MessageBoxResult.Yes) return;
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
    private static (string fondo, string texto) ColorEstado(string estado)
    {
        var e = estado.ToLowerInvariant();
        if (e.Contains("aprob") || e.Contains("public")) return ("#134E2A", "#00FF87"); // verde
        if (e.Contains("rechaz")) return ("#4E1320", "#FF7A90");                          // rojo
        if (e.Contains("revis")) return ("#4A3A12", "#FFC24D");                           // ámbar
        return ("#2A3358", "#8891B0");                                                    // gris (borrador/otros)
    }

    private Border CrearFilaPregunta(int num, int id, string enunciado, string estado)
    {
        var border = new Border
        {
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 9, 12, 9),
            Margin = new Thickness(4, 4, 4, 4),
            Background = (Brush)new BrushConverter().ConvertFrom("#1A2142")!
        };
        var contenido = new StackPanel();

        // Línea 1: número + enunciado
        var fila1 = new Grid();
        fila1.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(26) });
        fila1.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var numBlock = new TextBlock
        {
            Text = num.ToString(),
            FontWeight = FontWeights.Bold,
            Foreground = (Brush)new BrushConverter().ConvertFrom("#00FF87")!,
            VerticalAlignment = VerticalAlignment.Top
        };
        var textoBlock = new TextBlock
        {
            Text = enunciado,
            Foreground = Brushes.White,
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap
        };
        Grid.SetColumn(numBlock, 0);
        Grid.SetColumn(textoBlock, 1);
        fila1.Children.Add(numBlock);
        fila1.Children.Add(textoBlock);
        contenido.Children.Add(fila1);

        // Línea 2: etiqueta de estado + botones
        var fila2 = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(26, 8, 0, 0)
        };
        var (fondo, textoColor) = ColorEstado(estado);
        var etiqueta = new Border
        {
            CornerRadius = new CornerRadius(6),
            Background = (Brush)new BrushConverter().ConvertFrom(fondo)!,
            Padding = new Thickness(8, 3, 8, 3),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
            Child = new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(estado) ? "—" : estado,
                Foreground = (Brush)new BrushConverter().ConvertFrom(textoColor)!,
                FontSize = 11,
                FontWeight = FontWeights.Bold
            }
        };
        fila2.Children.Add(etiqueta);

        var btnAprobar = new Button { Content = "✓ Aprobar", Style = (Style)FindResource("BtnAccion"), Background = (Brush)new BrushConverter().ConvertFrom("#1E7A4D")! };
        btnAprobar.Click += (_, _) => AprobarPregunta(id);
        var btnEditar = new Button { Content = "✎", Style = (Style)FindResource("BtnAccion"), MinWidth = 40, Background = (Brush)new BrushConverter().ConvertFrom("#963CBD")! };
        btnEditar.Click += (_, _) => EditarPregunta(id, enunciado);
        var btnEliminar = new Button { Content = "🗑", Style = (Style)FindResource("BtnAccion"), MinWidth = 40, Background = (Brush)new BrushConverter().ConvertFrom("#FF3B5C")! };
        btnEliminar.Click += (_, _) => EliminarPregunta(id, enunciado);
        fila2.Children.Add(btnAprobar);
        fila2.Children.Add(btnEditar);
        fila2.Children.Add(btnEliminar);
        contenido.Children.Add(fila2);

        border.Child = contenido;
        return border;
    }

    private Border CrearFila(int num, string texto, string extra, int id = 0, bool activa = true,
        bool conAcciones = false, Action<int, string>? onSeleccion = null)
    {
        var border = new Border
        {
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(16, 12, 16, 12),
            Margin = new Thickness(6, 5, 6, 5),
            Background = (Brush)new BrushConverter().ConvertFrom("#1A2142")!
        };

        // Fila clicable (p. ej. materias del DOCENTE): al pulsar, se selecciona.
        if (onSeleccion != null)
        {
            border.Cursor = Cursors.Hand;
            border.MouseEnter += (_, _) => border.Background = (Brush)new BrushConverter().ConvertFrom("#26305A")!;
            border.MouseLeave += (_, _) => border.Background = (Brush)new BrushConverter().ConvertFrom("#1A2142")!;
            border.MouseLeftButtonUp += (_, _) => onSeleccion(id, texto);
        }
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        // Columna extra solo cuando la fila lleva botones de acción.
        if (conAcciones)
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var numBlock = new TextBlock
        {
            Text = num.ToString(),
            FontWeight = FontWeights.Bold,
            Foreground = (Brush)new BrushConverter().ConvertFrom("#00FF87")!,
            VerticalAlignment = VerticalAlignment.Center
        };
        var textoBlock = new TextBlock
        {
            Text = texto,
            Foreground = Brushes.White,
            FontSize = 14,
            VerticalAlignment = VerticalAlignment.Center
        };
        var extraBlock = new TextBlock
        {
            Text = extra,
            Foreground = (Brush)new BrushConverter().ConvertFrom("#8891B0")!,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, conAcciones ? 12 : 0, 0)
        };
        Grid.SetColumn(numBlock, 0);
        Grid.SetColumn(textoBlock, 1);
        Grid.SetColumn(extraBlock, 2);
        grid.Children.Add(numBlock);
        grid.Children.Add(textoBlock);
        grid.Children.Add(extraBlock);

        if (conAcciones)
        {
            var acciones = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center
            };

            var btnEditar = new Button
            {
                Content = "✎ Editar",
                Style = (Style)FindResource("BtnAccion"),
                Background = (Brush)new BrushConverter().ConvertFrom("#963CBD")!
            };
            btnEditar.Click += (_, _) => EditarMateria(id, texto, activa);

            var btnEliminar = new Button
            {
                Content = "🗑 Eliminar",
                Style = (Style)FindResource("BtnAccion"),
                Background = (Brush)new BrushConverter().ConvertFrom("#FF3B5C")!
            };
            btnEliminar.Click += (_, _) => EliminarMateria(id, texto);

            acciones.Children.Add(btnEditar);
            acciones.Children.Add(btnEliminar);

            Grid.SetColumn(acciones, 3);
            grid.Children.Add(acciones);
        }

        border.Child = grid;
        return border;
    }

    // Editar materia: PUT /api/materias/{id} con { nombre, activa } y refresco.
    private async void EditarMateria(int id, string nombreActual, bool activa)
    {
        var nuevo = PedirNombre(nombreActual);
        if (nuevo is null) return;            // el usuario canceló

        nuevo = nuevo.Trim();
        if (string.IsNullOrWhiteSpace(nuevo))
        {
            lblMateriasEstado.Text = "✗ El nombre de la materia no puede quedar vacío.";
            return;
        }

        try
        {
            // Conservamos el estado 'activa' que ya tenía la materia.
            var res = await ApiService.PutAsync($"materias/{id}", new { nombre = nuevo, activa = activa });

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

    // Eliminar materia: DELETE /api/materias/{id} (con confirmación) y refresco.
    private async void EliminarMateria(int id, string nombre)
    {
        var confirmar = MessageBox.Show(
            $"¿Eliminar la materia \"{nombre}\"?",
            "Confirmar eliminación",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (confirmar != MessageBoxResult.Yes) return;

        try
        {
            var res = await ApiService.DeleteAsync($"materias/{id}");

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

    // Mini diálogo (estilo oscuro/morado/verde) para pedir el nuevo nombre.
    // Devuelve el texto escrito, o null si se cancela.
    private string? PedirNombre(string actual)
    {
        string? resultado = null;

        var dlg = new Window
        {
            Title = "Editar materia",
            Width = 440,
            Height = 210,
            Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ResizeMode = ResizeMode.NoResize
        };

        var marco = new Border
        {
            CornerRadius = new CornerRadius(16),
            Background = (Brush)new BrushConverter().ConvertFrom("#0A0E27")!,
            Padding = new Thickness(22)
        };
        var contenido = new StackPanel();
        contenido.Children.Add(new TextBlock
        {
            Text = "EDITAR MATERIA",
            FontSize = 11,
            FontWeight = FontWeights.Bold,
            Foreground = (Brush)new BrushConverter().ConvertFrom("#00FF87")!,
            Margin = new Thickness(0, 0, 0, 12)
        });

        var cajaBorde = new Border
        {
            Background = (Brush)new BrushConverter().ConvertFrom("#0E1330")!,
            BorderBrush = (Brush)new BrushConverter().ConvertFrom("#3D2A54")!,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(12, 0, 12, 0)
        };
        var caja = new TextBox
        {
            Text = actual,
            Height = 42,
            VerticalContentAlignment = VerticalAlignment.Center,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = Brushes.White,
            CaretBrush = (Brush)new BrushConverter().ConvertFrom("#00FF87")!,
            FontSize = 14
        };
        cajaBorde.Child = caja;
        contenido.Children.Add(cajaBorde);

        var fila = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 18, 0, 0)
        };
        var btnCancelar = new Button
        {
            Content = "Cancelar",
            Style = (Style)FindResource("BtnAccion"),
            Height = 38,
            MinWidth = 100,
            Background = (Brush)new BrushConverter().ConvertFrom("#2A3358")!
        };
        btnCancelar.Click += (_, _) => { dlg.DialogResult = false; };
        var btnGuardar = new Button
        {
            Content = "Guardar",
            Style = (Style)FindResource("BtnAccion"),
            Height = 38,
            MinWidth = 100,
            Foreground = (Brush)new BrushConverter().ConvertFrom("#0A0E27")!,
            Background = (Brush)new BrushConverter().ConvertFrom("#00FF87")!
        };
        btnGuardar.Click += (_, _) => { resultado = caja.Text; dlg.DialogResult = true; };

        fila.Children.Add(btnCancelar);
        fila.Children.Add(btnGuardar);
        contenido.Children.Add(fila);

        marco.Child = contenido;
        dlg.Content = marco;

        // Enter = guardar, Esc = cancelar
        dlg.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { resultado = caja.Text; dlg.DialogResult = true; }
            else if (e.Key == Key.Escape) { dlg.DialogResult = false; }
        };

        caja.Focus();
        caja.SelectAll();

        var ok = dlg.ShowDialog();
        return ok == true ? resultado : null;
    }
}