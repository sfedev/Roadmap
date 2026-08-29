using DotNetLab.Contracts;
using DotNetLab.Web.Client.Services;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;

// Host de WebAssembly: este Main SOLO se ejecuta cuando el componente ya corre en el navegador.
// Durante el render en servidor (modo Auto, primera visita) este código no interviene.
var builder = WebAssemblyHostBuilder.CreateDefault(args);

// HttpClient apuntando al ORIGEN de la propia página. Las llamadas salen contra /api/... del
// host Blazor, que las reenvía a la Web API: así el navegador nunca ve la URL interna del
// contenedor de la API, no hay CORS y el secreto compartido se queda en el servidor.
builder.Services.AddScoped(_ => new HttpClient
{
    BaseAddress = new Uri(builder.HostEnvironment.BaseAddress)
});

// Deserialización con el contexto generado en compilación: sin reflexión, lo que además
// sobrevive al recorte de IL (trimming) que aplica la publicación de Blazor WASM.
builder.Services.AddSingleton(LabJsonSerializerContext.Default);

// Cliente tipado del laboratorio; Scoped porque depende del HttpClient scoped.
builder.Services.AddScoped<LabApiClient>();

// RunAsync arranca el bucle de renderizado sobre el runtime .NET compilado a WASM.
await builder.Build().RunAsync();
