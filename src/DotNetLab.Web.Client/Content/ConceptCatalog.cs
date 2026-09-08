namespace DotNetLab.Web.Client.Content;

/// <summary>
/// Ficha teórica de un concepto. Es un record porque solo transporta datos inmutables:
/// el catálogo se construye una vez y se comparte entre todos los componentes.
/// </summary>
public sealed record Concept(
    // Identificador estable usado como ancla (#id) en la navegación lateral.
    string Id,
    // Emoji del encabezado: peso cero comparado con un icono SVG o una fuente de iconos.
    string Icon,
    // Nombre del concepto tal y como aparece en la documentación oficial.
    string Title,
    // Frase de una línea que resume el porqué del concepto.
    string Tagline,
    // "Qué es": definición sin jerga.
    string WhatIsIt,
    // "Cómo funciona por dentro": Stack vs Heap, capas del runtime, coste real.
    string HowItWorks,
    // "Cuándo usarlo": el criterio de decisión, incluido cuándo NO usarlo.
    string WhenToUse,
    // Nota corta sobre el modelo de memoria, destacada visualmente en la tarjeta.
    string MemoryNote,
    // Fragmento de código REAL del backend (no pseudocódigo) que implementa el concepto.
    string Snippet,
    // Endpoint que ejecuta el playground de esta tarjeta.
    string Endpoint);

/// <summary>
/// Catálogo estático de los conceptos de la FASE 01. Vive en el cliente porque es
/// contenido de solo lectura: servirlo desde la API añadiría una llamada de red sin aportar nada.
/// </summary>
public static class ConceptCatalog
{
    /// <summary>Identificadores usados por las páginas para localizar cada ficha.</summary>
    public const string Span = "span";
    public const string Records = "records";
    public const string PrimaryConstructors = "primary-constructors";
    public const string PatternMatching = "pattern-matching";
    public const string Channels = "channels";
    public const string ValueTasks = "value-task";
    public const string KeyedServices = "keyed-services";

