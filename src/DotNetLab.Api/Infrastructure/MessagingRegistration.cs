using DotNetLab.Analysis;
using DotNetLab.Api.Consumers;
using MassTransit;

namespace DotNetLab.Api.Infrastructure;

/// <summary>
/// Registro de MassTransit para la API. Aísla en un punto la elección del transporte, de modo
/// que ni los endpoints ni los consumidores saben si detrás hay RabbitMQ, memoria o Azure.
/// </summary>
public static class MessagingRegistration
{
    /// <summary>Configura el bus según las opciones; no registra nada si está desactivado.</summary>
    public static IServiceCollection AddLabMessaging(this IServiceCollection services, MessagingOptions options)
    {
        // Bus desactivado: se sale sin registrar MassTransit. Los endpoints comprueban el flag
        // y responden 503, en vez de fallar al resolver IPublishEndpoint en tiempo de petición.
        if (!options.IsEnabled) return services;

        services.AddMassTransit(bus =>
        {
            // Nombres de cola en kebab-case a partir del nombre del consumidor
            // (AnalysisResultConsumer -> analysis-result). Sin esto, MassTransit usa el nombre
            // del tipo en PascalCase, que en la UI de RabbitMQ resulta ilegible.
            bus.SetKebabCaseEndpointNameFormatter();

            // La API consume los RESULTADOS para mantener su registro de trabajos al día.
            // Es un suscriptor más del mismo evento que consume el frontend: publish/subscribe
            // real, cada uno con su cola y sin saber el uno del otro.
            bus.AddConsumer<AnalysisResultConsumer>();

            // Solo en transporte en memoria la API hace además de Worker. Con RabbitMQ este
            // consumidor vive en su propio proceso y escala por separado.
            if (options.HostsAnalysisConsumer)
            {
                bus.AddConsumer<TelemetryAnalysisConsumer>();
            }

            if (options.IsInMemory)
            {
                // Transporte en memoria: sin broker, sin red y sin durabilidad. Vale para
                // desarrollo y para tests; si el proceso muere, los mensajes en vuelo se pierden.
                bus.UsingInMemory((context, cfg) => cfg.ConfigureEndpoints(context));
            }
            else
            {
                bus.UsingRabbitMq((context, cfg) =>
                {
                    // La contraseña se resuelve aquí, una sola vez al arrancar, desde el
                    // docker secret montado en fichero.
                    var password = options.ResolvePassword();

                    cfg.Host(options.Host, options.Port, options.VirtualHost, host =>
                    {
                        host.Username(options.Username);
                        host.Password(password);
                    });

                    // Reintentos del CONSUMIDOR, distintos de los de Polly: aquí se reintenta
                    // procesar un mensaje ya recibido. Tres intentos con espera creciente cubren
                    // el fallo transitorio; agotados, el mensaje va a la cola _error y no se pierde.
                    cfg.UseMessageRetry(retry => retry.Exponential(
                        retryLimit: 3,
                        minInterval: TimeSpan.FromSeconds(1),
                        maxInterval: TimeSpan.FromSeconds(10),
                        intervalDelta: TimeSpan.FromSeconds(2)));

                    // ConfigureEndpoints crea una cola por consumidor registrado aplicando el
                    // formateador de nombres. Debe ir DESPUÉS de las políticas para que las herede.
                    cfg.ConfigureEndpoints(context);
                });
            }
        });

        return services;
    }
}
