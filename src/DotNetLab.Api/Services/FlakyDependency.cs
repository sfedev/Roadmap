using System.Collections.Concurrent;

namespace DotNetLab.Api.Services;

/// <summary>Excepción de la dependencia simulada; es lo que las políticas deciden manejar.</summary>
// Tipo propio y no InvalidOperationException: el predicado de Polly filtra POR TIPO, y usar
// una excepción genérica haría que la política reintentara también errores de programación.
public sealed class FlakyDependencyException(string message) : Exception(message);

/// <summary>
/// Dependencia poco fiable y DETERMINISTA: falla exactamente las N primeras llamadas de cada
/// correlación y a partir de ahí responde bien.
///
/// El determinismo es el punto: una dependencia aleatoria produciría demostraciones distintas
/// en cada ejecución y haría imposible explicar la cronología de reintentos que se muestra.
/// </summary>
public sealed class FlakyDependency
{
    // Llamadas ya realizadas por correlación. Cada ejecución de la demo usa una clave nueva,
    // así que dos usuarios simultáneos no se interfieren.
    private readonly ConcurrentDictionary<Guid, int> _callCounts = new();

    /// <summary>
    /// Simula la llamada. Lanza <see cref="FlakyDependencyException"/> mientras no se hayan
    /// consumido los fallos configurados.
    /// </summary>
    public ValueTask<string> CallAsync(Guid correlationId, int failuresBeforeSuccess, CancellationToken cancellationToken)
    {
        // El token se comprueba aunque no haya I/O real: una operación cancelada debe dejar de
        // trabajar de inmediato, y así la demo respeta el timeout total del pipeline.
        cancellationToken.ThrowIfCancellationRequested();

        // AddOrUpdate es atómico: dos intentos concurrentes del mismo pipeline no pueden
        // quedarse con el mismo número de llamada.
        var attempt = _callCounts.AddOrUpdate(correlationId, 1, (_, current) => current + 1);

        if (attempt <= failuresBeforeSuccess)
        {
            // ValueTask.FromException y no 'throw': lanzar de forma síncrona desde un método que
            // devuelve ValueTask rompe la expectativa del llamante, que espera la excepción al
            // hacer await y no al invocar.
            return ValueTask.FromException<string>(
                new FlakyDependencyException($"Fallo simulado #{attempt} de {failuresBeforeSuccess}."));
        }

        // Ruta de éxito completamente síncrona: no asigna ningún Task en el Heap.
        return ValueTask.FromResult($"Respuesta correcta en el intento #{attempt}.");
    }

    /// <summary>Libera el contador de una correlación terminada.</summary>
    // Sin esto, el diccionario crecería con cada ejecución de la demo hasta agotar la memoria.
    public void Forget(Guid correlationId) => _callCounts.TryRemove(correlationId, out _);
}
