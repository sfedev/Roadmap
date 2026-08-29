using DotNetLab.Api.Services;
using DotNetLab.Contracts;
using Microsoft.AspNetCore.Http.HttpResults;

namespace DotNetLab.Api.Endpoints;

/// <summary>
/// Endpoints del módulo "Concurrencia y asincronía": Channels con backpressure real
/// y respuesta en streaming, más el contraste Task/ValueTask.
/// </summary>
public static class ConcurrencyEndpoints
{
    public static RouteGroupBuilder MapConcurrencyEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/concurrency").WithTags("Concurrency");

        // Devuelve IAsyncEnumerable: ASP.NET Core lo serializa como array JSON en streaming.
        group.MapGet("/channel-stream", ChannelStream)
            .WithName("ChannelStream")
            .WithSummary("Pipeline productor/transformador/consumidor sobre System.Threading.Channels.");

        group.MapGet("/channel-summary", ChannelSummaryAsync)
            .WithName("ChannelSummary")
            .WithSummary("Resumen agregado del pipeline devuelto como ValueTask (ruta síncrona si está cacheado).");

        return api;
    }

    /// <summary>
    /// Emite cada evento en cuanto el consumidor lo procesa. El cliente ve llegar los
    /// elementos progresivamente porque no se materializa ninguna lista intermedia.
    /// </summary>
    // El tipo de retorno IAsyncEnumerable<T> activa la serialización perezosa del framework:
    // el JSON se escribe elemento a elemento sobre el body de la respuesta.
    private static IAsyncEnumerable<ChannelEvent> ChannelStream(
        // Parámetros de query con valores por defecto pensados para que el backpressure se vea.
        int? events,
        int? capacity,
        int? producerDelayMs,
        int? consumerDelayMs,
        ChannelPipelineService pipeline,
        // CancellationToken enlazado a la desconexión del cliente: si cierra la pestaña,
        // el pipeline se cancela y deja de consumir CPU del contenedor.
        CancellationToken cancellationToken)
        => pipeline.StreamAsync(
            // 24 eventos: suficientes para llenar varias veces un canal de 4 huecos.
            eventCount: events ?? 24,
            capacity: capacity ?? 4,
            // Productor instantáneo frente a consumidor lento: la receta del backpressure.
            producerDelayMs: producerDelayMs ?? 0,
            consumerDelayMs: consumerDelayMs ?? 25,
            cancellationToken: cancellationToken);

    /// <summary>
    /// Ejecuta el pipeline y devuelve solo el agregado. La firma async ValueTask permite
    /// que la segunda llamada con los mismos parámetros no asigne un Task en el Heap.
    /// </summary>
    // async ValueTask<Ok<T>>: el framework la await-ea igual que un Task, pero la ruta
    // cacheada del servicio completa síncronamente.
    private static async ValueTask<Ok<ChannelSummary>> ChannelSummaryAsync(
        int? events,
        int? capacity,
        ChannelPipelineService pipeline,
        CancellationToken cancellationToken)
    {
        // Await del ValueTask del servicio: si venía de caché, no hubo salto de hilo.
        var summary = await pipeline.GetSummaryAsync(events ?? 5_000, capacity ?? 16, cancellationToken);
        return TypedResults.Ok(summary);
    }
}
