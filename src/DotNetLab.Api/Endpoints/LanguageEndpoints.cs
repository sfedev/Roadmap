using System.Globalization;
using DotNetLab.Api.Services;
using DotNetLab.Contracts;
using Microsoft.AspNetCore.Http.HttpResults;

namespace DotNetLab.Api.Endpoints;

/// <summary>
/// Endpoints del módulo "Novedades de C#": records y pattern matching ejecutándose
/// de verdad sobre los datos que envía el usuario desde la UI.
/// </summary>
public static class LanguageEndpoints
{
    public static RouteGroupBuilder MapLanguageEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/language").WithTags("Language");

        group.MapGet("/records", RecordsDemo)
            .WithName("RecordsDemo")
            .WithSummary("Igualdad por valor, expresión 'with' y deconstrucción de records.");

        group.MapGet("/pattern-match", PatternMatch)
            .WithName("PatternMatch")
            .WithSummary("Clasifica una lectura con property, relational, logical y list patterns.");

        return api;
    }

    /// <summary>Ejecuta la demo de records. Sin parámetros: el resultado es determinista.</summary>
    private static Ok<RecordsDemoResponse> RecordsDemo(LanguageShowcaseService showcase)
        // Expression body: el handler solo delega en el servicio y envuelve la respuesta.
        => TypedResults.Ok(showcase.RunRecordsDemo());

    /// <summary>
    /// Clasifica una lectura construida a partir de la query string. Los parámetros llegan
    /// como primitivas para que el playground de la UI pueda tocarlos con sliders.
    /// </summary>
    private static Results<Ok<PatternMatchResponse>, BadRequest<string>> PatternMatch(
        // Identificador del sensor; el prefijo "sensor-" activa una rama concreta del switch.
        string? sensorId,
        double? value,
        int? battery,
        // Serie temporal en formato "1.0,2.5,9.7": se parsea con spans, sin string.Split.
        string? series,
        LanguageShowcaseService showcase,
        TimeProvider timeProvider)
    {
        // Rango físico defendible para un sensor de temperatura; fuera de él es entrada inválida.
        if (value is < -273.15 or > 1_000)
        {
            // BadRequest<string> está declarado en el tipo de retorno, así que el compilador lo acepta.
            return TypedResults.BadRequest("El valor debe estar entre -273,15 y 1000 grados.");
        }

        // Porcentaje de batería fuera de [0,100] no tiene sentido físico.
        if (battery is < 0 or > 100)
        {
            return TypedResults.BadRequest("La batería debe expresarse entre 0 y 100.");
        }

        var reading = new SensorReading(
            // '??' aplica el valor por defecto solo cuando el parámetro no vino en la query.
            SensorId: string.IsNullOrWhiteSpace(sensorId) ? "sensor-0042" : sensorId,
            Value: value ?? 21.5,
            BatteryPercent: battery ?? 88,
            TakenAt: timeProvider.GetUtcNow());

        return TypedResults.Ok(showcase.Classify(reading, ParseSeries(series)));
    }

    /// <summary>Parsea "1.0,2.5,9.7" recorriendo el texto con slices, sin string.Split.</summary>
    // Coherencia pedagógica: el mismo patrón de spans que enseña el módulo de rendimiento.
    private static double[] ParseSeries(string? raw)
    {
        // Serie por defecto: muestra una tendencia ascendente clara en la UI.
        if (string.IsNullOrWhiteSpace(raw)) return [18.2, 19.4, 21.0, 24.6, 31.8];

        // Cota superior: 64 valores bastan para la demo y acotan el array reservado.
        var buffer = new double[64];
        var count = 0;

        // Ventana sobre el string original; avanzar la ventana no copia caracteres.
        var remaining = raw.AsSpan();

        while (!remaining.IsEmpty && count < buffer.Length)
        {
            var comma = remaining.IndexOf(',');
            var token = comma >= 0 ? remaining[..comma] : remaining;
            remaining = comma >= 0 ? remaining[(comma + 1)..] : default;

            // Trim sobre span devuelve otro span: recorta espacios sin asignar un string nuevo.
            if (double.TryParse(token.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
            {
                buffer[count++] = parsed;
            }
        }

        // AsSpan(0, count).ToArray() devuelve solo la parte útil, descartando el resto del buffer.
        return buffer.AsSpan(0, count).ToArray();
    }
}
