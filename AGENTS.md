# AGENTS.md

Documento de contexto para agentes de IA y asistentes de código que trabajen sobre **DotNetLab**.
Describe la arquitectura, la responsabilidad de cada componente y cómo extender el sistema sin
romper sus invariantes.

Complementa a `CLAUDE.md` (comandos y estilo) y a `PROJECT_DEVLOG.md` (por qué es así y qué se
intentó antes).

---

## 1. Propósito e invariantes

DotNetLab es un **portal pedagógico ejecutable**. Explica conceptos de .NET 10 / C# 14, Docker,
observabilidad, resiliencia y Kubernetes ejecutándolos de verdad y mostrando métricas reales del
proceso servidor.

Hay siete invariantes. Romper cualquiera invalida el proyecto aunque el código compile:

| # | Invariante | Cómo se protege |
|---|---|---|
| 1 | Las métricas se **miden**, nunca se simulan | `GC.GetAllocatedBytesForCurrentThread()` y `Stopwatch` dentro del handler |
| 2 | Las rutas "zero-allocation" asignan **exactamente 0 bytes** | Test `LaRutaConSpansNoAsignaEnElHeap` y una aserción en el job de integración de CI |
| 3 | Los snippets del portal son **código real del repositorio** | Cada `Concept` declara el archivo del que sale |
| 4 | El frontend funciona **sin red externa** | Sin CDN, sin fuentes remotas, resaltador propio |
| 5 | Toda medición va precedida de un **calentamiento del JIT** | `Warmup()` en `ITelemetryParser`, llamado desde el endpoint y desde el consumidor |
| 6 | Los servicios exportan **solo OTLP** | Ningún `using` de Jaeger, Prometheus ni Azure Monitor en `src/` |
| 7 | El mismo binario vale para Docker, Kubernetes y Azure | Los secretos se leen por **ruta de fichero**, nunca por valor |

---

## 2. Mapa de la arquitectura

```
                          Navegador
                    │                   ▲
   HTTP /api/...    │                   │  WebSocket /hubs/jobs
                    ▼                   │
┌───────────────────────────────────────────────────────────┐
│ DotNetLab.Web   (host Blazor · 8080 · réplicas 2)         │
│  · Render estático + interactivo de servidor + WASM       │
│  · PROXY /api/{**path} (GET y POST) → añade X-Lab-Key     │
│  · HUB DE SIGNALR: empuja el fin de los trabajos          │
│  · Consumidor del bus: TelemetryAnalysisCompleted/Failed  │
└──────────┬──────────────────────────────────┬─────────────┘
           │ HTTP interno + Polly             │ AMQP (suscriptor)
           │ (retry, breaker, timeouts)       │
           ▼                                  │
┌──────────────────────────────────────┐      │
│ DotNetLab.Api   (Minimal APIs · 8080)│      │
│  · Endpoints/  mapeo y validación    │      │
│  · Publica TelemetryAnalysisRequested│──────┼──► ┌──────────────┐
│  · Consume el resultado → JobRegistry│◄─────┤    │  RabbitMQ    │
│  · 202 Accepted, NUNCA procesa       │      │    │  (exchange + │
└────┬──────────────────────┬──────────┘      │    │  cola por    │
     │ TCP RESP (PING)      │ OTLP            │    │  consumidor) │
     ▼                      │                 │    └──────┬───────┘
┌─────────────┐             │                 │           │ AMQP
│ redis       │             │                 │           ▼
│ (sin puerto │             │                 │  ┌─────────────────────────────┐
│  publicado) │             │                 └──│ DotNetLab.Worker (·/health) │
└─────────────┘             │                    │  · Consume el trabajo pesado│
                            │                    │  · Parsea con Span<T>       │
                            │                    │  · Publica el resultado     │
                            ▼                    └──────────────┬──────────────┘
              ┌──────────────────────────┐                     │ OTLP
              │ OpenTelemetry Collector  │◄────────────────────┘
              │  (único destino OTLP)    │
              └───────┬─────────┬────────┘
                      │         │
                 trazas       métricas
                      ▼         ▼
                 ┌────────┐  ┌────────────┐     ┌──────────┐
                 │ Jaeger │  │ Prometheus │────►│ Grafana  │
                 └────────┘  └────────────┘     └──────────┘

Librerías compartidas:
  DotNetLab.Contracts        ──► Api, Web, Web.Client, Worker, Analysis  (records HTTP y bus)
  DotNetLab.ServiceDefaults  ──► Api, Web, Worker, Analysis   (OTel, health checks, Polly)
  DotNetLab.Analysis         ──► Api, Worker    (parsers, generador, consumidor del bus)
```

