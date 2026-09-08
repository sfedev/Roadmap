using System.Collections.Concurrent;
using DotNetLab.Contracts;

namespace DotNetLab.Api.Services;

/// <summary>
/// Registro en memoria del estado de los trabajos asíncronos, para que el endpoint de consulta
/// pueda responder mientras el resultado viaja por el bus.
///
/// LIMITACIÓN CONOCIDA Y DELIBERADA: al ser memoria del proceso, con varias réplicas de la API
/// una consulta puede aterrizar en una réplica que no recibió el evento. En producción esto va
/// a Redis o a una tabla; aquí se acepta porque el canal principal de notificación es SignalR
/// y añadir un almacén distribuido no enseñaría nada nuevo. Está documentado en AGENTS.md.
/// </summary>
public sealed class JobRegistry(TimeProvider timeProvider)
{
    // Techo de entradas: sin él, un bucle de peticiones haría crecer el diccionario sin límite
    // hasta agotar la memoria del contenedor. Es el mismo razonamiento que acotar un Channel.
    private const int MaxEntries = 200;

    // ConcurrentDictionary y no Dictionary+lock: las escrituras llegan desde el hilo de la
    // petición HTTP y desde el hilo del consumidor de mensajes a la vez.
    private readonly ConcurrentDictionary<Guid, AnalysisJobSnapshot> _jobs = new();

    // Orden de llegada para poder desalojar el más antiguo. Una cola aparte evita tener que
    // recorrer el diccionario entero buscando el menor UpdatedAt en cada inserción.
    private readonly ConcurrentQueue<Guid> _insertionOrder = new();

    /// <summary>Registra un trabajo recién aceptado, en estado "accepted".</summary>
    public AnalysisJobSnapshot Accept(Guid jobId, int rowCount, string strategy)
    {
        var snapshot = new AnalysisJobSnapshot(
            JobId: jobId,
            Status: "accepted",
            RowCount: rowCount,
            Strategy: strategy,
            // Sin resultado todavía: el null es información, no un hueco por rellenar.
            Result: null,
            QueueLatencyMs: 0,
            ProcessingMs: 0,
            WorkerInstance: null,
            FailureReason: null,
            UpdatedAt: timeProvider.GetUtcNow());

        _jobs[jobId] = snapshot;
        _insertionOrder.Enqueue(jobId);
        EvictOldest();

        return snapshot;
    }

    /// <summary>Marca un trabajo como completado con las métricas que publicó el Worker.</summary>
    public AnalysisJobSnapshot Complete(TelemetryAnalysisCompleted completed)
    {
        // AddOrUpdate y no un if: el evento de completado puede llegar ANTES de que esta réplica
        // haya registrado la aceptación (otra réplica aceptó el trabajo). En ese caso se crea la
        // entrada desde cero con lo que trae el mensaje, en vez de descartar el resultado.
        var snapshot = _jobs.AddOrUpdate(
            completed.JobId,
            // Fábrica para la clave ausente: se reconstruye lo que se sabe desde el evento.
            _ => new AnalysisJobSnapshot(
                completed.JobId, "completed", completed.Result.RowsParsed, completed.Result.Strategy,
                completed.Result, completed.QueueLatencyMs, completed.ProcessingMs,
                completed.WorkerInstance, null, completed.CompletedAt),
            // Actualización de la entrada existente: 'with' conserva RowCount y Strategy
            // originales, que son más fiables que los deducidos del resultado.
            (_, existing) => existing with
            {
                Status = "completed",
                Result = completed.Result,
                QueueLatencyMs = completed.QueueLatencyMs,
                ProcessingMs = completed.ProcessingMs,
                WorkerInstance = completed.WorkerInstance,
                UpdatedAt = completed.CompletedAt
            });

        return snapshot;
    }

    /// <summary>Marca un trabajo como fallido.</summary>
    public AnalysisJobSnapshot Fail(TelemetryAnalysisFailed failed)
    {
        return _jobs.AddOrUpdate(
            failed.JobId,
            _ => new AnalysisJobSnapshot(
                failed.JobId, "failed", 0, "unknown", null, 0, 0, null, failed.Reason, failed.FailedAt),
            (_, existing) => existing with
            {
                Status = "failed",
                FailureReason = failed.Reason,
                UpdatedAt = failed.FailedAt
            });
    }

    /// <summary>Devuelve un trabajo concreto, o null si esta réplica no lo conoce.</summary>
    public AnalysisJobSnapshot? Find(Guid jobId) =>
        // TryGetValue con patrón 'out var': una sola búsqueda en el diccionario.
        _jobs.TryGetValue(jobId, out var snapshot) ? snapshot : null;

    /// <summary>Últimos trabajos conocidos, del más reciente al más antiguo.</summary>
    public IReadOnlyList<AnalysisJobSnapshot> Recent(int take) =>
        // Values sobre ConcurrentDictionary devuelve una instantánea, así que ordenarla es
        // seguro aunque otro hilo esté escribiendo mientras tanto.
        [.. _jobs.Values.OrderByDescending(j => j.UpdatedAt).Take(take)];

    /// <summary>Elimina las entradas más antiguas cuando se supera el techo.</summary>
    private void EvictOldest()
    {
        // 'while' y no 'if': si varios hilos insertan a la vez, puede haber más de un exceso.
        while (_jobs.Count > MaxEntries && _insertionOrder.TryDequeue(out var oldest))
        {
            // TryRemove ignora el fallo a propósito: que otro hilo lo haya quitado ya es
            // exactamente el resultado buscado.
            _jobs.TryRemove(oldest, out _);
        }
    }
}