    /// <summary>Todas las fichas, en el orden pedagógico en que conviene leerlas.</summary>
    // IReadOnlyList impide que un componente mute el catálogo por accidente.
    public static IReadOnlyList<Concept> All { get; } =
    [
        new Concept(
            Id: Span,
            Icon: "⚡",
            Title: "Span<T> y ReadOnlySpan<char>",
            Tagline: "Ver la memoria sin copiarla.",
            WhatIsIt:
                "Span<T> es una vista (referencia + longitud) sobre memoria que ya existe: un array, " +
                "un string, memoria nativa o incluso la pila. No es un contenedor, no posee los datos " +
                "y no puede sobrevivir al bloque donde se declara.",
            HowItWorks:
                "Es un 'ref struct': el compilador GARANTIZA que solo puede vivir en la pila. Por eso no " +
                "puede ser campo de una clase, ni capturarse en una lambda, ni usarse tras un 'await'. " +
                "Cortar un span (span[5..20]) solo ajusta un puntero y una longitud, así que es O(1) y " +
                "no asigna nada en el Heap. Además, sus operaciones (IndexOf, SequenceEqual) están " +
                "vectorizadas con SIMD y comparan 16-32 caracteres por instrucción.",
            WhenToUse:
                "Al parsear, trocear o buscar dentro de buffers grandes: ficheros, cabeceras HTTP, CSV, " +
                "protocolos binarios. NO lo uses para almacenar datos ni para atravesar fronteras async: " +
                "ahí necesitas Memory<T>, que sí puede vivir en el Heap.",
            MemoryNote:
                "string.Split sobre 25.000 líneas asigna ~12 MB en Gen0. La misma operación con spans " +
                "asigna 0 bytes: el GC no llega ni a enterarse.",
            Snippet:
                """
                // Ventana sobre el MISMO buffer original; reasignarla mueve punteros, no copia datos.
                var remaining = payload;

                while (!remaining.IsEmpty)
                {
                    // IndexOf sobre span está vectorizado (SIMD): compara 16-32 chars por instrucción.
                    var newLine = remaining.IndexOf('\n');
                    var line = newLine >= 0 ? remaining[..newLine] : remaining;
                    remaining = newLine >= 0 ? remaining[(newLine + 1)..] : default;

                    var lastSeparator = line.LastIndexOf(';');
                    if (lastSeparator < 0) continue;

                    // Slice del campo numérico: sigue apuntando al buffer original, cero copias.
                    var valueSpan = line[(lastSeparator + 1)..].Trim();

                    // double.TryParse tiene sobrecarga para span: parsea sin crear un string intermedio.
                    if (double.TryParse(valueSpan, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                    {
                        sum += value;
                        rows++;
                    }
                }
                """,
            Endpoint: "GET /api/performance/span-demo"),

        new Concept(
            Id: Records,
            Icon: "🧬",
            Title: "Records",
            Tagline: "Tipos cuya identidad son sus valores.",
            WhatIsIt:
                "Un record es una clase (o struct, si se declara 'record struct') donde el compilador " +
                "genera constructor, Equals, GetHashCode, ToString, Deconstruct y el clonador de la " +
                "expresión 'with'. Cuatro líneas sustituyen a sesenta de un POCO escrito a mano.",
            HowItWorks:
                "'record class' sigue siendo un tipo por referencia y se asigna en el Heap; lo que cambia " +
                "es la SEMÁNTICA de igualdad, que pasa a comparar campo a campo. La expresión 'with' " +
                "invoca un constructor de copia sintetizado: crea un objeto NUEVO y sobrescribe los " +
                "miembros indicados, dejando el original intacto. 'record struct' vive en la pila y evita " +
                "la asignación, a costa de copiarse entero en cada paso de parámetro.",
            WhenToUse:
                "DTOs, mensajes, eventos, resultados de consulta y cualquier valor inmutable que viaje " +
                "entre capas. Evítalos para entidades con identidad propia (una Factura con Id no es " +
                "'igual' a otra por tener los mismos campos) o para objetos con estado mutable.",
            MemoryNote:
                "'with' NO muta: asigna un objeto nuevo. Por eso original == copia da true (valores) " +
                "mientras ReferenceEquals da false (direcciones distintas).",
            Snippet:
                """
                // Record posicional: constructor, Equals, GetHashCode, ToString y Deconstruct generados.
                public sealed record SensorReading(string SensorId, double Value, int BatteryPercent, DateTimeOffset TakenAt);

                // 'with' = mutación no destructiva: copia superficial con los campos indicados cambiados.
                var mutated = original with { Value = 97.3, BatteryPercent = 11 };

                // == compara campo a campo (igualdad estructural), no direcciones de memoria.
                var valueEquality = original == clone;      // true

                // ReferenceEquals sigue siendo false: 'with' asignó un objeto NUEVO en el Heap.
                var referenceEquality = ReferenceEquals(original, clone);   // false

                // Deconstrucción posicional sin escribir Deconstruct a mano.
                var (sensorId, value, battery, _) = original;
                """,
            Endpoint: "GET /api/language/records"),

        new Concept(
            Id: PrimaryConstructors,
            Icon: "🏗️",
            Title: "Primary Constructors",
            Tagline: "Las dependencias, en la firma del tipo.",
            WhatIsIt:
                "Desde C# 12 cualquier clase o struct puede declarar parámetros junto a su nombre. " +
                "Esos parámetros están disponibles en todo el cuerpo del tipo, incluidos los " +
                "inicializadores de campos y propiedades.",
            HowItWorks:
                "El compilador sintetiza un campo privado SOLO para los parámetros que realmente se " +
                "capturan en algún miembro; los que solo se usan en un inicializador no generan campo. " +
                "No son readonly de forma implícita, así que si necesitas inmutabilidad estricta " +
                "asígnalos a una propiedad 'get'. En un 'record' los parámetros primarios además " +
                "generan propiedades públicas: en una clase normal, no.",
            WhenToUse:
                "Servicios inyectados por DI, que es el caso mayoritario: elimina el trío " +
                "campo + constructor + asignación. Evítalos si el constructor necesita validar " +
                "argumentos o hacer trabajo real: para eso sigue haciendo falta un constructor explícito.",
            MemoryNote:
                "Un parámetro primario no capturado no ocupa memoria en la instancia: el compilador " +
                "no crea el campo si nadie lo usa fuera de los inicializadores.",
            Snippet:
                """
                // El parámetro 'logger' se captura como campo privado sintetizado por el compilador.
                public sealed class SpanTelemetryParser(ILogger<SpanTelemetryParser> logger) : ITelemetryParser
                {
                    public string Key => "span";

                    public ParseStrategyResult Parse(ReadOnlySpan<char> payload)
                    {
                        // 'logger' es visible en todo el cuerpo del tipo, sin declarar el campo.
                        ApiLog.ParserExecuted(logger, "span", rows, micros, allocated);
                    }
                }

                // También funciona en clases abstractas: 'lifetime' se usa en un miembro y en la base.
                public abstract class LifetimeProbe(string lifetime)
                {
                    public string InstanceId { get; } = Guid.CreateVersion7().ToString("N")[^8..];
                    public ServiceFootprint ToFootprint() => new(lifetime, InstanceId, CreatedAt);
                }

                public sealed class ScopedProbe() : LifetimeProbe("Scoped");
                """,
            Endpoint: "GET /api/di/lifetimes"),

        new Concept(
            Id: PatternMatching,
            Icon: "🔍",
            Title: "Pattern Matching avanzado",
            Tagline: "Describir la forma del dato en vez de interrogarlo.",
            WhatIsIt:
                "Un conjunto de patrones combinables: de propiedad ({ Value: > 90 }), relacionales (> < >= <=), " +
                "lógicos (and, or, not), de lista ([var first, .., var last]), de tipo, de descarte (_) y " +
                "guardas (when).",
            HowItWorks:
                "El compilador no traduce el switch a una cadena de ifs: construye un árbol de decisión " +
                "que evalúa cada subexpresión UNA sola vez y reordena las comprobaciones para minimizar " +
                "el trabajo. Además comprueba la exhaustividad, así que un switch expression sin rama " +
                "aplicable es un error de compilación, no una excepción en producción.",
            WhenToUse:
                "Clasificación, validación, máquinas de estado y traducción de datos externos a decisiones " +
                "de dominio. Si una rama necesita más de dos o tres líneas de lógica, extrae un método: " +
                "el patrón describe la forma, no debe esconder el algoritmo.",
            MemoryNote:
                "Los patrones de lista sobre arrays y strings NO copian: acceden por índice y longitud, " +
                "igual que un span.",
            Snippet:
                """
                var (severity, pattern, branch) = reading switch
                {
                    // Property pattern + relational patterns combinados en conjunción.
                    { Value: > 90, BatteryPercent: < 15 } => ("Critical", "...", 1),

                    // Patrón lógico 'or' con dos rangos: fuera del rango físico plausible.
                    { Value: > 90 or < -40 } => ("Warning", "...", 2),

                    // Rango cerrado con 'and'.
                    { BatteryPercent: >= 5 and <= 20 } => ("Warning", "...", 3),

                    // List pattern sobre el string del identificador ('sensor-' + lo que sea).
                    { SensorId: ['s','e','n','s','o','r','-', ..], Value: >= 15 and <= 30 } => ("Nominal", "...", 4),

                    // Guarda 'when' para lógica que no cabe en un patrón.
                    var r when r.TakenAt > timeProvider.GetUtcNow().AddMinutes(1) => ("Invalid", "...", 5),

                    // Descarte: hace exhaustivo el switch.
                    _ => ("Unknown", "_", 6)
                };

                // Slice pattern: captura extremos e ignora el centro de la serie.
                private static string DiagnoseSeries(double[] series) => series switch
                {
                    [] => "Serie vacía.",
                    [var only] => $"Una sola muestra ({only}).",
                    [var first, .., var last] when last - first > 10 => "Tendencia ascendente brusca.",
                    [_, _, ..] => "Serie estable.",
                    _ => "Forma no contemplada."
                };
                """,
            Endpoint: "GET /api/language/pattern-match"),

        new Concept(
            Id: Channels,
            Icon: "🔀",
            Title: "System.Threading.Channels",
            Tagline: "Colas productor/consumidor que no bloquean hilos.",
            WhatIsIt:
                "Una cola asíncrona y thread-safe entre uno o varios productores y uno o varios " +
                "consumidores. Es el equivalente moderno de BlockingCollection, pensado para async/await.",
            HowItWorks:
                "Un canal acotado (CreateBounded) reserva un buffer fijo. Cuando se llena, WriteAsync no " +
                "bloquea el hilo: devuelve una tarea incompleta y libera el hilo del ThreadPool para otro " +
                "trabajo; la continuación se reanuda cuando el consumidor libera un hueco. Eso es " +
                "backpressure real: el productor se acompasa al consumidor sin dormir hilos ni perder " +
                "eventos. Declarar SingleReader/SingleWriter activa rutas internas sin sincronización.",
            WhenToUse:
                "Pipelines de ingesta, procesamiento por lotes, desacoplar recepción de procesamiento, " +
                "background services. Si el canal es NO acotado, un productor rápido puede agotar la " +
                "memoria del proceso: acótalo siempre salvo que tengas una razón muy concreta.",
            MemoryNote:
                "Un canal acotado tiene techo de memoria conocido: capacidad × tamaño del elemento. " +
                "Un canal ilimitado no lo tiene, y ahí es donde nacen los OutOfMemory en producción.",
            Snippet:
                """
                // Canal ACOTADO: su capacidad fija es lo que genera backpressure cuando se llena.
                var raw = Channel.CreateBounded<ChannelEvent>(new BoundedChannelOptions(capacity)
                {
                    FullMode = BoundedChannelFullMode.Wait,   // esperar en vez de descartar
                    SingleWriter = true,                      // rutas internas sin sincronización
                    SingleReader = true
                });

                // TryWrite es la ruta rápida y síncrona: si hay hueco, escribe sin await.
                if (!raw.Writer.TryWrite(evt))
                {
                    Interlocked.Increment(ref backpressureWaits);
                    // WaitToWriteAsync suspende la continuación; NO bloquea el hilo del pool.
                    while (await raw.Writer.WaitToWriteAsync(cancellationToken))
                    {
                        if (raw.Writer.TryWrite(evt)) break;
                    }
                }

                // ReadAllAsync expone el canal como IAsyncEnumerable y termina solo al completarse.
                await foreach (var evt in processed.Reader.ReadAllAsync(cancellationToken))
                {
                    yield return evt with { Stage = "consumed", QueueDepth = raw.Reader.Count };
                }
                """,
            Endpoint: "GET /api/concurrency/channel-stream"),

        new Concept(
            Id: ValueTasks,
            Icon: "🪶",
            Title: "ValueTask<T>",
            Tagline: "No pagar un objeto por una respuesta que ya tienes.",
            WhatIsIt:
                "Un struct que representa una operación que puede haber terminado YA. Si el resultado " +
                "está disponible, lo transporta directamente; si no, envuelve un Task o un " +
                "IValueTaskSource.",
            HowItWorks:
                "Task<T> es una clase: cada await de un método async asigna al menos un objeto en el Heap. " +
                "ValueTask<T> es un struct, así que en la ruta síncrona no asigna nada. El precio es un " +
                "contrato más estricto: solo se puede await-ear UNA vez, no se puede hacer .Result ni " +
                "await concurrente, y guardarlo en un campo es un error. Si necesitas cualquiera de esas " +
                "cosas, llama a AsTask() y vuelve al mundo de Task.",
            WhenToUse:
                "Métodos calientes cuya ruta más frecuente es síncrona: caches, buffers ya llenos, " +
                "validaciones tempranas. Si el método casi siempre hace I/O real, Task<T> es igual de " +
                "bueno y mucho más difícil de usar mal.",
            MemoryNote:
                "En este laboratorio la primera llamada calcula (asigna Task); la segunda, ya cacheada, " +
                "completa de forma síncrona sin tocar el Heap.",
            Snippet:
                """
                // Devuelve ValueTask porque la ruta cacheada termina de forma SÍNCRONA.
                public ValueTask<ChannelSummary> GetSummaryAsync(int eventCount, int capacity, CancellationToken ct)
                {
                    var snapshot = Volatile.Read(ref _cache);

                    // Ruta rápida: el constructor de ValueTask con un valor listo NO toca el Heap.
                    if (snapshot is { } hit && hit.Events == eventCount && hit.Capacity == capacity)
                    {
                        return new ValueTask<ChannelSummary>(hit.Summary with { FullMode = "cacheado" });
                    }

                    // Ruta lenta: solo aquí se paga la máquina de estados async y el Task.
                    return new ValueTask<ChannelSummary>(ComputeSummaryAsync(eventCount, capacity, ct));
                }

                // El método async real es privado: separarlo mantiene la ruta rápida sin state machine.
                private async Task<ChannelSummary> ComputeSummaryAsync(int eventCount, int capacity, CancellationToken ct)
                """,
            Endpoint: "GET /api/concurrency/channel-summary"),

        new Concept(
            Id: KeyedServices,
            Icon: "🔑",
            Title: "Keyed Services y Factory",
            Tagline: "Varias implementaciones del mismo contrato, elegidas en runtime.",
            WhatIsIt:
                "Desde .NET 8 el contenedor integrado admite registrar N implementaciones de un mismo " +
                "interfaz distinguidas por una clave, y resolverlas con [FromKeyedServices] o " +
                "GetKeyedService.",
            HowItWorks:
                "El contenedor indexa los registros por (tipo de servicio, clave). Cada clave conserva su " +
                "propio lifetime, así que dos implementaciones del mismo interfaz pueden ser una " +
                "Singleton y otra Scoped. Envolver la resolución en una fábrica evita esparcir " +
                "IServiceProvider por el código: el Service Locator queda confinado a un único tipo " +
                "auditable y testeable.",
            WhenToUse:
                "Estrategias intercambiables: proveedores de pago, motores de parseo, algoritmos de " +
                "compresión, feature flags. Si la clave viene de una petición HTTP, valídala contra una " +
                "lista blanca: resolver claves arbitrarias permite sondear el contenedor.",
            MemoryNote:
                "Resolver por clave no asigna nada extra: es una búsqueda en el diccionario interno de " +
                "registros del proveedor.",
            Snippet:
                """
                // Dos implementaciones del MISMO interfaz distinguidas por clave.
                builder.Services.AddKeyedSingleton<ITelemetryParser, SpanTelemetryParser>("span");
                builder.Services.AddKeyedSingleton<ITelemetryParser, NaiveTelemetryParser>("naive");
                builder.Services.AddSingleton<ITelemetryParserFactory, TelemetryParserFactory>();

                public sealed class TelemetryParserFactory(IServiceProvider provider) : ITelemetryParserFactory
                {
                    // Lista blanca: resolver claves arbitrarias permitiría sondear el contenedor.
                    private static readonly string[] Keys = ["span", "naive"];

                    public ITelemetryParser? Resolve(string key)
                    {
                        if (!Keys.Contains(key, StringComparer.OrdinalIgnoreCase)) return null;

                        // GetKeyedService devuelve null en vez de lanzar: el endpoint responde 400, no 500.
                        return provider.GetKeyedService<ITelemetryParser>(key.ToLowerInvariant());
                    }
                }
                """,
            Endpoint: "GET /api/di/keyed-services")
    ];

