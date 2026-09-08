using Microsoft.Extensions.DependencyInjection;

namespace DotNetLab.Analysis;

/// <summary>
/// Registro del dominio de análisis. Lo invocan la API y el Worker con la MISMA llamada:
/// así es imposible que uno resuelva una implementación distinta del otro, que es justo el
/// tipo de divergencia que hace irreproducible un resultado entre servicios.
/// </summary>
public static class AnalysisServiceCollectionExtensions
{
    /// <summary>Registra los parsers por clave, su fábrica y el generador de muestras.</summary>
    public static IServiceCollection AddLabAnalysis(this IServiceCollection services)
    {
        // KEYED SERVICES: dos implementaciones del MISMO interfaz distinguidas por clave.
        // Antes de .NET 8 esto exigía un diccionario propio o un contenedor de terceros.
        services.AddKeyedSingleton<ITelemetryParser, SpanTelemetryParser>("span");
        services.AddKeyedSingleton<ITelemetryParser, NaiveTelemetryParser>("naive");

        // FACTORY: encapsula el acceso al contenedor para que ni los endpoints ni los
        // consumidores de mensajes hagan Service Locator por su cuenta.
        services.AddSingleton<ITelemetryParserFactory, TelemetryParserFactory>();

        // Singleton: semilla fija y sin estado por petición, una instancia sirve al proceso entero.
        services.AddSingleton<TelemetrySampleGenerator>();

        return services;
    }
}
