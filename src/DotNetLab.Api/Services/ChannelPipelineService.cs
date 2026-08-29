using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using DotNetLab.Api.Infrastructure;
using DotNetLab.Contracts;

namespace DotNetLab.Api.Services;

/// <summary>
/// Pipeline productor -> transformador -> consumidor construido sobre
/// <see cref="System.Threading.Channels"/>. Es el equivalente asíncrono de una
/// BlockingCollection: en vez de bloquear hilos del ThreadPool, suspende continuaciones.
/// </summary>
// Primary constructor con TimeProvider: inyectarlo (en vez de usar Task.Delay directo)
// permite sustituir el reloj en tests y ejecutar el pipeline sin esperas reales.
public sealed class ChannelPipelineService(ILogger<ChannelPipelineService> logger, TimeProvider timeProvider)
{
    // Entrada de caché inmutable: clave y valor viajan juntos en un solo objeto, así una
    // publicación atómica de la referencia no puede dejar clave y resumen descoordinados.
    private sealed record SummaryCache(int Events, int Capacity, ChannelSummary Summary);

    // Caché del último resumen calculado. Es el corazón de la demo de ValueTask:
    // si la respuesta ya está en memoria se devuelve SIN pasar por la máquina de estados async.
    // El servicio es Singleton, así que varias peticiones concurrentes leen este campo a la vez.
    private SummaryCache? _cache;

    // Límites defensivos: acotan lo que un cliente puede pedir a un contenedor compartido.
    // El stream se limita a 200 porque cada evento lleva un retardo artificial y viaja por la red.
    public const int MaxEvents = 200;
    // El resumen no tiene retardos ni serializa cada evento, así que admite dos órdenes de
    // magnitud más: es donde se aprecia el throughput real de un canal acotado.
    public const int MaxSummaryEvents = 50_000;
    public const int MaxCapacity = 64;

    /// <summary>
    /// Ejecuta el pipeline y devuelve cada evento en cuanto está listo.
    /// IAsyncEnumerable + [EnumeratorCancellation] hacen que ASP.NET Core escriba el JSON
    /// en streaming: el navegador recibe los primeros bytes sin esperar al último evento.
    /// </summary>
    public async IAsyncEnumerable<ChannelEvent> StreamAsync(
        int eventCount,
        int capacity,
        int producerDelayMs,
        int consumerDelayMs,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        // Normaliza la entrada antes de reservar nada: nunca confiar en la query string.
        eventCount = Math.Clamp(eventCount, 1, MaxEvents);
        capacity = Math.Clamp(capacity, 1, MaxCapacity);

        // Canal ACOTADO: su capacidad fija es lo que genera backpressure cuando se llena.
        var options = new BoundedChannelOptions(capacity)
        {
            // Wait = el productor espera espacio libre en vez de descartar eventos (sin pérdida).
            FullMode = BoundedChannelFullMode.Wait,
            // Un único productor: el canal aplica optimizaciones sin sincronización extra.
            SingleWriter = true,
            // Un único consumidor (este iterador): habilita la ruta rápida de lectura.
            SingleReader = true
        };

        // Canal 1: eventos crudos tal y como los emite el productor.
        var raw = Channel.CreateBounded<ChannelEvent>(options);
        // Canal 2: eventos ya transformados; encadenar canales evita compartir estado mutable.
        var processed = Channel.CreateBounded<ChannelEvent>(options);

        // Origen temporal común a las tres etapas para que los offsets sean comparables.
        var startTimestamp = Stopwatch.GetTimestamp();
        // Contador de esperas por canal lleno; se lee al final para el log de diagnóstico.
        var backpressureWaits = 0;

        // Etapa 1: productor. Task.Run lo saca del hilo actual para que el consumidor avance en paralelo.
        var producer = Task.Run(async () =>
        {
            try
            {
                for (var i = 1; i <= eventCount; i++)
                {
                    var evt = new ChannelEvent(
                        Sequence: i,
                        Stage: "produced",
                        Payload: $"telemetry-packet-{i:D3}",
                        // Offset desde el arranque del pipeline, en ms con decimales.
                        OffsetMs: Math.Round(Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds, 2),
                        // Profundidad de cola ANTES de escribir: si iguala capacity, viene espera.
                        QueueDepth: raw.Reader.Count);

                    // TryWrite es la ruta rápida y síncrona: si hay hueco, escribe sin await.
                    if (!raw.Writer.TryWrite(evt))
                    {
                        // Canal lleno: aquí es donde el productor se frena. Interlocked porque
                        // el contador lo lee otro hilo al terminar el pipeline.
                        Interlocked.Increment(ref backpressureWaits);
                        // WaitToWriteAsync suspende la continuación; NO bloquea el hilo del pool.
                        while (await raw.Writer.WaitToWriteAsync(cancellationToken).ConfigureAwait(false))
                        {
                            if (raw.Writer.TryWrite(evt)) break;
                        }
                    }

                    // Retardo opcional del productor para simular una fuente lenta.
                    if (producerDelayMs > 0)
                    {
                        // La sobrecarga con TimeProvider exige TimeSpan: el reloj inyectado es
                        // el que decide cuánto dura realmente la espera (falso en tests).
                        await Task.Delay(TimeSpan.FromMilliseconds(producerDelayMs), timeProvider, cancellationToken)
                            .ConfigureAwait(false);
                    }
                }
            }
            finally
            {
                // Complete() señala "no habrá más datos": sin esto el consumidor esperaría para siempre.
                // Va en finally para que una excepción tampoco deje el pipeline colgado.
                raw.Writer.Complete();
            }
        }, cancellationToken);

        // Etapa 2: transformador. Lee de 'raw', enriquece y escribe en 'processed'.
        var transformer = Task.Run(async () =>
        {
            try
            {
                // ReadAllAsync expone el canal como IAsyncEnumerable y termina solo al completarse.
                await foreach (var evt in raw.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
                {
                    // 'with' sobre un record struct: copia con un campo cambiado, sin tocar el original.
                    var transformed = evt with
                    {
                        Stage = "transformed",
                        Payload = evt.Payload.ToUpperInvariant(),
                        OffsetMs = Math.Round(Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds, 2)
                    };

                    // WriteAsync devuelve ValueTask: si hay hueco completa síncronamente sin asignar Task.
                    await processed.Writer.WriteAsync(transformed, cancellationToken).ConfigureAwait(false);
                }
            }
            finally
            {
                // Propaga la señal de fin al segundo canal para cerrar el iterador de abajo.
                processed.Writer.Complete();
            }
        }, cancellationToken);

        // Etapa 3: consumidor, que es este propio iterador. Cada yield viaja ya al cliente.
        await foreach (var evt in processed.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            // Retardo del consumidor: es lo que hace visible el backpressure en la UI.
            if (consumerDelayMs > 0)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(consumerDelayMs), timeProvider, cancellationToken)
                    .ConfigureAwait(false);
            }

            // Sella el evento con el instante de consumo y la ocupación pendiente del canal.
            yield return evt with
            {
                Stage = "consumed",
                OffsetMs = Math.Round(Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds, 2),
                QueueDepth = raw.Reader.Count
            };
        }

