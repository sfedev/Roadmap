using DotNetLab.Api.Services;
using DotNetLab.Contracts;
using Microsoft.AspNetCore.Http.HttpResults;

namespace DotNetLab.Api.Endpoints;

/// <summary>
/// Endpoints del módulo de resiliencia. Ejecutan políticas de Polly de verdad contra una
/// dependencia simulada y devuelven la cronología medida, no una descripción de lo que haría.
/// </summary>
public static class ResilienceEndpoints
{
    public static RouteGroupBuilder MapResilienceEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/resilience").WithTags("Resilience");

        group.MapGet("/demo", RunAsync)
            .WithName("ResilienceDemo")
            .WithSummary("Ejecuta un escenario de retry con backoff o de circuit breaker.");

        return api;
    }

    private static async Task<Results<Ok<ResilienceDemoResponse>, BadRequest<string>>> RunAsync(
        string? scenario,
        int? failures,
        ResilienceShowcaseService showcase,
        CancellationToken cancellationToken)
    {
        // "retry" por defecto: el endpoint responde algo útil sin parámetros.
        var selected = string.IsNullOrWhiteSpace(scenario) ? ResilienceShowcaseService.RetryScenario : scenario;

        // Lista blanca explícita: el escenario elige qué código se ejecuta, así que no puede
        // venir libre de la query string.
        if (selected is not (ResilienceShowcaseService.RetryScenario or ResilienceShowcaseService.CircuitBreakerScenario))
        {
            return TypedResults.BadRequest(
                $"Escenario '{selected}' no soportado. Válidos: " +
                $"{ResilienceShowcaseService.RetryScenario}, {ResilienceShowcaseService.CircuitBreakerScenario}.");
        }

        // 0..6 fallos: con más, los reintentos se agotan siempre y la demo pierde interés.
        // El techo también acota el tiempo total de la petición por el backoff exponencial.
        var configuredFailures = Math.Clamp(failures ?? 2, 0, 6);

        return TypedResults.Ok(await showcase.RunAsync(selected, configuredFailures, cancellationToken));
    }
}
