using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace StarAchiever.Desktop;

// Rutas de la API, escritas exactamente como las expone la nueva versión.
public static class Rutas
{
    public const string Login = "Auth/login";
    public const string Health = "health";                         // anónimo: { api, database }
    public const string Roles = "Roles";                           // cualquier usuario autenticado
    public const string Materias = "Materias";                     // GET: ADMIN y DOCENTE · POST/PUT/DELETE: ADMIN
    public const string Grados = "Grados";                         // solo ADMIN
    public const string Secciones = "Secciones";                   // solo ADMIN
    public const string Usuarios = "Usuarios";                     // solo ADMIN
    public const string PeriodosAcademicos = "PeriodosAcademicos"; // solo ADMIN

    // Asignaciones (solo ADMIN). OJO: la API actual (StarAchiever.Api) NO tiene estos
    // controladores; responden 404 y la app los marca como «no disponible».
    public const string AsignacionesDocente = "admin/asignaciones-docente"; // GET, POST, DELETE /{id}
    public const string EstudiantesSeccion = "admin/estudiantes-seccion";   // GET, POST, DELETE /{usuarioId}
}

// Este servicio es el que le habla a tu API. Guarda el token y hace las llamadas.
public static class ApiService
{
    public const string Servidor = "https://localhost:7173";
    private const string BaseUrl = Servidor + "/api";
    private static readonly HttpClient _http = CrearCliente();

    public static string? Token { get; private set; }
    public static string? Rol { get; private set; }
    public static string? NombreUsuario { get; private set; }
    public static int UsuarioId { get; private set; }
    // Vencimiento del token (claim "exp" del JWT), para avisar antes de llamar a la API.
    public static DateTime? TokenExpiraUtc { get; private set; }

    // Se dispara (una vez) cuando la sesión deja de ser válida: el token venció o la API
    // respondió 401 a una llamada autenticada. La sesión ya está cerrada al dispararse.
    public static event Action? SesionExpirada;

    // Peticiones en curso: solo informa a la interfaz (indicador de carga); no cambia nada.
    private static int _peticionesEnCurso;
    public static bool Ocupado => _peticionesEnCurso > 0;
    public static event Action? OcupadoCambio;

    private static HttpClient CrearCliente()
    {
        var handler = new HttpClientHandler
        {
            // Solo se acepta el certificado de desarrollo cuando el servidor es local
            // (localhost); para cualquier otro host se exige un certificado válido.
            ServerCertificateCustomValidationCallback = (mensaje, cert, cadena, errores) =>
                errores == SslPolicyErrors.None || mensaje.RequestUri?.IsLoopback == true
        };
        // Sin respuesta en 30 s se avisa al usuario en lugar de esperar los 100 s por defecto.
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
    }

    // ---- LOGIN ---- POST api/Auth/login
    // Si entra bien guarda token, rol y nombre. Si falla, el resultado trae el código
    // y el mensaje de la API (nunca la contraseña ni el token).
    public static async Task<ApiResult> LoginAsync(string usuario, string clave)
    {
        CerrarSesion();
        var res = await EnviarAsync(HttpMethod.Post, Rutas.Login,
            Json(new { correoOUsuario = usuario, clave = clave }), autenticada: false);
        if (!res.Exito) return res;

        JObject data;
        try { data = JObject.Parse(res.Contenido); }
        catch (JsonException) { return Fallo(res, "La API respondió al login con un formato inesperado."); }

        var token = data["token"]?.ToString();
        if (string.IsNullOrEmpty(token)) return Fallo(res, "La API respondió sin token.");

        Token = token;
        TokenExpiraUtc = LeerExpiracion(token);
        // El rol se normaliza a mayúsculas (ADMIN / DOCENTE / ESTUDIANTE).
        Rol = data["usuario"]?["rol"]?.ToString()?.Trim().ToUpperInvariant();
        NombreUsuario = data["usuario"]?["nombreCompleto"]?.ToString();
        UsuarioId = data["usuario"]?["id"]?.Value<int?>() ?? 0;
        res.Contenido = ""; // el cuerpo trae el token: no lo dejamos en el resultado
        return res;
    }

    private static ApiResult Fallo(ApiResult res, string mensaje)
    {
        CerrarSesion();
        res.Exito = false;
        res.Contenido = "";
        res.Mensaje = mensaje;
        return res;
    }

    // Borra todos los datos de la sesión en memoria (token incluido).
    public static void CerrarSesion()
    {
        Token = null;
        TokenExpiraUtc = null;
        Rol = null;
        NombreUsuario = null;
        UsuarioId = 0;
    }

    // ---- Estado del servidor ---- GET api/health (sin token).
    public static Task<ApiResult> ComprobarServidorAsync() =>
        EnviarAsync(HttpMethod.Get, Rutas.Health, null, autenticada: false);

