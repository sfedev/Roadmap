namespace DotNetLab.Web.Infrastructure;

/// <summary>
/// Logging de alto rendimiento del frontend, generado en tiempo de compilación.
/// Mismo motivo que en la API: los analizadores CA1848/CA1873 rechazan las llamadas directas
/// a logger.LogInformation porque hacen boxing y evalúan los argumentos aunque el nivel esté
/// apagado.
/// </summary>
internal static partial class WebLog
{
    // Confirma en el arranque a qué backend apunta el proxy: es el primer dato que se mira
    // cuando el frontend "no ve" la API.
    [LoggerMessage(EventId = 6000, Level = LogLevel.Information,
        Message = "Proxy configurado hacia {BaseUrl} (clave compartida: {HasKey})")]
    public static partial void ProxyConfigured(ILogger logger, string baseUrl, bool hasKey);

    // Un trabajo difundido por SignalR. Nivel Debug: en producción sería ruido, pero durante
    // una demostración permite ver que el evento llegó del bus al navegador.
    [LoggerMessage(EventId = 6001, Level = LogLevel.Debug,
        Message = "Trabajo {JobId} difundido por SignalR con estado {Status}")]
    public static partial void JobBroadcast(ILogger logger, Guid jobId, string status);
}
