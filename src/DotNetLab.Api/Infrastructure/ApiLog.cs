namespace DotNetLab.Api.Infrastructure;

/// <summary>
/// Logging de alto rendimiento generado en tiempo de compilación.
/// El generador convierte cada método parcial en un delegado <c>LoggerMessage</c> cacheado:
/// no hay boxing de los argumentos ni parseo de la plantilla en cada llamada, y si el nivel
/// está desactivado los argumentos ni siquiera se evalúan. Es lo que exigen CA1848 y CA1873.
/// </summary>
// 'partial' porque el source generator escribe la otra mitad; 'static' porque no hay estado.
internal static partial class ApiLog
{
    // Error: las dos estrategias deberían leer exactamente las mismas filas del mismo buffer.
    [LoggerMessage(EventId = 1001, Level = LogLevel.Error,
        Message = "Benchmark inconsistente: span leyó {SpanRows} filas y naive {NaiveRows}")]
    public static partial void BenchmarkMismatch(ILogger logger, int spanRows, int naiveRows);

    // Traza del pipeline de Channels; 'Waits' es la métrica que evidencia el backpressure.
    [LoggerMessage(EventId = 2000, Level = LogLevel.Information,
        Message = "Pipeline completado: {Events} eventos, capacidad {Capacity}, {Waits} esperas por canal lleno")]
    public static partial void PipelineCompleted(ILogger logger, int events, int capacity, int waits);

    // Warning y no Error: la API degrada correctamente si el contenedor auxiliar no responde.
    // El parámetro Exception lo detecta el generador por su tipo y lo adjunta al log.
    [LoggerMessage(EventId = 3000, Level = LogLevel.Warning,
        Message = "No se pudo contactar con la cache en {Host}:{Port}")]
    public static partial void CacheUnreachable(ILogger logger, Exception exception, string host, int port);

    // Aviso explícito: en producción, arrancar sin secreto configurado debe verse en los logs.
    [LoggerMessage(EventId = 4000, Level = LogLevel.Warning,
        Message = "Validación de clave compartida DESACTIVADA: no hay secreto configurado")]
    public static partial void SharedKeyDisabled(ILogger logger);

    // Confirma en el arranque de qué fuente salió el secreto (fichero de docker secret o config).
    [LoggerMessage(EventId = 4001, Level = LogLevel.Information,
        Message = "Validación de clave compartida activa en la cabecera {Header} (origen: {Source})")]
    public static partial void SharedKeyEnabled(ILogger logger, string header, string source);

    // Desenlace de un trabajo asíncrono recibido por el bus. El nombre de la réplica del Worker
    // permite comprobar en los logs que la carga se reparte entre pods.
    [LoggerMessage(EventId = 5000, Level = LogLevel.Information,
        Message = "Trabajo {JobId} {Status} (procesado por {Worker})")]
    public static partial void JobResultReceived(ILogger logger, Guid jobId, string status, string? worker);
}
