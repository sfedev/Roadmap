using DotNetLab.Contracts;

namespace DotNetLab.Api.Services;

/// <summary>
/// Demos ejecutables de las novedades del lenguaje: records (igualdad por valor,
/// mutación no destructiva) y pattern matching compuesto (property, relational,
/// logical y list patterns).
/// </summary>
// Primary constructor + TimeProvider: el servicio no depende de DateTimeOffset.UtcNow,
// así los tests pueden congelar el reloj y el resultado es determinista.
public sealed class LanguageShowcaseService(TimeProvider timeProvider)
{
    /// <summary>Ejecuta y explica la semántica de valor de los records.</summary>
    public RecordsDemoResponse RunRecordsDemo()
    {
        // Instancia base. Al ser un record posicional, el compilador ya generó
        // constructor, Equals, GetHashCode, ToString y Deconstruct.
        var original = new SensorReading("sensor-0042", 21.5, 88, timeProvider.GetUtcNow());

        // 'with' = mutación no destructiva: copia superficial con los campos indicados cambiados.
        // El objeto original queda intacto, que es lo que hace seguro compartirlo entre hilos.
        var mutated = original with { Value = 97.3, BatteryPercent = 11 };

        // Clon con los MISMOS valores pero creado por separado: sirve para probar la igualdad.
        var clone = original with { };

        // == en un record compara campo a campo (igualdad estructural), no direcciones de memoria.
        var valueEquality = original == clone;

        // ReferenceEquals sigue siendo false: 'with' asignó un objeto NUEVO en el Heap.
        // Esta pareja de booleanos es justo la confusión que la tarjeta de la UI aclara.
        var referenceEquality = ReferenceEquals(original, clone);

        // Deconstrucción posicional: el record expone Deconstruct sin escribirlo a mano.
        var (sensorId, value, battery, _) = original;

        return new RecordsDemoResponse(
            // ToString() generado imprime "SensorReading { SensorId = ..., Value = ... }".
            Original: original.ToString(),
            Mutated: mutated.ToString(),
            ValueEqualityHolds: valueEquality,
            ReferenceEqualityHolds: referenceEquality,
            // Los hash coinciden porque se derivan de los mismos valores de campo.
            OriginalHashCode: original.GetHashCode(),
            CloneHashCode: clone.GetHashCode(),
            Insight: $"Deconstruido a ({sensorId}, {value}, {battery}%). " +
                     "== compara valores (true) mientras ReferenceEquals compara punteros (false): " +
                     "'with' crea un objeto nuevo, no muta el original.");
    }

    /// <summary>
    /// Clasifica una lectura combinando varios tipos de patrón en un único switch expression.
    /// El switch es exhaustivo, así que el compilador garantiza que siempre hay una rama.
    /// </summary>
    public PatternMatchResponse Classify(SensorReading reading, double[] series)
    {
        // Switch expression sobre tuplas de salida: cada rama devuelve (severidad, patrón, índice).
        var (severity, pattern, branch) = reading switch
        {
            // 1. Property pattern + relational patterns combinados con 'and' implícito
            //    (varias propiedades en el mismo patrón se evalúan en conjunción).
            { Value: > 90, BatteryPercent: < 15 } =>
                ("Critical", "{ Value: > 90, BatteryPercent: < 15 }", 1),

            // 2. Patrón lógico 'or' con dos rangos relacionales: fuera del rango físico plausible.
            { Value: > 90 or < -40 } =>
                ("Warning", "{ Value: > 90 or < -40 }", 2),

            // 3. Rango cerrado con 'and': la batería está baja pero aún no es crítica.
            { BatteryPercent: >= 5 and <= 20 } =>
                ("Warning", "{ BatteryPercent: >= 5 and <= 20 }", 3),

            // 4. List pattern sobre el string del identificador: 'sensor-' seguido de lo que sea.
            //    Un string es indexable y contable, así que admite patrones de lista.
            { SensorId: ['s', 'e', 'n', 's', 'o', 'r', '-', ..] , Value: >= 15 and <= 30 } =>
                ("Nominal", "{ SensorId: ['s','e','n','s','o','r','-', ..], Value: >= 15 and <= 30 }", 4),

            // 5. Patrón de descarte con 'when': la guarda evalúa lógica que no cabe en un patrón.
            var r when r.TakenAt > timeProvider.GetUtcNow().AddMinutes(1) =>
                ("Invalid", "var r when r.TakenAt > ahora + 1min (lectura del futuro)", 5),

            // 6. Rama por defecto: obligatoria para que el switch sea exhaustivo.
            _ => ("Unknown", "_ (descarte)", 6)
        };

        return new PatternMatchResponse(
            Input: reading,
            Severity: severity,
            MatchedPattern: pattern,
            BranchIndex: branch,
            SeriesDiagnosis: DiagnoseSeries(series));
    }

    /// <summary>Diagnostica una serie temporal usando exclusivamente list patterns.</summary>
    // static porque no toca estado de instancia: el compilador puede evitar pasar 'this'.
    private static string DiagnoseSeries(double[] series) => series switch
    {
        // Patrón de lista vacía: equivale a Length == 0 pero sin salirse del switch.
        [] => "Serie vacía: no hay nada que diagnosticar.",

        // Un único elemento capturado con 'var' dentro del propio patrón de lista.
        [var only] => $"Una sola muestra ({only}): insuficiente para inferir tendencia.",

        // Slice pattern '..': captura primero y último ignorando todo lo del medio.
        // La guarda 'when' compara ambos extremos, algo que el patrón solo no puede expresar.
        [var first, .., var last] when last - first > 10 =>
            $"Tendencia ascendente brusca: {first} -> {last} (+{Math.Round(last - first, 2)}).",

        [var first, .., var last] when first - last > 10 =>
            $"Caída brusca: {first} -> {last} ({Math.Round(last - first, 2)}).",

        // Patrón de lista con longitud mínima: al menos dos elementos, resto irrelevante.
        [_, _, ..] => "Serie estable dentro de la banda esperada.",

        // Rama final exigida por la exhaustividad del switch.
        _ => "Forma de serie no contemplada."
    };
}
