using System.Diagnostics;
using System.Globalization;
using DotNetLab.Contracts;
using DotNetLab.ServiceDefaults;
using Microsoft.Extensions.Logging;

namespace DotNetLab.Analysis;

/// <summary>
/// Ruta "de manual": string.Split. Cada Split crea un array más un string por fragmento,
/// todos en el Heap de Gen0. Es correcta y legible, pero multiplica el trabajo del GC.
/// </summary>
public sealed class NaiveTelemetryParser(ILogger<NaiveTelemetryParser> logger) : ITelemetryParser
{
    // Clave de registro en DI, simétrica a la del parser de spans.
    public string Key => "naive";

    // Descripción mostrada en la UI para explicar por qué esta ruta pierde la comparativa.
    public string Description =>
        "Materializa el buffer en string y usa string.Split: un array y N strings por línea en el Heap.";

    /// <summary>Ejecuta la ruta real sin medir: fuerza la compilación JIT del bucle.</summary>
    public void Warmup(ReadOnlySpan<char> payload) => _ = ParseCore(payload);

    public ParseStrategyResult Parse(ReadOnlySpan<char> payload)
    {
        // Mismo patrón que la ruta rápida: el span se abre fuera de la ventana medida para que
        // la instrumentación no altere la cifra que se está comparando.
        using var activity = LabTelemetry.Source.StartActivity("telemetry.parse.naive", ActivityKind.Internal);

        var gen0Before = GC.CollectionCount(0);
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var start = Stopwatch.GetTimestamp();

        var (rows, sum) = ParseCore(payload);

        var elapsed = Stopwatch.GetElapsedTime(start);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        var gen0 = GC.CollectionCount(0) - gen0Before;

        // Las mismas tres métricas que la ruta rápida, con la etiqueta 'strategy' distinta: es
        // lo que permite superponer ambas series en la misma gráfica de Grafana.
        LabTelemetry.ParseOperations.Add(1, new KeyValuePair<string, object?>("strategy", Key));
        LabTelemetry.ParseAllocatedBytes.Record(allocated, new KeyValuePair<string, object?>("strategy", Key));
        LabTelemetry.ParseDuration.Record(elapsed.TotalSeconds, new KeyValuePair<string, object?>("strategy", Key));

        activity?.SetTag("lab.parse.strategy", Key);
        activity?.SetTag("lab.parse.rows", rows);
        activity?.SetTag("lab.parse.allocated_bytes", allocated);

        // Mismo delegado generado que la ruta span: las métricas quedan comparables en el log.
        AnalysisLog.ParserExecuted(logger, "naive", rows, elapsed.TotalMicroseconds, allocated);

        var average = rows == 0 ? 0 : sum / rows;

        return new ParseStrategyResult(
            Strategy: "string.Split (heap)",
            RowsParsed: rows,
            AverageValue: Math.Round(average, 4),
            ElapsedMicroseconds: Math.Round(elapsed.TotalMicroseconds, 2),
            AllocatedBytes: allocated,
            Gen0Collections: gen0);
    }

    /// <summary>El parseo propiamente dicho, sin instrumentación de ninguna clase.</summary>
    private static (int Rows, double Sum) ParseCore(ReadOnlySpan<char> payload)
    {
        // Primera asignación grande: copia TODO el buffer al Heap (2 bytes por char en UTF-16).
        // Es el peaje de entrada de cualquier API que trabaje con string en vez de span.
        var text = payload.ToString();

        // Segunda asignación: un string[] más un string nuevo por cada línea del documento.
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        double sum = 0;
        var rows = 0;

        foreach (var line in lines)
        {
            // Tercera asignación, esta vez por iteración: otro array y otro string por campo.
            var parts = line.Split(';');
            // Descarta líneas mal formadas con la misma semántica que la ruta de spans.
            if (parts.Length < 3) continue;

            // Índice desde el final (^1): mismo campo que el LastIndexOf de la ruta span.
            if (double.TryParse(parts[^1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            {
                sum += value;
                rows++;
            }
        }

        return (rows, sum);
    }
}