### Tres decisiones estructurales que hay que entender antes de tocar nada

**1. El proxy.** El cliente WebAssembly llama a `/api/...` **del propio origen** y el host reenvía
a la API. Evita CORS con preflight en cada llamada, impide que la URL interna del contenedor llegue
al navegador y mantiene la clave compartida en el servidor.
Ver `src/DotNetLab.Web/Infrastructure/ApiProxy.cs`.

**2. El hub de SignalR está en el FRONTEND, no en la API.** Así el navegador abre el WebSocket
contra su propio origen: la API no necesita ser accesible desde fuera y el proxy no tiene que
soportar negociación de WebSocket. Como efecto secundario, queda una demostración limpia de
publish/subscribe: la API y el frontend consumen **el mismo evento**, cada uno con su cola y para
algo distinto, sin conocerse.

**3. Los servicios solo hablan OTLP.** Ninguno conoce a Jaeger, a Prometheus ni a Azure Monitor:
exportan al Collector y este reparte. Cambiar de backend es editar `observability/otel-collector-config.yaml`,
no recompilar tres servicios.

---

## 3. Responsabilidad de cada componente

### `DotNetLab.Contracts`

Records compartidos por API y frontend. **Sin lógica, sin dependencias externas.**

| Archivo | Contiene |
|---|---|
| `PerformanceContracts.cs` | `ParseStrategyResult`, `SpanDemoResponse` |
| `ConcurrencyContracts.cs` | `ChannelEvent` (record struct), `ChannelSummary` |
| `LanguageContracts.cs` | `SensorReading`, `RecordsDemoResponse`, `PatternMatchResponse` |
| `DependencyInjectionContracts.cs` | `ServiceFootprint`, `LifetimesDemoResponse`, `KeyedServiceResponse`, `HealthResponse`, `DependencyProbe` |
| `ResilienceContracts.cs` | `ResilienceAttempt` (record struct), `ResilienceDemoResponse` |
| `MessagingContracts.cs` | **Eventos del bus**: `TelemetryAnalysisRequested`, `TelemetryAnalysisCompleted`, `TelemetryAnalysisFailed`; y los DTOs HTTP `AnalysisJobRequest`, `AnalysisJobAccepted`, `AnalysisJobSnapshot` |
| `LabJsonSerializerContext.cs` | Contexto `System.Text.Json` generado en compilación |

> **Regla:** todo tipo raíz nuevo debe registrarse con `[JsonSerializable]` en
> `LabJsonSerializerContext`. Si no, el cliente WASM puede perder sus metadatos al recortar el IL.

### `DotNetLab.ServiceDefaults`

Configuración transversal de los TRES procesos. Cambiar algo aquí cambia el comportamiento de la
API, el frontend y el Worker a la vez — que es exactamente el motivo de que exista.

| Archivo | Contiene |
|---|---|
| `LabTelemetry.cs` | `ActivitySource` y `Meter` propios, más los siete instrumentos de métrica |
| `ServiceDefaultsExtensions.cs` | `AddLabServiceDefaults()` (OTel + health checks) y `MapLabDefaultEndpoints()` (`/health/live`, `/health/ready`) |
| `ResilienceDefaults.cs` | `AddLabResilience()`: el pipeline estándar de Polly entre servicios |

> Lleva `<FrameworkReference Include="Microsoft.AspNetCore.App" />` **en un `ItemGroup`** (es un
> item de MSBuild, no una propiedad). Por eso puede usar tipos de ASP.NET Core siendo una librería.

### `DotNetLab.Analysis`

Dominio compartido por la API y el Worker. Se extrajo de la API cuando el Worker necesitó los
mismos parsers: la alternativa era que el Worker referenciara el proyecto web entero.

| Archivo | Responsabilidad |
|---|---|
| `ITelemetryParser.cs` | Contrato con `Parse` (medido) y `Warmup` (sin instrumentar) |
| `SpanTelemetryParser.cs` / `NaiveTelemetryParser.cs` | Las dos estrategias; cada una con un `ParseCore` privado que comparten `Parse` y `Warmup` |
| `TelemetrySampleGenerator.cs` | Genera el buffer; la semilla es un parámetro del constructor |
| `TelemetryParserFactory.cs` | Resuelve por clave contra una lista blanca |
| `TelemetryAnalysisConsumer.cs` | **Consumidor del bus**: el mismo caso de uso que los endpoints, disparado por un mensaje |
| `AnalysisServiceCollectionExtensions.cs` | `AddLabAnalysis()`, que llaman API y Worker por igual |

### `DotNetLab.Api`

