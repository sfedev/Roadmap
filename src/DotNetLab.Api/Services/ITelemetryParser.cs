using DotNetLab.Contracts;

namespace DotNetLab.Api.Services;

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
}
