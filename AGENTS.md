# AGENTS.md

Documento de contexto para agentes de IA y asistentes de código que trabajen sobre **DotNetLab**.
Describe la arquitectura, la responsabilidad de cada componente y cómo extender el sistema sin
romper sus invariantes.

Complementa a `CLAUDE.md` (comandos y estilo) y a `PROJECT_DEVLOG.md` (por qué es así y qué se
intentó antes).

---

## 1. Propósito e invariantes

DotNetLab es un **portal pedagógico ejecutable**. Explica conceptos de .NET 10 / C# 14 y de Docker
ejecutándolos de verdad y mostrando métricas reales del proceso servidor.

Hay cuatro invariantes. Romper cualquiera de ellos invalida el proyecto aunque el código compile:

| # | Invariante | Cómo se protege |
|---|---|---|
| 1 | Las métricas se **miden**, nunca se simulan | `GC.GetAllocatedBytesForCurrentThread()` y `Stopwatch` dentro del handler |
| 2 | Las rutas "zero-allocation" asignan **exactamente 0 bytes** | Test `LaRutaConSpansNoAsignaEnElHeap` |
| 3 | Los snippets del portal son **código real del repositorio** | Cada `Concept` declara el archivo del que sale |
| 4 | El frontend funciona **sin red externa** | Sin CDN, sin fuentes remotas, resaltador propio |

---

## 2. Mapa de la arquitectura

```
Navegador
   │  HTTP (rutas relativas /api/...)
   ▼
┌──────────────────────────────────────────────┐
│ DotNetLab.Web  (host Blazor, puerto 8080)    │
│  · Render estático + interactivo de servidor │
│  · Sirve el runtime WASM del cliente         │
│  · PROXY /api/{**path} → añade X-Lab-Key     │
└───────────────┬──────────────────────────────┘
                │  HTTP interno (http://api:8080)
                ▼
┌──────────────────────────────────────────────┐
│ DotNetLab.Api  (Minimal APIs, puerto 8080)   │
│  · Endpoints/  → mapeo y validación           │
│  · Services/   → la lógica que se enseña      │
│  · Valida X-Lab-Key contra el docker secret   │
└───────────────┬──────────────────────────────┘
                │  TCP RESP (PING)
                ▼
┌──────────────────────────────────────────────┐
│ cache  (redis:7-alpine, sin puertos públicos)│
└──────────────────────────────────────────────┘

DotNetLab.Contracts  ──► referenciado por Api, Web y Web.Client
DotNetLab.Web.Client ──► se descarga al navegador como WebAssembly
```

### Por qué el proxy

El cliente WebAssembly llama a `/api/...` **del propio origen**. El host reenvía a la API. Esto
evita CORS con preflight en cada llamada, impide que la URL interna del contenedor llegue al
navegador y mantiene la clave compartida en el servidor. Ver `src/DotNetLab.Web/Infrastructure/ApiProxy.cs`.

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
| `LabJsonSerializerContext.cs` | Contexto `System.Text.Json` generado en compilación |

> **Regla:** todo tipo raíz nuevo debe registrarse con `[JsonSerializable]` en
> `LabJsonSerializerContext`. Si no, el cliente WASM puede perder sus metadatos al recortar el IL.

### `DotNetLab.Api`

| Carpeta | Responsabilidad | Qué NO va aquí |
|---|---|---|
| `Endpoints/` | Mapeo de rutas, validación de entrada, forma de la respuesta | Lógica de negocio |
| `Services/` | La lógica real que el portal enseña | Conocimiento de HTTP |
| `Infrastructure/` | Logging generado, seguridad de la clave compartida | Lógica de dominio |
| `Program.cs` | Registro de DI y orden del pipeline HTTP | Handlers |

Servicios y su lifetime:

| Servicio | Lifetime | Por qué |
|---|---|---|
| `SpanTelemetryParser` | Keyed Singleton `"span"` | Sin estado |
| `NaiveTelemetryParser` | Keyed Singleton `"naive"` | Sin estado |
| `TelemetryParserFactory` | Singleton | Solo envuelve al proveedor |
| `TelemetrySampleGenerator` | Singleton | Semilla fija, sin estado por petición |
| `ChannelPipelineService` | Singleton | **Cachea** el último resumen entre peticiones |
| `LanguageShowcaseService` | Singleton | Solo depende de `TimeProvider` |
| `CachePingProbe` | Singleton | Abre y cierra socket por sonda |
| `SharedKeyValidator` | Singleton | Lee el secreto una vez al arrancar |
| `SingletonProbe` / `ScopedProbe` / `TransientProbe` | uno de cada | **Son** la demostración de lifetimes |

