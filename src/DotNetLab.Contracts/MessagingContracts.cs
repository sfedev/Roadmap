namespace DotNetLab.Contracts;

/// <summary>
/// Evento publicado por la API cuando acepta un trabajo de análisis pesado.
/// Vive en Contracts (y no en la API) porque el contrato del mensaje lo comparten TRES
/// procesos: quien publica, quien consume y quien reacciona a la finalización.
/// </summary>
// Un record inmutable es el tipo correcto para un mensaje: una vez publicado, su contenido
// ya no puede cambiar, y MassTransit lo serializa a JSON sin necesitar atributos.
public sealed record TelemetryAnalysisRequested(
    // Identidad del trabajo. La genera la API y viaja hasta la notificación final por SignalR,
    // así que es la clave de correlación de todo el flujo asíncrono.
    Guid JobId,
    // Filas de telemetría sintética que el Worker debe generar y parsear.
    int RowCount,
    // Clave del parser a usar ("span" | "naive"): el Worker resuelve el servicio por esa clave.
    string Strategy,
    // Instante de aceptación en la API; restado del inicio del consumo da la latencia de cola.
    DateTimeOffset RequestedAt);

/// <summary>Evento publicado por el Worker cuando el análisis termina con éxito.</summary>
public sealed record TelemetryAnalysisCompleted(
    // Mismo identificador que en la petición: es lo que permite casar respuesta y solicitud.
    Guid JobId,
    // Métricas reales del parseo, con el MISMO record que usan los endpoints síncronos.
    ParseStrategyResult Result,
    // Milisegundos que el mensaje pasó esperando en RabbitMQ antes de ser consumido.
    // Es la métrica que delata si el Worker va corto de réplicas.
    double QueueLatencyMs,
    // Milisegundos de procesamiento efectivo dentro del consumidor.
    double ProcessingMs,
    // Nombre del host que lo procesó: en Kubernetes es el nombre del pod, así que hace visible
    // el reparto de carga entre réplicas del Worker.
    string WorkerInstance,
    DateTimeOffset CompletedAt);

/// <summary>Evento publicado por el Worker cuando el análisis falla de forma definitiva.</summary>
public sealed record TelemetryAnalysisFailed(
    Guid JobId,
    // Motivo legible; el detalle técnico se queda en los logs y en la traza, no en el mensaje.
    string Reason,
    // Intentos consumidos antes de rendirse: lo aporta la política de reintentos de MassTransit.
    int Attempts,
    DateTimeOffset FailedAt);

/// <summary>Cuerpo de la petición POST que encola un análisis.</summary>
public sealed record AnalysisJobRequest(
    // Filas a generar y parsear. La API lo acota al rango operativo antes de publicar.
    int RowCount,
    // Clave del parser ("span" | "naive"); se valida contra la lista blanca en el borde.
    string Strategy);

/// <summary>Cuerpo de la respuesta 202 Accepted que devuelve la API al aceptar un trabajo.</summary>
public sealed record AnalysisJobAccepted(
    Guid JobId,
    // URL donde consultar el estado. Es lo que exige la semántica de 202: el recurso todavía
    // no existe, pero se indica dónde aparecerá.
    string StatusUrl,
    int RowCount,
    string Strategy,
    // Nombre del hub de SignalR al que suscribirse para recibir el resultado sin hacer polling.
    string NotificationHub,
    DateTimeOffset AcceptedAt);

/// <summary>Estado de un trabajo, tal y como lo ve la UI (por SignalR o por consulta directa).</summary>
public sealed record AnalysisJobSnapshot(
    Guid JobId,
    // "accepted" | "completed" | "failed": la UI decide el color con un switch sobre este valor.
    string Status,
    int RowCount,
    string Strategy,
    // Métricas del parseo; null mientras el trabajo no haya terminado.
    ParseStrategyResult? Result,
    // Latencia de cola y tiempo de proceso; 0 mientras siga pendiente.
    double QueueLatencyMs,
    double ProcessingMs,
    // Host que lo procesó, o null si aún no lo ha tomado ningún Worker.
    string? WorkerInstance,
    // Motivo del fallo cuando Status es "failed".
    string? FailureReason,
    DateTimeOffset UpdatedAt);
