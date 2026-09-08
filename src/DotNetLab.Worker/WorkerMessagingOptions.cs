namespace DotNetLab.Worker;

/// <summary>
/// Conexión del Worker al broker. Es un tipo propio y no el MessagingOptions de la API porque
/// las opciones NO son las mismas: el Worker no elige transporte (siempre RabbitMQ) y en cambio
/// necesita el prefetch, que a la API no le sirve de nada.
/// </summary>
public sealed class WorkerMessagingOptions
{
    /// <summary>Sección de configuración; en Docker llega como Messaging__Host, etc.</summary>
    public const string SectionName = "Messaging";

    /// <summary>Host del broker; en docker-compose y en Kubernetes es el nombre del servicio.</summary>
    public string Host { get; init; } = "rabbitmq";

    /// <summary>Puerto AMQP estándar.</summary>
    public ushort Port { get; init; } = 5672;

    /// <summary>Virtual host: aísla los recursos de esta aplicación dentro del broker.</summary>
    public string VirtualHost { get; init; } = "/";

    /// <summary>Usuario del broker.</summary>
    public string Username { get; init; } = "guest";

    /// <summary>Contraseña en claro; solo para desarrollo.</summary>
    public string Password { get; init; } = "guest";

    /// <summary>Ruta al docker secret con la contraseña. Tiene PRIORIDAD sobre Password.</summary>
    public string? PasswordFile { get; init; }

    /// <summary>
    /// Mensajes que esta réplica procesa simultáneamente.
    /// Con 4, un pod de 512 MB puede tener cuatro buffers de telemetría en memoria a la vez;
    /// subirlo sin subir el límite de memoria es la forma más rápida de provocar un OOMKilled.
    /// </summary>
    public ushort PrefetchCount { get; init; } = 4;

    /// <summary>Devuelve la contraseña efectiva, priorizando el fichero montado por Docker.</summary>
    public string ResolvePassword()
    {
        // File.Exists evita la excepción si la ruta está configurada pero el secret no se montó.
        if (!string.IsNullOrWhiteSpace(PasswordFile) && File.Exists(PasswordFile))
        {
            // Trim() elimina el salto de línea final que añaden casi todos los editores.
            return File.ReadAllText(PasswordFile).Trim();
        }

        return Password;
    }
}
