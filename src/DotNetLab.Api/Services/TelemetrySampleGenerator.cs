using System.Globalization;
using System.Text;

namespace DotNetLab.Api.Services;

/// <summary>
/// Genera el buffer de telemetría sintética que consumen los parsers.
/// Se registra como Singleton: no guarda estado por petición y la semilla es fija,
/// así dos ejecuciones con el mismo tamaño producen exactamente el mismo texto.
/// </summary>
public sealed class TelemetrySampleGenerator
{
    // Semilla constante => datos reproducibles => la comparativa span/naive es justa.
    private const int Seed = 20_251_001;

    // Techo de seguridad: 250k filas son ~12 MB de texto, suficiente para ver el efecto
    // sin que una petición maliciosa reserve gigabytes en el contenedor.
    public const int MaxRows = 250_000;

    // Suelo: menos de 100 filas no da señal por encima del ruido del temporizador.
    public const int MinRows = 100;

    /// <summary>Recorta el tamaño pedido por el cliente al rango operativo permitido.</summary>
    // Math.Clamp evita dos ifs y documenta la intención en una línea.
    public static int ClampRows(int requested) => Math.Clamp(requested, MinRows, MaxRows);

    /// <summary>
    /// Construye un documento "sensorId;timestampISO;valor" con una fila por línea.
    /// El coste de esta construcción queda FUERA de la región medida por los parsers:
    /// se genera antes de arrancar el cronómetro para no contaminar las métricas.
    /// </summary>
    public string Generate(int rows)
    {
        // Random con semilla explícita (no Random.Shared) para que el resultado sea determinista.
        var random = new Random(Seed);
        // Capacidad estimada (~48 chars/fila): evita que StringBuilder duplique su buffer N veces.
        var builder = new StringBuilder(rows * 48);
        // Instante base fijo: el timestamp no depende del reloj real, otra fuente de determinismo.
        var baseTime = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        for (var i = 0; i < rows; i++)
        {
            // Id de sensor con formato fijo de 4 dígitos: todas las líneas miden casi lo mismo.
            builder.Append("sensor-").Append((i % 512).ToString("D4", CultureInfo.InvariantCulture));
            builder.Append(';');
            // Timestamp ISO-8601 con 'O': formato redondo y parseable sin ambigüedad.
            builder.Append(baseTime.AddSeconds(i).ToString("O", CultureInfo.InvariantCulture));
            builder.Append(';');
            // Valor en [15.0, 95.0) con 3 decimales; F3 e InvariantCulture fuerzan el '.' decimal.
            builder.Append((15 + random.NextDouble() * 80).ToString("F3", CultureInfo.InvariantCulture));
            // '\n' y no Environment.NewLine: el separador debe ser idéntico en Linux y Windows.
            builder.Append('\n');
        }

        // ToString hace UNA copia final; a partir de aquí el buffer es inmutable y compartible.
        return builder.ToString();
    }
}