| Carpeta | Responsabilidad | Qué NO va aquí |
|---|---|---|
| `Endpoints/` | Mapeo de rutas, validación de entrada, forma de la respuesta | Lógica de negocio |
| `Services/` | Lógica propia de la API (registro de trabajos, escenarios de Polly) | El dominio de análisis, que vive en su librería |
| `Consumers/` | Reacción a los eventos del bus | Trabajo pesado |
| `Infrastructure/` | Logging generado, clave compartida, registro del bus | Lógica de dominio |
| `Program.cs` | Registro de DI y orden del pipeline HTTP | Handlers |

Servicios y su lifetime:

| Servicio | Lifetime | Por qué |
|---|---|---|
| `SpanTelemetryParser` | Keyed Singleton `"span"` | Sin estado |
| `NaiveTelemetryParser` | Keyed Singleton `"naive"` | Sin estado |
| `TelemetryParserFactory` | Singleton | Solo envuelve al proveedor |
| `TelemetrySampleGenerator` | Singleton | Semilla inyectada, sin estado por petición |
| `ChannelPipelineService` | Singleton | **Cachea** el último resumen entre peticiones |
| `LanguageShowcaseService` | Singleton | Solo depende de `TimeProvider` |
| `CachePingProbe` | Singleton | Abre y cierra socket por sonda |
| `SharedKeyValidator` | Singleton | Lee el secreto una vez al arrancar |
| `JobRegistry` | Singleton | Estado compartido entre la petición HTTP y el consumidor |
| `FlakyDependency` | Singleton | Mantiene el contador de fallos por correlación |
| `ResilienceShowcaseService` | Singleton | Sin estado; construye un pipeline por ejecución |
| `IAnalysisJobPublisher` | **Scoped** | Depende de `IPublishEndpoint`, que es Scoped. Registrarlo Singleton capturaría un servicio de vida más corta y reventaría en la primera petición |
| `SingletonProbe` / `ScopedProbe` / `TransientProbe` | uno de cada | **Son** la demostración de lifetimes |

### `DotNetLab.Worker`

SDK **`Microsoft.NET.Sdk.Web`**, no `Worker`: un proceso sin endpoint HTTP no puede tener sondas de
liveness ni readiness en Kubernetes, y el kubelet no sabría distinguir un consumidor colgado de uno
ocioso. Expone exactamente dos rutas (`/health/live`, `/health/ready`) y ninguna de negocio. Su
único consumidor registrado es `TelemetryAnalysisConsumer`, que vive en la librería de dominio.

### `DotNetLab.Web` (host)

- `Program.cs`: DI, pipeline, `MapStaticAssets`, proxy, hub de SignalR y `MapRazorComponents`.
- `Infrastructure/ApiProxy.cs`: `ApiOptions` + reenvío con streaming (GET y POST).
- `Infrastructure/WebMessagingOptions.cs`: suscripción al bus. **No admite transporte en memoria**
  a propósito: el publicador vive en otro proceso, así que daría una falsa sensación de funcionar.
- `Realtime/JobsHub.cs`: el hub (vacío, solo difusión servidor→navegador) y
  `JobNotificationConsumer`, el puente bus → SignalR.
- `Components/`: `App.razor`, `Routes.razor`, `Layout/`, `Pages/Home.razor`.
- `wwwroot/app.css`: sistema de diseño completo, con tokens CSS.

### `DotNetLab.Web.Client` (WebAssembly)

- `Pages/`: `ModernDotNet.razor`, `DockerGuide.razor` y `CloudNative.razor`, todas
  `@rendermode InteractiveAuto`.
- `Components/`: `ConceptCard` (contenedor genérico, con playground **opcional**), `CodeSnippet`,
  `MetricRow`, `CSharpHighlighter`.
- `Components/Playgrounds/`: uno por concepto ejecutable.
- `Content/ConceptCatalog.cs`: dos listas, `All` (Fase 01) y `CloudNative` (Fase 02).
  **Es contenido, no lógica.**
- `Services/LabApiClient.cs`: único punto que conoce rutas y query strings.

### Infraestructura (fuera de `src/`)

| Carpeta | Para qué | Cuándo tocarla |
|---|---|---|
| `k8s/` | Manifiestos comentados campo a campo | Material de estudio y despliegue con `kubectl` |
| `helm/dotnetlab/` | Chart parametrizado (un `range` genera los tres Deployments) | La vía de despliegue real, con valores por entorno |
| `infrastructure/` | Bicep: `main.bicep` + módulos ACR, AKS, Key Vault, monitoring, Container Apps | Aprovisionar Azure |
| `observability/` | Config del colector OTel, Prometheus y provisioning de Grafana | Cambiar el destino de la telemetría o añadir paneles |

