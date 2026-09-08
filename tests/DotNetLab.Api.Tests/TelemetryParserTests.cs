using DotNetLab.Analysis;
using Microsoft.Extensions.Logging.Abstractions;

namespace DotNetLab.Api.Tests;

/// <summary>
/// Pruebas de los dos parsers. Lo importante no es que "vayan rápido" (eso depende de la
/// máquina y haría el test inestable), sino que produzcan EXACTAMENTE el mismo resultado y
/// que la ruta con spans no asigne memoria en el Heap.
/// </summary>
public sealed class TelemetryParserTests
{
    // NullLogger evita ruido en la salida del test sin tener que montar un mock.
    private readonly SpanTelemetryParser _span = new(NullLogger<SpanTelemetryParser>.Instance);
    private readonly NaiveTelemetryParser _naive = new(NullLogger<NaiveTelemetryParser>.Instance);
    private readonly TelemetrySampleGenerator _generator = new();

    [Fact]
    public void AmbasEstrategiasProducenElMismoResultado()
    {
        // El generador usa semilla fija, así que este buffer es idéntico en cada ejecución.
        var payload = _generator.Generate(2_000);

        var spanResult = _span.Parse(payload);
        var naiveResult = _naive.Parse(payload);

        // Si el número de filas difiere, la comparativa del endpoint no sería válida.
        Assert.Equal(naiveResult.RowsParsed, spanResult.RowsParsed);
        Assert.Equal(2_000, spanResult.RowsParsed);
        // Igualdad exacta de la media: ambas rutas parsean los mismos dobles.
        Assert.Equal(naiveResult.AverageValue, spanResult.AverageValue);
    }

    [Fact]
    public void LaRutaConSpansNoAsignaEnElHeap()
    {
        var payload = _generator.Generate(5_000);

        // Calentamiento: la PRIMERA ejecución paga la compilación JIT del método, y ese coste
        // se contabiliza como memoria del hilo. Sin este paso el test sería intermitente.
        _span.Warmup(payload.AsSpan(0, 1_000));

        var result = _span.Parse(payload);

        // Cero bytes exactos: cualquier regresión que introduzca un ToString() lo rompe.
        Assert.Equal(0, result.AllocatedBytes);
    }

    [Fact]
    public void LaRutaNaiveAsignaProporcionalmenteAlBuffer()
    {
        var payload = _generator.Generate(5_000);

        var result = _naive.Parse(payload);

        // Como mínimo copia el buffer entero a un string: 2 bytes por char en UTF-16.
        Assert.True(result.AllocatedBytes > payload.Length * 2,
            $"Se esperaba más de {payload.Length * 2} bytes y se midieron {result.AllocatedBytes}.");
    }

    [Theory]
    // Por debajo del mínimo se sube al suelo; por encima del máximo se recorta al techo.
    [InlineData(0, TelemetrySampleGenerator.MinRows)]
    [InlineData(50, TelemetrySampleGenerator.MinRows)]
    [InlineData(1_000, 1_000)]
    [InlineData(int.MaxValue, TelemetrySampleGenerator.MaxRows)]
    public void ElTamanoSolicitadoSeAcotaAlRangoOperativo(int requested, int expected)
        // Es la defensa contra una query string maliciosa: nadie pide 2.000 millones de filas.
        => Assert.Equal(expected, TelemetrySampleGenerator.ClampRows(requested));

    [Fact]
    public void LasLineasMalFormadasSeDescartanSinRomperElParseo()
    {
        // Buffer manual con una línea válida, una sin separadores y una vacía.
        const string payload = "sensor-0001;2026-01-01T00:00:00Z;20.5\nbasura\n\nsensor-0002;2026-01-01T00:00:01Z;30.5\n";

        var result = _span.Parse(payload);

        // Solo las dos líneas bien formadas cuentan, y la media es la de sus valores.
        Assert.Equal(2, result.RowsParsed);
        Assert.Equal(25.5, result.AverageValue);
    }
}
