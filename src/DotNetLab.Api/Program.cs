using System.Threading.RateLimiting;
using DotNetLab.Api.Endpoints;
using DotNetLab.Api.Infrastructure;
using DotNetLab.Api.Services;
using DotNetLab.Contracts;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

// CreateSlimBuilder en lugar de CreateBuilder: arranca sin los proveedores que un contenedor
// no usa (IIS, user secrets, hosting startup assemblies). Menos arranque en frío y menos IL
// que recortar. Mantiene appsettings.json, variables de entorno, args y logging a consola.
var builder = WebApplication.CreateSlimBuilder(args);

// ---------------------------------------------------------------------------
// Configuración tipada
// ---------------------------------------------------------------------------

// Bind de la sección "Cache" a CacheOptions. En Docker llega como Cache__Host / Cache__Port:
// el doble guion bajo es el separador jerárquico del proveedor de variables de entorno.
builder.Services.Configure<CacheOptions>(builder.Configuration.GetSection(CacheOptions.SectionName));

// SharedKeyOptions se materializa aquí (y no vía IOptions) porque el validador lo necesita
// en su constructor de Singleton, antes de que exista cualquier petición.
var sharedKeyOptions = builder.Configuration.GetSection(SharedKeyOptions.SectionName).Get<SharedKeyOptions>()
                       ?? new SharedKeyOptions();

// ---------------------------------------------------------------------------
// Serialización
// ---------------------------------------------------------------------------

builder.Services.ConfigureHttpJsonOptions(options =>
{
    // Inserta el resolver generado en tiempo de compilación al principio de la cadena:
    // los tipos conocidos se serializan sin reflexión y el resto cae al resolver por defecto.
    options.SerializerOptions.TypeInfoResolverChain.Insert(0, LabJsonSerializerContext.Default);
});

// ---------------------------------------------------------------------------
// Inyección de dependencias
// ---------------------------------------------------------------------------

// TimeProvider abstrae el reloj del sistema; inyectarlo hace testeable todo lo que use la hora.
builder.Services.AddSingleton(TimeProvider.System);

// KEYED SERVICES: dos implementaciones del MISMO interfaz distinguidas por clave.
// Antes de .NET 8 esto exigía un diccionario propio o un contenedor de terceros.
builder.Services.AddKeyedSingleton<ITelemetryParser, SpanTelemetryParser>("span");
builder.Services.AddKeyedSingleton<ITelemetryParser, NaiveTelemetryParser>("naive");

// FACTORY: encapsula el acceso al contenedor para que los endpoints no hagan Service Locator.
builder.Services.AddSingleton<ITelemetryParserFactory, TelemetryParserFactory>();

// Singleton: sin estado por petición y con semilla fija, una instancia sirve a todo el proceso.
builder.Services.AddSingleton<TelemetrySampleGenerator>();

// Singleton porque cachea el último resumen entre peticiones (la demo de ValueTask lo necesita).
builder.Services.AddSingleton<ChannelPipelineService>();

// Singleton: solo depende de TimeProvider y no guarda estado mutable.
builder.Services.AddSingleton<LanguageShowcaseService>();

// Singleton: abre y cierra un socket por sonda, no mantiene conexión persistente.
builder.Services.AddSingleton<CachePingProbe>();

// Las tres sondas de lifetime, cada una registrada con el suyo. Son la demo de /api/di/lifetimes.
builder.Services.AddSingleton<SingletonProbe>();   // Una instancia para todo el proceso.
builder.Services.AddScoped<ScopedProbe>();         // Una instancia por petición HTTP.
builder.Services.AddTransient<TransientProbe>();   // Una instancia por cada resolución.

// El validador lee el docker secret UNA vez al construirse, de ahí el lifetime Singleton.
builder.Services.AddSingleton(sp => new SharedKeyValidator(
    sharedKeyOptions, sp.GetRequiredService<ILogger<SharedKeyValidator>>()));

// ProblemDetails uniformiza los errores (RFC 9457): mismo formato JSON para 400, 401 y 500.
builder.Services.AddProblemDetails();

// Documento OpenAPI nativo de ASP.NET Core: sin Swashbuckle ni NSwag.
builder.Services.AddOpenApi();

