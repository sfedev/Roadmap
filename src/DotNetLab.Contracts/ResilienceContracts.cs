namespace DotNetLab.Contracts;

/// <summary>Un intento concreto dentro de un pipeline de resiliencia.</summary>
// readonly record struct: la lista de intentos es corta y se serializa de inmediato,
// así que no merece una asignación en el Heap por elemento.
public readonly record struct ResilienceAttempt(
    // Número de intento empezando en 1; el 1 es la ejecución original, no un reintento.
    int Attempt,
    // "success" | "failure" | "circuit-open": resultado observado en ESE intento.
    string Outcome,
    // Espera aplicada ANTES de este intento, en milisegundos. Es donde se ve el backoff
    // exponencial y el jitter: los valores no son 500/1000/2000 exactos, sino dispersos.
    double DelayBeforeMs,
    // Instante del intento medido desde el arranque del pipeline.
    double ElapsedMs,
    // Detalle de la excepción o del éxito, para mostrarlo en la UI sin adivinar.
    string Detail);

/// <summary>Resultado de ejecutar una demostración de políticas de Polly.</summary>
public sealed record ResilienceDemoResponse(
    // Escenario ejecutado: "retry" (se recupera) o "circuit-breaker" (se rinde y abre).
    string Scenario,
    // Fallos configurados antes de que la dependencia simulada empiece a responder bien.
    int ConfiguredFailures,
    // true si el pipeline consiguió una respuesta correcta dentro de su presupuesto.
    bool Succeeded,
    // Intentos realmente ejecutados. Con reintentos exponenciales suele ser menor que el máximo.
    int TotalAttempts,
    // Duración total del pipeline, esperas incluidas.
    double TotalElapsedMs,
    // Excepción final o mensaje de éxito.
    string FinalOutcome,
    // Estado del circuito al terminar: "closed" | "open" | "half-open".
    string CircuitState,
    // Cronología completa, que es lo que hace visible el backoff en la UI.
    IReadOnlyList<ResilienceAttempt> Attempts,
    // Conclusión ya redactada en servidor para la tarjeta del portal.
    string Insight);
