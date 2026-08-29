namespace DotNetLab.Contracts;

/// <summary>Resultado de una estrategia de parseo concreta (span o naive).</summary>
// 'record' de referencia: el compilador genera Equals/GetHashCode/ToString por valor.
// 'sealed' impide herencia y permite al JIT devirtualizar llamadas (menos indirecciones).
public sealed record ParseStrategyResult(
    // Nombre de la estrategia usada, para pintarlo en la tarjeta del frontend.
    string Strategy,
    // Filas realmente parseadas: confirma que ambas estrategias hicieron el mismo trabajo.
    int RowsParsed,
    // Media de los valores parseados: prueba de que el resultado numérico es idéntico.
    double AverageValue,
    // Microsegundos: la unidad ms pierde resolución en cargas pequeñas.
    double ElapsedMicroseconds,
    // Bytes asignados en el Heap durante la ejecución (medido con GC.GetAllocatedBytesForCurrentThread).
    long AllocatedBytes,
    // Recolecciones de Gen0 provocadas: la métrica que delata la presión sobre el GC.
    int Gen0Collections);

/// <summary>Respuesta comparativa del endpoint /api/performance/span-demo.</summary>
public sealed record SpanDemoResponse(
    // Filas sintéticas generadas para el benchmark.
    int RowCount,
    // Tamaño del buffer de texto en caracteres: contexto para interpretar las asignaciones.
    int PayloadChars,
    // Resultado de la ruta zero-allocation basada en ReadOnlySpan<char>.
    ParseStrategyResult Span,
    // Resultado de la ruta clásica string.Split, que sí materializa strings intermedios.
    ParseStrategyResult Naive,
    // Cuántas veces menos memoria asignó Span (naive / span). NULL significa "infinito":
    // la ruta con spans no asignó ni un byte, así que el cociente no está definido.
    double? AllocationRatio,
    // Cuántas veces más rápido fue Span (naive / span) en la misma máquina y proceso.
    double SpeedUpRatio,
    // Frase corta lista para mostrar en la UI sin lógica extra en el cliente.
    string Verdict);
