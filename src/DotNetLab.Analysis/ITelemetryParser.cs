using DotNetLab.Contracts;

namespace DotNetLab.Analysis;

/// <summary>
/// Estrategia de parseo de telemetría. Existen dos implementaciones registradas como
/// Keyed Services ("span" y "naive") para poder compararlas en caliente desde la UI.
/// </summary>
public interface ITelemetryParser
{
    /// <summary>Clave con la que el servicio está registrado en el contenedor de DI.</summary>
    string Key { get; }

    /// <summary>Descripción pedagógica que la UI muestra junto al resultado.</summary>
    string Description { get; }

    /// <summary>
    /// Parsea el buffer y devuelve métricas de tiempo y memoria.
    /// Recibe <see cref="ReadOnlySpan{T}"/> y no string: la firma obliga a que cualquier
    /// implementación que quiera un string tenga que copiarlo, y esa copia se ve en las métricas.
    /// </summary>
    ParseStrategyResult Parse(ReadOnlySpan<char> payload);

    /// <summary>
    /// Ejecuta la MISMA ruta de código sin medir ni publicar métricas.
    ///
    /// Existe por una razón concreta y medida: la primera ejecución de un método incluye el
    /// trabajo del JIT por niveles (y, en bucles largos, el reemplazo en pila u OSR). Ese coste
    /// se contabiliza en el hilo que lo ejecuta, así que aparece como "memoria asignada" en la
    /// medición aunque el parser no asigne nada. Sin calentar, el primer trabajo de un proceso
    /// reportaba 7.840 bytes; el segundo, 3.320; el tercero, 0.
    ///
    /// Llamar a Parse para calentar tampoco vale: ensuciaría los histogramas de OpenTelemetry
    /// con muestras de un buffer diminuto que nadie pidió procesar.
    /// </summary>
    void Warmup(ReadOnlySpan<char> payload);
}
