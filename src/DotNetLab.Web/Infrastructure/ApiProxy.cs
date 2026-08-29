namespace DotNetLab.Web.Infrastructure;

/// <summary>Configuración del backend vista desde el frontend.</summary>
public sealed class ApiOptions
{
    /// <summary>Sección de configuración: Api__BaseUrl, Api__SharedKeyFile... en Docker.</summary>
    public const string SectionName = "Api";

    /// <summary>Nombre del HttpClient nombrado que apunta a la Web API.</summary>
    public const string HttpClientName = "lab-api";

    /// <summary>
    /// URL interna de la API. En docker-compose es http://api:8080, donde "api" es el nombre
    /// del servicio y lo resuelve el DNS embebido de la red de Docker.
    /// </summary>
    public string BaseUrl { get; init; } = "http://localhost:5280";

    /// <summary>Ruta del docker secret con la clave compartida.</summary>
    public string? SharedKeyFile { get; init; }

    /// <summary>Clave en claro para desarrollo local; menor prioridad que el fichero.</summary>
    public string? SharedKey { get; init; }

    /// <summary>Cabecera HTTP que transporta la clave; debe coincidir con la que valida la API.</summary>
    public string HeaderName { get; init; } = "X-Lab-Key";

    /// <summary>Devuelve el secreto efectivo, priorizando el fichero montado por Docker.</summary>
    public string? ResolveSharedKey()
    {
        // File.Exists evita la excepción cuando la ruta viene configurada pero el secreto no está montado.
        if (!string.IsNullOrWhiteSpace(SharedKeyFile) && File.Exists(SharedKeyFile))
        {
            // Trim() quita el salto de línea final que añaden casi todos los editores.
            return File.ReadAllText(SharedKeyFile).Trim();
        }

        return SharedKey;
    }
}

/// <summary>
/// Proxy de reenvío hacia la Web API. Existe para que el cliente WebAssembly pueda llamar a
/// rutas relativas del propio origen: así no hay preflight de CORS, la URL interna del
/// contenedor de la API no se publica y la clave compartida se queda en el servidor.
/// </summary>
public static class ApiProxy
{
    // Cabeceras que NO deben copiarse tal cual: las gestiona el servidor de destino o Kestrel,
    // y reenviarlas provoca respuestas corruptas (doble compresión, longitudes incoherentes).
    // "content-type" también se excluye porque se asigna aparte, justo antes del bucle.
    private static readonly string[] BlockedResponseHeaders =
    [
        "transfer-encoding", "content-encoding", "content-length",
        "connection", "keep-alive", "server", "content-type"
    ];

    /// <summary>Registra el endpoint comodín /api/{**path}.</summary>
    public static IEndpointRouteBuilder MapApiProxy(this IEndpointRouteBuilder endpoints)
    {
        // {**path} es un catch-all que NO escapa las barras: conserva la ruta completa.
        endpoints.MapGet("/api/{**path}", ForwardAsync)
            // El proxy no renderiza HTML: excluirlo del antiforgery evita validaciones inútiles.
            .DisableAntiforgery();

        return endpoints;
    }

    /// <summary>Reenvía la petición manteniendo el streaming de la respuesta.</summary>
    private static async Task ForwardAsync(
        string path,
        HttpContext context,
        IHttpClientFactory clientFactory,
        CancellationToken cancellationToken)
    {
        // El cliente nombrado ya lleva BaseAddress, timeout y la cabecera del secreto compartido.
        var client = clientFactory.CreateClient(ApiOptions.HttpClientName);

        // QueryString se reenvía tal cual: los parámetros del playground viajan íntegros.
        var target = new Uri(client.BaseAddress!, $"api/{path}{context.Request.QueryString}");

        using var request = new HttpRequestMessage(HttpMethod.Get, target);

        // ResponseHeadersRead es la clave del streaming: devuelve el control en cuanto llegan las
        // cabeceras, sin esperar (ni bufferizar en memoria) el cuerpo completo. Sin esto, el
        // endpoint de Channels dejaría de emitir eventos progresivamente.
        using var response = await client.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        // Se propaga el código de estado real: un 401 del backend debe verse como 401 en el cliente.
        context.Response.StatusCode = (int)response.StatusCode;

        // Content-Type incluye el charset; se copia para que el navegador interprete bien el cuerpo.
        if (response.Content.Headers.ContentType is { } contentType)
        {
            context.Response.ContentType = contentType.ToString();
        }

        // Resto de cabeceras de contenido (Cache-Control, ETag...) salvo las de transporte.
        foreach (var header in response.Content.Headers)
        {
            if (BlockedResponseHeaders.Contains(header.Key, StringComparer.OrdinalIgnoreCase)) continue;
            context.Response.Headers[header.Key] = header.Value.ToArray();
        }

        // Copia directa de stream a stream: el cuerpo nunca se materializa entero en memoria.
        await response.Content.CopyToAsync(context.Response.Body, cancellationToken);
    }
}
