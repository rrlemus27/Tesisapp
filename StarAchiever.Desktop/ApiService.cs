using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace StarAchiever.Desktop;

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

    // ---- LOGIN ----
    // Devuelve true si entró bien, y guarda token, rol y nombre.
    public static async Task<bool> LoginAsync(string usuario, string clave)
    {
        var body = new { correoOUsuario = usuario, clave = clave };
        var contenido = new StringContent(JsonConvert.SerializeObject(body), Encoding.UTF8, "application/json");

        var resp = await _http.PostAsync($"{BaseUrl}/auth/login", contenido);
        if (!resp.IsSuccessStatusCode) return false;

        var json = await resp.Content.ReadAsStringAsync();
        var data = JObject.Parse(json);

        Token = data["token"]?.ToString();
        Rol = data["usuario"]?["rol"]?.ToString();
        NombreUsuario = data["usuario"]?["nombreCompleto"]?.ToString();
        UsuarioId = data["usuario"]?["id"]?.Value<int>() ?? 0;

        return !string.IsNullOrEmpty(Token);
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
        var resp = await _http.GetAsync($"{BaseUrl}/{ruta}");
        var body = await resp.Content.ReadAsStringAsync();
        return new ApiResult
        {
            Exito = resp.IsSuccessStatusCode,
            Codigo = (int)resp.StatusCode,
            Contenido = body,
            Mensaje = ExtraerMensaje(body, resp)
        };
    }

    // ---- POST genérico ---- manda 'datos' como JSON con el token y devuelve
    // si tuvo éxito, el código de estado y el mensaje que responda la API.
    public static async Task<ApiResult> PostAsync(string ruta, object datos)
    {
        UsarToken();
        var contenido = new StringContent(JsonConvert.SerializeObject(datos), Encoding.UTF8, "application/json");
        var resp = await _http.PostAsync($"{BaseUrl}/{ruta}", contenido);
        var body = await resp.Content.ReadAsStringAsync();
        return new ApiResult
        {
            Exito = resp.IsSuccessStatusCode,
            Codigo = (int)resp.StatusCode,
            Contenido = body,
            Mensaje = ExtraerMensaje(body, resp)
        };
    }

    // ---- PUT genérico ---- actualiza un recurso con el token.
    public static async Task<ApiResult> PutAsync(string ruta, object datos)
    {
        UsarToken();
        var contenido = new StringContent(JsonConvert.SerializeObject(datos), Encoding.UTF8, "application/json");
        var resp = await _http.PutAsync($"{BaseUrl}/{ruta}", contenido);
        var body = await resp.Content.ReadAsStringAsync();
        return new ApiResult
        {
            Exito = resp.IsSuccessStatusCode,
            Codigo = (int)resp.StatusCode,
            Contenido = body,
            Mensaje = ExtraerMensaje(body, resp)
        };
    }

    // ---- PATCH genérico ---- para cambios parciales (p. ej. cambiar el estado).
    // 'datos' es opcional; muchos PATCH usan solo query params en la ruta.
    public static async Task<ApiResult> PatchAsync(string ruta, object? datos = null)
    {
        UsarToken();
        var json = datos is null ? "{}" : JsonConvert.SerializeObject(datos);
        var contenido = new StringContent(json, Encoding.UTF8, "application/json");
        var resp = await _http.PatchAsync($"{BaseUrl}/{ruta}", contenido);
        var body = await resp.Content.ReadAsStringAsync();
        return new ApiResult
        {
            Exito = resp.IsSuccessStatusCode,
            Codigo = (int)resp.StatusCode,
            Contenido = body,
            Mensaje = ExtraerMensaje(body, resp)
        };
    }

    // ---- DELETE genérico ---- elimina un recurso con el token.
    public static async Task<ApiResult> DeleteAsync(string ruta)
    {
        UsarToken();
        var resp = await _http.DeleteAsync($"{BaseUrl}/{ruta}");
        var body = await resp.Content.ReadAsStringAsync();
        return new ApiResult
        {
            Exito = resp.IsSuccessStatusCode,
            Codigo = (int)resp.StatusCode,
            Contenido = body,
            Mensaje = ExtraerMensaje(body, resp)
        };
    }

    // Intenta sacar el campo "mensaje" del JSON; si no hay, usa el motivo HTTP.
    private static string ExtraerMensaje(string body, HttpResponseMessage resp)
    {
        try
        {
            var token = JToken.Parse(body);
            var msg = token["mensaje"]?.ToString() ?? token["message"]?.ToString();
            if (!string.IsNullOrWhiteSpace(msg)) return msg!;
        }
        catch { /* el cuerpo no era JSON (p. ej. 403/401 sin cuerpo) */ }
        return resp.ReasonPhrase ?? "";
    }
}

// Resultado de una llamada a la API, con lo necesario para depurar en pantalla.
public class ApiResult
{
    public bool Exito { get; set; }
    public int Codigo { get; set; }
    public string Contenido { get; set; } = "";
    public string Mensaje { get; set; } = "";
}