using System.Diagnostics;
using System.Globalization;
using DotNetLab.Contracts;
using DotNetLab.ServiceDefaults;
using Microsoft.Extensions.Logging;

namespace DotNetLab.Analysis;

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

    /// <summary>Ejecuta la ruta real sin medir: fuerza la compilación JIT del bucle.</summary>
    // El descarte '_' deja explícito que el resultado no interesa: solo importa que el método
    // quede compilado antes de la medición.
    public void Warmup(ReadOnlySpan<char> payload) => _ = ParseCore(payload);

    public ParseStrategyResult Parse(ReadOnlySpan<char> payload)
    {
        // El span se abre ANTES de tomar la instantánea de memoria: crear una Activity asigna,
        // y hacerlo dentro de la ventana medida contaminaría el resultado que el portal presume
        // de mantener en 0 bytes. Devuelve null si no hay ningún listener, y entonces no cuesta nada.
        using var activity = LabTelemetry.Source.StartActivity("telemetry.parse.span", ActivityKind.Internal);

        // Contador de Gen0 ANTES de empezar: su delta revela la presión real sobre el GC.
        var gen0Before = GC.CollectionCount(0);
        // Bytes asignados por ESTE hilo: aísla la medición del ruido de otras peticiones.
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        // Timestamp monotónico de alta resolución; inmune a cambios de hora del sistema.
        var start = Stopwatch.GetTimestamp();

        // Todo el trabajo ocurre en ParseCore, que es el método que Warmup deja compilado.
        var (rows, sum) = ParseCore(payload);

        // GetElapsedTime convierte ticks a TimeSpan sin instanciar un objeto Stopwatch.
        var elapsed = Stopwatch.GetElapsedTime(start);
        // Delta de bytes: en esta ruta se queda en 0. La ventana medida termina AQUÍ, así que
        // todo lo que viene después (métricas, tags, logs) ya no puede falsear la cifra.
        var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        var gen0 = GC.CollectionCount(0) - gen0Before;

        // Métricas OpenTelemetry: las mismas cifras que muestra el portal, pero agregadas y
        // consultables en Prometheus. La etiqueta 'strategy' permite comparar ambas rutas en
        // una única gráfica de Grafana.
        LabTelemetry.ParseOperations.Add(1, new KeyValuePair<string, object?>("strategy", Key));
        LabTelemetry.ParseAllocatedBytes.Record(allocated, new KeyValuePair<string, object?>("strategy", Key));
        LabTelemetry.ParseDuration.Record(elapsed.TotalSeconds, new KeyValuePair<string, object?>("strategy", Key));

        // Tags del span: en Jaeger permiten filtrar "muéstrame los parseos de más de 50.000 filas".
        // El '?' evita el trabajo cuando no hay listener y activity es null.
        activity?.SetTag("lab.parse.strategy", Key);
        activity?.SetTag("lab.parse.rows", rows);
        activity?.SetTag("lab.parse.allocated_bytes", allocated);

        // Log estructurado vía delegado generado: sin boxing y sin evaluar argumentos
        // si el nivel Information está apagado (ver AnalysisLog).
        AnalysisLog.ParserExecuted(logger, "span", rows, elapsed.TotalMicroseconds, allocated);

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

    /// <summary>El parseo propiamente dicho, sin instrumentación de ninguna clase.</summary>
    // static: no toca estado de instancia, así que el JIT no necesita pasar 'this'.
    // Devuelve una tupla de valor: dos primitivas en la pila, sin asignar nada en el Heap.
    private static (int Rows, double Sum) ParseCore(ReadOnlySpan<char> payload)
    {
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

        return (rows, sum);
    }
}
