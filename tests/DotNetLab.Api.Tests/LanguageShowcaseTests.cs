using DotNetLab.Api.Services;
using DotNetLab.Contracts;
using Microsoft.Extensions.Time.Testing;

namespace DotNetLab.Api.Tests;

/// <summary>
/// Pruebas del servicio de records y pattern matching. Usan FakeTimeProvider para congelar
/// el reloj: sin él, la rama "lectura del futuro" dependería del instante de ejecución.
/// </summary>
public sealed class LanguageShowcaseTests
{
    // Instante fijo compartido por todos los tests de la clase.
    private static readonly DateTimeOffset Now = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);

    // FakeTimeProvider devuelve siempre 'Now' mientras nadie avance el reloj a mano.
    private readonly LanguageShowcaseService _service = new(new FakeTimeProvider(Now));

    [Fact]
    public void LosRecordsComparanPorValorYNoPorReferencia()
    {
        var result = _service.RunRecordsDemo();

        // Igualdad estructural: el compilador genera Equals comparando campo a campo.
        Assert.True(result.ValueEqualityHolds);
        // 'with' crea un objeto nuevo, así que las referencias NO coinciden.
        Assert.False(result.ReferenceEqualityHolds);
        // Contrato Equals/GetHashCode: dos objetos iguales deben compartir hash.
        Assert.Equal(result.OriginalHashCode, result.CloneHashCode);
    }

    [Theory]
    // Valor alto + batería baja => la primera rama, la más específica del switch.
    [InlineData(95.0, 10, "Critical", 1)]
    // Valor alto con batería sana => cae a la rama del patrón 'or'.
    [InlineData(95.0, 90, "Warning", 2)]
    // Valor normal con batería en la banda baja => rama del rango con 'and'.
    [InlineData(22.0, 12, "Warning", 3)]
    // Todo nominal y con el prefijo esperado => rama del list pattern.
    [InlineData(22.0, 90, "Nominal", 4)]
    public void ElSwitchEligeLaRamaEsperada(double value, int battery, string severity, int branch)
    {
        var reading = new SensorReading("sensor-0042", value, battery, Now);

        var result = _service.Classify(reading, [1.0, 2.0]);

        Assert.Equal(severity, result.Severity);
        // El índice de rama confirma que ganó el patrón previsto y no otro que coincidiera.
        Assert.Equal(branch, result.BranchIndex);
    }

    [Fact]
    public void UnaLecturaDelFuturoCaeEnLaRamaDeGuarda()
    {
        // Dos minutos por delante del reloj congelado: supera el margen de un minuto del 'when'.
        var reading = new SensorReading("otro-sensor", 50, 60, Now.AddMinutes(2));

        var result = _service.Classify(reading, []);

        Assert.Equal("Invalid", result.Severity);
        Assert.Equal(5, result.BranchIndex);
    }

    [Theory]
    // Serie vacía, un solo elemento y subida brusca: las tres formas de list pattern.
    [InlineData(new double[] { }, "Serie vacía")]
    [InlineData(new[] { 42.0 }, "Una sola muestra")]
    [InlineData(new[] { 10.0, 15.0, 40.0 }, "Tendencia ascendente brusca")]
    [InlineData(new[] { 40.0, 15.0, 10.0 }, "Caída brusca")]
    [InlineData(new[] { 20.0, 21.0, 22.0 }, "Serie estable")]
    public void LosListPatternsDiagnosticanLaFormaDeLaSerie(double[] series, string expectedPrefix)
    {
        var reading = new SensorReading("sensor-0001", 22, 90, Now);

        var result = _service.Classify(reading, series);

        // StartsWith y no Equals: el mensaje incluye los valores concretos de la serie.
        Assert.StartsWith(expectedPrefix, result.SeriesDiagnosis, StringComparison.Ordinal);
    }
}