### `DotNetLab.Web` (host)

- `Program.cs`: DI, pipeline, `MapStaticAssets`, proxy y `MapRazorComponents`.
- `Infrastructure/ApiProxy.cs`: `ApiOptions` + reenvío con streaming.
- `Components/`: `App.razor` (documento raíz), `Routes.razor` (router), `Layout/`, `Pages/Home.razor`.
- `wwwroot/app.css`: sistema de diseño completo, con tokens CSS.

### `DotNetLab.Web.Client` (WebAssembly)

- `Pages/`: `ModernDotNet.razor` y `DockerGuide.razor`, ambas `@rendermode InteractiveAuto`.
- `Components/`: `ConceptCard` (contenedor genérico), `CodeSnippet`, `MetricRow`, `CSharpHighlighter`.
- `Components/Playgrounds/`: uno por concepto.
- `Content/ConceptCatalog.cs`: teoría y snippets. **Es contenido, no lógica.**
- `Services/LabApiClient.cs`: único punto que conoce rutas y query strings.

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

---

## 6. Trampas conocidas (no las repitas)

| Trampa | Síntoma | Solución |
|---|---|---|
| Prefijo de `Guid.CreateVersion7()` | Ids "únicos" idénticos | Usa `[^8..]`, no `[..8]`: el prefijo es el timestamp |
| `"Urls"` en `appsettings.json` | `dotnet run` ignora el puerto de launchSettings | No fijes el puerto en el JSON; usa `ASPNETCORE_HTTP_PORTS` |
| `logger.LogInformation(...)` directo | El build falla con CA1848/CA1873 | Añade un método a `ApiLog` con `[LoggerMessage]` |
| `RegexOptions.Compiled` en el cliente | Falla en WebAssembly (no hay `Reflection.Emit`) | Usa `[GeneratedRegex]` |
| Concatenar interpolaciones en `FormattableString.Invariant` | `CS1503` y pérdida de la cultura invariante | Escapa en locales y deja una sola expresión |
| Medir sin calentar el JIT | La primera estrategia siempre "pierde" | Ejecuta un fragmento antes de arrancar el cronómetro |
| Dividir entre un contador de bytes que vale 0 | Ratios absurdos (`9740344x`) | Devuelve `null` y redacta el veredicto aparte |
| `UseStaticFiles()` en el host Blazor | Aviso de assets no mapeados; `@Assets[...]` sin huella | `MapStaticAssets()` antes de `AddInteractiveWebAssemblyRenderMode()` |
| Página interactiva en el proyecto host | Nunca se ejecuta en WebAssembly | Muévela a `DotNetLab.Web.Client` |
| Canal sin acotar | Crecimiento de memoria sin techo | `Channel.CreateBounded` siempre |

---

## 7. Comprobaciones antes de dar por terminado un cambio

```bash
dotnet build
```

```bash
dotnet test
```

Además:

- Si tocaste un endpoint: `curl` contra él y revisar el JSON.
- Si tocaste la UI: abrirla y ejecutar el playground afectado.
- Si el snippet de una tarjeta corresponde a código que has modificado: **actualízalo**. El
  invariante 3 dice que los snippets son código real.
- Si descubriste algo no obvio: entrada nueva en `PROJECT_DEVLOG.md`.

---

## 8. Qué NO cambiar sin una razón explícita

- El **contexto de build en la raíz** de los Dockerfile: los proyectos referencian `Contracts`.
- La **exclusión del endpoint de diagnóstico** del filtro de clave compartida: lo consume el
  `HEALTHCHECK`, que no tiene el secreto.
- El **`ENTRYPOINT` en forma exec**: en forma shell, `sh` sería PID 1 y no propagaría `SIGTERM`.
- La ausencia de `ports` en el servicio `cache`: es una base de datos sin autenticación.
- `FixedTimeEquals` en la validación de la clave: `==` filtra información por canal lateral.
- `TreatWarningsAsErrors`: media docena de mejoras reales del código salieron de ahí.