---

## 4. Flujo de una interacción, de principio a fin

Ejemplo: el usuario pulsa "Ejecutar comparativa" en la tarjeta de `Span<T>`.

1. `SpanPlayground.RunAsync()` pone `_busy = true` y llama a `LabApiClient.GetSpanDemoAsync(rows)`.
2. `LabApiClient` hace `GET api/performance/span-demo?rows=25000` sobre su `HttpClient`.
   - En **WebAssembly**, la base es el origen de la página → llega al proxy del host.
   - En **servidor** (prerender), la base es la URL interna de la API → llega directo.
3. `ApiProxy.ForwardAsync` añade la cabecera `X-Lab-Key` y reenvía con `ResponseHeadersRead`.
4. `SharedKeyEndpointFilter` valida la cabecera en tiempo constante.
5. El limitador de tasa comprueba la ventana de 20 peticiones / 10 s.
6. `PerformanceEndpoints.SpanDemo` acota las filas, genera el buffer, **calienta el JIT**, ejecuta
   ambas estrategias y compone la respuesta.
7. Cada parser mide Gen0, bytes asignados y tiempo alrededor de su bucle.
8. La respuesta se serializa con el contexto JSON generado.
9. El playground pinta `MetricRow` con las barras proporcionales.

### El flujo asíncrono (el que atraviesa TODO el sistema)

Ejemplo: el usuario pulsa "Encolar análisis" en la tarjeta de arquitectura dirigida por eventos.

1. `AsyncJobPlayground.SubmitAsync()` llama a `LabApiClient.SubmitAnalysisJobAsync(rows, strategy)`.
2. `POST /api/analysis/jobs` llega al **proxy** del frontend, que copia el método y el cuerpo
   (envolviendo el `Stream` sin bufferizarlo) y añade `X-Lab-Key`.
3. La API valida la estrategia **en el borde**: una clave desconocida se rechaza con 400 sin llegar
   a publicar nada, porque ese mensaje solo podría acabar en la cola de errores.
4. `BusAnalysisJobPublisher` registra el trabajo en `JobRegistry` (primero el registro, luego la
   publicación: al revés habría una ventana en la que el resultado llega antes que la aceptación),
   publica `TelemetryAnalysisRequested` e incrementa `jobs.accepted` y `jobs.in_flight`.
5. La API responde **202 Accepted** con la cabecera `Location`. Aquí termina la petición HTTP.
6. RabbitMQ encamina el evento a la cola del `TelemetryAnalysisConsumer` del **Worker**.
7. El consumidor calcula la **latencia de cola** (instante actual menos `RequestedAt`, que viaja en
   el mensaje), **calienta el JIT**, parsea con `Span<T>` y publica `TelemetryAnalysisCompleted`.
8. Ese evento llega a **dos** colas independientes:
   - La de la API → `AnalysisResultConsumer` actualiza `JobRegistry` y baja `jobs.in_flight`.
   - La del frontend → `JobNotificationConsumer` lo empuja por SignalR al navegador.
9. El playground pinta el resultado y **anota por qué canal llegó** (`SignalR` o `consulta`).
   Su bucle de consulta periódica se detiene solo al ver un estado terminal.
10. La traza completa (pasos 2 a 8, salto por RabbitMQ incluido) es **una sola** en Jaeger, porque
    MassTransit propaga el `traceparent` en las cabeceras del mensaje.

---

## 5. Cómo extender el sistema

### 5.1 Añadir un módulo pedagógico completo (concepto + endpoint + tarjeta)

Siete pasos. El orden importa: el contrato primero, la UI al final.

**Paso 1 — Contrato.** Crea el record en `src/DotNetLab.Contracts/` con un comentario por campo.

```csharp
public sealed record FrozenCollectionsResponse(
    // Milisegundos de la búsqueda sobre Dictionary<K,V> clásico.
    double DictionaryMicroseconds,
    // Milisegundos sobre FrozenDictionary, optimizado para lectura.
    double FrozenMicroseconds,
    string Verdict);
```

**Paso 2 — Registrar el tipo en el contexto JSON.** En `LabJsonSerializerContext.cs`:

```csharp
[JsonSerializable(typeof(FrozenCollectionsResponse))]
```

**Paso 3 — Servicio.** En `src/DotNetLab.Api/Services/`, con primary constructor y `TimeProvider` si
necesita el reloj. Si mide rendimiento, sigue el patrón exacto de `SpanTelemetryParser`:
`GC.CollectionCount(0)` → `GC.GetAllocatedBytesForCurrentThread()` → `Stopwatch.GetTimestamp()`.

