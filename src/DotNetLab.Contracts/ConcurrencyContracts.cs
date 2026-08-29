namespace DotNetLab.Contracts;

/// <summary>Evento individual que viaja por el Channel desde el productor al consumidor.</summary>
// 'readonly record struct': vive en la pila del stream, sin asignación en Heap por evento.
public readonly record struct ChannelEvent(
    // Orden de emisión: permite al frontend detectar reordenamientos o pérdidas.
    int Sequence,
    // Etapa del pipeline que produjo el evento (produced/transformed/consumed).
    string Stage,
    // Carga útil textual simulada del evento.
    string Payload,
    // Milisegundos desde el arranque del pipeline: sirve para dibujar la línea temporal.
    double OffsetMs,
    // Ocupación del canal en el instante de emitir: hace visible el backpressure.
    int QueueDepth);

/// <summary>Resumen agregado de una ejecución completa del pipeline de Channels.</summary>
public sealed record ChannelSummary(
    // Eventos escritos por el productor.
    int Produced,
    // Eventos leídos por el consumidor; debe coincidir con Produced si nadie se perdió.
    int Consumed,
    // Capacidad del canal acotado: el número que provoca (o no) el backpressure.
    int Capacity,
    // Veces que el productor tuvo que esperar espacio libre: la prueba del backpressure.
    int BackpressureWaits,
    // Duración total del pipeline en milisegundos.
    double TotalMs,
    // Modo de espera aplicado al llenarse el canal (Wait, DropOldest, ...).
    string FullMode);
