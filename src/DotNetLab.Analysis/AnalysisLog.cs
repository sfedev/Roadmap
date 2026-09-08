using Microsoft.Extensions.Logging;

namespace DotNetLab.Analysis;

/// <summary>
/// Logging de alto rendimiento del dominio de análisis, generado en tiempo de compilación.
/// Cada método parcial se convierte en un delegado <c>LoggerMessage</c> cacheado: sin boxing
/// de argumentos, sin parsear la plantilla en cada llamada y sin evaluar los parámetros si el
/// nivel está desactivado. Es lo que exigen los analizadores CA1848 y CA1873.
/// </summary>
internal static partial class AnalysisLog
{
    // EventId estable: permite filtrar exactamente este evento en un agregador de logs aunque
    // el texto del mensaje cambie con el tiempo.
    [LoggerMessage(EventId = 1000, Level = LogLevel.Information,
        Message = "Parser {Strategy} procesó {Rows} filas en {Microseconds} us asignando {Bytes} bytes")]
    public static partial void ParserExecuted(ILogger logger, string strategy, int rows, double microseconds, long bytes);

    // Información y no Error: un trabajo terminado es el caso normal, y a este nivel se puede
    // apagar en producción sin perder los errores.
    [LoggerMessage(EventId = 1010, Level = LogLevel.Information,
        Message = "Trabajo {JobId} procesado: {Rows} filas en {ProcessingMs} ms asignando {Bytes} bytes")]
    public static partial void JobProcessed(ILogger logger, Guid jobId, int rows, double processingMs, long bytes);

    // Warning y no Error: el mensaje es inválido, pero el servicio funciona correctamente al
    // rechazarlo. Un Error aquí dispararía alertas por un problema del emisor, no del consumidor.
    [LoggerMessage(EventId = 1011, Level = LogLevel.Warning,
        Message = "Trabajo {JobId} rechazado: la estrategia '{Strategy}' no está registrada")]
    public static partial void JobRejected(ILogger logger, Guid jobId, string strategy);
}
