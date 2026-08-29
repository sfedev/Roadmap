using DotNetLab.Contracts;

namespace DotNetLab.Api.Services;

/// <summary>
/// Base de las sondas de lifetime. Cada instancia se autoidentifica al construirse,
/// de modo que comparando InstanceId se ve qué objetos reutiliza el contenedor.
/// </summary>
// Clase abstracta con primary constructor: el parámetro 'lifetime' queda disponible
// en los inicializadores de campo y de propiedad de esta misma clase.
public abstract class LifetimeProbe(string lifetime)
{
    // Id corto y único por instancia, calculado en el inicializador de propiedad (una vez por objeto).
    // Se toman los ULTIMOS 8 caracteres a propósito: en un GUID v7 los primeros son el timestamp
    // en milisegundos, así que dos instancias creadas en el mismo ms compartirían prefijo y la
    // demo de lifetimes mostraría ids idénticos para objetos distintos. La cola es la parte aleatoria.
    public string InstanceId { get; } = Guid.CreateVersion7().ToString("N")[^8..];

    // Momento exacto de construcción: hace visible cuándo el contenedor creó el objeto.
    public DateTimeOffset CreatedAt { get; } = DateTimeOffset.UtcNow;

    /// <summary>Proyecta la sonda al DTO que viaja al frontend.</summary>
    // Expression-bodied member: no hay lógica, solo mapeo, así que una línea basta.
    public ServiceFootprint ToFootprint() => new(lifetime, InstanceId, CreatedAt);
}

/// <summary>Registrada como Singleton: una sola instancia para todo el proceso.</summary>
// El ": base(...)" pasa la etiqueta que aparecerá en el JSON de respuesta.
public sealed class SingletonProbe() : LifetimeProbe("Singleton");

/// <summary>Registrada como Scoped: una instancia por petición HTTP.</summary>
public sealed class ScopedProbe() : LifetimeProbe("Scoped");

/// <summary>Registrada como Transient: una instancia nueva en cada resolución.</summary>
public sealed class TransientProbe() : LifetimeProbe("Transient");
