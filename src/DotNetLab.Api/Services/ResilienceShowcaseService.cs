using System.Diagnostics;
using DotNetLab.Contracts;
using DotNetLab.ServiceDefaults;
using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;

namespace DotNetLab.Api.Services;

/// <summary>
/// Ejecuta políticas de Polly v8 de verdad contra una dependencia simulada y devuelve la
/// cronología completa: cuántos intentos, cuánto esperó antes de cada uno y cómo terminó.
///
/// Las esperas que se muestran están MEDIDAS con Stopwatch, no calculadas a partir de la
/// configuración: es la única forma de que se vea el jitter, que por definición no es predecible.
/// </summary>
public sealed class ResilienceShowcaseService(FlakyDependency dependency)
{
    // Escenarios admitidos; la lista blanca evita que la query string elija rutas no previstas.
    public const string RetryScenario = "retry";
    public const string CircuitBreakerScenario = "circuit-breaker";

    /// <summary>Ejecuta el escenario indicado y devuelve la cronología de intentos.</summary>
    public async Task<ResilienceDemoResponse> RunAsync(
        string scenario, int failures, CancellationToken cancellationToken)
    {
        // Correlación propia por ejecución: aísla esta demo de cualquier otra en curso.
        var correlationId = Guid.CreateVersion7();

        try
        {
            // Switch expression sobre el escenario: añadir uno nuevo es añadir una rama y un método.
            return scenario switch
            {
                CircuitBreakerScenario => await RunCircuitBreakerAsync(correlationId, cancellationToken),
                _ => await RunRetryAsync(correlationId, failures, cancellationToken)
            };
        }
        finally
        {
            // finally: el contador se libera aunque la ejecución termine en excepción.
            dependency.Forget(correlationId);
        }
    }

