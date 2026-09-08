using DotNetLab.Analysis;
using DotNetLab.Contracts;
using DotNetLab.ServiceDefaults;
using DotNetLab.Worker;
using MassTransit;

// CreateSlimBuilder: el Worker solo necesita Kestrel para las dos rutas de salud, así que se
// ahorra todo lo que un host web completo arrastra y que aquí nadie usaría.
var builder = WebApplication.CreateSlimBuilder(args);

// Misma telemetría que la API y el frontend. El nombre de servicio distingue sus trazas y
// métricas en Jaeger y Prometheus sin necesidad de más configuración.
builder.AddLabServiceDefaults("dotnetlab-worker");

// Opciones del bus. A diferencia de la API, aquí el transporte "inmemory" no tiene sentido:
// un Worker con bus en memoria no recibiría nada, porque quien publica es otro proceso.
var messaging = builder.Configuration.GetSection(WorkerMessagingOptions.SectionName)
                    .Get<WorkerMessagingOptions>() ?? new WorkerMessagingOptions();

builder.Services.ConfigureHttpJsonOptions(options =>
{
    // El Worker solo serializa las respuestas de salud, pero mantener el mismo resolver
    // generado en los tres procesos evita divergencias de formato en el JSON.
    options.SerializerOptions.TypeInfoResolverChain.Insert(0, LabJsonSerializerContext.Default);
});

// Reloj inyectable: el consumidor lo usa para calcular la latencia de cola.
builder.Services.AddSingleton(TimeProvider.System);

// El MISMO registro de dominio que hace la API: parsers por clave, fábrica y generador.
builder.Services.AddLabAnalysis();

builder.Services.AddMassTransit(bus =>
{
    // Nombre de cola en kebab-case derivado del consumidor: "telemetry-analysis".
    bus.SetKebabCaseEndpointNameFormatter();

    // Único consumidor del Worker: el trabajo pesado. Vive en la librería de dominio.
    bus.AddConsumer<TelemetryAnalysisConsumer>();

    bus.UsingRabbitMq((context, cfg) =>
    {
        cfg.Host(messaging.Host, messaging.Port, messaging.VirtualHost, host =>
        {
            host.Username(messaging.Username);
            // Contraseña resuelta desde el docker secret montado en fichero.
            host.Password(messaging.ResolvePassword());
        });

        // Límite de mensajes procesados a la vez POR RÉPLICA. Es el equivalente al prefetch de
        // RabbitMQ y la palanca que evita que un pod acepte cien mensajes pesados y se quede
        // sin memoria: mejor que esperen en la cola y Kubernetes escale por longitud de cola.
        cfg.PrefetchCount = messaging.PrefetchCount;

        // Reintentos del consumidor. Si se agotan, el mensaje va a la cola _error, donde queda
        // disponible para inspección o reproceso: no se pierde ni bloquea la cola principal.
        cfg.UseMessageRetry(retry => retry.Exponential(
            retryLimit: 3,
            minInterval: TimeSpan.FromSeconds(1),
            maxInterval: TimeSpan.FromSeconds(10),
            intervalDelta: TimeSpan.FromSeconds(2)));

        // Crea la cola del consumidor aplicando las políticas anteriores.
        cfg.ConfigureEndpoints(context);
    });
});

var app = builder.Build();

// Las dos únicas rutas del Worker. Sin ellas, Kubernetes no podría distinguir un pod colgado
// de uno simplemente ocioso, y nunca reiniciaría un consumidor bloqueado.
// El health check "masstransit-bus" que registra MassTransit lleva la etiqueta "ready", así que
// un Worker sin conexión al broker se marca como no listo, pero NO se reinicia.
app.MapLabDefaultEndpoints();

// Raíz informativa: útil al hacer `docker exec` o al abrir el puerto por error.
app.MapGet("/", () => TypedResults.Ok(new
{
    service = "DotNetLab.Worker",
    role = "Consume TelemetryAnalysisRequested y publica TelemetryAnalysisCompleted",
    // Nombre del pod en Kubernetes: identifica qué réplica responde.
    instance = Environment.MachineName,
    prefetch = messaging.PrefetchCount
}));

app.Run();
