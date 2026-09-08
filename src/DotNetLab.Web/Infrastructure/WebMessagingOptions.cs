namespace DotNetLab.Web.Infrastructure;

/// <summary>
/// Conexión del frontend al broker. Solo necesita los datos para SUSCRIBIRSE: el frontend no
/// publica ningún mensaje, únicamente escucha el desenlace de los trabajos para reenviarlo
/// por SignalR al navegador.
/// </summary>
public sealed class WebMessagingOptions
{
    /// <summary>Sección de configuración; en Docker llega como Messaging__Host, etc.</summary>
    public const string SectionName = "Messaging";

    /// <summary>
    /// Transporte: "rabbitmq" lo activa, cualquier otro valor lo deja apagado.
    /// El frontend NO admite transporte en memoria: el publicador vive en otro proceso, así que
    /// un bus en memoria aquí no recibiría jamás un mensaje y daría una falsa sensación de que
    /// las notificaciones funcionan.
    /// </summary>
    public string Transport { get; init; } = "disabled";

    /// <summary>Host del broker; en docker-compose es el nombre del servicio.</summary>
    public string Host { get; init; } = "rabbitmq";

    /// <summary>Puerto AMQP estándar.</summary>
    public ushort Port { get; init; } = 5672;

    /// <summary>Virtual host del broker.</summary>
    public string VirtualHost { get; init; } = "/";

    /// <summary>Usuario del broker.</summary>
    public string Username { get; init; } = "guest";

    /// <summary>Contraseña en claro; solo para desarrollo.</summary>
    public string Password { get; init; } = "guest";

    /// <summary>Ruta al docker secret con la contraseña. Tiene PRIORIDAD sobre Password.</summary>
    public string? PasswordFile { get; init; }

    /// <summary>true solo con transporte RabbitMQ.</summary>
    // Comparación ordinal: el valor llega de una variable de entorno escrita a mano.
    public bool IsEnabled =>
        string.Equals(Transport, "rabbitmq", StringComparison.OrdinalIgnoreCase);

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
