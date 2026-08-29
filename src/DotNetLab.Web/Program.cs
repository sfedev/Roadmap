using DotNetLab.Contracts;
using DotNetLab.Web.Components;
using DotNetLab.Web.Infrastructure;
using DotNetLab.Web.Client.Services;

// CreateBuilder (no Slim): el hosting de Blazor necesita los servicios completos del host web,
// entre ellos el mapeo de static web assets del proyecto cliente.
var builder = WebApplication.CreateBuilder(args);

// ---------------------------------------------------------------------------
// Configuración del backend
// ---------------------------------------------------------------------------

// Opciones del backend: URL interna y ruta del secreto compartido.
var apiOptions = builder.Configuration.GetSection(ApiOptions.SectionName).Get<ApiOptions>() ?? new ApiOptions();

// El secreto se resuelve UNA vez al arrancar: leer el fichero por petición sería I/O inútil.
var sharedKey = apiOptions.ResolveSharedKey();

// ---------------------------------------------------------------------------
// Servicios
// ---------------------------------------------------------------------------

builder.Services.AddRazorComponents()
    // Habilita el render interactivo por SignalR (lo que usa el modo Auto en la primera visita).
    .AddInteractiveServerComponents()
    // Habilita el render en WebAssembly y sirve los archivos del runtime del proyecto cliente.
    .AddInteractiveWebAssemblyComponents();

// Cliente HTTP nombrado hacia la Web API. Es el ÚNICO punto del frontend que conoce la URL
// interna del contenedor de la API.
builder.Services.AddHttpClient(ApiOptions.HttpClientName, http =>
{
    // BaseAddress terminada en '/' para que las rutas relativas se concatenen correctamente.
    http.BaseAddress = new Uri(apiOptions.BaseUrl);
    // Timeout explícito: el valor por defecto (100 s) deja peticiones colgadas demasiado tiempo.
    http.Timeout = TimeSpan.FromSeconds(30);

    // La clave compartida se añade aquí, en el servidor. El navegador nunca la ve.
    if (!string.IsNullOrWhiteSpace(sharedKey))
    {
        http.DefaultRequestHeaders.Add(apiOptions.HeaderName, sharedKey);
    }
});

// Contexto JSON generado en compilación, compartido con el cliente WASM.
builder.Services.AddSingleton(LabJsonSerializerContext.Default);

// HttpClient que usará LabApiClient durante el render EN SERVIDOR: apunta directamente a la API,
// sin pasar por el proxy (sería una llamada del servidor a sí mismo).
builder.Services.AddScoped(sp => sp.GetRequiredService<IHttpClientFactory>().CreateClient(ApiOptions.HttpClientName));

// El mismo cliente tipado que usan los componentes en WebAssembly: un único código para ambos modos.
builder.Services.AddScoped<LabApiClient>();

var app = builder.Build();

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
