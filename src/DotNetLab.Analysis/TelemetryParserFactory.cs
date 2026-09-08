using Microsoft.Extensions.DependencyInjection;

namespace DotNetLab.Analysis;

/// <summary>
/// Fábrica sobre los Keyed Services: traduce una clave que llega por HTTP a la
/// implementación registrada, sin que los endpoints toquen <see cref="IServiceProvider"/>.
/// Así el Service Locator queda encapsulado en un único punto auditable.
/// </summary>
public interface ITelemetryParserFactory
{
    /// <summary>Claves válidas; la UI las usa para pintar los botones disponibles.</summary>
    IReadOnlyList<string> AvailableKeys { get; }

    /// <summary>Devuelve el parser de esa clave o null si la clave no existe.</summary>
    ITelemetryParser? Resolve(string key);
}

/// <summary>
/// Implementación de la fábrica. El primary constructor recibe el propio contenedor,
/// que se registra como Singleton porque no captura nada con lifetime más corto.
/// </summary>
public sealed class TelemetryParserFactory(IServiceProvider provider) : ITelemetryParserFactory
{
    // Lista blanca estática: resolver por clave arbitraria abriría la puerta a sondear el contenedor.
    // FrozenSet sería aún más rápido, pero con 2 claves un array es más simple y suficiente.
    private static readonly string[] Keys = ["span", "naive"];

    // Se expone como IReadOnlyList para que el llamante no pueda mutar el array interno.
    public IReadOnlyList<string> AvailableKeys => Keys;

    public ITelemetryParser? Resolve(string key)
    {
        // Comparación ordinal e ignorando mayúsculas: no depende de la cultura del contenedor.
        // Contains sobre 2 elementos es una comparación lineal trivialmente barata.
        if (!Keys.Contains(key, StringComparer.OrdinalIgnoreCase)) return null;

        // GetKeyedService (sin "Required") devuelve null en vez de lanzar si no está registrado:
        // el endpoint puede responder 400 en lugar de un 500 no controlado.
        // ToLowerInvariant normaliza la clave al mismo casing usado en el registro.
        return provider.GetKeyedService<ITelemetryParser>(key.ToLowerInvariant());
    }
}
