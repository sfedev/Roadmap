using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Reflection;

namespace DotNetLab.ServiceDefaults;

/// <summary>
/// Punto único donde se declaran la fuente de trazas y los instrumentos de métrica propios.
/// Centralizarlos evita el error clásico de crear un <see cref="Meter"/> nuevo por clase: cada
/// Meter es un recurso que hay que registrar en el MeterProvider, y uno sin registrar produce
/// métricas que simplemente no salen, sin ningún error visible.
/// </summary>
public static class LabTelemetry
{
    /// <summary>Nombre lógico del sistema; se usa como prefijo de todas las métricas.</summary>
    public const string SystemName = "dotnetlab";

    // Versión del ensamblado: viaja en el recurso OTel para poder correlacionar una regresión
    // de latencia con el despliegue exacto que la introdujo.
    private static readonly string Version =
        typeof(LabTelemetry).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? "0.0.0";

    /// <summary>
    /// Fuente de spans manuales. El nombre debe registrarse con AddSource en el TracerProvider;
    /// si no, los spans se crean pero nadie los escucha y desaparecen sin dejar rastro.
    /// </summary>
    public static readonly ActivitySource Source = new(SystemName, Version);

    /// <summary>Meter propio, registrado con AddMeter en el MeterProvider por el mismo motivo.</summary>
    public static readonly Meter Meter = new(SystemName, Version);

    // --- Instrumentos ---------------------------------------------------------------------
    // Se crean UNA vez como campos estáticos: crear un instrumento por operación filtra memoria
    // en el MeterProvider, que los mantiene vivos mientras exista el Meter.

    /// <summary>Cuántos parseos se han ejecutado, etiquetados por estrategia (span/naive).</summary>
    // Counter: solo crece. Prometheus lo expone como _total y calcula la tasa con rate().
    public static readonly Counter<long> ParseOperations = Meter.CreateCounter<long>(
        name: $"{SystemName}.parse.operations",
        unit: "{operation}",
        description: "Parseos de telemetría ejecutados, por estrategia.");

    /// <summary>Bytes asignados en el Heap por cada parseo.</summary>
    // Histogram y no Gauge: interesa la DISTRIBUCIÓN (p50, p95, p99), no el último valor.
    // La unidad "By" es la abreviatura UCUM que exige la convención semántica de OpenTelemetry.
    public static readonly Histogram<long> ParseAllocatedBytes = Meter.CreateHistogram<long>(
        name: $"{SystemName}.parse.allocated_bytes",
        unit: "By",
        description: "Bytes asignados en el Heap gestionado durante un parseo.");

    /// <summary>Duración de cada parseo.</summary>
    // Segundos, no milisegundos: OpenTelemetry normaliza las duraciones a segundos y las
    // herramientas de visualización asumen esa unidad al formatear los ejes.
    public static readonly Histogram<double> ParseDuration = Meter.CreateHistogram<double>(
        name: $"{SystemName}.parse.duration",
        unit: "s",
        description: "Duración de un parseo de telemetría.");

    /// <summary>Trabajos asíncronos aceptados por la API y publicados al bus.</summary>
    public static readonly Counter<long> JobsAccepted = Meter.CreateCounter<long>(
        name: $"{SystemName}.jobs.accepted",
        unit: "{job}",
        description: "Trabajos de análisis aceptados con 202 y publicados al bus de mensajes.");

    /// <summary>Trabajos terminados por el Worker, etiquetados por resultado.</summary>
    public static readonly Counter<long> JobsCompleted = Meter.CreateCounter<long>(
        name: $"{SystemName}.jobs.completed",
        unit: "{job}",
        description: "Trabajos procesados por el Worker, por resultado (success/failure).");

    /// <summary>Trabajos actualmente en vuelo.</summary>
    // UpDownCounter: sube al aceptar y baja al completar. Es la métrica correcta para una
    // cola de trabajo, y la que alimenta el autoescalado por longitud de cola en Kubernetes.
    public static readonly UpDownCounter<int> JobsInFlight = Meter.CreateUpDownCounter<int>(
        name: $"{SystemName}.jobs.in_flight",
        unit: "{job}",
        description: "Trabajos aceptados que aún no han terminado.");

    /// <summary>Intentos consumidos por las políticas de resiliencia, por resultado.</summary>
    public static readonly Counter<long> ResilienceAttempts = Meter.CreateCounter<long>(
        name: $"{SystemName}.resilience.attempts",
        unit: "{attempt}",
        description: "Intentos ejecutados dentro de un pipeline de resiliencia, por resultado.");
}
