using DotNetLab.Contracts;
using DotNetLab.ServiceDefaults;
using DotNetLab.Web.Client.Services;
using DotNetLab.Web.Components;
using DotNetLab.Web.Infrastructure;
using DotNetLab.Web.Realtime;
using MassTransit;

// CreateBuilder (no Slim): el hosting de Blazor necesita los servicios completos del host web,
// entre ellos el mapeo de static web assets del proyecto cliente.
var builder = WebApplication.CreateBuilder(args);

// ---------------------------------------------------------------------------
// Observabilidad y health checks
// ---------------------------------------------------------------------------

// La misma telemetría que la API y el Worker. Con la instrumentación de HttpClient, el span
// del navegador se enlaza con el de la API a través del proxy: una única traza distribuida.
builder.AddLabServiceDefaults("dotnetlab-web");

// ---------------------------------------------------------------------------
// Configuración del backend
// ---------------------------------------------------------------------------

// Opciones del backend: URL interna y ruta del secreto compartido.
var apiOptions = builder.Configuration.GetSection(ApiOptions.SectionName).Get<ApiOptions>() ?? new ApiOptions();

// El secreto se resuelve UNA vez al arrancar: leer el fichero por petición sería I/O inútil.
var sharedKey = apiOptions.ResolveSharedKey();

// Opciones del bus. El frontend solo CONSUME el resultado para difundirlo por SignalR; nunca
// publica nada, así que con el transporte desactivado sigue funcionando todo lo demás.
var messagingOptions = builder.Configuration.GetSection(WebMessagingOptions.SectionName)
                           .Get<WebMessagingOptions>() ?? new WebMessagingOptions();

// ---------------------------------------------------------------------------
// Servicios
// ---------------------------------------------------------------------------

builder.Services.AddRazorComponents()
    // Habilita el render interactivo por SignalR (lo que usa el modo Auto en la primera visita).
    .AddInteractiveServerComponents()
    // Habilita el render en WebAssembly y sirve los archivos del runtime del proyecto cliente.
    .AddInteractiveWebAssemblyComponents();

// Hub propio para notificar el fin de los trabajos. Es independiente del circuito que usa
// Blazor Server: comparten tecnología, pero no conexión ni ciclo de vida.
builder.Services.AddSignalR();

// Cliente HTTP nombrado hacia la Web API. Es el ÚNICO punto del frontend que conoce la URL
// interna del contenedor de la API.
builder.Services.AddHttpClient(ApiOptions.HttpClientName, http =>
{
    // BaseAddress terminada en '/' para que las rutas relativas se concatenen correctamente.
    http.BaseAddress = new Uri(apiOptions.BaseUrl);
    // Timeout explícito: el valor por defecto (100 s) deja peticiones colgadas demasiado tiempo.
    // Es mayor que el timeout total de Polly para no cortar el pipeline antes de que decida.
    http.Timeout = TimeSpan.FromSeconds(60);

    // La clave compartida se añade aquí, en el servidor. El navegador nunca la ve.
    if (!string.IsNullOrWhiteSpace(sharedKey))
    {
        http.DefaultRequestHeaders.Add(apiOptions.HeaderName, sharedKey);
    }
})
// Retry con backoff exponencial y jitter, circuit breaker y timeouts. Cubre el caso real que
// justifica reintentar: un pod de la API reciclándose durante un despliegue.
.AddLabResilience();

// Contexto JSON generado en compilación, compartido con el cliente WASM.
builder.Services.AddSingleton(LabJsonSerializerContext.Default);

// HttpClient que usará LabApiClient durante el render EN SERVIDOR: apunta directamente a la API,
// sin pasar por el proxy (sería una llamada del servidor a sí mismo).
builder.Services.AddScoped(sp => sp.GetRequiredService<IHttpClientFactory>().CreateClient(ApiOptions.HttpClientName));

// El mismo cliente tipado que usan los componentes en WebAssembly: un único código para ambos modos.
builder.Services.AddScoped<LabApiClient>();

// El bus solo se registra si hay broker. Sin él, la página de trabajos sigue funcionando por
// consulta periódica al endpoint de estado: SignalR acelera la notificación, no la habilita.
if (messagingOptions.IsEnabled)
{
    builder.Services.AddMassTransit(bus =>
    {
        bus.SetKebabCaseEndpointNameFormatter();

        // Consumidor puente bus -> SignalR. Es un suscriptor independiente del de la API.
        bus.AddConsumer<JobNotificationConsumer>();

        bus.UsingRabbitMq((context, cfg) =>
        {
            cfg.Host(messagingOptions.Host, messagingOptions.Port, messagingOptions.VirtualHost, host =>
            {
                host.Username(messagingOptions.Username);
                host.Password(messagingOptions.ResolvePassword());
            });

            cfg.ConfigureEndpoints(context);
        });
    });
}

var app = builder.Build();

// Deja constancia en el arranque de a qué backend apunta el proxy y si lleva clave: es el
// primer dato que se consulta cuando el frontend no ve la API.
// El booleano se calcula fuera de la llamada porque el analizador exige que los argumentos de
// un log no impliquen trabajo (CA1873), aunque aquí solo se ejecute una vez al arrancar.
var hasSharedKey = !string.IsNullOrWhiteSpace(sharedKey);
WebLog.ProxyConfigured(app.Logger, apiOptions.BaseUrl, hasSharedKey);

// ---------------------------------------------------------------------------
// Pipeline HTTP
// ---------------------------------------------------------------------------

if (app.Environment.IsDevelopment())
{
    // Middleware que traduce los errores de la app WASM al navegador durante el desarrollo.
    app.UseWebAssemblyDebugging();
}
else
{
    // Página de error propia y HSTS solo fuera de desarrollo, donde sí hay TLS delante.
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

// Protección anti-forgery: necesaria para los formularios de Blazor con render de servidor.
app.UseAntiforgery();

// MapStaticAssets (no UseStaticFiles): en .NET 9+ publica los archivos de wwwroot con huella
// de contenido, compresión previa y cabeceras de caché inmutables. Es lo que hace funcionar
// a @Assets["app.css"], y DEBE registrarse antes de AddInteractiveWebAssemblyRenderMode.
app.MapStaticAssets();

// Sondas de Kubernetes, idénticas a las de la API y el Worker.
app.MapLabDefaultEndpoints();

// Hub de notificación. El navegador se conecta a su PROPIO origen, así que el WebSocket no
// atraviesa el proxy ni obliga a exponer la API.
app.MapHub<JobsHub>(JobsHub.Route);

// PROXY: el navegador (WASM) llama a /api/... del propio origen y este endpoint lo reenvía a la
// Web API. Tres beneficios: no hay CORS, la URL interna del contenedor no se expone, y el
// secreto compartido se añade en el servidor sin viajar nunca al cliente.
app.MapApiProxy();

app.MapRazorComponents<App>()
    // Registra el modo de render de servidor para los componentes que lo pidan.
    .AddInteractiveServerRenderMode()
    // Registra el modo WebAssembly y publica los archivos del ensamblado cliente.
    .AddInteractiveWebAssemblyRenderMode()
    // Sin esto, el router no encontraría las páginas que viven en el proyecto cliente.
    .AddAdditionalAssemblies(typeof(DotNetLab.Web.Client._Imports).Assembly);

app.Run();
