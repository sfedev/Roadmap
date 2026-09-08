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

Antes del primer arranque hay que generar los **dos** secretos. En Git Bash, WSL, macOS o Linux:

```bash
openssl rand -base64 32 > secrets/lab_shared_key.txt && openssl rand -base64 24 > secrets/rabbitmq_password.txt
```

En **PowerShell** no sirve ese comando, por dos motivos independientes: `openssl` no está en el
PATH (aunque Git for Windows lo trae en `C:\Program Files\Git\usr\bin\openssl.exe`) y, sobre todo,
**la redirección `>` de PowerShell 5.1 escribe UTF-16 con BOM**. El contenedor de RabbitMQ lee ese
fichero con `cat` para generar su `rabbitmq.conf`: con BOM y bytes nulos no arranca, y el error que
se ve es un `dependency failed to start` que no apunta a la causa.

```powershell
$rng = [System.Security.Cryptography.RandomNumberGenerator]::Create(); $b = New-Object byte[] 32; $rng.GetBytes($b); [Convert]::ToBase64String($b) | Out-File -Encoding ascii -NoNewline secrets\lab_shared_key.txt
```

```powershell
$rng = [System.Security.Cryptography.RandomNumberGenerator]::Create(); $b = New-Object byte[] 24; $rng.GetBytes($b); [Convert]::ToBase64String($b) | Out-File -Encoding ascii -NoNewline secrets\rabbitmq_password.txt
```

Comprobar que quedaron sin BOM (deben ser 44 y 32 bytes):

