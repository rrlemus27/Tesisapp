using System.Net.Mail;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace StarAchiever.Desktop;

// Validaciones de los formularios del ADMIN. Las reglas de nombre, usuario/correo y
// contraseña son las mismas que aplica la API en Auth/registro; los largos máximos
// son los de las columnas de la base (StringLength de los modelos de la API).
public static class Validacion
{
    public const int MaxMateria = 80;
    public const int MaxGrado = 50;
    public const int MaxSeccion = 30;
    public const int MaxPeriodo = 50;
    public const int MaxNombreCompleto = 150;
    public const int MaxCorreoOUsuario = 120;
    public const int MinClave = 8;
    public const int MaxClave = 128;

    private static readonly Regex NombrePersona = new(
        @"^[\p{L}\p{M}][\p{L}\p{M} .'-]{1,149}$", RegexOptions.CultureInvariant);

    private static readonly Regex Espacios = new(@"\s+", RegexOptions.CultureInvariant);

    // Quita los espacios del principio y del final y deja uno solo entre palabras.
    public static string Limpiar(string? texto) => Espacios.Replace((texto ?? "").Trim(), " ");

    // Campo de texto obligatorio con largo máximo. Devuelve el error o null.
    public static string? Texto(string valor, string campo, int max, int min = 1)
    {
        if (valor.Length == 0) return $"✗ {campo}: es obligatorio.";
        if (valor.Length < min) return $"✗ {campo}: debe tener al menos {min} caracteres.";
        if (valor.Length > max) return $"✗ {campo}: máximo {max} caracteres (tiene {valor.Length}).";
        return null;
    }

    public static string? NombreCompleto(string nombre)
    {
        var error = Texto(nombre, "Nombre completo", MaxNombreCompleto, min: 2);
        if (error != null) return error;
        return NombrePersona.IsMatch(nombre)
            ? null
            : "✗ Nombre completo: solo letras, espacios, apóstrofes, puntos o guiones (sin números).";
    }

    // Usuario o correo: 2-120 caracteres, sin espacios; si lleva @ debe ser un correo válido.
    public static string? CorreoOUsuario(string identidad)
    {
        var error = Texto(identidad, "Usuario o correo", MaxCorreoOUsuario, min: 2);
        if (error != null) return error;
        if (identidad.Any(char.IsWhiteSpace)) return "✗ Usuario o correo: no puede contener espacios.";
        if (!identidad.Contains('@')) return null;
        return MailAddress.TryCreate(identidad, out var correo)
               && string.Equals(correo.Address, identidad, StringComparison.OrdinalIgnoreCase)
            ? null
            : "✗ Usuario o correo: el correo no tiene un formato válido (ej.: nombre@dominio.com).";
    }

    // Contraseña nueva: 8-128 caracteres y sin espacios al inicio o al final
    // (no se recortan en silencio: la contraseña se guarda tal como se escribe).
    public static string? ClaveNueva(string clave)
    {
        if (clave.Length == 0) return "✗ Contraseña: es obligatoria.";
        if (string.IsNullOrWhiteSpace(clave)) return "✗ Contraseña: no puede estar formada solo por espacios.";
        if (clave != clave.Trim()) return "✗ Contraseña: no puede empezar ni terminar con espacios.";
        if (clave.Length < MinClave || clave.Length > MaxClave)
            return $"✗ Contraseña: debe tener entre {MinClave} y {MaxClave} caracteres.";
        return null;
    }

    // ¿Ya hay en la lista un elemento con ese valor en 'campo' (sin distinguir mayúsculas)?
    // Se excluye el propio registro al editar y se puede acotar con 'filtro' (p. ej. mismo grado).
    public static bool Duplicado(JArray? datos, string campo, string valor, int excluirId = 0,
        Func<JToken, bool>? filtro = null)
    {
        if (datos is null) return false;
        return datos.Any(item =>
            (item["id"]?.Value<int?>() ?? 0) != excluirId
            && (filtro is null || filtro(item))
            && string.Equals(Limpiar(item[campo]?.ToString()), valor, StringComparison.OrdinalIgnoreCase));
    }
}
