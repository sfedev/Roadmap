namespace DotNetLab.Contracts;

/// <summary>Lectura de un sensor: tipo de dominio usado en las demos de records y patrones.</summary>
// Record posicional: 4 líneas de código que equivalen a ~60 de una clase POCO clásica.
public sealed record SensorReading(
    // Identificador del sensor emisor.
    string SensorId,
    // Valor medido (temperatura en °C para la demo).
    double Value,
    // Nivel de batería en porcentaje, útil para patrones relacionales.
    int BatteryPercent,
    // Instante de la lectura en UTC.
    DateTimeOffset TakenAt);

/// <summary>Salida de la demo de records: igualdad por valor, 'with' y deconstrucción.</summary>
public sealed record RecordsDemoResponse(
    // Instancia original serializada como texto legible.
    string Original,
    // Copia creada con la expresión 'with' (mutación no destructiva).
    string Mutated,
    // Resultado de original == copiaIdéntica: true, porque compara valores, no referencias.
    bool ValueEqualityHolds,
    // Resultado de ReferenceEquals: false, porque 'with' crea un objeto nuevo en el Heap.
    bool ReferenceEqualityHolds,
    // HashCode del original: se genera a partir de todos los campos posicionales.
    int OriginalHashCode,
    // HashCode del clon por valor: idéntico al anterior, requisito del contrato Equals/GetHashCode.
    int CloneHashCode,
    // Explicación corta ya redactada en servidor para la tarjeta de la UI.
    string Insight);

/// <summary>Resultado de clasificar una lectura mediante pattern matching compuesto.</summary>
public sealed record PatternMatchResponse(
    // Entrada evaluada, devuelta para que la UI muestre qué se clasificó.
    SensorReading Input,
    // Etiqueta de severidad resultante (Critical, Warning, Nominal...).
    string Severity,
    // Patrón concreto que hizo match: el valor pedagógico del endpoint.
    string MatchedPattern,
    // Rama del switch que se ejecutó, numerada para seguirla en el snippet.
    int BranchIndex,
    // Resultado de aplicar list patterns sobre una serie de valores.
    string SeriesDiagnosis);