        // Await final de las etapas: si alguna lanzó, la excepción sale aquí y no se pierde.
        await Task.WhenAll(producer, transformer).ConfigureAwait(false);

        ApiLog.PipelineCompleted(logger, eventCount, capacity, backpressureWaits);
    }

    /// <summary>
    /// Resumen agregado del pipeline. Devuelve <see cref="ValueTask{TResult}"/> y no Task
    /// porque la ruta cacheada termina de forma síncrona: en ese caso no se asigna ningún
    /// objeto Task en el Heap, que es exactamente el escenario para el que existe ValueTask.
    /// </summary>
    public ValueTask<ChannelSummary> GetSummaryAsync(int eventCount, int capacity, CancellationToken cancellationToken)
    {
        eventCount = Math.Clamp(eventCount, 1, MaxSummaryEvents);
        capacity = Math.Clamp(capacity, 1, MaxCapacity);

        // Volatile.Read garantiza que se lee la referencia publicada por otro hilo, no una copia
        // obsoleta cacheada en registro por el JIT.
        var snapshot = Volatile.Read(ref _cache);

        // Ruta rápida: el resultado ya existe para estos parámetros. Property pattern sobre el
        // record para comprobar clave y presencia en una sola expresión.
        // El constructor de ValueTask con un valor ya disponible NO toca el Heap.
        if (snapshot is { } hit && hit.Events == eventCount && hit.Capacity == capacity)
        {
            return new ValueTask<ChannelSummary>(hit.Summary with { FullMode = "Wait (resultado cacheado)" });
        }

        // Ruta lenta: solo aquí se paga la máquina de estados async y la asignación del Task.
        return new ValueTask<ChannelSummary>(ComputeSummaryAsync(eventCount, capacity, cancellationToken));
    }

    // Método async real, privado: separarlo mantiene la ruta rápida libre de state machine.
    private async Task<ChannelSummary> ComputeSummaryAsync(int eventCount, int capacity, CancellationToken cancellationToken)
    {
        var startTimestamp = Stopwatch.GetTimestamp();
        var backpressureWaits = 0;
        var consumed = 0;

        var channel = Channel.CreateBounded<int>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleWriter = true,
            SingleReader = true
        });

        // Productor sin retardos: mide el coste puro del canal, no el de los Task.Delay.
        var producer = Task.Run(async () =>
        {
            for (var i = 0; i < eventCount; i++)
            {
                if (!channel.Writer.TryWrite(i))
                {
                    Interlocked.Increment(ref backpressureWaits);
                    while (await channel.Writer.WaitToWriteAsync(cancellationToken).ConfigureAwait(false))
                    {
                        if (channel.Writer.TryWrite(i)) break;
                    }
                }
            }

            channel.Writer.Complete();
        }, cancellationToken);

        // Consumidor: cuenta lo recibido para verificar que no se perdió ningún evento.
        await foreach (var _ in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            consumed++;
        }

        await producer.ConfigureAwait(false);

        var summary = new ChannelSummary(
            Produced: eventCount,
            Consumed: consumed,
            Capacity: capacity,
            BackpressureWaits: backpressureWaits,
            TotalMs: Math.Round(Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds, 3),
            FullMode: "Wait (cálculo real)");

        // Publicación atómica: un único escritor de referencia deja clave y valor consistentes
        // para cualquier lector, sin necesidad de lock.
        Volatile.Write(ref _cache, new SummaryCache(eventCount, capacity, summary));

        return summary;
    }
}