    // ---- GET con detalle ---- informa éxito, código y mensaje.
    public static Task<ApiResult> GetResultAsync(string ruta) =>
        EnviarAsync(HttpMethod.Get, ruta, null);

    // ---- POST genérico ---- manda 'datos' como JSON con el token.
    public static Task<ApiResult> PostAsync(string ruta, object datos) =>
        EnviarAsync(HttpMethod.Post, ruta, Json(datos));

    // ---- PUT genérico ---- actualiza un recurso con el token.
    public static Task<ApiResult> PutAsync(string ruta, object datos) =>
        EnviarAsync(HttpMethod.Put, ruta, Json(datos));

    // ---- PATCH genérico ---- para cambios parciales (p. ej. cambiar el estado).
    // 'datos' es opcional; muchos PATCH usan solo query params en la ruta.
    public static Task<ApiResult> PatchAsync(string ruta, object? datos = null) =>
        EnviarAsync(HttpMethod.Patch, ruta, datos is null
            ? new StringContent("{}", Encoding.UTF8, "application/json")
            : Json(datos));

    // ---- DELETE genérico ---- elimina un recurso con el token.
    public static Task<ApiResult> DeleteAsync(string ruta) =>
        EnviarAsync(HttpMethod.Delete, ruta, null);

    // Todas las llamadas pasan por aquí: pone el token en ESTA petición (no en el cliente
    // compartido), convierte los fallos de red en un resultado con Codigo = 0 y detecta
    // la sesión vencida (token caducado o 401).
    private static async Task<ApiResult> EnviarAsync(HttpMethod metodo, string ruta, HttpContent? contenido,
        bool autenticada = true)
    {
        if (autenticada && (Token is null || SesionVencida()))
        {
            AvisarSesionExpirada();
            return ApiResult.SesionNoValida();
        }

        using var peticion = new HttpRequestMessage(metodo, $"{BaseUrl}/{ruta}") { Content = contenido };
        if (autenticada) peticion.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);

        if (Interlocked.Increment(ref _peticionesEnCurso) == 1) OcupadoCambio?.Invoke();
        try
        {
            using var resp = await _http.SendAsync(peticion);
            var res = await ResultadoAsync(resp);
            if (autenticada && res.Codigo == 401)
            {
                AvisarSesionExpirada();
                return ApiResult.SesionNoValida();
            }
            return res;
        }
        catch (TaskCanceledException)
        {
            return ApiResult.SinConexion($"La API ({Servidor}) no respondió a tiempo. Inténtalo de nuevo en unos segundos.");
        }
        catch (HttpRequestException)
        {
            return ApiResult.SinConexion($"No se pudo conectar con la API ({Servidor}). Verifica que esté en ejecución y que tengas conexión.");
        }
        finally
        {
            if (Interlocked.Decrement(ref _peticionesEnCurso) == 0) OcupadoCambio?.Invoke();
        }
    }

    private static bool SesionVencida() => TokenExpiraUtc is DateTime expira && DateTime.UtcNow >= expira;

    private static void AvisarSesionExpirada()
    {
        if (Token is null) return; // ya se avisó (o no había sesión)
        CerrarSesion();
        SesionExpirada?.Invoke();
    }

    // Lee el claim "exp" del JWT (sin validarlo: eso lo hace la API) para saber cuándo vence.
    private static DateTime? LeerExpiracion(string token)
    {
        try
        {
            var partes = token.Split('.');
            if (partes.Length < 2) return null;
            var payload = partes[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
            var json = JObject.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(payload)));
            var exp = json["exp"]?.Value<long?>();
            return exp is null ? null : DateTimeOffset.FromUnixTimeSeconds(exp.Value).UtcDateTime;
        }
        catch (Exception ex) when (ex is FormatException or JsonException or ArgumentException)
        {
            return null;
        }
    }

    private static StringContent Json(object datos) =>
        new(JsonConvert.SerializeObject(datos), Encoding.UTF8, "application/json");

    private static async Task<ApiResult> ResultadoAsync(HttpResponseMessage resp)
    {
        var body = await resp.Content.ReadAsStringAsync();
        return new ApiResult
        {
            Exito = resp.IsSuccessStatusCode,
            Codigo = (int)resp.StatusCode,
            Contenido = body,
            Mensaje = resp.IsSuccessStatusCode ? MensajeDeExito(body) : MensajeDeError(body, resp)
        };
    }

    // En una respuesta correcta solo usamos un mensaje explícito de la API
    // ({ "mensaje": ... } o un texto); si devuelve el objeto creado, no hay mensaje.
    private static string MensajeDeExito(string body)
    {
        try
        {
            var token = JToken.Parse(body);
            if (token.Type == JTokenType.String) return token.ToString();
            if (token is JObject obj) return Campo(obj, "mensaje") ?? Campo(obj, "message") ?? "";
        }
        catch (JsonException)
        {
            if (EsTextoCorto(body)) return body.Trim();
        }
        return "";
    }

    // Saca el mensaje real de un error. Entiende:
    //  - { "mensaje": "..." } / { "message": "..." } / { "error": "..." } / { "detail": "..." }
    //  - errores de validación de ASP.NET: { "title": "...", "errors": { "Campo": ["..."] } }
    //  - un texto plano o un string JSON (p. ej. BadRequest("texto"))
    // Si no hay nada útil (p. ej. 401/403 sin cuerpo), usa el motivo HTTP.
    private static string MensajeDeError(string body, HttpResponseMessage resp)
    {
        try
        {
            var token = JToken.Parse(body);
            if (token.Type == JTokenType.String && !string.IsNullOrWhiteSpace(token.ToString()))
                return token.ToString();

            if (token is JObject obj)
            {
                var msg = Campo(obj, "mensaje") ?? Campo(obj, "message") ?? Campo(obj, "error") ?? Campo(obj, "detail");
                if (msg != null) return msg;

                var errores = obj.GetValue("errors", StringComparison.OrdinalIgnoreCase);
                var lista = errores switch
                {
                    JObject porCampo => porCampo.Properties().SelectMany(p =>
                        p.Value is JArray a ? a.Select(x => $"{p.Name}: {x}") : new[] { $"{p.Name}: {p.Value}" }),
                    JArray arr => arr.Select(x => x.ToString()),
                    _ => Enumerable.Empty<string>()
                };
                var texto = string.Join(" | ", lista);
                if (!string.IsNullOrWhiteSpace(texto)) return texto;

                var titulo = Campo(obj, "title");
                if (titulo != null) return titulo;
            }
        }
        catch (JsonException)
        {
            // El cuerpo no era JSON: si es un texto corto (no una página HTML), lo mostramos tal cual.
            if (EsTextoCorto(body)) return body.Trim();
        }
        return resp.ReasonPhrase ?? ((HttpStatusCode)resp.StatusCode).ToString();
    }

    private static string? Campo(JObject obj, string nombre)
    {
        var valor = obj.GetValue(nombre, StringComparison.OrdinalIgnoreCase)?.ToString();
        return string.IsNullOrWhiteSpace(valor) ? null : valor;
    }

    // Texto corto y de una sola línea: así no se muestran trazas de excepción del servidor.
    private static bool EsTextoCorto(string body) =>
        !string.IsNullOrWhiteSpace(body) && body.Length <= 300 && !body.TrimStart().StartsWith("<")
        && !body.Trim().Contains('\n');
}