    /// <summary>
    /// Escenario 1: la dependencia falla N veces y luego funciona. El retry con backoff
    /// exponencial y jitter debe conseguir la respuesta sin que el usuario note nada.
    /// </summary>
    private async Task<ResilienceDemoResponse> RunRetryAsync(
        Guid correlationId, int failures, CancellationToken cancellationToken)
    {
        var attempts = new List<ResilienceAttempt>();
        var start = Stopwatch.GetTimestamp();

        // Instante del intento anterior: la resta da la espera REAL aplicada por la política,
        // que con jitter no coincide con el valor nominal configurado.
        var lastAttemptAt = 0d;

        var pipeline = new ResiliencePipelineBuilder()
            .AddRetry(new RetryStrategyOptions
            {
                // Solo se reintenta la excepción de la dependencia. Un NullReferenceException
                // es un bug: reintentarlo lo repetiría cuatro veces y ocultaría la causa.
                ShouldHandle = new PredicateBuilder().Handle<FlakyDependencyException>(),
                // Cuatro reintentos = hasta cinco intentos en total.
                MaxRetryAttempts = 4,
                // Base corta para que la demo quepa en una petición HTTP. La media de las esperas
                // sigue 150/300/600/1200 ms, pero con jitter los valores concretos se dispersan
                // alrededor de esa media y no salen ordenados.
                Delay = TimeSpan.FromMilliseconds(150),
                BackoffType = DelayBackoffType.Exponential,
                // Jitter: dispersa los reintentos de clientes que fallaron a la vez y evita que
                // todos vuelvan a golpear el servicio caído en el mismo instante.
                UseJitter = true,
                // El callback se ejecuta ENTRE intentos: es donde se captura la cronología.
                OnRetry = args =>
                {
                    var elapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds;

                    attempts.Add(new ResilienceAttempt(
                        // args.AttemptNumber es 0 en el primer fallo: se suma 1 para numerar desde 1.
                        Attempt: args.AttemptNumber + 1,
                        Outcome: "failure",
                        // Espera transcurrida desde el intento anterior, medida y no supuesta.
                        DelayBeforeMs: Math.Round(elapsed - lastAttemptAt, 1),
                        ElapsedMs: Math.Round(elapsed, 1),
                        Detail: args.Outcome.Exception?.Message ?? "sin excepción"));

                    lastAttemptAt = elapsed;

                    LabTelemetry.ResilienceAttempts.Add(
                        1, new KeyValuePair<string, object?>("outcome", "retry"));

                    // ValueTask completado: el callback no hace I/O, así que no asigna nada.
                    return default;
                }
            })
            .Build();

        var succeeded = false;
        string finalOutcome;

        try
        {
            // ExecuteAsync recibe el token para que la cancelación atraviese toda la cadena.
            var result = await pipeline.ExecuteAsync(
                async ct => await dependency.CallAsync(correlationId, failures, ct),
                cancellationToken);

            succeeded = true;
            finalOutcome = result;

            attempts.Add(new ResilienceAttempt(
                Attempt: attempts.Count + 1,
                Outcome: "success",
                DelayBeforeMs: Math.Round(Stopwatch.GetElapsedTime(start).TotalMilliseconds - lastAttemptAt, 1),
                ElapsedMs: Math.Round(Stopwatch.GetElapsedTime(start).TotalMilliseconds, 1),
                Detail: result));

            LabTelemetry.ResilienceAttempts.Add(1, new KeyValuePair<string, object?>("outcome", "success"));
        }
        // Solo se captura la excepción de la dependencia: si se agotan los reintentos, Polly
        // relanza la última. Cualquier otra excepción debe propagarse como el bug que es.
        catch (FlakyDependencyException ex)
        {
            finalOutcome = $"Reintentos agotados: {ex.Message}";
            LabTelemetry.ResilienceAttempts.Add(1, new KeyValuePair<string, object?>("outcome", "exhausted"));
        }

        var totalMs = Math.Round(Stopwatch.GetElapsedTime(start).TotalMilliseconds, 1);

        return new ResilienceDemoResponse(
            Scenario: RetryScenario,
            ConfiguredFailures: failures,
            Succeeded: succeeded,
            TotalAttempts: attempts.Count,
            TotalElapsedMs: totalMs,
            FinalOutcome: finalOutcome,
            // En este escenario no hay breaker, así que el circuito nunca deja de estar cerrado.
            CircuitState: "closed",
            Attempts: attempts,
            Insight: succeeded
                ? $"El retry absorbió {failures} fallo(s) en {totalMs} ms y el usuario recibió una " +
                  "respuesta correcta sin enterarse de nada. Las esperas NO son 150/300/600 ms ni " +
                  "tienen por qué crecer una a una: con UseJitter, Polly aplica jitter " +
                  "decorrelacionado, que aleatoriza cada espera alrededor de una media que sí crece " +
                  "de forma exponencial. Esa dispersión es justo lo que evita que mil clientes " +
                  "reintenten en el mismo milisegundo."
                : $"Ni con 5 intentos se pudo completar. Con {failures} fallos configurados, el " +
                  "presupuesto de reintentos se agota y el error llega al usuario: reintentar no " +
                  "arregla una caída prolongada, solo un fallo transitorio.");
    }

