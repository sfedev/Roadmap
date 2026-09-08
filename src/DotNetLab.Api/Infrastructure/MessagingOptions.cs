namespace DotNetLab.Api.Infrastructure;

/// <summary>
/// Configuración del bus de mensajes. El transporte es un valor de configuración, no una
/// decisión de compilación: el mismo binario habla RabbitMQ en Docker, memoria en local y
/// Azure Service Bus en la nube sin recompilar.
/// </summary>
public sealed class MessagingOptions
{
    /// <summary>Sección de configuración; en Docker llega como Messaging__Transport, etc.</summary>
    public const string SectionName = "Messaging";

    /// <summary>Transporte en memoria: todo ocurre dentro del proceso, sin broker.</summary>
    public const string InMemoryTransport = "inmemory";

    /// <summary>Transporte RabbitMQ: el broker real que levanta docker-compose.</summary>
    public const string RabbitMqTransport = "rabbitmq";

    /// <summary>Bus desactivado: los endpoints asíncronos responden 503.</summary>
    public const string DisabledTransport = "disabled";

    /// <summary>Transporte activo. Por defecto "inmemory" para que `dotnet run` funcione solo.</summary>
    public string Transport { get; init; } = InMemoryTransport;

    /// <summary>Host del broker; en docker-compose es el nombre del servicio.</summary>
    public string Host { get; init; } = "rabbitmq";

    /// <summary>Puerto AMQP estándar.</summary>
    public ushort Port { get; init; } = 5672;

    /// <summary>Virtual host de RabbitMQ: aísla recursos de distintas aplicaciones en un broker.</summary>
    public string VirtualHost { get; init; } = "/";

    /// <summary>Usuario del broker.</summary>
    public string Username { get; init; } = "guest";

    /// <summary>Contraseña en claro; solo para desarrollo local.</summary>
    public string Password { get; init; } = "guest";

    /// <summary>Ruta al docker secret con la contraseña. Tiene PRIORIDAD sobre Password.</summary>
    public string? PasswordFile { get; init; }

    /// <summary>true si hay un bus operativo con el que publicar y consumir.</summary>
    // Comparación ordinal e ignorando mayúsculas: el valor viene de una variable de entorno
    // escrita a mano y "RabbitMQ" debe funcionar igual que "rabbitmq".
    public bool IsEnabled =>
        !string.Equals(Transport, DisabledTransport, StringComparison.OrdinalIgnoreCase);

    /// <summary>true cuando el transporte es en memoria.</summary>
    public bool IsInMemory =>
        string.Equals(Transport, InMemoryTransport, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// true si este proceso debe alojar además el consumidor del trabajo pesado.
    /// Solo ocurre con el transporte en memoria: sin broker, publicador y consumidor tienen
    /// que vivir en el mismo proceso o el mensaje no llegaría a ninguna parte.
    /// </summary>
    public bool HostsAnalysisConsumer => IsInMemory;

    /// <summary>Devuelve la contraseña efectiva, priorizando el fichero montado por Docker.</summary>
    public string ResolvePassword()
    {
        // File.Exists evita la excepción cuando la ruta está configurada pero el secret no
        // se ha montado todavía (por ejemplo, al ejecutar la imagen fuera de compose).
        if (!string.IsNullOrWhiteSpace(PasswordFile) && File.Exists(PasswordFile))
        {
            // Trim() quita el salto de línea final que añaden casi todos los editores.
            return File.ReadAllText(PasswordFile).Trim();
        }

        return Password;
    }
}