**Paso 4 — Endpoints.** Un archivo nuevo en `Endpoints/` siguiendo la plantilla:

```csharp
public static class CollectionsEndpoints
{
    public static RouteGroupBuilder MapCollectionsEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/collections").WithTags("Collections");
        group.MapGet("/frozen-demo", FrozenDemo).WithName("FrozenDemo").WithSummary("...");
        return api;
    }

    private static Ok<FrozenCollectionsResponse> FrozenDemo(FrozenCollectionsService service)
        => TypedResults.Ok(service.Run());
}
```

**Paso 5 — Registro en `Program.cs`.** Dos líneas: el servicio en el bloque de DI y
`securedApi.MapCollectionsEndpoints();` junto a los demás. Añade la ruta a `endpointCatalog`.

**Paso 6 — Ficha en el catálogo.** En `Content/ConceptCatalog.cs`: una constante con el id, y una
entrada en `All` con los siete campos de texto (`WhatIsIt`, `HowItWorks`, `WhenToUse`, `MemoryNote`,
`Snippet`, `Endpoint`). **El snippet debe ser código copiado del backend, no reescrito.**

**Paso 7 — Playground y conexión.** Un `.razor` nuevo en `Components/Playgrounds/` siguiendo el
patrón (`_busy`, `_error`, `_result`, captura específica de excepciones), un método en
`LabApiClient` y un `case` en el `switch` de `ModernDotNet.razor`. Añade también la entrada al
`switch` de `SourceFileFor`.

**Paso 8 — Pruebas.** Un test unitario del servicio y uno de integración del endpoint.

### 5.2 Añadir solo un endpoint de rendimiento a un módulo existente

Pasos 1, 2, 3, 4 y 8 de arriba. Si el concepto ya tiene tarjeta, basta con añadir un botón al
playground existente.

### 5.3 Añadir una estrategia con Keyed Services

1. Implementa `ITelemetryParser` en un servicio nuevo.
2. Regístralo: `builder.Services.AddKeyedSingleton<ITelemetryParser, MiParser>("mi-clave");`
3. **Añade la clave a la lista blanca** de `TelemetryParserFactory.Keys`. Sin esto, la fábrica la
   rechaza — es deliberado: impide que un cliente sondee el contenedor con claves arbitrarias.
4. Añádela a `Keys` en `KeyedServicesPlayground.razor` para que aparezca el botón.
5. Test parametrizado en `EndpointsIntegrationTests.LasClavesResuelvenLaImplementacionEsperada`.

### 5.4 Añadir un contenedor auxiliar

1. Servicio nuevo en `docker-compose.yml`, en la red `labnet`, **sin publicar puertos** salvo que
   haya una razón explícita.
2. `healthcheck` propio y `depends_on: condition: service_healthy` en quien lo consuma.
3. Configuración por variables `Xxx__*`; secretos por `secrets:` y fichero montado.
4. Sonda en la API siguiendo el patrón de `CachePingProbe`: timeout propio, captura de
   `SocketException` y `OperationCanceledException`, y degradación elegante (nunca lanzar).

### 5.5 Cambiar el estilo visual

Todo pasa por los tokens de `:root` en `src/DotNetLab.Web/wwwroot/app.css`. Cambiar `--accent`
reestiliza el portal entero. No añadas frameworks CSS ni fuentes remotas.

### 5.6 Añadir una métrica a OpenTelemetry

**Paso 1 — Elige el instrumento por la pregunta que responde.** Es la decisión importante, y
equivocarse produce paneles que no significan nada:

| Pregunta | Instrumento | Ejemplo en el repositorio |
|---|---|---|
| "¿Cuántas veces ha pasado?" | `Counter<T>` (solo crece) | `dotnetlab.parse.operations` |
| "¿Cómo se distribuye?" (p50, p95) | `Histogram<T>` | `dotnetlab.parse.duration` |
| "¿Cuántos hay AHORA?" | `UpDownCounter<T>` | `dotnetlab.jobs.in_flight` |
| "¿Cuál es el valor actual de algo que ya existe?" | `ObservableGauge<T>` | *(ninguno todavía)* |

**Paso 2 — Declárala en `LabTelemetry`,** como campo `static readonly`. Nunca crees un instrumento
dentro de un método: el `MeterProvider` los mantiene vivos y acabarías filtrando memoria.

```csharp
public static readonly Counter<long> CacheProbes = Meter.CreateCounter<long>(
    name: $"{SystemName}.cache.probes",   // el prefijo agrupa las métricas del sistema
    unit: "{probe}",                       // UCUM: "By" bytes, "s" segundos, "{x}" adimensional
    description: "Sondas enviadas al contenedor auxiliar, por resultado.");
```