// CORS: el cliente Blazor WASM puede llamar directamente a la API durante la exploración
// con el navegador. Los orígenes salen de configuración, nunca hardcodeados.
var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
builder.Services.AddCors(options =>
{
    options.AddPolicy("frontend", policy => policy
        // Lista blanca explícita: AllowAnyOrigin dejaría la API abierta a cualquier web.
        .WithOrigins(allowedOrigins)
        .AllowAnyHeader()
        .AllowAnyMethod());
});

// Rate limiting: los endpoints de rendimiento reservan MBs por llamada, así que un bucle
// de peticiones podría tumbar el contenedor. Es una defensa de capacidad, no de seguridad.
builder.Services.AddRateLimiter(options =>
{
    // 429 en vez del 503 por defecto: el cliente sabe que debe reintentar más tarde.
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    options.AddFixedWindowLimiter("heavy", limiter =>
    {
        // 20 peticiones por ventana de 10 s: holgado para un humano pulsando botones.
        limiter.PermitLimit = 20;
        limiter.Window = TimeSpan.FromSeconds(10);
        // Cola pequeña: encolar demasiado solo retrasa el rechazo y consume memoria.
        limiter.QueueLimit = 5;
        limiter.QueueProcessingOrder = QueueProcessingOrder.OldestFirst;
    });
});

var app = builder.Build();

// ---------------------------------------------------------------------------
// Pipeline HTTP (el orden de los middlewares ES la semántica)
// ---------------------------------------------------------------------------

// Convierte excepciones no controladas en ProblemDetails; debe ir el primero para envolver al resto.
app.UseExceptionHandler();

// Traduce códigos de estado sin cuerpo (404, 401...) a ProblemDetails con el mismo formato.
app.UseStatusCodePages();

// CORS antes del enrutado: el preflight OPTIONS debe responderse sin llegar al endpoint.
app.UseCors("frontend");

// El limitador va después de CORS y antes de los endpoints, para no rechazar preflights.
app.UseRateLimiter();

// El documento OpenAPI solo se publica fuera de producción: es superficie de ataque gratis.
if (app.Environment.IsDevelopment())
{
    // Expone /openapi/v1.json, consumible por Scalar, Swagger UI o el cliente REST del IDE.
    app.MapOpenApi();
}

// Grupo público: lo consume el HEALTHCHECK del contenedor, que no conoce el secreto compartido.
var publicApi = app.MapGroup("/api");
publicApi.MapDiagnosticsEndpoints();

// Grupo protegido: todos los endpoints pedagógicos exigen la clave compartida cuando existe.
var securedApi = app.MapGroup("/api")
    // El filtro se resuelve del contenedor y se ejecuta antes que cualquier handler del grupo.
    .AddEndpointFilter<SharedKeyEndpointFilter>()
    // La política de rate limiting se hereda por todo el subárbol de rutas.
    .RequireRateLimiting("heavy");

// Cada módulo registra sus rutas; añadir uno nuevo es una línea aquí y un archivo en Endpoints/.
securedApi.MapPerformanceEndpoints();
securedApi.MapConcurrencyEndpoints();
securedApi.MapLanguageEndpoints();
securedApi.MapDependencyInjectionEndpoints();

// Catálogo de rutas creado UNA vez y capturado por la lambda: si se construyera dentro del
// handler se reasignaría un array nuevo en cada petición (CA1861).
string[] endpointCatalog =
[
    "/api/performance/span-demo",
    "/api/concurrency/channel-stream",
    "/api/concurrency/channel-summary",
    "/api/language/records",
    "/api/language/pattern-match",
    "/api/di/keyed-services",
    "/api/di/lifetimes",
    "/api/diagnostics/health"
];

// Raíz informativa: evita el 404 desnudo cuando alguien abre la URL base del contenedor.
app.MapGet("/", (IOptions<CacheOptions> cache) => TypedResults.Ok(new
{
    service = "DotNetLab.Api",
    // Confirma de un vistazo si la API leyó la configuración esperada del compose.
    cacheHost = cache.Value.Host,
    endpoints = endpointCatalog
}));

app.Run();

// Clase parcial pública vacía: hace visible el Program generado por top-level statements
// para WebApplicationFactory<Program> en el proyecto de tests de integración.
public partial class Program;
