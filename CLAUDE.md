# CLAUDE.md

Guía de contexto y reglas para trabajar con Claude Code en **DotNetLab**.
Léela antes de tocar código: recoge los comandos reales del repositorio y los estándares que ya
sigue el código existente.

---

## Qué es este proyecto

Portal interactivo que explica **cómo funcionan por dentro** las tecnologías de la FASE 01 del plan
de estudio: Modern .NET / C# y Docker. No es una demo decorativa — cada tarjeta del portal ejecuta
código real contra la Web API y muestra métricas medidas en el proceso del servidor.

**Consecuencia práctica:** cualquier cambio que sustituya una medición real por un valor simulado
rompe el propósito del proyecto, aunque compile.

---

## Comandos habituales

### Compilación

```bash
dotnet build
```

```bash
dotnet build -c Release
```

> El repositorio compila con **`TreatWarningsAsErrors=true`**. Un aviso rompe el build: es
> intencionado. No lo desactives; arregla el aviso o justifica un `#pragma warning disable` concreto
> con un comentario que explique por qué.

### Pruebas

```bash
dotnet test
```

```bash
dotnet test --filter "FullyQualifiedName~TelemetryParserTests"
```

```bash
dotnet test --collect:"XPlat Code Coverage"
```

### Ejecución local (sin Docker)

Necesitas **dos terminales**. La API primero:

```bash
dotnet run --project src/DotNetLab.Api
```

```bash
dotnet run --project src/DotNetLab.Web
```

| Servicio | URL local | Notas |
|---|---|---|
| Web API | http://localhost:5280 | OpenAPI en `/openapi/v1.json` (solo en Development) |
| Portal Blazor | http://localhost:5120 | Es el que hay que abrir en el navegador |

En local, la validación de clave compartida está **desactivada** (no hay secreto configurado) y la
sonda a la cache responde "deshabilitada". Es el comportamiento correcto, no un fallo.

### Docker

Antes del primer arranque hay que generar el secreto:

```bash
openssl rand -base64 32 > secrets/lab_shared_key.txt
```

```bash
docker compose up --build
```

```bash
docker compose logs -f api
```

```bash
docker compose down -v
```

| Servicio | Puerto publicado | Notas |
|---|---|---|
| `web` | http://localhost:8081 | El portal |
| `api` | http://localhost:8080 | Publicado solo para inspeccionar el JSON crudo |
| `cache` | *(ninguno)* | Solo accesible desde la red interna `labnet` |

Construir una imagen suelta:

```bash
docker build -f src/DotNetLab.Api/Dockerfile -t dotnetlab-api .
```

> El contexto de build es **la raíz del repositorio** (el `.` final), no la carpeta del proyecto:
> ambos proyectos referencian `DotNetLab.Contracts`, que vive fuera de sus carpetas.

### Diagnóstico rápido

```bash
curl -s "http://localhost:5280/api/performance/span-demo?rows=25000"
```

```bash
curl -s "http://localhost:5280/api/diagnostics/health"
```

---

## Estructura y responsabilidades

```
src/
  DotNetLab.Api/          Web API con Minimal APIs. Ejecuta todas las demostraciones.
    Endpoints/            Un archivo por módulo pedagógico. Solo mapeo y validación.
    Services/             La lógica real. Es lo que el portal enseña.
    Infrastructure/       Transversal: logging generado, seguridad de la clave compartida.
  DotNetLab.Contracts/    Records compartidos entre API y frontend. Sin lógica.
  DotNetLab.Web/          Host Blazor: render de servidor, estáticos y proxy hacia la API.
  DotNetLab.Web.Client/   Ensamblado WebAssembly: páginas interactivas y playgrounds.
tests/
  DotNetLab.Api.Tests/    Unitarias de servicios + integración de endpoints.
```

Ver `AGENTS.md` para el mapa detallado y las guías paso a paso de extensión.

---

## Estándares de C# / .NET 10

### Configuración global

Está centralizada en `Directory.Build.props` y aplica a **todos** los proyectos:

| Propiedad | Valor | Motivo |
|---|---|---|
| `TargetFramework` | `net10.0` | Un único TFM en toda la solución |
| `LangVersion` | `14.0` | Explícito para documentar la intención |
| `Nullable` | `enable` | El compilador exige anotar lo que puede ser null |
| `ImplicitUsings` | `enable` | Menos ruido de `using` en cada archivo |
| `TreatWarningsAsErrors` | `true` | La deuda se resuelve en el commit que la crea |
| `AnalysisLevel` | `latest-recommended` | Reglas CA activas |
| `InvariantGlobalization` | `true` | Mismo formato en local y en contenedor |

### Convenciones de nombres

| Elemento | Convención | Ejemplo |
|---|---|---|
| Clases, records, métodos, propiedades | `PascalCase` | `SpanTelemetryParser` |
| Parámetros y variables locales | `camelCase` | `allocatedBefore` |
| Campos privados | `_camelCase` | `_cache` |
| Constantes | `PascalCase` | `MaxEvents` |
| Interfaces | `I` + `PascalCase` | `ITelemetryParser` |
| Métodos asíncronos | sufijo `Async` | `GetSummaryAsync` |
| Ficheros | un tipo público por archivo, nombre igual al tipo | |
| Tests | `MetodoOEscenario_ResultadoEsperado` en español | `LaRutaConSpansNoAsignaEnElHeap` |

Las **claves de Keyed Services** van en minúscula y sin espacios (`"span"`, `"naive"`), y se validan
contra una lista blanca antes de resolverlas.

### Formato

- **4 espacios** de indentación, nunca tabuladores.
- Longitud de línea objetivo: **~110 columnas**.
- Llaves en línea propia (estilo Allman), como el resto del repositorio.
- `var` cuando el tipo es evidente en la parte derecha; tipo explícito cuando aporta claridad
  (`double sum = 0;`).