**Paso 3 — Regístrala donde ocurre el hecho,** con etiquetas de **cardinalidad baja**:

```csharp
LabTelemetry.CacheProbes.Add(1, new KeyValuePair<string, object?>("result", "reachable"));
```

> ⚠️ Una etiqueta con un `jobId`, un id de usuario o una URL completa crea **una serie temporal
> por valor distinto**. Es la forma más rápida de tumbar Prometheus. Si necesitas ese detalle,
> va en un span, no en una métrica.

**Paso 4 — No hay paso 4 para el registro.** `AddMeter(LabTelemetry.SystemName)` ya está en
`ServiceDefaults` y cubre todos los instrumentos del mismo Meter.

**Paso 5 — Compruébala.** Con el stack levantado, en `http://localhost:9090` busca
`dotnetlab_cache_probes_total` (Prometheus añade el sufijo `_total` a los contadores y convierte
los puntos en guiones bajos: el nombre en C# y el de la consulta **no coinciden literalmente**).

**Paso 6 — Añade el panel** a `observability/grafana/dashboards/dotnetlab.json` si merece
seguimiento continuo.

### 5.7 Registrar un consumidor de eventos nuevo

**Paso 1 — Define el evento** en `src/DotNetLab.Contracts/MessagingContracts.cs` como un record
inmutable, y regístralo con `[JsonSerializable]` en `LabJsonSerializerContext`.

> Los mensajes son **contratos**: solo se les añaden campos opcionales. Quitar o renombrar uno
> rompe a los consumidores que todavía no se han desplegado.

**Paso 2 — Escribe el consumidor.** Decide primero **dónde vive**, que es la parte que se hace mal:

| Si el consumidor… | Va en… |
|---|---|
| Hace trabajo pesado de dominio | `DotNetLab.Analysis` (lo alojan el Worker y, en modo memoria, la API) |
| Solo actualiza estado de la API | `DotNetLab.Api/Consumers/` |
| Notifica al navegador | `DotNetLab.Web/Realtime/` |

```csharp
public sealed class MiConsumidor(MiServicio servicio) : IConsumer<MiEvento>
{
    public async Task Consume(ConsumeContext<MiEvento> context)
    {
        // IDEMPOTENTE: RabbitMQ garantiza entrega "al menos una vez". Este método puede
        // ejecutarse dos veces con el mismo mensaje y el resultado debe ser el mismo.
        await servicio.HacerAlgo(context.Message, context.CancellationToken);
    }
}
```

**Paso 3 — Regístralo** en el `AddMassTransit` del proceso que lo aloja:

```csharp
bus.AddConsumer<MiConsumidor>();
```

`SetKebabCaseEndpointNameFormatter()` le dará una cola propia (`mi-consumidor`). Cada consumidor
tiene **su** cola: si uno falla y reintenta, no bloquea a los demás.

**Paso 4 — Publica el evento** con `context.Publish(...)` o `IPublishEndpoint.Publish(...)`.
`Publish` (evento, N suscriptores) y no `Send` (comando, un destinatario concreto).

**Paso 5 — Pruébalo** con el transporte en memoria: `AsyncJobIntegrationTests` recorre el flujo
completo sin levantar RabbitMQ, porque en ese modo la API aloja también el consumidor.

**Paso 6 — Comprueba las colas** en `http://localhost:15672` con el stack levantado. Si aparecen
mensajes en la cola `_error`, el consumidor agotó sus reintentos: el mensaje no se ha perdido y se
puede inspeccionar allí.

### 5.8 Extender los manifiestos de Kubernetes

**Regla previa:** hay **dos** definiciones que mantener sincronizadas, `k8s/` (estudio) y
`helm/dotnetlab/` (despliegue). Un cambio de comportamiento va en las dos.

**Para añadir un servicio nuevo al chart** basta una entrada en `values.yaml`; el `range` de
`templates/deployment.yaml` y `templates/service.yaml` hace el resto:

```yaml
components:
  minuevo:
    enabled: true
    image: dotnetlab-minuevo
    replicaCount: 2
    maxUnavailable: 0
    terminationGracePeriodSeconds: 30
    resources:
      requests: { cpu: 100m, memory: 192Mi }
      limits:   { cpu: "1",  memory: 512Mi }
    secretKeys: [rabbitmq-password]     # SOLO lo que ese pod necesita ver
    autoscaling:
      enabled: true
      minReplicas: 2
      maxReplicas: 8
      targetCPUUtilizationPercentage: 70
      targetMemoryUtilizationPercentage: 0
    sessionAffinity: None
```

**Para cambiar configuración**: `k8s/configmap.yaml` y la sección `config` de `values.yaml`. La
anotación `checksum/config` del Deployment fuerza el rollout; **sin ella el ConfigMap se actualiza
y los pods siguen con los valores viejos**, y el despliegue parece haber funcionado.

**Para añadir un secreto**: la aplicación debe leerlo por **RUTA** (`Xxx__File`), no por valor. Es
lo que permite que el mismo binario funcione con docker secrets, con `Secret` de Kubernetes y con
Key Vault vía CSI driver sin cambiar una línea de C#.

**Antes de dar por bueno un manifiesto:**

```bash
kubeconform -strict -summary -kubernetes-version 1.31.0 k8s/
```

```bash
helm template lab helm/dotnetlab -f helm/dotnetlab/values-prod.yaml --set secrets.existingSecret=x
```

Comprueba en el render que **el Deployment con HPA no emite `replicas`**: si lo hiciera, cada
`helm upgrade` desharía el autoescalado. Hay un paso de CI dedicado a esto.

### 5.9 Añadir un recurso de Azure (Bicep)

1. Módulo nuevo en `infrastructure/modules/`, con parámetros tipados y `@description` en cada uno.
2. Invócalo desde `main.bicep` pasándole `logAnalyticsWorkspaceId` para sus diagnósticos.
3. Nombres con `uniqueString(resourceGroup().id)` si el recurso exige unicidad global.
4. Asignaciones de rol con `guid(...)` **determinista**, o cada despliegue creará una duplicada.
5. Identidad gestionada siempre; nunca una contraseña o una clave de acceso.
6. Valida con `az bicep build --file infrastructure/main.bicep` y revisa con
   `az deployment group what-if` antes de crear nada.

---

## 6. Trampas conocidas (no las repitas)

| Trampa | Síntoma | Solución |
|---|---|---|
| Prefijo de `Guid.CreateVersion7()` | Ids "únicos" idénticos | Usa `[^8..]`, no `[..8]`: el prefijo es el timestamp |
| `"Urls"` en `appsettings.json` | `dotnet run` ignora el puerto de launchSettings | No fijes el puerto en el JSON; usa `ASPNETCORE_HTTP_PORTS` |
| `logger.LogInformation(...)` directo | El build falla con CA1848/CA1873 | Añade un método a `ApiLog` con `[LoggerMessage]` |
| `RegexOptions.Compiled` en el cliente | Falla en WebAssembly (no hay `Reflection.Emit`) | Usa `[GeneratedRegex]` |
| Concatenar interpolaciones en `FormattableString.Invariant` | `CS1503` y pérdida de la cultura invariante | Escapa en locales y deja una sola expresión |
| Medir sin calentar el JIT | La primera ejecución reporta miles de bytes en una ruta que asigna 0 | Llama a `Warmup(...)` antes de `Parse(...)` |
| Calentar llamando a `Parse` | Los histogramas se sesgan con muestras de 8 KB | Usa `Warmup`, que ejecuta la misma ruta sin instrumentar |
| Dividir entre un contador de bytes que vale 0 | Ratios absurdos (`9740344x`) | Devuelve `null` y redacta el veredicto aparte |
| `UseStaticFiles()` en el host Blazor | Aviso de assets no mapeados; `@Assets[...]` sin huella | `MapStaticAssets()` antes de `AddInteractiveWebAssemblyRenderMode()` |
| Página interactiva en el proyecto host | Nunca se ejecuta en WebAssembly | Muévela a `DotNetLab.Web.Client` |
| Canal sin acotar | Crecimiento de memoria sin techo | `Channel.CreateBounded` siempre |
| `FrameworkReference` en `PropertyGroup` | `MSB4066: no se reconoce el atributo Include` | Es un **item**: va en `ItemGroup` |
| `builder.Logging.AddOpenTelemetry(x => ...)` no compila | "Ninguna sobrecarga toma 1 argumento" | Falta `using Microsoft.Extensions.Logging;` |
| `TestContext.Current` en los tests | `CS0103: no existe en el contexto actual` | Es API de xUnit **v3**; el repositorio usa xUnit 2 |
| `replicas` emitido con HPA activo | Cada `helm upgrade` deshace el autoescalado | Envuélvelo en `{{- if not $component.autoscaling.enabled }}` |
| Falta `checksum/config` en el pod | El ConfigMap cambia y los pods siguen igual | Anota el hash del ConfigMap renderizado |
| Dependencia externa en la sonda de **liveness** | Una caída del broker reinicia todos los pods | Liveness → `/health/live` (sin dependencias); readiness → `/health/ready` |
| NetworkPolicy sin regla para el kubelet | Las sondas fallan y los pods se reinician en bucle | El kubelet sonda desde la **IP del nodo**, no desde un pod |
| Etiqueta de métrica con `jobId` o id de usuario | Prometheus explota por cardinalidad | Ese detalle va en un span, no en una métrica |
| Subir MassTransit a 9.x | Licencia comercial (Massient, Inc.) | Quédate en 8.5.10, que es Apache-2.0 |
| Bus en memoria en el **frontend** | Nunca recibe nada y parece que SignalR falla | El publicador está en otro proceso: el frontend solo admite `rabbitmq` |
| Restore en Docker copiando solo los `.csproj` (proyecto Blazor) | `/_framework/blazor.web.js` → 404; la página se ve pero nada es interactivo | `<RequiresAspNetWebAssets>true</RequiresAspNetWebAssets>` en el csproj del host: el SDK solo descarga ese pack si ve `.razor` en el restore |
| `href="#id"` con `<base href="/">` | El enlace de ancla lleva a la portada | Ruta completa: `href="modern-dotnet#id"` |

---

## 7. Comprobaciones antes de dar por terminado un cambio

```bash
dotnet build
```

```bash
dotnet test
```

Si tocaste infraestructura:

```bash
kubeconform -strict -summary -kubernetes-version 1.31.0 k8s/
```

```bash
helm lint helm/dotnetlab && helm template lab helm/dotnetlab -f helm/dotnetlab/values-dev.yaml > /dev/null
```

```bash
az bicep build --file infrastructure/main.bicep
```

Además:

- Si tocaste un endpoint: `curl` contra él y revisar el JSON.
- Si tocaste la UI: abrirla y ejecutar el playground afectado.
- Si tocaste algo del bus: recorrer el flujo asíncrono completo (encolar y ver el resultado).
- Si el snippet de una tarjeta corresponde a código que has modificado: **actualízalo**. El
  invariante 3 dice que los snippets son código real.
- Si descubriste algo no obvio: entrada nueva en `PROJECT_DEVLOG.md`.

---

## 8. Qué NO cambiar sin una razón explícita

- El **contexto de build en la raíz** de los Dockerfile: los proyectos referencian librerías que
  viven fuera de sus carpetas.
- La **exclusión de los endpoints de salud** del filtro de clave compartida: los consumen el
  `HEALTHCHECK` del contenedor y el kubelet, que no tienen el secreto.
- El **`ENTRYPOINT` en forma exec**: en forma shell, `sh` sería PID 1 y no propagaría `SIGTERM`.
  En el Worker esto es crítico: sin la señal, el proceso muere a mitad de un mensaje y RabbitMQ lo
  reencola para que otro pod repita el trabajo.
- La ausencia de `ports` en `cache` y del puerto AMQP de `rabbitmq`: son servicios sin
  autenticación fuerte y no deben ser accesibles desde el host.
- `FixedTimeEquals` en la validación de la clave: `==` filtra información por canal lateral.
- `TreatWarningsAsErrors`: media docena de mejoras reales del código salieron de ahí.
- Que los servicios exporten **solo OTLP**: es lo que permite cambiar de backend de observabilidad
  sin recompilar.
- Que el hub de SignalR viva en el **frontend**: moverlo a la API obligaría a exponerla y a que el
  proxy soportara negociación de WebSocket.
- El `Warmup` antes de cada medición: sin él, las cifras que muestra el portal incluyen el trabajo
  del JIT y son falsas (reto 12 del devlog).

---

## 9. Limitaciones conocidas (no son bugs, son deudas documentadas)

| Limitación | Impacto | Solución cuando toque |
|---|---|---|
| `JobRegistry` es memoria del proceso | Con varias réplicas de la API, `GET /api/analysis/jobs/{id}` puede dar 404 en la réplica equivocada | Redis o una tabla. El canal principal (SignalR) no se ve afectado |
| El HPA del Worker escala por CPU | Un pod saturado con 10.000 mensajes en cola marca lo mismo que con 10 | KEDA con el scaler de RabbitMQ (esbozado en `k8s/hpa.yaml`) |
| `sessionAffinity: ClientIP` en el frontend | Detrás de un NAT corporativo, muchos usuarios comparten IP y el reparto se desequilibra | Backplane de SignalR con Redis o Azure SignalR Service |
| RabbitMQ y Redis en `k8s/dependencies.yaml` sin persistencia | Al morir el pod se pierden las colas | Operador de RabbitMQ, o Azure Service Bus / Cache for Redis |
| Sin `Job`/`CronJob` de Kubernetes ni Blob Storage | Lagunas frente a los temarios de CKAD y AZ-204 | Identificadas en la sección *Career & Resume Impact* del devlog |
