using DotNetLab.Analysis;
using DotNetLab.Api.Infrastructure;
using DotNetLab.Contracts;
using DotNetLab.ServiceDefaults;
using MassTransit;

namespace DotNetLab.Api.Services;

/// <summary>
/// Publica solicitudes de análisis en el bus. Se abstrae en un interfaz para que el endpoint
/// no tenga que preguntar por la configuración: si el bus está apagado, el contenedor le
/// inyecta una implementación que lo dice, en vez de fallar al resolver IPublishEndpoint.
/// </summary>
public interface IAnalysisJobPublisher
{
    /// <summary>false cuando el transporte está en "disabled": el endpoint responde 503.</summary>
    bool IsEnabled { get; }

    /// <summary>Registra el trabajo y publica el evento. Devuelve lo que se envía en el 202.</summary>
    ValueTask<AnalysisJobAccepted> PublishAsync(int rowCount, string strategy, CancellationToken cancellationToken);
}

/// <summary>Implementación real: registra el trabajo y publica el evento en el bus.</summary>
public sealed class BusAnalysisJobPublisher(
    IPublishEndpoint publishEndpoint,
    JobRegistry registry,
    TimeProvider timeProvider) : IAnalysisJobPublisher
{
    public bool IsEnabled => true;

    public async ValueTask<AnalysisJobAccepted> PublishAsync(
        int rowCount, string strategy, CancellationToken cancellationToken)
    {
        // Guid v7: ordenable por tiempo, así que los identificadores de trabajo consecutivos
        // quedan contiguos en cualquier índice o log ordenado.
        var jobId = Guid.CreateVersion7();
        var acceptedAt = timeProvider.GetUtcNow();

        // Se acota ANTES de publicar: si el mensaje viajase con un tamaño absurdo, el Worker
        // tendría que rechazarlo después de haberlo transportado y encolado para nada.
        var rows = TelemetrySampleGenerator.ClampRows(rowCount);

        // Primero el registro local y luego la publicación. Al revés existiría una ventana en
        // la que el resultado llega antes que la aceptación y el registro no sabría de qué va.
        registry.Accept(jobId, rows, strategy);

        await publishEndpoint.Publish(
            new TelemetryAnalysisRequested(jobId, rows, strategy, acceptedAt),
            cancellationToken).ConfigureAwait(false);

        // Métricas de negocio: aceptados (contador) y en vuelo (sube aquí, baja al completar).
        LabTelemetry.JobsAccepted.Add(1, new KeyValuePair<string, object?>("strategy", strategy));
        LabTelemetry.JobsInFlight.Add(1);

        return new AnalysisJobAccepted(
            JobId: jobId,
            // Ruta relativa: la absoluta la compone el endpoint, que sí conoce el host real.
            StatusUrl: $"/api/analysis/jobs/{jobId}",
            RowCount: rows,
            Strategy: strategy,
            // El cliente se suscribe a este hub para enterarse sin hacer polling.
            NotificationHub: "/hubs/jobs",
            AcceptedAt: acceptedAt);
    }
}

/// <summary>
/// Implementación nula para cuando el bus está desactivado. Existir es su función: permite
/// que el endpoint siga siendo inyectable y devuelva un 503 explicativo en lugar de un 500
/// por una dependencia que no se puede resolver.
/// </summary>
public sealed class DisabledAnalysisJobPublisher : IAnalysisJobPublisher
{
    public bool IsEnabled => false;

    // NotSupportedException y no un valor falso: el endpoint comprueba IsEnabled antes de
    // llamar, así que llegar aquí sería un bug de programación, no un estado esperado.
    public ValueTask<AnalysisJobAccepted> PublishAsync(int rowCount, string strategy, CancellationToken cancellationToken)
        => throw new NotSupportedException("El bus de mensajes está desactivado (Messaging__Transport=disabled).");
}