- Un `using` por línea, ordenados: `System.*` primero, luego el resto alfabéticamente.
- `sealed` por defecto en las clases que no se diseñan para heredar.

### Patrones que el código ya usa (síguelos)

1. **Primary constructors** para servicios inyectados:
   ```csharp
   public sealed class SpanTelemetryParser(ILogger<SpanTelemetryParser> logger) : ITelemetryParser
   ```

2. **Records** para todo DTO y valor inmutable. `record struct` cuando el tipo es pequeño y viaja
   en bucles calientes (`ChannelEvent`, `ServiceFootprint`).

3. **`TypedResults`** y no `Results`:
   ```csharp
   private static Results<Ok<SpanDemoResponse>, ProblemHttpResult> SpanDemo(...)
   ```

4. **Logging generado en compilación**. Nunca `logger.LogInformation("... {X}", x)` directamente:
   añade un método a `ApiLog` con `[LoggerMessage]`. Los analizadores lo exigen (CA1848/CA1873).

5. **`TimeProvider` inyectado**, nunca `DateTime.Now` ni `DateTimeOffset.UtcNow` en servicios.

6. **`CancellationToken` propagado** hasta el final en todo método asíncrono.

7. **`ConfigureAwait(false)`** en el código de librería del backend (servicios), no en los
   componentes Blazor, donde el contexto sí importa.

8. **Validación de entrada en el borde**: `Math.Clamp` o un 400 explícito en el endpoint. Los
   servicios asumen entrada ya acotada, pero vuelven a acotar lo que reservaría memoria.

### Reglas de rendimiento

- En rutas que el portal presenta como "zero-allocation", **no se asigna nada**. Ni un `ToString()`,
  ni una interpolación, ni un `Split`. Hay un test que lo verifica con
  `Assert.Equal(0, result.AllocatedBytes)`.
- `Span<T>` / `ReadOnlySpan<char>` para recorrer buffers. `Memory<T>` solo si hay que cruzar un
  `await`.
- `ValueTask<T>` únicamente donde la ruta síncrona es la frecuente. En caso de duda, `Task<T>`.
- Canales **siempre acotados** (`CreateBounded`). Un canal ilimitado es un `OutOfMemory` esperando.

---

## Estándares de Blazor / Razor

- **Páginas interactivas → proyecto `DotNetLab.Web.Client`.** Es requisito del render mode Auto.
  Una página en el host nunca podrá ejecutarse en WebAssembly.
- **Sin interactividad → proyecto `DotNetLab.Web`**, sin `@rendermode`. Se sirve como HTML estático
  y no consume circuito ni descarga.
- `@rendermode InteractiveAuto` en las páginas del cliente.
- `@key` en todo `@foreach` que renderice elementos con identidad, para que el diff reutilice nodos.
- Estado de carga explícito: campo `_busy` que deshabilita los controles durante la llamada.
- **Captura de excepciones específica** (`HttpRequestException`, `TaskCanceledException`), nunca
  `catch (Exception)`: un bug debe llegar al ErrorBoundary, no morir en silencio.
- Los estilos van en `src/DotNetLab.Web/wwwroot/app.css` usando los tokens CSS ya definidos
  (`--accent`, `--surface`, `--good`, `--bad`...). **Sin frameworks ni CDN**: el portal debe
  funcionar sin red.

---

## Regla de documentación del código

Es la convención más visible del repositorio y **no es negociable**:

> Cada línea o bloque significativo lleva un comentario que explica **el porqué** o **qué ocurre por
> dentro**, no lo que ya dice el código.

```csharp
// ✅ Explica el mecanismo interno
// IndexOf sobre span está vectorizado (SIMD): compara 16-32 chars por instrucción.
var newLine = remaining.IndexOf('\n');

// ❌ Repite lo que se lee
// Busca el salto de línea
var newLine = remaining.IndexOf('\n');
```

Los comentarios son **breves** (una o dos líneas). Un comentario de cinco líneas suele significar
que el código necesita un método con nombre, no un párrafo.

Aplica igual a `.cs`, `.razor`, `Dockerfile`, `.yml` y `.json` de configuración.

---

## Al terminar un cambio

1. `dotnet build` — debe salir con **0 avisos**.
2. `dotnet test` — las 31 pruebas en verde.
3. Si tocaste un endpoint, pruébalo con `curl` y comprueba el JSON.
4. Si tocaste la UI, ábrela en el navegador y ejecuta el playground afectado.
5. Si el cambio revela algo no obvio (una medición sorprendente, una trampa del runtime), añade una
   entrada a `PROJECT_DEVLOG.md`.

---

## Convenciones de Git

- Mensajes de commit en español, en imperativo, con prefijo semántico:
  `feat:`, `fix:`, `docs:`, `refactor:`, `test:`, `chore:`, `perf:`.
- Un commit por unidad lógica de cambio.
- **Los commits van a nombre del autor del repositorio.** No añadas trailers de coautoría ni
  referencias a herramientas de IA en mensajes de commit, descripciones de PR ni comentarios.

---

## Cosas que NO hay que hacer

- ❌ Sustituir una métrica medida por un valor inventado o "de ejemplo".
- ❌ Desactivar `TreatWarningsAsErrors` para que compile.
- ❌ Añadir un paquete NuGet para algo que el framework ya resuelve (la sonda a Redis usa un socket
  crudo justamente por esto).
- ❌ Introducir dependencias de CDN en el frontend.
- ❌ Meter secretos en `appsettings.json`, en variables de entorno del compose o en cualquier capa
  de imagen. Van por fichero montado.
- ❌ Publicar el puerto de la cache en el host.
- ❌ Escribir un comentario que repita lo que el código ya dice.
