using System.Runtime.InteropServices;
using DotNetLab.Api.Services;
using DotNetLab.Contracts;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Options;

namespace DotNetLab.Api.Endpoints;

/// <summary>
/// Endpoints de diagnóstico: estado del proceso dentro del contenedor y sonda del
/// contenedor auxiliar. Los usa tanto la UI como el HEALTHCHECK del Dockerfile.
/// </summary>
public static class DiagnosticsEndpoints
{
    public static RouteGroupBuilder MapDiagnosticsEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/diagnostics").WithTags("Diagnostics");

        group.MapGet("/health", HealthAsync)
            .WithName("Health")
            .WithSummary("Estado del servicio y de sus dependencias externas.");

        return api;
    }

    /// <summary>Compone el estado global a partir del proceso y de la sonda de cache.</summary>
    private static async ValueTask<Ok<HealthResponse>> HealthAsync(
        CachePingProbe cacheProbe,
        // Las opciones se inyectan para distinguir "cache caída" de "cache no configurada".
        IOptions<CacheOptions> cacheOptions,
        IHostEnvironment environment,
        CancellationToken cancellationToken)
    {
        // La sonda nunca lanza: devuelve un DependencyProbe con Reachable=false si falla.
        var cache = await cacheProbe.PingAsync(cancellationToken);

        // Variable que el runtime oficial de .NET define dentro de sus imágenes base.
        var insideContainer =
            Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER") is "true" or "1";

        // Degraded solo si la cache ESTÁ configurada y no responde. Sin contenedor auxiliar
        // (ejecución local) el servicio está sano: no hay dependencia que pueda fallar.
        // Nunca Unhealthy: la API sirve todos sus endpoints igualmente, y un 503 haría que el
        // orquestador reiniciara un contenedor perfectamente funcional.
        var status = !cacheOptions.Value.Enabled || cache.Reachable ? "Healthy" : "Degraded";

        return TypedResults.Ok(new HealthResponse(
            Status: status,
            EnvironmentName: environment.EnvironmentName,
            // FrameworkDescription imprime el runtime exacto: confirma qué .NET hay dentro de la imagen.
            RuntimeVersion: RuntimeInformation.FrameworkDescription,
            InsideContainer: insideContainer,
            // En Docker, MachineName es el id corto del contenedor: útil para ver el balanceo.
            MachineName: Environment.MachineName,
            Cache: cache));
    }
}
