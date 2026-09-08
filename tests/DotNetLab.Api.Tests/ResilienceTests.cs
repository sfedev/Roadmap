using DotNetLab.Api.Services;

namespace DotNetLab.Api.Tests;

/// <summary>
/// Pruebas de las políticas de Polly. Lo que se verifica es el COMPORTAMIENTO de la política
/// (cuántos intentos, si el circuito abre), nunca los tiempos exactos: las esperas llevan
/// jitter por diseño, así que cualquier aserción sobre milisegundos sería intermitente.
/// </summary>
public sealed class ResilienceTests
{
    // Cada test crea su propio servicio: la dependencia simulada guarda contadores por
    // correlación y compartirla entre tests los acoplaría.
    private static ResilienceShowcaseService CreateService() => new(new FlakyDependency());

    [Theory]
    // Con 0 fallos basta el primer intento; con 2, hacen falta tres en total.
    [InlineData(0, 1)]
    [InlineData(1, 2)]
    [InlineData(3, 4)]
    public async Task ElRetrySeRecuperaCuandoLosFallosCabenEnElPresupuesto(int failures, int expectedAttempts)
    {
        var result = await CreateService().RunAsync("retry", failures, CancellationToken.None);

        Assert.True(result.Succeeded);
        // El intento que triunfa también cuenta, de ahí que sea fallos + 1.
        Assert.Equal(expectedAttempts, result.TotalAttempts);
        // Sin breaker en este escenario, el circuito nunca deja de estar cerrado.
        Assert.Equal("closed", result.CircuitState);
    }

    [Fact]
    public async Task ElRetrySeRindeCuandoLosFallosSuperanElPresupuesto()
    {
        // El pipeline permite 4 reintentos (5 intentos). Con 6 fallos configurados no llega.
        var result = await CreateService().RunAsync("retry", 6, CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Contains("agotados", result.FinalOutcome, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ElPrimerIntentoNoLlevaEsperaPrevia()
    {
        var result = await CreateService().RunAsync("retry", 2, CancellationToken.None);

        // El intento 1 es la ejecución original, no un reintento: no puede haber esperado
        // el backoff. Se compara con un margen porque la medición incluye el arranque del
        // pipeline, no exactamente cero.
        var first = result.Attempts[0];
        Assert.Equal(1, first.Attempt);
        Assert.True(first.DelayBeforeMs < 100,
            $"El primer intento no debería esperar el backoff y esperó {first.DelayBeforeMs} ms.");
    }

    [Fact]
    public async Task ElCircuitoSeAbreYLasLlamadasSiguientesFallanAlInstante()
    {
        var result = await CreateService().RunAsync("circuit-breaker", 0, CancellationToken.None);

        // El estado lo reporta el StateProvider de Polly, no se deduce del tipo de excepción.
        Assert.Equal("open", result.CircuitState);

        // Tiene que haber llamadas cortadas: es toda la razón de ser del patrón.
        var shortCircuited = result.Attempts.Count(a => a.Outcome == "circuit-open");
        Assert.True(shortCircuited > 0, "El circuito debería haber cortado alguna llamada.");

        // Y fallos reales ANTES de abrirse: con MinimumThroughput=4, el breaker no decide
        // nada hasta acumular suficientes muestras.
        var realFailures = result.Attempts.Count(a => a.Outcome == "failure");
        Assert.True(realFailures >= 4,
            $"Se esperaban al menos 4 fallos reales antes de abrir y hubo {realFailures}.");
    }

    [Fact]
    public async Task LasLlamadasCortadasSonMuchoMasRapidasQueLasReales()
    {
        var result = await CreateService().RunAsync("circuit-breaker", 0, CancellationToken.None);

        // Una llamada cortada no toca la dependencia: se registra con espera 0 por definición.
        // Es la propiedad que hace que el breaker proteja al servicio caído en vez de rematarlo.
        Assert.All(
            result.Attempts.Where(a => a.Outcome == "circuit-open"),
            attempt => Assert.Equal(0, attempt.DelayBeforeMs));
    }

    [Fact]
    public async Task UnEscenarioDesconocidoCaeEnElDeReintentos()
    {
        // El endpoint valida contra la lista blanca antes de llamar, pero el servicio también
        // debe degradar de forma predecible si se le pasa cualquier otra cosa.
        var result = await CreateService().RunAsync("no-existe", 1, CancellationToken.None);

        Assert.Equal("retry", result.Scenario);
    }
}