    // --- Fase 02 · Cloud, resiliencia y producción -----------------------------------------

    /// <summary>Identificadores de las fichas de la segunda fase.</summary>
    public const string Observability = "observability";
    public const string Resilience = "resilience";
    public const string EventDriven = "event-driven";
    public const string Kubernetes = "kubernetes";
    public const string InfrastructureAsCode = "iac";

    /// <summary>Fichas del módulo de nube y producción.</summary>
    public static IReadOnlyList<Concept> CloudNative { get; } =
    [
        new Concept(
            Id: Observability,
            Icon: "📡",
            Title: "OpenTelemetry",
            Tagline: "Saber qué pasó dentro sin adivinarlo.",
            WhatIsIt:
                "Un estándar (no un producto) para producir trazas, métricas y logs correlacionados. " +
                "Define el formato del dato y el protocolo de transporte, OTLP; quién los almacena y " +
                "los pinta es una decisión aparte.",
            HowItWorks:
                "Cada petición genera una traza con un identificador que viaja en la cabecera W3C " +
                "'traceparent'. Cuando el frontend llama a la API, esa cabecera cruza el proxy; cuando " +
                "la API publica un mensaje, MassTransit la mete en las cabeceras de RabbitMQ. Por eso " +
                "una traza puede abarcar tres procesos y un salto por el broker, y verse como un único " +
                "árbol de spans. Las métricas usan instrumentos distintos según la pregunta: Counter " +
                "para cuántas veces ha ocurrido algo, Histogram para cómo se distribuye, y " +
                "UpDownCounter para cuántos hay ahora mismo.",
            WhenToUse:
                "Siempre, desde el primer día: instrumentar después de un incidente llega tarde. La " +
                "decisión real no es si instrumentar, sino cuánto muestrear: al 100% de trazas, un " +
                "servicio con tráfico serio genera más telemetría que datos de negocio.",
            MemoryNote:
                "ActivitySource.StartActivity devuelve null si nadie escucha, así que el coste es cero " +
                "cuando no hay listener. Aun así, aquí los spans se abren FUERA de la ventana medida: " +
                "crear una Activity asigna, y hacerlo dentro falsearía los 0 bytes del parser.",
            Snippet:
                """
                // ServiceDefaults: la misma configuración para API, frontend y Worker.
                builder.Services.AddOpenTelemetry()
                    .WithTracing(tracing => tracing
                        .AddAspNetCoreInstrumentation(o =>
                        {
                            // Las sondas de Kubernetes golpean /health cada pocos segundos:
                            // trazarlas multiplicaría el volumen sin aportar nada.
                            o.Filter = context => !context.Request.Path.StartsWithSegments("/health");
                            o.RecordException = true;
                        })
                        // Propaga traceparent en las llamadas salientes: enlaza los procesos.
                        .AddHttpClientInstrumentation()
                        .AddSource(LabTelemetry.SystemName)
                        // Cierra la traza a través de RabbitMQ.
                        .AddSource("MassTransit"))
                    .WithMetrics(metrics => metrics
                        .AddRuntimeInstrumentation()   // GC, ThreadPool, excepciones
                        .AddMeter(LabTelemetry.SystemName)
                        .AddMeter("Polly"));           // reintentos y aperturas de circuito

                // Instrumentos declarados UNA vez: crearlos por operación filtra memoria.
                public static readonly Histogram<long> ParseAllocatedBytes = Meter.CreateHistogram<long>(
                    name: "dotnetlab.parse.allocated_bytes",
                    unit: "By",   // abreviatura UCUM que exige la convención semántica
                    description: "Bytes asignados en el Heap durante un parseo.");
                """,
            Endpoint: "OTLP → Collector → Jaeger + Prometheus"),

        new Concept(
            Id: Resilience,
            Icon: "🛡️",
            Title: "Polly: retry y circuit breaker",
            Tagline: "Fallar bien es parte del diseño.",
            WhatIsIt:
                "Un conjunto de estrategias componibles que envuelven una operación que puede fallar: " +
                "reintento, cortacircuitos, timeout, limitador de concurrencia y hedging. En .NET se " +
                "integran con IHttpClientFactory mediante Microsoft.Extensions.Http.Resilience.",
            HowItWorks:
                "El pipeline se ejecuta de fuera adentro: timeout total → reintento → cortacircuitos → " +
                "timeout por intento. El reintento con backoff exponencial espera cada vez más, y el " +
                "jitter aleatoriza esa espera para que N clientes que fallaron a la vez no vuelvan a " +
                "la vez. El cortacircuitos cuenta fallos en una ventana y, superado el umbral, deja de " +
                "intentarlo: las llamadas siguientes fallan en microsegundos sin tocar la red.",
            WhenToUse:
                "Reintentar SOLO operaciones idempotentes y fallos transitorios (un pod reciclándose, " +
                "un timeout de red). Nunca un 400 o un 401: reintentar un error del cliente lo repite " +
                "idéntico. Y nunca reintentos sin cortacircuitos: por sí solos AMPLIFICAN una caída, " +
                "porque multiplican la carga sobre el servicio que ya está mal.",
            MemoryNote:
                "Un cortacircuitos abierto ahorra más que ancho de banda: cada llamada evitada es un " +
                "hilo que no se bloquea esperando su timeout. Sin él, una dependencia caída agota el " +
                "pool de conexiones del que depende todo lo demás.",
            Snippet:
                """
                // Política estándar de todas las llamadas entre servicios (ServiceDefaults).
                builder.AddStandardResilienceHandler(options =>
                {
                    options.AttemptTimeout.Timeout = TimeSpan.FromSeconds(10);
                    options.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(45);

                    options.Retry.MaxRetryAttempts = 3;
                    options.Retry.BackoffType = DelayBackoffType.Exponential;
                    options.Retry.Delay = TimeSpan.FromMilliseconds(500);
                    // Sin jitter, N réplicas que fallan juntas reintentan juntas.
                    options.Retry.UseJitter = true;

                    options.CircuitBreaker.FailureRatio = 0.5;
                    // La librería EXIGE que sea al menos el doble del timeout por intento.
                    options.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(30);
                    // Sin umbral mínimo, un único fallo (1 de 1 = 100%) abriría el circuito.
                    options.CircuitBreaker.MinimumThroughput = 8;
                    options.CircuitBreaker.BreakDuration = TimeSpan.FromSeconds(15);
                });
                """,
            Endpoint: "GET /api/resilience/demo"),

        new Concept(
            Id: EventDriven,
            Icon: "📨",
            Title: "202 Accepted, MassTransit y SignalR",
            Tagline: "Aceptar rápido, procesar aparte, avisar al terminar.",
            WhatIsIt:
                "El patrón de trabajo asíncrono: la API valida, publica un evento y responde 202 con " +
                "la ubicación del futuro resultado. Otro proceso lo consume y hace el trabajo pesado. " +
                "El desenlace vuelve al navegador por SignalR, sin que nadie haya mantenido abierta " +
                "una petición HTTP durante todo el proceso.",
            HowItWorks:
                "MassTransit publica el evento en un exchange de RabbitMQ, y cada consumidor registrado " +
                "obtiene SU PROPIA cola enlazada a ese exchange. Por eso aquí dos servicios distintos " +
                "reaccionan al mismo evento sin conocerse: la API actualiza su registro de trabajos y " +
                "el frontend lo reenvía por SignalR. Si un consumidor falla, sus reintentos no afectan " +
                "al otro, y agotados los intentos el mensaje va a una cola _error donde queda " +
                "disponible para inspección en vez de perderse.",
            WhenToUse:
                "Cuando el trabajo dura más de lo que un usuario tolera esperar, cuando hay que " +
                "absorber picos sin tumbar el sistema, o cuando varios subsistemas deben reaccionar al " +
                "mismo hecho. El precio es real: consistencia eventual, posibles mensajes duplicados " +
                "(el consumidor DEBE ser idempotente) y un broker más que operar.",
            MemoryNote:
                "El equivalente al canal acotado es aquí PrefetchCount: cuántos mensajes procesa a la " +
                "vez cada réplica del Worker. Subirlo sin subir el límite de memoria del pod es la " +
                "forma más rápida de provocar un OOMKilled en Kubernetes.",
            Snippet:
                """
                // API: acepta, publica y responde. NO procesa.
                var accepted = await publisher.PublishAsync(request.RowCount, strategy, cancellationToken);
                // Accepted<T> escribe la cabecera Location además del cuerpo.
                return TypedResults.Accepted(accepted.StatusUrl, accepted);

                // Worker: consume, hace el trabajo pesado con Span<T> y publica el resultado.
                public async Task Consume(ConsumeContext<TelemetryAnalysisRequested> context)
                {
                    // Latencia de cola: solo se puede calcular con el instante de publicación
                    // que viaja dentro del propio mensaje.
                    var queueLatency = (timeProvider.GetUtcNow() - message.RequestedAt).TotalMilliseconds;

                    var result = parser.Parse(generator.Generate(rows));

                    // Publish (y no Send): N suscriptores independientes pueden reaccionar.
                    await context.Publish(new TelemetryAnalysisCompleted(
                        message.JobId, result, queueLatency, processingMs,
                        Environment.MachineName, timeProvider.GetUtcNow()));
                }

                // Frontend: consume el mismo evento y lo empuja al navegador.
                await hub.Clients.All.SendAsync("JobUpdated", snapshot, context.CancellationToken);
                """,
            Endpoint: "POST /api/analysis/jobs"),

        new Concept(
            Id: Kubernetes,
            Icon: "☸️",
            Title: "Kubernetes: sondas, recursos y HPA",
            Tagline: "Decirle al cluster cómo se comporta tu aplicación.",
            WhatIsIt:
                "Los manifiestos de /k8s: Deployment, Service, ConfigMap, Secret, Ingress, HPA y " +
                "NetworkPolicy. No describen cómo instalar la aplicación, sino qué estado debe " +
                "mantener el cluster; el reconciliador se encarga del resto.",
            HowItWorks:
                "Las dos sondas NO son lo mismo, y confundirlas es el error clásico: si falla la de " +
                "liveness, Kubernetes REINICIA el contenedor; si falla la de readiness, lo saca del " +
                "balanceador pero lo deja vivo. Por eso la de liveness apunta a /health/live, que no " +
                "consulta ninguna dependencia externa: si comprobara la base de datos, una caída de " +
                "esta reiniciaría todos los pods a la vez. El HPA, por su parte, compara el uso con el " +
                "'requests' del contenedor, NUNCA con el 'limits': un requests mal calculado arruina " +
                "el autoescalado aunque el HPA esté perfecto.",
            WhenToUse:
                "Cuando hacen falta control total, portabilidad entre nubes o extensiones del plano de " +
                "control (operadores, CRDs). Si no es el caso, Container Apps da el 90% del valor sin " +
                "cluster que operar: por eso el Bicep de este repositorio aprovisiona ambos.",
            MemoryNote:
                "Superar el límite de MEMORIA provoca OOMKilled inmediato; superar el de CPU solo " +
                "provoca throttling. De ahí que el Worker tenga 512Mi de límite con PrefetchCount=4: " +
                "cuatro buffers de telemetría vivos a la vez tienen que caber.",
            Snippet:
                """
                # Las dos sondas tienen semánticas OPUESTAS: una reinicia, la otra solo despublica.
                startupProbe:            # da margen al arranque en frío sin relajar liveness
                  httpGet: { path: /health/live, port: http }
                  periodSeconds: 2
                  failureThreshold: 30   # hasta 60 s para levantar
                livenessProbe:           # si falla -> REINICIA el contenedor
                  httpGet: { path: /health/live, port: http }
                  periodSeconds: 10
                readinessProbe:          # si falla -> lo saca del Service, sin reiniciar
                  httpGet: { path: /health/ready, port: http }
                  periodSeconds: 5

                resources:
                  requests:              # lo que reserva el scheduler Y la base del cálculo del HPA
                    cpu: 100m
                    memory: 192Mi
                  limits:                # superar memoria = OOMKilled; superar CPU = throttling
                    cpu: "1"
                    memory: 512Mi

                securityContext:
                  runAsNonRoot: true
                  readOnlyRootFilesystem: true
                  capabilities:
                    drop: ["ALL"]
                """,
            Endpoint: "kubectl apply -f k8s/"),

        new Concept(
            Id: InfrastructureAsCode,
            Icon: "🏗️",
            Title: "Helm y Bicep",
            Tagline: "La infraestructura también se revisa en un pull request.",
            WhatIsIt:
                "Helm empaqueta los manifiestos de Kubernetes en un chart parametrizable con historial " +
                "de releases y rollback. Bicep describe los recursos de Azure (AKS, ACR, Key Vault, " +
                "Container Apps) en un lenguaje declarativo que compila a plantillas ARM.",
            HowItWorks:
                "Ambos son declarativos e idempotentes: se describe el estado final y la herramienta " +
                "calcula el cambio. En Bicep, lo que lo hace idempotente es uniqueString(), que deriva " +
                "los nombres de un hash determinista del grupo de recursos: el mismo despliegue produce " +
                "siempre los mismos nombres, pero dos suscripciones no chocan. En Helm, la anotación " +
                "checksum/config es lo que fuerza el rollout de los pods cuando cambia el ConfigMap; " +
                "sin ella, la configuración se actualiza y los pods siguen con la vieja.",
            WhenToUse:
                "Desde el primer despliegue. Un recurso creado a mano en el portal es un recurso que " +
                "nadie sabe reproducir y que no aparece en ninguna revisión de código.",
            MemoryNote:
                "En este repositorio el binario lee sus secretos por RUTA. Pasar de un docker secret a " +
                "Azure Key Vault (montado por el CSI driver) no cambia una sola línea de C#: solo la " +
                "ruta del fichero en el ConfigMap.",
            Snippet:
                """
                # Helm: un único deployment.yaml recorre los tres servicios.
                {{- range $name, $component := .Values.components }}
                spec:
                  # Con HPA activo NO se emite 'replicas': si se emitiera, cada `helm upgrade`
                  # devolvería el número de pods al valor del chart y desharía el autoescalado.
                  {{- if not $component.autoscaling.enabled }}
                  replicas: {{ $component.replicaCount }}
                  {{- end }}
                  template:
                    metadata:
                      annotations:
                        # Fuerza el rollout de los pods cuando cambia la configuración.
                        checksum/config: {{ include (print $.Template.BasePath "/configmap.yaml") $ | sha256sum }}
                {{- end }}

                // Bicep: nombres deterministas e idempotentes.
                var uniqueSuffix = uniqueString(resourceGroup().id)

                // El kubelet descarga imágenes con su identidad gestionada: ningún
                // imagePullSecret que guardar ni rotar.
                resource acrPullAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
                  name: guid(acrId, aks.id, acrPullRoleId)   // GUID determinista = idempotente
                  properties: {
                    principalId: aks.properties.identityProfile.kubeletidentity.objectId
                    principalType: 'ServicePrincipal'
                  }
                }
                """,
            Endpoint: "helm upgrade --install · az deployment group create")
    ];

    /// <summary>Busca una ficha por su identificador.</summary>
    // First y no FirstOrDefault: un id inexistente es un bug de programación, no un caso de uso.
    // Busca en las dos fases: el catálogo está partido por módulo, pero los ids son únicos.
    public static Concept ById(string id) => All.Concat(CloudNative).First(c => c.Id == id);
}