    /// <summary>
    /// Escenario 2: la dependencia está caída del todo. Tras unos fallos, el circuito se abre y
    /// las llamadas siguientes fallan AL INSTANTE, sin tocar la dependencia.
    /// </summary>
    private async Task<ResilienceDemoResponse> RunCircuitBreakerAsync(Guid correlationId, CancellationToken cancellationToken)
    {
        var attempts = new List<ResilienceAttempt>();
        var start = Stopwatch.GetTimestamp();

        // El proveedor de estado permite consultar el circuito desde fuera del pipeline; sin él
        // habría que deducir el estado por el tipo de excepción, que es mucho menos fiable.
        var stateProvider = new CircuitBreakerStateProvider();

        var pipeline = new ResiliencePipelineBuilder()
            .AddCircuitBreaker(new CircuitBreakerStrategyOptions
            {
                ShouldHandle = new PredicateBuilder().Handle<FlakyDependencyException>(),
                // Con la mitad de fallos en la ventana basta para abrir.
                FailureRatio = 0.5,
                // Mínimo de llamadas antes de decidir: sin él, el primer fallo (1 de 1 = 100%)
                // abriría el circuito y la demo no mostraría la fase de "acumulación".
                MinimumThroughput = 4,
                // Ventana de muestreo corta para que la demo quepa en una petición HTTP.
                SamplingDuration = TimeSpan.FromSeconds(10),
                // Tiempo que permanece abierto antes de probar de nuevo (half-open).
                BreakDuration = TimeSpan.FromSeconds(5),
                StateProvider = stateProvider
            })
            .Build();

        // 10 llamadas: suficientes para superar el umbral mínimo y ver el circuito abrirse.
        const int TotalCalls = 10;

        for (var i = 1; i <= TotalCalls; i++)
        {
            var attemptStart = Stopwatch.GetElapsedTime(start).TotalMilliseconds;

            try
            {
                // int.MaxValue fallos configurados = la dependencia nunca se recupera.
                await pipeline.ExecuteAsync(
                    async ct => await dependency.CallAsync(correlationId, int.MaxValue, ct),
                    cancellationToken);
            }
            // BrokenCircuitException se lanza SIN llamar a la dependencia: es el fail-fast que
            // protege al servicio caído. Se distingue del fallo normal porque el mensaje y el
            // tiempo transcurrido (prácticamente 0 ms) son radicalmente distintos.
            catch (BrokenCircuitException)
            {
                attempts.Add(new ResilienceAttempt(
                    Attempt: i,
                    Outcome: "circuit-open",
                    DelayBeforeMs: 0,
                    ElapsedMs: Math.Round(Stopwatch.GetElapsedTime(start).TotalMilliseconds, 1),
                    Detail: "Circuito abierto: la llamada ni siquiera salió."));

                LabTelemetry.ResilienceAttempts.Add(1, new KeyValuePair<string, object?>("outcome", "circuit-open"));
                continue;
            }
            catch (FlakyDependencyException ex)
            {
                attempts.Add(new ResilienceAttempt(
                    Attempt: i,
                    Outcome: "failure",
                    DelayBeforeMs: Math.Round(Stopwatch.GetElapsedTime(start).TotalMilliseconds - attemptStart, 1),
                    ElapsedMs: Math.Round(Stopwatch.GetElapsedTime(start).TotalMilliseconds, 1),
                    Detail: ex.Message));

                LabTelemetry.ResilienceAttempts.Add(1, new KeyValuePair<string, object?>("outcome", "failure"));
            }
        }

        var totalMs = Math.Round(Stopwatch.GetElapsedTime(start).TotalMilliseconds, 1);
        // Cuántas llamadas se cortaron sin salir: es la cifra que resume el ahorro.
        var shortCircuited = attempts.Count(a => a.Outcome == "circuit-open");

        return new ResilienceDemoResponse(
            Scenario: CircuitBreakerScenario,
            ConfiguredFailures: TotalCalls,
            Succeeded: false,
            TotalAttempts: attempts.Count,
            TotalElapsedMs: totalMs,
            FinalOutcome: $"{shortCircuited} de {TotalCalls} llamadas se cortaron sin salir.",
            // ToString() del enum en minúsculas para que la UI no tenga que traducirlo.
            CircuitState: stateProvider.CircuitState.ToString().ToLowerInvariant(),
            Attempts: attempts,
            Insight: $"Tras {TotalCalls - shortCircuited} fallos reales el circuito se abrió y las " +
                     $"{shortCircuited} llamadas restantes fallaron en microsegundos, sin tocar la " +
                     "dependencia. Eso es lo que evita que un servicio caído arrastre a los que " +
                     "dependen de él: sin breaker, cada llamada esperaría su timeout completo.");
    }
}
