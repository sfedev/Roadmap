using DotNetLab.Api.Services;
using DotNetLab.Contracts;
using Microsoft.AspNetCore.Http.HttpResults;

namespace DotNetLab.Api.Endpoints;

/// <summary>
/// Endpoints del módulo "Inyección de dependencias avanzada": Keyed Services,
/// patrón Factory y comparación de lifetimes dentro de una misma petición.
/// </summary>
public static class DependencyInjectionEndpoints
{
    public static RouteGroupBuilder MapDependencyInjectionEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/di").WithTags("DependencyInjection");

        group.MapGet("/keyed-services", KeyedServices)
            .WithName("KeyedServices")
            .WithSummary("Resuelve una implementación por clave y la ejecuta de verdad.");

        group.MapGet("/lifetimes", Lifetimes)
            .WithName("Lifetimes")
            .WithSummary("Resuelve dos veces cada lifetime en la misma petición y compara instancias.");

        return api;
    }

    /// <summary>Resuelve el parser correspondiente a la clave y lo ejecuta sobre un buffer real.</summary>
    private static Results<Ok<KeyedServiceResponse>, BadRequest<string>> KeyedServices(
        string? key,
        int? rows,
        ITelemetryParserFactory factory,
        TelemetrySampleGenerator generator)
    {
        // "span" por defecto: el endpoint responde algo útil aunque no se pase la clave.
        var requestedKey = string.IsNullOrWhiteSpace(key) ? "span" : key;

        // La fábrica devuelve null para claves desconocidas en vez de lanzar: 400, no 500.
        var parser = factory.Resolve(requestedKey);
        if (parser is null)
        {
            // string.Join sobre las claves válidas: el mensaje de error es accionable.
            return TypedResults.BadRequest(
                $"Clave '{requestedKey}' no registrada. Claves válidas: {string.Join(", ", factory.AvailableKeys)}.");
        }

        // Muestra más pequeña que en el módulo de rendimiento: aquí importa QUÉ resolvió, no cuánto tarda.
        var payload = generator.Generate(TelemetrySampleGenerator.ClampRows(rows ?? 5_000));

        return TypedResults.Ok(new KeyedServiceResponse(
            RequestedKey: requestedKey,
            // GetType().Name expone el tipo CLR concreto: la prueba de que la clave decidió la implementación.
            ResolvedImplementation: parser.GetType().Name,
            StrategyDescription: parser.Description,
            Execution: parser.Parse(payload),
            AvailableKeys: factory.AvailableKeys));
    }

    /// <summary>
    /// Resuelve dos veces cada lifetime DENTRO de la misma petición. Comparando InstanceId
    /// se ve el comportamiento real del contenedor sin necesidad de explicarlo con palabras.
    /// </summary>
    private static Ok<LifetimesDemoResponse> Lifetimes(
        // HttpContext da acceso a RequestServices, que ES el scope de esta petición.
        HttpContext context,
        // Primera resolución: la hace el propio binder al construir los argumentos.
        SingletonProbe singletonFirst,
        ScopedProbe scopedFirst,
        TransientProbe transientFirst)
    {
        // Segunda resolución manual desde el MISMO scope de petición.
        // GetRequiredService lanza si falta el registro: aquí es lo correcto, sería un bug de arranque.
        var provider = context.RequestServices;
        var singletonSecond = provider.GetRequiredService<SingletonProbe>();
        var scopedSecond = provider.GetRequiredService<ScopedProbe>();
        var transientSecond = provider.GetRequiredService<TransientProbe>();

        return TypedResults.Ok(new LifetimesDemoResponse(
            SingletonFirst: singletonFirst.ToFootprint(),
            SingletonSecond: singletonSecond.ToFootprint(),
            ScopedFirst: scopedFirst.ToFootprint(),
            ScopedSecond: scopedSecond.ToFootprint(),
            TransientFirst: transientFirst.ToFootprint(),
            TransientSecond: transientSecond.ToFootprint(),
            // TraceIdentifier identifica la petición: al repetir la llamada cambia, y con él el scoped.
            RequestId: context.TraceIdentifier,
            Insight: "Singleton: mismo id siempre. Scoped: mismo id dentro de esta petición, " +
                     "distinto al recargar. Transient: id distinto en cada resolución."));
    }
}