```powershell
Get-ChildItem secrets\*.txt -Exclude *example* | ForEach-Object { "$($_.Name) $((Get-Item $_).Length) bytes" }
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

> **En Git Bash sobre Windows**, usa `-p:Propiedad=valor` y no `/p:Propiedad=valor`: el conversor de
> rutas de MSYS interpreta `/p:` como una ruta y MSBuild falla con `MSB1008`. Dentro del contenedor
> (Linux) `/p:` funciona sin problema, que es como está escrito en los Dockerfile.

### Observabilidad local

Con `docker compose up`, además del portal quedan disponibles:

| Herramienta | URL | Para qué |
|---|---|---|
| Jaeger | http://localhost:16686 | Trazas distribuidas. Servicios: `dotnetlab-web`, `dotnetlab-api`, `dotnetlab-worker` |
| Prometheus | http://localhost:9090 | Métricas. Empieza por `dotnetlab_parse_allocated_bytes_bucket` |
| Grafana | http://localhost:3000 | Paneles ya aprovisionados (admin/admin) |
| RabbitMQ | http://localhost:15672 | Colas, mensajes pendientes y cola `_error` |

Escalar el Worker para ver el reparto de trabajos entre réplicas:

```bash
docker compose up -d --scale worker=3 worker
```

> Los servicios exportan **solo OTLP** contra el colector. Sin `OTEL_EXPORTER_OTLP_ENDPOINT`
> configurado, el exportador no se registra: es por lo que `dotnet run` en local no llena la
> consola de errores de conexión.

### Kubernetes en local (Kind o Minikube)

```bash
kind create cluster --name dotnetlab
```

Las imágenes se cargan en el cluster; sin esto, el kubelet intentaría descargarlas de un registro:

```bash
docker build -f src/DotNetLab.Api/Dockerfile -t dotnetlab-api:local .
```

```bash
kind load docker-image dotnetlab-api:local dotnetlab-web:local dotnetlab-worker:local --name dotnetlab
```

Manifiestos sueltos (material de estudio, todo comentado):

```bash
kubectl apply -f k8s/
```

O el chart de Helm (la vía de despliegue real):

```bash
helm upgrade --install lab ./helm/dotnetlab -n dotnetlab --create-namespace -f helm/dotnetlab/values-dev.yaml
```

```bash
kubectl -n dotnetlab get pods,svc,hpa
```

```bash
kubectl -n dotnetlab port-forward svc/lab-dotnetlab-web 8081:8080
```

Diagnóstico habitual:

```bash
kubectl -n dotnetlab describe pod -l app.kubernetes.io/name=worker
```

```bash
kubectl -n dotnetlab logs -l app.kubernetes.io/name=worker --tail 100 -f
```

```bash
helm rollback lab -n dotnetlab
```

> El HPA necesita **metrics-server**; sin él se queda en `<unknown>` para siempre y sin dar ningún
> error. En Minikube: `minikube addons enable metrics-server`.
>
> Las **NetworkPolicies** necesitan un CNI que las implemente (Calico, Cilium). Con el CNI por
> defecto de Kind se crean pero no se aplican, que es el error más común al probarlas.

### Validar sin desplegar

```bash
helm lint helm/dotnetlab
```

```bash
helm template lab ./helm/dotnetlab -f helm/dotnetlab/values-prod.yaml --set secrets.existingSecret=dotnetlab-secrets
```

```bash
kubeconform -strict -summary -kubernetes-version 1.31.0 k8s/
```

> `kubectl apply --dry-run=client` **no** vale sin cluster: pese a su nombre, contacta con el
> servidor de API para descubrir los tipos de recurso. kubeconform valida contra los esquemas JSON
> publicados, sin red.

### Azure (Bicep)

```bash
az bicep build --file infrastructure/main.bicep
```

```bash
az group create -n rg-dotnetlab-dev -l switzerlandnorth
```

```bash
az deployment group what-if -g rg-dotnetlab-dev -f infrastructure/main.bicep -p infrastructure/parameters/dev.bicepparam
```

```bash
az deployment group create -g rg-dotnetlab-dev -f infrastructure/main.bicep -p infrastructure/parameters/dev.bicepparam
```

> Ejecuta **siempre** `what-if` antes de `create`: muestra exactamente qué se va a crear, modificar
> o borrar. Es el equivalente de `terraform plan` y evita el susto de un recurso recreado.

### Diagnóstico rápido

```bash
curl -s "http://localhost:5280/api/performance/span-demo?rows=25000"
```

```bash
curl -s "http://localhost:5280/api/diagnostics/health"
```

```bash
curl -s "http://localhost:5280/api/resilience/demo?scenario=circuit-breaker"
```

Encolar un trabajo asíncrono y seguir su estado:

```bash
curl -s -X POST http://localhost:5280/api/analysis/jobs -H "Content-Type: application/json" -d '{"rowCount":50000,"strategy":"span"}'
```

```bash
curl -s "http://localhost:5280/api/analysis/jobs?take=5"
```

---

## Estructura y responsabilidades

```
src/
  DotNetLab.ServiceDefaults/  OpenTelemetry, health checks y resiliencia. La MISMA
                              configuración para los tres procesos, en una línea.
  DotNetLab.Analysis/         Dominio compartido API + Worker: parsers, generador y el
                              consumidor de MassTransit del trabajo pesado.
  DotNetLab.Contracts/        Records compartidos (HTTP y bus). Sin lógica.
  DotNetLab.Api/              Web API con Minimal APIs. Acepta trabajos y los publica.
    Endpoints/                Un archivo por módulo. Solo mapeo y validación.
    Services/                 Lógica específica de la API (registro de trabajos, Polly).
    Consumers/                Consume el resultado para actualizar su registro.
    Infrastructure/           Logging generado, clave compartida, registro del bus.
  DotNetLab.Worker/           Consume del bus y ejecuta el parseo. Solo expone /health/*.
  DotNetLab.Web/              Host Blazor: render de servidor, proxy y hub de SignalR.
    Realtime/                 Hub y consumidor puente bus -> navegador.
  DotNetLab.Web.Client/       Ensamblado WebAssembly: páginas y playgrounds.
tests/
  DotNetLab.Api.Tests/        Unitarias de servicios + integración de endpoints y del bus.

k8s/              Manifiestos comentados uno a uno (material de estudio CKAD).
helm/dotnetlab/   Chart parametrizado (la vía de despliegue real).
infrastructure/   Bicep: AKS, ACR, Key Vault, monitoring y Container Apps.
observability/    Configuración del colector OTel, Prometheus y Grafana.
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
- **Calienta antes de medir.** Toda ruta que mida asignaciones debe llamar antes a `Warmup(...)`
  con un fragmento pequeño. La primera ejecución paga la compilación del JIT por niveles, y ese
  coste se imputa al hilo: sin calentar, el primer trabajo de un proceso reportaba 7.840 bytes en
  una ruta que asigna 0 (ver reto 12 del devlog). **Nunca uses `Parse` para calentar**: ensuciaría
  los histogramas de OpenTelemetry con muestras que nadie pidió.

### Patrones de observabilidad

1. **Instrumentos declarados UNA vez** en `LabTelemetry`, como campos estáticos. Crear un
   `Counter` o un `Histogram` por operación filtra memoria: el `MeterProvider` los mantiene vivos.

2. **El span se abre FUERA de la ventana medida.** Crear una `Activity` asigna; hacerlo dentro
   falsearía la cifra que el portal presume de mantener en cero.

3. **Elige el instrumento por la pregunta que responde:**
   - `Counter<T>` → "cuántas veces ha ocurrido" (solo crece).
   - `Histogram<T>` → "cómo se distribuye" (p50, p95, p99).
   - `UpDownCounter<T>` → "cuántos hay ahora" (sube y baja).

4. **Unidades según la convención semántica**: `By` para bytes, `s` para segundos (nunca
   milisegundos), `{operation}` para conteos adimensionales.

5. **Etiquetas de cardinalidad BAJA.** `strategy` (dos valores) sí; un `jobId` como etiqueta
   crearía una serie temporal nueva por trabajo y reventaría Prometheus.

### Patrones de resiliencia

1. **Reintenta solo lo idempotente y lo transitorio.** Un 400 o un 401 reintentado da exactamente
   el mismo error; un GET fallido por un pod reciclándose, no.

2. **Nunca reintentos sin cortacircuitos.** Por sí solos AMPLIFICAN una caída: multiplican la carga
   sobre el servicio que ya está mal.

3. **Siempre jitter** (`UseJitter = true`). Sin él, N réplicas que fallan a la vez reintentan a la
   vez y el pico se repite idéntico en cada ronda.

4. **Filtra por tipo de excepción** en `ShouldHandle`. Un `NullReferenceException` es un bug:
   reintentarlo cuatro veces solo esconde la causa.

5. Las llamadas entre servicios usan `.AddLabResilience()` de `ServiceDefaults`. No configures
   políticas ad hoc por cliente salvo que tengas una razón que puedas escribir en un comentario.

### Patrones de mensajería

1. **Los consumidores deben ser idempotentes.** RabbitMQ garantiza entrega *al menos una vez*: un
   mismo mensaje puede llegar dos veces si el consumidor muere tras procesarlo y antes de confirmar.

2. **`Publish` para eventos, `Send` para comandos.** `Publish` permite N suscriptores sin que el
   emisor los conozca; es lo que hace que la API y el frontend reaccionen al mismo evento.

3. **Valida en el borde, no en el consumidor.** Publicar un mensaje que solo puede acabar en la
   cola de errores gasta transporte y cola para nada.

4. **Los mensajes son contratos.** Viven en `DotNetLab.Contracts` y solo se les añaden campos
   opcionales: quitar o renombrar uno rompe a los consumidores que aún no se han desplegado.

5. **El instante de publicación viaja en el mensaje.** Es la única forma de calcular la latencia de
   cola, que es la métrica que dice si el Worker necesita más réplicas.

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

Aplica igual a `.cs`, `.razor`, `Dockerfile`, `.yml`, `.bicep`, las plantillas de Helm y los
`.json` de configuración. En un manifiesto de Kubernetes, el comentario que importa es el que
explica **por qué ese valor** (por qué 512Mi y no 256Mi, por qué `maxUnavailable: 0`), no qué hace
el campo.

---

## Al terminar un cambio

1. `dotnet build` — debe salir con **0 avisos**.
2. `dotnet test` — las 49 pruebas en verde.
3. Si tocaste un endpoint, pruébalo con `curl` y comprueba el JSON.
4. Si tocaste la UI, ábrela en el navegador y ejecuta el playground afectado.
5. Si tocaste un manifiesto o el chart: `kubeconform -strict k8s/` y `helm lint helm/dotnetlab`.
6. Si tocaste el Bicep: `az bicep build --file infrastructure/main.bicep`.
7. Si el snippet de una tarjeta corresponde a código que has modificado, **actualízalo**: el portal
   afirma que sus fragmentos son código real del repositorio.
8. Si el cambio revela algo no obvio (una medición sorprendente, una trampa del runtime), añade una
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
- ❌ Publicar el puerto de la cache o el AMQP de RabbitMQ en el host.
- ❌ Escribir un comentario que repita lo que el código ya dice.
- ❌ Medir asignaciones sin calentar antes el JIT, o calentar llamando al método instrumentado.
- ❌ Emitir `replicas` en un Deployment que tiene HPA: cada `helm upgrade` desharía el autoescalado.
- ❌ Poner una comprobación de dependencia externa en la sonda de **liveness**: una caída de esa
  dependencia reiniciaría todos los pods en vez de sacarlos del balanceador.
- ❌ Usar una etiqueta de métrica de cardinalidad alta (un `jobId`, un id de usuario): crea una
  serie temporal por valor y revienta Prometheus.
- ❌ Subir MassTransit a la rama 9: es de licencia comercial. La 8.5.10 es Apache-2.0.
- ❌ Reintentar sin cortacircuitos, o reintentar errores 4xx.
