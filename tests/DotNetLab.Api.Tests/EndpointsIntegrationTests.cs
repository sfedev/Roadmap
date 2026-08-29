using System.Net;
using System.Net.Http.Json;
using DotNetLab.Contracts;
using Microsoft.AspNetCore.Mvc.Testing;

namespace DotNetLab.Api.Tests;

/// <summary>
/// Pruebas de integración: levantan la API completa EN MEMORIA (sin abrir un puerto ni
/// depender de la red) y recorren los endpoints tal y como los llama el frontend.
/// </summary>
// IClassFixture comparte una única instancia del host entre todos los tests de la clase:
// arrancar la aplicación por cada test multiplicaría el tiempo sin aportar aislamiento real.
public sealed class EndpointsIntegrationTests(WebApplicationFactory<Program> factory)
    : IClassFixture<WebApplicationFactory<Program>>
{
    // CreateClient devuelve un HttpClient conectado al servidor de pruebas por memoria.
    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task SpanDemoDevuelveMetricasCoherentes()
    {
        var response = await _client.GetFromJsonAsync<SpanDemoResponse>("/api/performance/span-demo?rows=2000");

        Assert.NotNull(response);
        // Las dos estrategias deben haber leído las mismas filas: si no, el endpoint miente.
        Assert.Equal(response.Span.RowsParsed, response.Naive.RowsParsed);
        // La ruta con spans no asigna, así que el cociente es indefinido y viaja como null.
        Assert.Equal(0, response.Span.AllocatedBytes);
        Assert.True(response.Naive.AllocatedBytes > 0);
    }

    [Fact]
    public async Task ElNumeroDeFilasSeAcotaEnElServidor()
    {
        // Petición abusiva: el servidor debe recortarla a su techo, no intentar servirla.
        var response = await _client.GetFromJsonAsync<SpanDemoResponse>("/api/performance/span-demo?rows=99999999");

        Assert.NotNull(response);
        Assert.Equal(TelemetrySampleGeneratorLimits.MaxRows, response.RowCount);
    }

    [Fact]
    public async Task ElStreamDeChannelsEntregaTodosLosEventosEnOrden()
    {
        // consumerDelayMs=0 para que el test no dependa de esperas reales.
        var events = await _client.GetFromJsonAsync<List<ChannelEvent>>(
            "/api/concurrency/channel-stream?events=12&capacity=3&consumerDelayMs=0");

        Assert.NotNull(events);
        // Ningún evento se pierde por el camino, que es la garantía de FullMode.Wait.
        Assert.Equal(12, events.Count);
        // El canal preserva el orden FIFO: las secuencias llegan 1..12.
        Assert.Equal(Enumerable.Range(1, 12), events.Select(e => e.Sequence));
        // El consumidor es la última etapa del pipeline.
        Assert.All(events, e => Assert.Equal("consumed", e.Stage));
    }

    [Fact]
    public async Task ElResumenSeSirveDesdeCacheEnLaSegundaLlamada()
    {
        const string url = "/api/concurrency/channel-summary?events=2000&capacity=8";

        var first = await _client.GetFromJsonAsync<ChannelSummary>(url);
        var second = await _client.GetFromJsonAsync<ChannelSummary>(url);

        Assert.NotNull(first);
        Assert.NotNull(second);
        // La primera llamada calcula de verdad...
        Assert.Contains("real", first.FullMode, StringComparison.Ordinal);
        // ...y la segunda sale de la caché por la ruta síncrona del ValueTask.
        Assert.Contains("cacheado", second.FullMode, StringComparison.Ordinal);
        // El resultado cacheado es el mismo dato, no un recálculo distinto.
        Assert.Equal(first.Produced, second.Produced);
    }

    [Theory]
    // Cada clave registrada debe resolver a SU implementación concreta.
    [InlineData("span", "SpanTelemetryParser")]
    [InlineData("naive", "NaiveTelemetryParser")]
    public async Task LasClavesResuelvenLaImplementacionEsperada(string key, string expectedType)
    {
        var response = await _client.GetFromJsonAsync<KeyedServiceResponse>($"/api/di/keyed-services?key={key}&rows=1000");

        Assert.NotNull(response);
        Assert.Equal(expectedType, response.ResolvedImplementation);
    }

    [Fact]
    public async Task UnaClaveNoRegistradaDevuelve400YNo500()
    {
        var response = await _client.GetAsync("/api/di/keyed-services?key=no-existe");

        // 400: es un error del cliente, no una excepción del servidor.
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task LosLifetimesSeComportanSegunSuRegistro()
    {
        var response = await _client.GetFromJsonAsync<LifetimesDemoResponse>("/api/di/lifetimes");

        Assert.NotNull(response);
        // Singleton y Scoped devuelven la misma instancia dentro de una petición...
        Assert.Equal(response.SingletonFirst.InstanceId, response.SingletonSecond.InstanceId);
        Assert.Equal(response.ScopedFirst.InstanceId, response.ScopedSecond.InstanceId);
        // ...mientras que Transient crea una nueva en cada resolución.
        Assert.NotEqual(response.TransientFirst.InstanceId, response.TransientSecond.InstanceId);
    }

    [Fact]
    public async Task ElScopedCambiaEntrePeticionesDistintas()
    {
        var first = await _client.GetFromJsonAsync<LifetimesDemoResponse>("/api/di/lifetimes");
        var second = await _client.GetFromJsonAsync<LifetimesDemoResponse>("/api/di/lifetimes");

        Assert.NotNull(first);
        Assert.NotNull(second);
        // El singleton sobrevive a la petición...
        Assert.Equal(first.SingletonFirst.InstanceId, second.SingletonFirst.InstanceId);
        // ...pero cada petición HTTP abre su propio scope.
        Assert.NotEqual(first.ScopedFirst.InstanceId, second.ScopedFirst.InstanceId);
    }

    [Theory]
    // Fuera del rango físico: el endpoint valida ANTES de construir la lectura.
    [InlineData("value=5000")]
    [InlineData("battery=250")]
    public async Task LasLecturasImposiblesSeRechazan(string query)
    {
        var response = await _client.GetAsync($"/api/language/pattern-match?{query}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ElHealthCheckRespondeSinSecretoConfigurado()
    {
        // El endpoint de diagnóstico va en el grupo PÚBLICO: el HEALTHCHECK del contenedor
        // no conoce la clave compartida y aun así debe poder consultarlo.
        var response = await _client.GetFromJsonAsync<HealthResponse>("/api/diagnostics/health");

        Assert.NotNull(response);
        // Sin cache configurada en tests, el servicio se considera sano.
        Assert.Equal("Healthy", response.Status);
    }
}

/// <summary>Constantes replicadas para no depender de tipos internos en las aserciones.</summary>
// Se declara aparte para que el test documente el límite esperado como parte del contrato
// público del endpoint, y no como un detalle de implementación del generador.
internal static class TelemetrySampleGeneratorLimits
{
    public const int MaxRows = 250_000;
}
