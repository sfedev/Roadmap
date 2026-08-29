using System.Security.Cryptography;
using System.Text;

namespace DotNetLab.Api.Infrastructure;

/// <summary>Opciones de la clave compartida entre el frontend y la API.</summary>
public sealed class SharedKeyOptions
{
    /// <summary>Sección de configuración: SharedKey__* como variables de entorno.</summary>
    public const string SectionName = "SharedKey";

    /// <summary>
    /// Ruta al fichero del secreto. Docker Compose monta los secrets en /run/secrets/&lt;nombre&gt;,
    /// que es el mecanismo recomendado: el valor nunca aparece en `docker inspect` ni en el historial de capas.
    /// </summary>
    public string? File { get; init; }

    /// <summary>Valor en claro (solo para desarrollo local sin Docker). Tiene menor prioridad que File.</summary>
    public string? Value { get; init; }

    /// <summary>Nombre de la cabecera HTTP que transporta la clave.</summary>
    public string HeaderName { get; init; } = "X-Lab-Key";
}

/// <summary>
/// Resuelve el secreto efectivo una sola vez al arrancar y valida las cabeceras entrantes.
/// Se registra como Singleton: leer el fichero en cada petición sería I/O innecesario.
/// </summary>
public sealed class SharedKeyValidator
{
    // Bytes del secreto esperado. Null = validación desactivada (modo desarrollo).
    private readonly byte[]? _expected;

    /// <summary>Cabecera que se inspecciona; la expone el proxy del frontend para rellenarla.</summary>
    public string HeaderName { get; }

    /// <summary>true si hay secreto configurado y por tanto la validación está activa.</summary>
    public bool IsEnabled => _expected is not null;

    public SharedKeyValidator(SharedKeyOptions options, ILogger<SharedKeyValidator> logger)
    {
        HeaderName = options.HeaderName;

        // Prioridad 1: fichero de secreto montado por Docker. Se lee una vez y se descarta la ruta.
        // Trim() elimina el salto de línea final que casi todos los editores añaden al fichero.
        var fromFile = !string.IsNullOrWhiteSpace(options.File) && File.Exists(options.File)
            ? File.ReadAllText(options.File).Trim()
            : null;

        // Prioridad 2: valor en configuración. El '??' encadena ambas fuentes en una expresión.
        var resolved = fromFile ?? options.Value;

        // Sin secreto => modo abierto. Se registra explícitamente para que nadie lo dé por hecho en producción.
        if (string.IsNullOrWhiteSpace(resolved))
        {
            _expected = null;
            ApiLog.SharedKeyDisabled(logger);
            return;
        }

        // Se guarda en bytes UTF-8 porque la comparación en tiempo constante opera sobre bytes.
        _expected = Encoding.UTF8.GetBytes(resolved);
        ApiLog.SharedKeyEnabled(logger, HeaderName, fromFile is null ? "configuración" : "docker secret");
    }

    /// <summary>Compara la cabecera recibida con el secreto esperado.</summary>
    public bool IsValid(string? candidate)
    {
        // Sin secreto configurado todo pasa: permite `dotnet run` sin ceremonia.
        if (_expected is null) return true;
        // Cabecera ausente o vacía: rechazo inmediato, sin tocar criptografía.
        if (string.IsNullOrEmpty(candidate)) return false;

        // Comparación en tiempo constante: una comparación normal (==) filtraría información
        // por canal lateral, ya que aborta en el primer byte distinto.
        // FixedTimeEquals devuelve false si las longitudes difieren, sin lanzar.
        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(candidate), _expected);
    }
}

/// <summary>
/// Filtro de endpoint que aplica <see cref="SharedKeyValidator"/> a un grupo entero de rutas.
/// Los endpoint filters son la alternativa ligera al middleware: solo corren para las rutas
/// a las que se adjuntan, sin atravesar todo el pipeline.
/// </summary>
public sealed class SharedKeyEndpointFilter(SharedKeyValidator validator) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        // Atajo: si la validación está apagada no se toca la petición en absoluto.
        if (!validator.IsEnabled) return await next(context);

        // TryGetValue evita la excepción por clave ausente; el valor es un StringValues.
        var provided = context.HttpContext.Request.Headers[validator.HeaderName].ToString();

        if (!validator.IsValid(provided))
        {
            // 401 y no 403: falta o es incorrecta la credencial, no es un problema de permisos.
            // TypedResults produce una respuesta ProblemDetails tipada, no un string suelto.
            return TypedResults.Problem(
                title: "Clave compartida inválida",
                detail: $"La cabecera {validator.HeaderName} falta o no coincide con el secreto del servicio.",
                statusCode: StatusCodes.Status401Unauthorized);
        }

        // next(context) continúa la cadena: el siguiente filtro o el handler final.
        return await next(context);
    }
}
