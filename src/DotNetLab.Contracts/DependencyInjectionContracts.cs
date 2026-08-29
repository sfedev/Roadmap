namespace DotNetLab.Contracts;

/// <summary>Huella de una instancia resuelta del contenedor de DI.</summary>
public readonly record struct ServiceFootprint(
    // Lifetime declarado en el registro (Singleton/Scoped/Transient).
    string Lifetime,
    // Identificador único creado en el constructor: si cambia, es otra instancia.
    string InstanceId,
    // Momento de construcción; delata si el objeto se reusó o se creó ahora.
    DateTimeOffset CreatedAt);

/// <summary>Respuesta de /api/di/lifetimes: dos resoluciones por lifetime en la MISMA petición.</summary>
public sealed record LifetimesDemoResponse(
    // Primera y segunda resolución del singleton: mismo InstanceId siempre.
    ServiceFootprint SingletonFirst, ServiceFootprint SingletonSecond,
    // Scoped: mismo InstanceId dentro de la petición, distinto entre peticiones.
    ServiceFootprint ScopedFirst, ServiceFootprint ScopedSecond,
    // Transient: InstanceId distinto en cada resolución, incluso en la misma línea.
    ServiceFootprint TransientFirst, ServiceFootprint TransientSecond,
    // Id de la petición HTTP: correlaciona el resultado scoped entre llamadas sucesivas.
    string RequestId,
    // Conclusión redactada para la tarjeta del frontend.
    string Insight);

/// <summary>Respuesta de /api/di/keyed-services: qué implementación resolvió la clave.</summary>
public sealed record KeyedServiceResponse(
    // Clave solicitada por el cliente ("span" | "naive").
    string RequestedKey,
    // Nombre del tipo CLR que el contenedor devolvió para esa clave.
    string ResolvedImplementation,
    // Descripción de la estrategia, expuesta por el propio servicio.
    string StrategyDescription,
    // Resultado real de ejecutar el servicio resuelto, para probar que no es un mock.
    ParseStrategyResult Execution,
    // Claves disponibles en el contenedor, para que la UI pinte los botones.
    IReadOnlyList<string> AvailableKeys);

/// <summary>Estado del servicio y de sus dependencias externas (contenedor auxiliar).</summary>
public sealed record HealthResponse(
    // Estado global: Healthy | Degraded.
    string Status,
    // Nombre del entorno ASP.NET Core (Development/Production).
    string EnvironmentName,
    // Versión del runtime .NET que ejecuta el contenedor.
    string RuntimeVersion,
    // true si el proceso corre dentro de un contenedor (variable DOTNET_RUNNING_IN_CONTAINER).
    bool InsideContainer,
    // Nombre del host: en Docker es el id corto del contenedor.
    string MachineName,
    // Resultado del PING al contenedor auxiliar (cache) sin librerías externas.
    DependencyProbe Cache);

/// <summary>Sonda de una dependencia externa.</summary>
public readonly record struct DependencyProbe(
    // Nombre lógico de la dependencia.
    string Name,
    // true si respondió correctamente dentro del timeout.
    bool Reachable,
    // Latencia observada en milisegundos.
    double LatencyMs,
    // Detalle textual: respuesta recibida o motivo del fallo.
    string Detail);
