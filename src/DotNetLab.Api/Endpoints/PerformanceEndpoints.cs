using DotNetLab.Api.Infrastructure;
using DotNetLab.Analysis;
using DotNetLab.Api.Services;
using DotNetLab.Contracts;
using Microsoft.AspNetCore.Http.HttpResults;

namespace DotNetLab.Api.Endpoints;

/// <summary>
/// Endpoints del módulo "Rendimiento y memoria". Ejecutan trabajo real y devuelven
/// métricas medidas en el propio proceso, no valores simulados.
/// </summary>
// static class + extension method: el patrón idiomático para trocear Program.cs
// sin introducir controladores ni reflexión.
public static class PerformanceEndpoints
{
    /// <summary>Registra el subgrupo /performance dentro del grupo /api recibido.</summary>
    public static RouteGroupBuilder MapPerformanceEndpoints(this RouteGroupBuilder api)
    {
        // MapGroup crea un prefijo común: metadatos y filtros se aplican a todo el subárbol.
        var group = api.MapGroup("/performance").WithTags("Performance");

        // Delegado con nombre en vez de lambda: el handler es testeable de forma aislada.
        group.MapGet("/span-demo", SpanDemo)
            // Nombre de operación usado por OpenAPI y por LinkGenerator.
            .WithName("SpanDemo")
            .WithSummary("Compara ReadOnlySpan<char> contra string.Split sobre el mismo buffer.");

        return api;
    }

    /// <summary>
    /// Genera un documento de telemetría y lo parsea dos veces, una por estrategia,
    /// midiendo tiempo, bytes asignados y recolecciones de Gen0 en cada pasada.
    /// </summary>
    // Results<T1,T2> declara TODAS las respuestas posibles: OpenAPI las documenta solo
    // y el compilador impide devolver un tipo no declarado.
    private static Results<Ok<SpanDemoResponse>, ProblemHttpResult> SpanDemo(
        // Parámetro de query opcional; ASP.NET Core lo enlaza por nombre automáticamente.
        int? rows,
        // Servicios inyectados por el contenedor directamente en la firma del handler.
        TelemetrySampleGenerator generator,
        ITelemetryParserFactory factory,
        ILogger<TelemetrySampleGenerator> logger)
    {
        // 25.000 filas por defecto: ~1,2 MB, suficiente para que la diferencia sea evidente.
        var rowCount = TelemetrySampleGenerator.ClampRows(rows ?? 25_000);

        // Generación FUERA de la zona medida: su coste no se imputa a ninguna estrategia.
        var payload = generator.Generate(rowCount);

        // Resolución por clave a través de la fábrica; el '!' es seguro porque son claves fijas.
        var spanParser = factory.Resolve("span")!;
        var naiveParser = factory.Resolve("naive")!;

        // Calentamiento con un trozo pequeño: fuerza al JIT a compilar ambos métodos ANTES de
        // medir. Sin esto, la primera estrategia cargaría con el coste de compilación, que se
        // contabiliza como memoria asignada del hilo.
        // Warmup y no Parse: recorre la misma ruta pero sin publicar métricas, para no meter en
        // los histogramas de OpenTelemetry muestras de un buffer de 8 KB que nadie pidió parsear.
        var warmup = payload.AsSpan(0, Math.Min(payload.Length, 8_192));
        spanParser.Warmup(warmup);
        naiveParser.Warmup(warmup);

        // Medición real. AsSpan() no copia: entrega una vista sobre el string existente.
        var spanResult = spanParser.Parse(payload);
        var naiveResult = naiveParser.Parse(payload);

        // Si el span no asignó nada, el cociente no existe: se devuelve null en vez de inventar
        // un número gigante. La UI lo interpreta como "infinito / sin asignaciones".
        double? allocationRatio = spanResult.AllocatedBytes <= 0
            ? null
            : (double)naiveResult.AllocatedBytes / spanResult.AllocatedBytes;

        var speedUpRatio = spanResult.ElapsedMicroseconds <= 0
            ? 1
            : naiveResult.ElapsedMicroseconds / spanResult.ElapsedMicroseconds;

        // Coherencia: ambas estrategias deben leer las mismas filas y dar la misma media.
        // Si no coinciden, el benchmark no es válido y devolverlo como éxito sería mentir.
        if (spanResult.RowsParsed != naiveResult.RowsParsed)
        {
            ApiLog.BenchmarkMismatch(logger, spanResult.RowsParsed, naiveResult.RowsParsed);

            return TypedResults.Problem(
                title: "Benchmark inconsistente",
                detail: "Las dos estrategias parsearon un número distinto de filas.",
                statusCode: StatusCodes.Status500InternalServerError);
        }

        var response = new SpanDemoResponse(
            RowCount: rowCount,
            PayloadChars: payload.Length,
            Span: spanResult,
            Naive: naiveResult,
            // Math.Round sobre un double? propaga el null: no hace falta comprobarlo antes.
            AllocationRatio: allocationRatio is { } ratio ? Math.Round(ratio, 1) : null,
            SpeedUpRatio: Math.Round(speedUpRatio, 2),
            // El veredicto se redacta distinto según haya o no cociente definido; así el texto
            // que pinta la UI nunca contiene un "9740344x" sin sentido.
            Verdict: allocationRatio is { } r
                ? $"Span asignó {Math.Round(r, 1)}x menos memoria y fue " +
                  $"{Math.Round(speedUpRatio, 2)}x más rápido sobre {rowCount:N0} filas."
                : $"Span no asignó NADA en el Heap (0 B) frente a {naiveResult.AllocatedBytes / 1024.0 / 1024.0:F2} MB " +
                  $"de string.Split, y además fue {Math.Round(speedUpRatio, 2)}x más rápido sobre {rowCount:N0} filas.");

        // TypedResults.Ok<T> es la variante fuertemente tipada de Results.Ok: sin boxing del resultado.
        return TypedResults.Ok(response);
    }
}
