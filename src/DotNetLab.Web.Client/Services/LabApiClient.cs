using System.Net.Http.Json;
using System.Text.Json;
using DotNetLab.Contracts;

namespace DotNetLab.Web.Client.Services;

/// <summary>
/// Cliente tipado de la Web API. Es el único punto del frontend que conoce rutas y query
/// strings: los componentes trabajan con records, no con URLs.
/// Funciona igual en WebAssembly y en el render de servidor porque en ambos casos el
/// HttpClient inyectado lleva ya la BaseAddress correcta para ese entorno.
/// </summary>
// Primary constructor: HttpClient y las opciones de JSON llegan del contenedor.
public sealed class LabApiClient(HttpClient http, LabJsonSerializerContext jsonContext)
{
    // Opciones que apuntan al resolver generado: evita reflexión y sobrevive al trimming.
    private JsonSerializerOptions SerializerOptions => jsonContext.Options;

    /// <summary>Ejecuta la comparativa Span vs string.Split para el número de filas indicado.</summary>
    public Task<SpanDemoResponse?> GetSpanDemoAsync(int rows, CancellationToken cancellationToken = default)
        // InvariantCulture en la interpolación: un separador decimal local rompería la query string.
        => http.GetFromJsonAsync<SpanDemoResponse>(
            FormattableString.Invariant($"api/performance/span-demo?rows={rows}"),
            SerializerOptions,
            cancellationToken);

    /// <summary>Demo de records: igualdad por valor, 'with' y deconstrucción.</summary>
    public Task<RecordsDemoResponse?> GetRecordsDemoAsync(CancellationToken cancellationToken = default)
        => http.GetFromJsonAsync<RecordsDemoResponse>("api/language/records", SerializerOptions, cancellationToken);

    /// <summary>Clasifica una lectura con el switch de patrones del backend.</summary>
    public Task<PatternMatchResponse?> GetPatternMatchAsync(
        string sensorId, double value, int battery, string series, CancellationToken cancellationToken = default)
    {
        // Uri.EscapeDataString sobre cada valor: un sensorId con '&' partiría la query en dos.
        // Se escapan antes para que la interpolación quede en UNA sola expresión: concatenar
        // dos strings interpolados produce un string, no un FormattableString, y perdería la
        // cultura invariante que fuerza el '.' decimal en 'value'.
        var id = Uri.EscapeDataString(sensorId);
        var samples = Uri.EscapeDataString(series);

        var query = FormattableString.Invariant($"api/language/pattern-match?sensorId={id}&value={value}&battery={battery}&series={samples}");

        return http.GetFromJsonAsync<PatternMatchResponse>(query, SerializerOptions, cancellationToken);
    }

    /// <summary>Resumen agregado del pipeline de Channels (endpoint que devuelve ValueTask).</summary>
    public Task<ChannelSummary?> GetChannelSummaryAsync(int events, int capacity, CancellationToken cancellationToken = default)
        => http.GetFromJsonAsync<ChannelSummary>(
            FormattableString.Invariant($"api/concurrency/channel-summary?events={events}&capacity={capacity}"),
            SerializerOptions,
            cancellationToken);

    /// <summary>
    /// Consume el stream de eventos del Channel según van llegando.
    /// GetFromJsonAsAsyncEnumerable deserializa el array JSON de forma incremental: cada
    /// elemento se entrega al componente sin esperar a que la respuesta termine.
    /// </summary>
    public IAsyncEnumerable<ChannelEvent> StreamChannelEventsAsync(
        // Sin [EnumeratorCancellation]: este método no es un iterador, solo reenvía el token
        // al helper de System.Net.Http.Json, que ya lo propaga al enumerador que construye.
        int events, int capacity, int consumerDelayMs, CancellationToken cancellationToken = default)
        => http.GetFromJsonAsAsyncEnumerable<ChannelEvent>(
            FormattableString.Invariant(
                $"api/concurrency/channel-stream?events={events}&capacity={capacity}&consumerDelayMs={consumerDelayMs}"),
            SerializerOptions,
            cancellationToken);

    /// <summary>Resuelve un parser por clave en el contenedor de DI del servidor y lo ejecuta.</summary>
    public Task<KeyedServiceResponse?> GetKeyedServiceAsync(string key, int rows, CancellationToken cancellationToken = default)
        => http.GetFromJsonAsync<KeyedServiceResponse>(
            FormattableString.Invariant($"api/di/keyed-services?key={Uri.EscapeDataString(key)}&rows={rows}"),
            SerializerOptions,
            cancellationToken);

    /// <summary>Compara Singleton, Scoped y Transient dentro de una misma petición HTTP.</summary>
    public Task<LifetimesDemoResponse?> GetLifetimesAsync(CancellationToken cancellationToken = default)
        => http.GetFromJsonAsync<LifetimesDemoResponse>("api/di/lifetimes", SerializerOptions, cancellationToken);

    /// <summary>Estado del servicio y de su contenedor auxiliar.</summary>
    public Task<HealthResponse?> GetHealthAsync(CancellationToken cancellationToken = default)
        => http.GetFromJsonAsync<HealthResponse>("api/diagnostics/health", SerializerOptions, cancellationToken);
}
