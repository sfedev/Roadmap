using DotNetLab.Analysis;
using DotNetLab.Api.Infrastructure;
using DotNetLab.Api.Services;
using DotNetLab.Contracts;
using Microsoft.AspNetCore.Http.HttpResults;

namespace DotNetLab.Api.Endpoints;

/// <summary>
/// Endpoints del caso de uso asíncrono. La API no ejecuta el trabajo pesado: lo acepta,
/// lo publica en el bus y devuelve 202 con la ubicación donde consultarlo.
/// </summary>
public static class AnalysisEndpoints
{
    public static RouteGroupBuilder MapAnalysisEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/analysis").WithTags("Analysis");

        group.MapPost("/jobs", SubmitAsync)
            .WithName("SubmitAnalysisJob")
            .WithSummary("Acepta un análisis pesado, lo publica en el bus y responde 202 Accepted.");

        group.MapGet("/jobs/{jobId:guid}", GetJob)
            .WithName("GetAnalysisJob")
            .WithSummary("Estado de un trabajo concreto (respaldo por si se pierde la notificación).");

        group.MapGet("/jobs", GetRecentJobs)
            .WithName("GetRecentAnalysisJobs")
            .WithSummary("Últimos trabajos conocidos por esta réplica de la API.");

        return api;
    }

    /// <summary>
    /// Acepta el trabajo y responde de inmediato. El 202 es la respuesta correcta cuando el
    /// recurso todavía NO existe: prometer un 200 con el resultado obligaría a mantener la
    /// conexión abierta durante todo el procesamiento, que es justo lo que se quiere evitar.
    /// </summary>
    private static async Task<Results<Accepted<AnalysisJobAccepted>, BadRequest<string>, ProblemHttpResult>> SubmitAsync(
        // El cuerpo se enlaza al record del contrato; el binder valida el JSON por nosotros.
        AnalysisJobRequest request,
        IAnalysisJobPublisher publisher,
        ITelemetryParserFactory parserFactory,
        CancellationToken cancellationToken)
    {
        // Bus apagado: 503 con ProblemDetails y una explicación accionable. No es un error del
        // cliente (400) ni un fallo del servidor (500): es una capacidad no disponible ahora.
        if (!publisher.IsEnabled)
        {
            return TypedResults.Problem(
                title: "Bus de mensajes no disponible",
                detail: "El procesamiento asíncrono requiere el bus. Levanta la solución con " +
                        "`docker compose up` o usa Messaging__Transport=inmemory en local.",
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        // La estrategia se valida AQUÍ y no en el Worker: rechazar en el borde evita publicar
        // un mensaje que solo puede acabar en la cola de errores.
        var strategy = string.IsNullOrWhiteSpace(request.Strategy) ? "span" : request.Strategy;
        if (parserFactory.Resolve(strategy) is null)
        {
            return TypedResults.BadRequest(
                $"Estrategia '{strategy}' no registrada. Válidas: {string.Join(", ", parserFactory.AvailableKeys)}.");
        }

        var accepted = await publisher.PublishAsync(request.RowCount, strategy, cancellationToken);

        // Accepted<T> escribe la cabecera Location además del cuerpo: es lo que convierte el
        // 202 en una respuesta accionable en lugar de un "vale, ya veremos".
        return TypedResults.Accepted(accepted.StatusUrl, accepted);
    }

    /// <summary>Consulta el estado de un trabajo. Es el respaldo del canal de SignalR.</summary>
    private static Results<Ok<AnalysisJobSnapshot>, NotFound<string>> GetJob(Guid jobId, JobRegistry registry)
    {
        var snapshot = registry.Find(jobId);

        // 404 con explicación: el registro vive en memoria de la réplica, así que un id válido
        // atendido por OTRA réplica también da 404. Decirlo evita una hora de depuración ajena.
        return snapshot is null
            ? TypedResults.NotFound(
                $"El trabajo {jobId} no consta en esta réplica. El registro es en memoria: " +
                "si la API tiene varias réplicas, consulta el resultado por SignalR.")
            : TypedResults.Ok(snapshot);
    }

    /// <summary>Últimos trabajos, para que la UI pueda pintar un historial al recargar.</summary>
    private static Ok<IReadOnlyList<AnalysisJobSnapshot>> GetRecentJobs(int? take, JobRegistry registry)
        // Clamp defensivo: 'take' llega de la query string y podría pedir el registro entero.
        => TypedResults.Ok(registry.Recent(Math.Clamp(take ?? 10, 1, 50)));
}
