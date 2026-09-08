using System.Diagnostics;
using DotNetLab.Contracts;
using DotNetLab.ServiceDefaults;
using MassTransit;
using Microsoft.Extensions.Logging;

namespace DotNetLab.Analysis;

/// <summary>
/// Consumidor del trabajo pesado: recibe una solicitud de análisis del bus, genera el buffer,
/// lo parsea con <see cref="ReadOnlySpan{T}"/> y publica el resultado.
///
/// Vive en la librería de dominio y no en el Worker por dos razones: es el mismo caso de uso
/// que ejecutan los endpoints síncronos, y así el proceso de la API puede alojarlo con el
/// transporte en memoria durante el desarrollo local, sin necesidad de levantar RabbitMQ.
/// </summary>
// IConsumer<T> es el contrato de MassTransit: un tipo, un mensaje. El registro por convención
// (AddConsumers) lo descubre solo, y el nombre de la cola sale del nombre de la clase.
public sealed class TelemetryAnalysisConsumer(
    ITelemetryParserFactory parserFactory,
    TelemetrySampleGenerator generator,
    TimeProvider timeProvider,
    ILogger<TelemetryAnalysisConsumer> logger)
    : IConsumer<TelemetryAnalysisRequested>
{
    public async Task Consume(ConsumeContext<TelemetryAnalysisRequested> context)
    {
        var message = context.Message;

        // Latencia de cola: cuánto esperó el mensaje entre publicarse y empezar a procesarse.
        // Es la métrica que dice si el Worker necesita más réplicas, y no se puede calcular
        // dentro del Worker sin el instante de publicación que viaja en el propio mensaje.
        var queueLatency = (timeProvider.GetUtcNow() - message.RequestedAt).TotalMilliseconds;

        // Span propio anidado bajo el span de consumo que crea MassTransit: en Jaeger la traza
        // enlaza la petición HTTP original, la publicación, el salto por RabbitMQ y este trabajo.
        using var activity = LabTelemetry.Source.StartActivity("telemetry.analysis.consume", ActivityKind.Consumer);
        activity?.SetTag("lab.job.id", message.JobId);
        activity?.SetTag("lab.job.rows", message.RowCount);
        activity?.SetTag("lab.job.strategy", message.Strategy);
        activity?.SetTag("lab.job.queue_latency_ms", queueLatency);

        var processingStart = Stopwatch.GetTimestamp();

        // Clave desconocida = mensaje inválido. NO se relanza: reintentarlo daría exactamente
        // el mismo error para siempre y acabaría en la cola de errores sin aportar nada.
        var parser = parserFactory.Resolve(message.Strategy);
        if (parser is null)
        {
            AnalysisLog.JobRejected(logger, message.JobId, message.Strategy);

            await context.Publish(new TelemetryAnalysisFailed(
                JobId: message.JobId,
                Reason: $"Estrategia '{message.Strategy}' no registrada.",
                // GetRetryAttempt() devuelve los reintentos que MassTransit ya había gastado.
                Attempts: context.GetRetryAttempt() + 1,
                FailedAt: timeProvider.GetUtcNow())).ConfigureAwait(false);

            // Marca el span como erróneo para que en Jaeger aparezca en rojo y sea filtrable.
            activity?.SetStatus(ActivityStatusCode.Error, "Estrategia desconocida");
            return;
        }

        // Se acota igual que en el endpoint HTTP: un mensaje también puede venir con basura,
        // y aquí no hay un modelo de binding que valide por nosotros.
        var rows = TelemetrySampleGenerator.ClampRows(message.RowCount);

        // La generación queda fuera de la ventana que mide el parser, igual que en la ruta HTTP.
        var payload = generator.Generate(rows);

        // Calentamiento, igual que en el endpoint síncrono. Sin él, el PRIMER trabajo de cada
        // proceso reportaba 7.840 bytes asignados y 12,3 ms; el segundo, 3.320 bytes y 3,3 ms;
        // el tercero, 0 bytes. Eso no lo asigna el parser: es el JIT por niveles compilando el
        // bucle (y aplicando reemplazo en pila), y su coste se imputa al hilo que lo ejecuta.
        parser.Warmup(payload.AsSpan(0, Math.Min(payload.Length, 8_192)));

        // AQUÍ está el trabajo pesado: el mismo parser con Span<T> que usa el endpoint síncrono.
        var result = parser.Parse(payload);

        var processingMs = Stopwatch.GetElapsedTime(processingStart).TotalMilliseconds;

        AnalysisLog.JobProcessed(logger, message.JobId, rows, processingMs, result.AllocatedBytes);

        // Publicar (y no enviar a una cola concreta) permite que N suscriptores independientes
        // reaccionen: la API actualiza su registro y el frontend notifica por SignalR, sin que
        // este consumidor sepa siquiera que existen.
        await context.Publish(new TelemetryAnalysisCompleted(
            JobId: message.JobId,
            Result: result,
            QueueLatencyMs: Math.Round(queueLatency, 2),
            ProcessingMs: Math.Round(processingMs, 2),
            // En Kubernetes es el nombre del pod: hace visible qué réplica atendió el trabajo.
            WorkerInstance: Environment.MachineName,
            CompletedAt: timeProvider.GetUtcNow())).ConfigureAwait(false);

        // Métrica de negocio: trabajos terminados por resultado, la que alimenta el SLO.
        LabTelemetry.JobsCompleted.Add(1, new KeyValuePair<string, object?>("result", "success"));
        // El contador de trabajos en vuelo baja aquí; subió al aceptarse en la API.
        LabTelemetry.JobsInFlight.Add(-1);
    }
}
