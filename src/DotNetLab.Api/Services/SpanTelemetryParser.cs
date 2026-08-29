using System.Diagnostics;
using System.Globalization;
using DotNetLab.Api.Infrastructure;
using DotNetLab.Contracts;

namespace DotNetLab.Api.Services;

/// <summary>
/// Ruta rápida: recorre el buffer con slices de <see cref="ReadOnlySpan{T}"/> y no asigna
/// ni un solo objeto en el Heap gestionado. Un slice es (referencia + longitud) en la pila.
/// </summary>
// Primary constructor (C# 12+): el parámetro 'logger' se captura como campo privado
// sintetizado por el compilador; no hay que declarar el campo ni escribir el constructor.
public sealed class SpanTelemetryParser(ILogger<SpanTelemetryParser> logger) : ITelemetryParser
{
    // Clave de registro en DI; la UI la usa para pedir explícitamente esta implementación.
    public string Key => "span";

    // Descripción mostrada en la tarjeta del frontend junto a las métricas.
    public string Description =>
        "Recorre el buffer con slices de pila (ReadOnlySpan<char>) y parsea sin materializar strings.";

    public ParseStrategyResult Parse(ReadOnlySpan<char> payload)
    {
        // Contador de Gen0 ANTES de empezar: su delta revela la presión real sobre el GC.
        var gen0Before = GC.CollectionCount(0);
        // Bytes asignados por ESTE hilo: aísla la medición del ruido de otras peticiones.
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        // Timestamp monotónico de alta resolución; inmune a cambios de hora del sistema.
        var start = Stopwatch.GetTimestamp();

        // Acumuladores en pila: son structs, viven en el marco de este método.
        double sum = 0;
        var rows = 0;

        // Ventana sobre el MISMO buffer original; reasignarla mueve punteros, no copia datos.
        var remaining = payload;

        while (!remaining.IsEmpty)
        {
            // IndexOf sobre span está vectorizado (SIMD): compara 16-32 chars por instrucción.
            var newLine = remaining.IndexOf('\n');
            // Sin salto de línea, la última línea es todo lo que queda del buffer.
            var line = newLine >= 0 ? remaining[..newLine] : remaining;
            // Avanza la ventana detrás del '\n'; 'default' (span vacío) corta el bucle.
            remaining = newLine >= 0 ? remaining[(newLine + 1)..] : default;

            // Descarta líneas vacías (p. ej. el '\r' suelto o el final del documento).
            if (line.IsEmpty || line is ['\r']) continue;
            // Formato esperado: sensorId;timestamp;valor -> el valor va tras el último ';'.
            var lastSeparator = line.LastIndexOf(';');
            if (lastSeparator < 0) continue;

            // Slice del campo numérico: sigue apuntando al buffer original, cero copias.
            var valueSpan = line[(lastSeparator + 1)..].Trim();
            // double.TryParse tiene sobrecarga para span: parsea sin crear un string intermedio.
            // InvariantCulture fija el '.' decimal sea cual sea la cultura del host.
            if (double.TryParse(valueSpan, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            {
                sum += value;
                rows++;
            }
        }

        // GetElapsedTime convierte ticks a TimeSpan sin instanciar un objeto Stopwatch.
        var elapsed = Stopwatch.GetElapsedTime(start);
        // Delta de bytes: en esta ruta se queda en ~0 salvo el boxing del logging estructurado.
        var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        var gen0 = GC.CollectionCount(0) - gen0Before;

        // Log estructurado vía delegado generado: sin boxing y sin evaluar argumentos
        // si el nivel Information está apagado (ver ApiLog).
        ApiLog.ParserExecuted(logger, "span", rows, elapsed.TotalMicroseconds, allocated);

        // División protegida: sin filas la media es 0 y no NaN, que rompería el JSON.
        var average = rows == 0 ? 0 : sum / rows;

        return new ParseStrategyResult(
            Strategy: "Span<T> (zero-allocation)",
            RowsParsed: rows,
            // Redondeo a 4 decimales: la UI compara la igualdad numérica entre estrategias.
            AverageValue: Math.Round(average, 4),
            ElapsedMicroseconds: Math.Round(elapsed.TotalMicroseconds, 2),
            AllocatedBytes: allocated,
            Gen0Collections: gen0);
    }
}
