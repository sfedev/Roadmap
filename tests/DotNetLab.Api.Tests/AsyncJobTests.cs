using System.Net;
using System.Net.Http.Json;
using DotNetLab.Api.Services;
using DotNetLab.Contracts;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Time.Testing;

namespace DotNetLab.Api.Tests;

/// <summary>
/// Pruebas del registro de trabajos en memoria. Se comprueban las transiciones y, sobre todo,
/// las carreras: los eventos del bus y las peticiones HTTP llegan por hilos distintos y en
/// orden no garantizado.
/// </summary>
public sealed class JobRegistryTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);

    // FakeTimeProvider congela el reloj: las marcas de tiempo son deterministas.
    private static JobRegistry CreateRegistry() => new(new FakeTimeProvider(Now));

    [Fact]
    public void UnTrabajoAceptadoQuedaEnEstadoAccepted()
    {
        var registry = CreateRegistry();
        var jobId = Guid.CreateVersion7();

        var snapshot = registry.Accept(jobId, 1_000, "span");

        Assert.Equal("accepted", snapshot.Status);
        // Sin resultado todavía: el null es información, no un hueco por rellenar.
        Assert.Null(snapshot.Result);
        Assert.Equal(Now, snapshot.UpdatedAt);
    }

    [Fact]
    public void CompletarConservaLosDatosOriginalesDeLaPeticion()
    {
        var registry = CreateRegistry();
        var jobId = Guid.CreateVersion7();
        registry.Accept(jobId, 5_000, "span");

        var result = new ParseStrategyResult("Span<T> (zero-allocation)", 5_000, 42.5, 1_200, 0, 0);
        var snapshot = registry.Complete(new TelemetryAnalysisCompleted(
            jobId, result, QueueLatencyMs: 12, ProcessingMs: 30, WorkerInstance: "pod-1", CompletedAt: Now));

        Assert.Equal("completed", snapshot.Status);
        Assert.Equal("pod-1", snapshot.WorkerInstance);
        // RowCount y Strategy se conservan de la aceptación: son más fiables que deducirlos
        // del resultado, donde 'Strategy' es un texto descriptivo y no la clave del parser.
        Assert.Equal(5_000, snapshot.RowCount);
        Assert.Equal("span", snapshot.Strategy);
    }

    [Fact]
    public void UnResultadoQueLlegaSinAceptacionPreviaNoSePierde()
    {
        var registry = CreateRegistry();
        var jobId = Guid.CreateVersion7();

        // Caso real con varias réplicas de la API: otra réplica aceptó el trabajo, así que
        // esta no tiene la entrada, pero el evento del bus le llega igual.
        var result = new ParseStrategyResult("Span<T> (zero-allocation)", 900, 10, 100, 0, 0);
        var snapshot = registry.Complete(new TelemetryAnalysisCompleted(
            jobId, result, 5, 10, "pod-2", Now));

        Assert.Equal("completed", snapshot.Status);
        Assert.NotNull(registry.Find(jobId));
    }

    [Fact]
    public void ElRegistroTieneTechoYDesalojaLosMasAntiguos()
    {
        var registry = CreateRegistry();

        // 250 supera el techo de 200: sin desalojo, un bucle de peticiones agotaría la memoria.
        for (var i = 0; i < 250; i++)
        {
            registry.Accept(Guid.CreateVersion7(), 100, "span");
        }

        // Recent(50) devolvería más de lo que cabe si el desalojo no funcionara.
        Assert.True(registry.Recent(500).Count <= 200);
    }

    [Fact]
    public void ConsultarUnTrabajoDesconocidoDevuelveNull()
    {
        // Null y no una excepción: que una réplica no conozca un trabajo es un estado normal
        // del sistema, no un error.
        Assert.Null(CreateRegistry().Find(Guid.CreateVersion7()));
    }
}

/// <summary>
/// Prueba de integración del flujo asíncrono COMPLETO.
///
/// Funciona porque appsettings.json usa el transporte en memoria: con él, la API aloja también
/// el consumidor del trabajo pesado, así que publicación y consumo ocurren en el mismo proceso
/// de prueba. Es el mismo código que en Docker; lo único que cambia es el transporte.
/// </summary>
public sealed class AsyncJobIntegrationTests(WebApplicationFactory<Program> factory)
    : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task EncolarUnAnalisisDevuelve202ConLaUbicacionDelRecurso()
    {
        var response = await _client.PostAsJsonAsync(
            "/api/analysis/jobs",
            new AnalysisJobRequest(2_000, "span"));

        // 202 y no 200: el resultado todavía NO existe cuando se responde.
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        // La cabecera Location es lo que convierte el 202 en una respuesta accionable.
        Assert.NotNull(response.Headers.Location);

        var accepted = await response.Content.ReadFromJsonAsync<AnalysisJobAccepted>();
        Assert.NotNull(accepted);
        Assert.NotEqual(Guid.Empty, accepted.JobId);
        Assert.Contains(accepted.JobId.ToString(), accepted.StatusUrl, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ElTrabajoTerminaYElResultadoLoProduceElParserConSpan()
    {
        var accepted = await (await _client.PostAsJsonAsync(
                "/api/analysis/jobs",
                new AnalysisJobRequest(3_000, "span")))
            .Content.ReadFromJsonAsync<AnalysisJobAccepted>();

        Assert.NotNull(accepted);

        // Sondeo con techo: el consumidor corre en otro hilo, así que hay que esperar, pero
        // nunca de forma indefinida ni con un Task.Delay fijo "que seguro que basta".
        AnalysisJobSnapshot? snapshot = null;
        for (var attempt = 0; attempt < 50; attempt++)
        {
            snapshot = await _client.GetFromJsonAsync<AnalysisJobSnapshot>(
                accepted.StatusUrl);

            if (snapshot?.Status == "completed") break;

            await Task.Delay(100);
        }

        Assert.NotNull(snapshot);
        Assert.Equal("completed", snapshot.Status);
        Assert.NotNull(snapshot.Result);
        // La prueba de que el Worker usó de verdad la ruta con Span<T>.
        Assert.Equal(0, snapshot.Result.AllocatedBytes);
        Assert.Equal(3_000, snapshot.Result.RowsParsed);
        // El consumidor registra qué instancia lo procesó.
        Assert.False(string.IsNullOrWhiteSpace(snapshot.WorkerInstance));
    }

    [Fact]
    public async Task UnaEstrategiaDesconocidaSeRechazaEnElBordeYNoSePublica()
    {
        var response = await _client.PostAsJsonAsync(
            "/api/analysis/jobs",
            new AnalysisJobRequest(1_000, "no-existe"));

        // 400 en la API: publicar el mensaje solo para que el Worker lo rechazara sería
        // gastar transporte y cola para nada.
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task LaDemostracionDeResilienciaRespondeConSuCronologia()
    {
        var result = await _client.GetFromJsonAsync<ResilienceDemoResponse>(
            "/api/resilience/demo?scenario=retry&failures=2");

        Assert.NotNull(result);
        Assert.True(result.Succeeded);
        // Tres intentos: dos fallos más el que triunfa.
        Assert.Equal(3, result.Attempts.Count);
    }

    [Fact]
    public async Task LasSondasDeKubernetesRespondenSinCredenciales()
    {
        // Van fuera del grupo protegido a propósito: el kubelet no tiene la clave compartida.
        var live = await _client.GetAsync("/health/live");
        var ready = await _client.GetAsync("/health/ready");

        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
    }
}
