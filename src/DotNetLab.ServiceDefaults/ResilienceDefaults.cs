using Microsoft.Extensions.DependencyInjection;
using Polly;

namespace DotNetLab.ServiceDefaults;

/// <summary>
/// Política de resiliencia estándar para toda llamada HTTP saliente entre servicios.
/// Los valores están calibrados para comunicación DENTRO del cluster (latencias de
/// milisegundos), no para APIs de terceros a través de Internet.
/// </summary>
public static class ResilienceDefaults
{
    /// <summary>
    /// Aplica el pipeline estándar de Polly v8 al cliente HTTP indicado.
    /// El orden de las estrategias lo fija el propio handler y es deliberado:
    /// timeout total → retry → circuit breaker → timeout por intento.
    /// </summary>
    public static IHttpClientBuilder AddLabResilience(this IHttpClientBuilder builder)
    {
        builder.AddStandardResilienceHandler(options =>
        {
            // --- Timeout por intento ---------------------------------------------------
            // Corta una petición individual que se quedó colgada. Debe ser el más corto de
            // los dos timeouts: es el que permite que el retry llegue a ejecutarse.
            options.AttemptTimeout.Timeout = TimeSpan.FromSeconds(10);

            // --- Timeout total ---------------------------------------------------------
            // Techo absoluto para intento + reintentos + esperas. Sin él, tres reintentos con
            // backoff exponencial podrían mantener una petición viva varios minutos.
            options.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(45);

            // --- Reintentos ------------------------------------------------------------
            // Tres reintentos cubren el caso real que justifica reintentar: un pod que se está
            // reciclando durante un despliegue. Más reintentos solo amplifican una caída.
            options.Retry.MaxRetryAttempts = 3;
            // Backoff exponencial: 0,5 s → 1 s → 2 s. Reintentar de inmediato contra un servicio
            // saturado es la receta del "retry storm" que lo termina de tumbar.
            options.Retry.BackoffType = DelayBackoffType.Exponential;
            options.Retry.Delay = TimeSpan.FromMilliseconds(500);
            // Jitter: sin él, N réplicas que fallan a la vez reintentan a la vez, y el pico se
            // repite exactamente igual en cada ronda. El jitter dispersa esos picos.
            options.Retry.UseJitter = true;

            // --- Circuit breaker -------------------------------------------------------
            // Si más de la mitad de las peticiones fallan, el circuito se abre y las siguientes
            // fallan al instante, sin tocar la red: se le da al servicio caído margen para
            // recuperarse en vez de rematarlo a peticiones.
            options.CircuitBreaker.FailureRatio = 0.5;
            // Ventana de observación. La librería EXIGE que sea al menos el doble del timeout
            // por intento; con 10 s de intento, 30 s deja margen suficiente.
            options.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(30);
            // Mínimo de peticiones en la ventana antes de decidir: sin este umbral, un único
            // fallo aislado con poco tráfico abriría el circuito (1 de 1 = 100% de error).
            options.CircuitBreaker.MinimumThroughput = 8;
            // Tiempo que el circuito permanece abierto antes de dejar pasar una petición de prueba.
            options.CircuitBreaker.BreakDuration = TimeSpan.FromSeconds(15);
        });

        return builder;
    }
}
