using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace DotNetLab.ServiceDefaults;

/// <summary>
/// Configuración transversal que aplican por igual la API, el frontend y el Worker.
/// Un único punto de verdad: si mañana cambia el muestreo de trazas, cambia para los tres.
/// </summary>
public static class ServiceDefaultsExtensions
{
    /// <summary>Etiqueta de los health checks que responden "el proceso está vivo".</summary>
    // Kubernetes distingue dos sondas y confundirlas es un error clásico: si la de liveness
    // comprueba dependencias, una caída de la base de datos REINICIA todos los pods en vez
    // de sacarlos del balanceador.
    public const string LivenessTag = "live";

    /// <summary>Etiqueta de los health checks que responden "puede recibir tráfico".</summary>
    public const string ReadinessTag = "ready";

    /// <summary>
    /// Registra OpenTelemetry (trazas, métricas y logs) y los health checks base.
    /// Recibe <see cref="IHostApplicationBuilder"/>, la abstracción que implementan tanto
    /// WebApplicationBuilder como HostApplicationBuilder: por eso vale para la API y el Worker.
    /// </summary>
    public static IHostApplicationBuilder AddLabServiceDefaults(
        this IHostApplicationBuilder builder,
        string serviceName)
    {
        builder.ConfigureLabOpenTelemetry(serviceName);

        // Health check base: no comprueba nada externo, solo demuestra que el proceso responde.
        // Es exactamente lo que debe verificar una sonda de liveness.
        builder.Services.AddHealthChecks()
            .AddCheck("self", () => HealthCheckResult.Healthy("El proceso responde."), tags: [LivenessTag]);

        return builder;
    }

    /// <summary>Configura los tres pilares de la telemetría contra un único exportador OTLP.</summary>
    private static void ConfigureLabOpenTelemetry(this IHostApplicationBuilder builder, string serviceName)
    {
        // El "recurso" describe QUIÉN emite la telemetría. Sin él, en un cluster con veinte pods
        // todos los spans parecen venir del mismo sitio.
        var resource = ResourceBuilder.CreateDefault()
            .AddService(
                serviceName: serviceName,
                serviceNamespace: LabTelemetry.SystemName,
                // El id de instancia distingue réplicas: en Kubernetes es el nombre del pod.
                serviceInstanceId: Environment.MachineName)
            .AddAttributes(
            [
                // Convención semántica de OpenTelemetry para separar dev de producción en el backend.
                new KeyValuePair<string, object>("deployment.environment.name", builder.Environment.EnvironmentName)
            ]);

        builder.Logging.AddOpenTelemetry(logging =>
        {
            logging.SetResourceBuilder(resource);
            // Sin esto solo viaja la plantilla ("Parser {Strategy} procesó..."), no el texto final.
            logging.IncludeFormattedMessage = true;
            // Los scopes de logging (BeginScope) se convierten en atributos consultables.
            logging.IncludeScopes = true;
        });

        builder.Services.AddOpenTelemetry()
            .ConfigureResource(r => r.AddService(serviceName, LabTelemetry.SystemName, serviceInstanceId: Environment.MachineName))

            .WithTracing(tracing => tracing
                .SetResourceBuilder(resource)
                // Instrumentación de las peticiones entrantes: un span raíz por request.
                .AddAspNetCoreInstrumentation(o =>
                {
                    // Las sondas de Kubernetes golpean /health cada pocos segundos. Trazarlas
                    // multiplicaría por diez el volumen sin aportar ninguna información.
                    o.Filter = context => !context.Request.Path.StartsWithSegments("/health");
                    // Adjunta la excepción al span cuando el request termina en error.
                    o.RecordException = true;
                })
                // Instrumentación de las llamadas salientes: propaga el traceparent W3C, que es
                // lo que enlaza el span del frontend con el de la API y con el del Worker.
                .AddHttpClientInstrumentation()
                // Spans propios (LabTelemetry.Source). Sin este AddSource se emiten al vacío.
                .AddSource(LabTelemetry.SystemName)
                // MassTransit emite sus propios spans de publicación y consumo con este nombre:
                // es lo que cierra la traza distribuida a través de RabbitMQ.
                .AddSource("MassTransit"))

            .WithMetrics(metrics => metrics
                .SetResourceBuilder(resource)
                // Métricas HTTP estándar del servidor (duración, códigos de estado, rutas).
                .AddAspNetCoreInstrumentation()
                // Métricas del cliente HTTP saliente.
                .AddHttpClientInstrumentation()
                // GC por generación, bytes asignados, hilos del ThreadPool, excepciones.
                .AddRuntimeInstrumentation()
                // Instrumentos propios declarados en LabTelemetry.
                .AddMeter(LabTelemetry.SystemName)
                // Polly v8 publica aquí sus métricas: reintentos, aperturas de circuito y timeouts.
                .AddMeter("Polly"));

        // El exportador se añade SOLO si hay endpoint configurado. Sin esta guarda, ejecutar
        // `dotnet run` sin el stack de observabilidad llenaría la consola de errores de conexión
        // cada pocos segundos.
        var otlpEndpoint = builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"];
        if (!string.IsNullOrWhiteSpace(otlpEndpoint))
        {
            // UseOtlpExporter configura los TRES señales (trazas, métricas y logs) de una vez y
            // lee el resto de variables OTEL_* estándar (protocolo, cabeceras, timeout).
            builder.Services.AddOpenTelemetry().UseOtlpExporter();
        }
    }

    /// <summary>
    /// Publica los dos endpoints de salud que consumen las sondas de Kubernetes.
    /// Se separan a propósito: comparten implementación pero tienen semánticas distintas.
    /// </summary>
    public static WebApplication MapLabDefaultEndpoints(this WebApplication app)
    {
        // LIVENESS: ¿sigue vivo el proceso? Si falla, Kubernetes REINICIA el contenedor.
        // Filtra por la etiqueta "live" para que ninguna dependencia externa pueda provocarlo.
        app.MapHealthChecks("/health/live", new HealthCheckOptions
        {
            Predicate = check => check.Tags.Contains(LivenessTag)
        })
        // Excluida del rate limiting y de la clave compartida: la sonda no tiene credenciales.
        .AllowAnonymous();

        // READINESS: ¿puede atender tráfico? Si falla, Kubernetes lo saca del Service (deja de
        // recibir peticiones) pero NO lo reinicia: se le da tiempo a que la dependencia vuelva.
        app.MapHealthChecks("/health/ready", new HealthCheckOptions
        {
            Predicate = check => check.Tags.Contains(ReadinessTag)
        })
        .AllowAnonymous();

        return app;
    }
}
