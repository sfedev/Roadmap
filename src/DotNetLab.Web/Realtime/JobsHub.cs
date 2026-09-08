using DotNetLab.Contracts;
using DotNetLab.Web.Infrastructure;
using MassTransit;
using Microsoft.AspNetCore.SignalR;

namespace DotNetLab.Web.Realtime;

/// <summary>
/// Hub de notificación de trabajos. No expone ningún método invocable desde el cliente: el
/// tráfico es de servidor a navegador y en un solo sentido, así que un hub vacío es la
/// superficie mínima. Cualquier método público aquí sería una API pública sin autenticar.
/// </summary>
public sealed class JobsHub : Hub
{
    /// <summary>Ruta en la que se publica el hub; la comparten servidor y cliente.</summary>
    public const string Route = "/hubs/jobs";

    /// <summary>Nombre del mensaje que recibe el navegador cuando un trabajo cambia de estado.</summary>
    // Constante y no un literal repetido: si cambia, servidor y cliente cambian a la vez o
    // el compilador avisa. Un literal duplicado se desincroniza en silencio.
    public const string JobUpdatedMessage = "JobUpdated";
}

/// <summary>
/// Puente entre el bus de mensajes y el navegador: consume el desenlace de los trabajos y lo
/// reenvía por SignalR.
///
/// Que este consumidor viva en el FRONTEND y no en la API es deliberado: así el navegador
/// abre el WebSocket contra su propio origen, sin atravesar el proxy ni exponer la API. Es
/// además la demostración de publish/subscribe: el mismo evento lo consumen dos servicios
/// distintos, cada uno con su cola y para algo distinto.
/// </summary>
public sealed class JobNotificationConsumer(IHubContext<JobsHub> hub, ILogger<JobNotificationConsumer> logger)
    : IConsumer<TelemetryAnalysisCompleted>, IConsumer<TelemetryAnalysisFailed>
{
    public async Task Consume(ConsumeContext<TelemetryAnalysisCompleted> context)
    {
        var message = context.Message;

        // Se envía el MISMO record que devuelve el endpoint de consulta: el componente Blazor
        // tiene un solo camino de código para pintar el resultado, venga de donde venga.
        var snapshot = new AnalysisJobSnapshot(
            JobId: message.JobId,
            Status: "completed",
            RowCount: message.Result.RowsParsed,
            Strategy: message.Result.Strategy,
            Result: message.Result,
            QueueLatencyMs: message.QueueLatencyMs,
            ProcessingMs: message.ProcessingMs,
            WorkerInstance: message.WorkerInstance,
            FailureReason: null,
            UpdatedAt: message.CompletedAt);

        WebLog.JobBroadcast(logger, message.JobId, "completed");

        // Clients.All: el laboratorio no tiene usuarios, así que todo el que mire la página ve
        // los trabajos. Con autenticación, esto sería Clients.User(userId) y el trabajo llevaría
        // el identificador de su propietario.
        await hub.Clients.All.SendAsync(JobsHub.JobUpdatedMessage, snapshot, context.CancellationToken)
            .ConfigureAwait(false);
    }

    public async Task Consume(ConsumeContext<TelemetryAnalysisFailed> context)
    {
        var message = context.Message;

        var snapshot = new AnalysisJobSnapshot(
            JobId: message.JobId,
            Status: "failed",
            RowCount: 0,
            Strategy: "unknown",
            Result: null,
            QueueLatencyMs: 0,
            ProcessingMs: 0,
            WorkerInstance: null,
            FailureReason: message.Reason,
            UpdatedAt: message.FailedAt);

        WebLog.JobBroadcast(logger, message.JobId, "failed");

        await hub.Clients.All.SendAsync(JobsHub.JobUpdatedMessage, snapshot, context.CancellationToken)
            .ConfigureAwait(false);
    }
}
