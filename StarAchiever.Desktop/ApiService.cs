using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace StarAchiever.Desktop;

// Rutas de la API, escritas exactamente como las expone la nueva versión.
public static class Rutas
{
    public const string Login = "Auth/login";
    public const string Materias = "Materias";                     // GET: ADMIN y DOCENTE · POST/PUT/DELETE: ADMIN
    public const string Grados = "Grados";                         // solo ADMIN
    public const string Secciones = "Secciones";                   // solo ADMIN
    public const string Usuarios = "Usuarios";                     // solo ADMIN
    public const string PeriodosAcademicos = "PeriodosAcademicos"; // solo ADMIN
}

// Este servicio es el que le habla a tu API. Guarda el token y hace las llamadas.
public static class ApiService
{
    private const string BaseUrl = "https://localhost:7173/api";
    private static readonly HttpClient _http = CrearCliente();

    public static string? Token { get; set; }
    public static string? Rol { get; set; }
    public static string? NombreUsuario { get; set; }
    public static int UsuarioId { get; set; }

    private static HttpClient CrearCliente()
    {
        // Ignora el certificado local de desarrollo (localhost)
        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (m, c, ch, e) => true
        };
        return new HttpClient(handler);
    }

    // Pone el token en las cabeceras para las llamadas protegidas
    private static void UsarToken()
    {
        _http.DefaultRequestHeaders.Authorization =
            string.IsNullOrEmpty(Token)
                ? null
                : new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", Token);
    }

    // ---- LOGIN ---- POST api/Auth/login
    // Si entra bien guarda token, rol y nombre. Si falla, el resultado trae
    // el código y el mensaje reales de la API para mostrarlos en pantalla.
    public static async Task<ApiResult> LoginAsync(string usuario, string clave)
    {
        var res = await PostAsync(Rutas.Login, new { correoOUsuario = usuario, clave = clave });
        if (!res.Exito) return res;

        var data = JObject.Parse(res.Contenido);
        Token = data["token"]?.ToString();
        // El rol se normaliza a mayúsculas (ADMIN / DOCENTE / ESTUDIANTE).
        Rol = data["usuario"]?["rol"]?.ToString()?.Trim().ToUpperInvariant();
        NombreUsuario = data["usuario"]?["nombreCompleto"]?.ToString();
        UsuarioId = data["usuario"]?["id"]?.Value<int?>() ?? 0;

        if (string.IsNullOrEmpty(Token))
        {
            res.Exito = false;
            res.Mensaje = "La API respondió sin token.";
        }
        return res;
    }

    // ---- GET genérico ---- devuelve el JSON crudo de cualquier endpoint
    public static async Task<string> GetAsync(string ruta)
    {
        UsarToken();
        var resp = await _http.GetAsync($"{BaseUrl}/{ruta}");
        return await resp.Content.ReadAsStringAsync();
    }

    // ---- GET con detalle ---- igual que GetAsync pero informa éxito, código y mensaje
    // para poder mostrar el error real en pantalla cuando algo falla.
    public static async Task<ApiResult> GetResultAsync(string ruta)
    {
        UsarToken();
        return await ResultadoAsync(await _http.GetAsync($"{BaseUrl}/{ruta}"));
    }

    // ---- POST genérico ---- manda 'datos' como JSON con el token y devuelve
    // si tuvo éxito, el código de estado y el mensaje que responda la API.
    public static async Task<ApiResult> PostAsync(string ruta, object datos)
    {
        UsarToken();
        return await ResultadoAsync(await _http.PostAsync($"{BaseUrl}/{ruta}", Json(datos)));
    }

    // ---- PUT genérico ---- actualiza un recurso con el token.
    public static async Task<ApiResult> PutAsync(string ruta, object datos)
    {
        UsarToken();
        return await ResultadoAsync(await _http.PutAsync($"{BaseUrl}/{ruta}", Json(datos)));
    }

    // ---- PATCH genérico ---- para cambios parciales (p. ej. cambiar el estado).
    // 'datos' es opcional; muchos PATCH usan solo query params en la ruta.
    public static async Task<ApiResult> PatchAsync(string ruta, object? datos = null)
    {
        UsarToken();
        var contenido = datos is null
            ? new StringContent("{}", Encoding.UTF8, "application/json")
            : Json(datos);
        return await ResultadoAsync(await _http.PatchAsync($"{BaseUrl}/{ruta}", contenido));
    }

    // ---- DELETE genérico ---- elimina un recurso con el token.
    public static async Task<ApiResult> DeleteAsync(string ruta)
    {
        UsarToken();
        return await ResultadoAsync(await _http.DeleteAsync($"{BaseUrl}/{ruta}"));
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

    private static bool EsTextoCorto(string body) =>
        !string.IsNullOrWhiteSpace(body) && body.Length <= 500 && !body.TrimStart().StartsWith("<");
}

// Resultado de una llamada a la API, con lo necesario para depurar en pantalla.
public class ApiResult
{
    public bool Exito { get; set; }
    public int Codigo { get; set; }
    public string Contenido { get; set; } = "";
    public string Mensaje { get; set; } = "";
}