// Resultado de una llamada a la API.
public class ApiResult
{
    public bool Exito { get; set; }
    // Código HTTP; 0 = no hubo respuesta (sin conexión o tiempo agotado).
    public int Codigo { get; set; }
    public string Contenido { get; set; } = "";
    public string Mensaje { get; set; } = "";

    public bool SinRespuesta => Codigo == 0;

    // 404 sin cuerpo = la ruta no existe en la API (un 404 «de negocio» trae { mensaje }).
    public bool EndpointInexistente => Codigo == 404 && string.IsNullOrWhiteSpace(Contenido);

    public static ApiResult SinConexion(string mensaje) => new() { Codigo = 0, Mensaje = mensaje };

    public static ApiResult SesionNoValida() =>
        new() { Codigo = 401, Mensaje = "Tu sesión expiró o ya no es válida. Vuelve a iniciar sesión." };

    // Explicación para el usuario, según el código de la respuesta.
    public string Explicacion() => Codigo switch
    {
        0 => Mensaje,
        401 => "Tu sesión expiró o ya no es válida. Vuelve a iniciar sesión.",
        403 => "Tu usuario no tiene permiso para esta acción.",
        404 when EndpointInexistente => "La API conectada no tiene este servicio (404).",
        >= 500 => "El servidor tuvo un error interno. Suele pasar cuando el dato ya existe o "
                  + "está relacionado con otros registros.",
        _ => string.IsNullOrWhiteSpace(Mensaje) ? "Respuesta inesperada de la API." : Mensaje
    };

    // Texto de error listo para una etiqueta de estado: «✗ No se pudo <accion>. <explicación> (HTTP n)».
    public string Error(string accion)
    {
        var codigo = Codigo > 0 ? $" (HTTP {Codigo})" : "";
        return $"✗ No se pudo {accion}. {Explicacion()}{codigo}";
    }

    // Mensaje de éxito de la API o, si no trae uno, el texto por defecto.
    public string Ok(string porDefecto) =>
        $"✓ {(string.IsNullOrWhiteSpace(Mensaje) ? porDefecto : Mensaje)}";
}
