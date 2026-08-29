using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using DotNetLab.Api.Infrastructure;
using DotNetLab.Contracts;
using Microsoft.Extensions.Options;

namespace DotNetLab.Api.Services;

/// <summary>Configuración del contenedor auxiliar (cache) inyectada por variables de entorno.</summary>
public sealed class CacheOptions
{
    /// <summary>Sección de appsettings / prefijo de variables de entorno (Cache__Host, Cache__Port).</summary>
    public const string SectionName = "Cache";

    /// <summary>Host del contenedor auxiliar; en docker-compose es el nombre del servicio.</summary>
    public string Host { get; init; } = "localhost";

    /// <summary>Puerto TCP del servicio de cache.</summary>
    public int Port { get; init; } = 6379;

    /// <summary>Permite apagar la sonda cuando se ejecuta sin Docker.</summary>
    public bool Enabled { get; init; }

    /// <summary>Timeout de la sonda; una dependencia lenta no debe colgar el health check.</summary>
    public int TimeoutMs { get; init; } = 750;
}

/// <summary>
/// Sonda de disponibilidad del contenedor auxiliar. Habla RESP (el protocolo de Redis)
/// a pelo sobre TCP: sirve para demostrar la comunicación entre contenedores sin añadir
/// una dependencia NuGet, y de paso ejercita Span/ArrayPool en I/O real.
/// </summary>
// IOptions<T> en el primary constructor: la configuración llega validada y tipada.
public sealed class CachePingProbe(IOptions<CacheOptions> options, ILogger<CachePingProbe> logger)
{
    // .Value se resuelve una vez y se guarda: evita desreferenciar el wrapper en cada llamada.
    private readonly CacheOptions _options = options.Value;

    // Comando RESP precompilado: *1 (array de 1 elemento), $4 (bulk string de 4 bytes), PING.
    // El literal u8 se materializa en tiempo de compilación como bytes UTF-8 en los datos del ensamblado.
    private static readonly byte[] PingCommand = "*1\r\n$4\r\nPING\r\n"u8.ToArray();

    /// <summary>Envía PING y espera +PONG. Devuelve ValueTask porque la ruta "deshabilitado" es síncrona.</summary>
    public async ValueTask<DependencyProbe> PingAsync(CancellationToken cancellationToken)
    {
        // Ruta síncrona: si la sonda está apagada no hay I/O y no se paga máquina de estados.
        if (!_options.Enabled)
        {
            return new DependencyProbe("cache", false, 0, "Sonda deshabilitada (Cache__Enabled=false).");
        }

        var start = Stopwatch.GetTimestamp();

        try
        {
            // CancellationTokenSource enlazado: corta por timeout propio O por cancelación del cliente.
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            // CancelAfter arma un temporizador; al dispararse, las operaciones de socket lanzan.
            timeout.CancelAfter(TimeSpan.FromMilliseconds(_options.TimeoutMs));

            // 'using' asegura el cierre del socket incluso si la conexión falla a medias.
            using var client = new TcpClient();
            // ConnectAsync con token: la resolución DNS del nombre de servicio Docker ocurre aquí.
            await client.ConnectAsync(_options.Host, _options.Port, timeout.Token).ConfigureAwait(false);

            // NetworkStream sobre el socket ya conectado; no se cierra aparte porque lo hace el TcpClient.
            var stream = client.GetStream();

            // WriteAsync sobre ReadOnlyMemory<byte>: la sobrecarga moderna, sin offset/count.
            // PingCommand es estático, así que el comando no se reconstruye en cada sonda.
            await stream.WriteAsync(PingCommand, timeout.Token).ConfigureAwait(false);

            // Buffer de 64 bytes en el Heap: la respuesta "+PONG\r\n" ocupa 7. No se usa stackalloc
            // porque ReadAsync exige Memory<byte>, y Memory no puede envolver memoria de pila.
            var buffer = new byte[64];
            var read = await stream.ReadAsync(buffer, timeout.Token).ConfigureAwait(false);

            // Decodifica solo los bytes recibidos; el resto del buffer es basura sin leer.
            var reply = Encoding.ASCII.GetString(buffer, 0, read).Trim();
            var elapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds;

            // Pattern matching sobre la respuesta: "+PONG" es el único éxito posible.
            return reply switch
            {
                "+PONG" => new DependencyProbe("cache", true, Math.Round(elapsed, 2), "Respondió +PONG."),
                // Cualquier otra respuesta significa "hay algo escuchando, pero no es lo esperado".
                var other => new DependencyProbe("cache", false, Math.Round(elapsed, 2), $"Respuesta inesperada: {other}")
            };
        }
        // OperationCanceledException cubre tanto el timeout propio como la cancelación del cliente.
        catch (OperationCanceledException)
        {
            return new DependencyProbe("cache", false, _options.TimeoutMs,
                $"Timeout de {_options.TimeoutMs} ms contra {_options.Host}:{_options.Port}.");
        }
        // SocketException = host inalcanzable, DNS fallido o conexión rechazada.
        catch (SocketException ex)
        {
            // Warning y no Error: la app degrada, no cae, si el contenedor auxiliar no está.
            ApiLog.CacheUnreachable(logger, ex, _options.Host, _options.Port);
            return new DependencyProbe("cache", false, Math.Round(Stopwatch.GetElapsedTime(start).TotalMilliseconds, 2),
                $"Socket error: {ex.SocketErrorCode}.");
        }
    }
}
