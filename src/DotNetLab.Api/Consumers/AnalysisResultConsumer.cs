using DotNetLab.Api.Infrastructure;
using DotNetLab.Api.Services;
using DotNetLab.Contracts;
using MassTransit;

namespace DotNetLab.Api.Consumers;

/// <summary>
/// Consumidor de la API: escucha el desenlace de los trabajos para mantener actualizado su
/// registro, de modo que GET /api/analysis/jobs/{id} pueda responder.
///
/// Implementa DOS interfaces IConsumer en la misma clase: MassTransit crea una única cola para
/// el consumidor y encamina ambos tipos de mensaje a ella. Separarlos en dos clases duplicaría
/// la infraestructura sin ninguna ventaja, porque el estado que tocan es el mismo.
/// </summary>
public sealed class AnalysisResultConsumer(JobRegistry registry, ILogger<AnalysisResultConsumer> logger)
    : IConsumer<TelemetryAnalysisCompleted>, IConsumer<TelemetryAnalysisFailed>
{
    public Task Consume(ConsumeContext<TelemetryAnalysisCompleted> context)
    {
        registry.Complete(context.Message);

        ApiLog.JobResultReceived(logger, context.Message.JobId, "completed", context.Message.WorkerInstance);

        // Task.CompletedTask y no async: el método no espera a nada, y marcarlo async solo
        // añadiría una máquina de estados para no usarla.
        return Task.CompletedTask;
    }

    public Task Consume(ConsumeContext<TelemetryAnalysisFailed> context)
    {
        registry.Fail(context.Message);

        ApiLog.JobResultReceived(logger, context.Message.JobId, "failed", null);

        // El contador de trabajos en vuelo también baja en el camino de fallo; si solo bajara
        // en el de éxito, la métrica crecería sin techo cada vez que algo fallase.
        DotNetLab.ServiceDefaults.LabTelemetry.JobsInFlight.Add(-1);
        DotNetLab.ServiceDefaults.LabTelemetry.JobsCompleted.Add(
            1, new KeyValuePair<string, object?>("result", "failure"));

        return Task.CompletedTask;
    }
}
