# PROJECT_DEVLOG

Registro de desarrollo de **DotNetLab**, el portal interactivo que explica Modern .NET, Docker,
observabilidad, resiliencia, arquitectura dirigida por eventos y Kubernetes ejecutándolos de verdad.
Cada entrada recoge **qué se implementó**, **qué decisión de arquitectura se tomó**, **por qué** y
**qué alternativa se descartó**. Los problemas encontrados se documentan con su medición y su
solución, no como anécdota.

Formato: entradas en orden cronológico de implementación. Las versiones citadas son las reales del
repositorio (.NET SDK 10.0.3xx/10.0.4xx, C# 14). Las fases 0 a 5 construyen la Fase 01 del plan de
estudio; las fases 6 y 7 la llevan a nivel de aplicación empresarial.

---

## Fase 0 · Cimientos de la solución

### Qué se implementó

- Solución en formato **`.slnx`** (el nuevo formato XML del SDK 10, sustituto del `.sln`).
- Cinco proyectos: `DotNetLab.Api`, `DotNetLab.Web`, `DotNetLab.Web.Client`, `DotNetLab.Contracts`
  y `tests/DotNetLab.Api.Tests`.
- `Directory.Build.props` y `global.json` en la raíz.

### Decisiones y porqués

**1. Un proyecto `Contracts` compartido en lugar de duplicar DTOs.**
La alternativa habitual es que el frontend declare sus propias clases y confíe en que el JSON
encaje. Con un proyecto compartido, el compilador es quien garantiza el contrato: renombrar un
campo en la API rompe la compilación del frontend inmediatamente, no en producción. El coste es un
acoplamiento en tiempo de compilación entre front y back, aceptable porque se despliegan juntos.

**2. `Directory.Build.props` con `TreatWarningsAsErrors=true`.**
Un aviso que nadie arregla es un aviso que nadie lee. Con esta política, la deuda no se acumula: se
resuelve en el commit que la introduce. Esto tuvo consecuencias reales inmediatas (ver Fase 1).

**3. `AnalysisLevel=latest-recommended`.**
Activa el conjunto de reglas CA recomendado. Fue una decisión deliberadamente incómoda: obligó a
reescribir todo el logging (ver Fase 1, reto 2).

**4. `global.json` con `rollForward: latestFeature`.**
Fija la banda 10.0.3xx. Sin esto, instalar un SDK 11 preview cambiaría silenciosamente las reglas de
compilación del repositorio.

---

## Fase 1 · Web API: rendimiento, lenguaje, concurrencia y DI

### Qué se implementó

| Endpoint | Concepto que demuestra |
|---|---|
| `GET /api/performance/span-demo` | `Span<T>` / `ReadOnlySpan<char>` frente a `string.Split` |
| `GET /api/concurrency/channel-stream` | `System.Threading.Channels` con backpressure, en streaming |
| `GET /api/concurrency/channel-summary` | `ValueTask<T>`: ruta síncrona cacheada vs ruta async |
| `GET /api/language/records` | Records: igualdad por valor, `with`, deconstrucción |
| `GET /api/language/pattern-match` | Property, relational, logical y list patterns |
| `GET /api/di/keyed-services` | Keyed Services + patrón Factory |
| `GET /api/di/lifetimes` | Singleton / Scoped / Transient comparados en una misma petición |
| `GET /api/diagnostics/health` | Estado del proceso y sonda al contenedor auxiliar |

### Decisiones y porqués

**1. `WebApplication.CreateSlimBuilder` en lugar de `CreateBuilder`.**
El builder "slim" omite proveedores que un contenedor no usa (IIS integration, user secrets,
hosting startup assemblies). Menos arranque en frío y menos IL. Conserva lo necesario:
`appsettings.json`, variables de entorno, argumentos y logging a consola.

**2. Endpoints en clases estáticas con métodos de extensión, no en `Program.cs`.**
`Program.cs` queda como mapa de la aplicación (qué se registra, en qué orden corre el pipeline) y
cada módulo pedagógico vive en su archivo. Se descartaron los controladores MVC: añaden reflexión,
convenciones implícitas y un modelo de binding que aquí no aporta nada.

**3. `Results<Ok<T>, BadRequest<string>>` como tipo de retorno.**
Declara todas las respuestas posibles en la firma. OpenAPI las documenta sin atributos
`[ProducesResponseType]` y el compilador impide devolver un tipo no declarado.

**4. `TimeProvider` inyectado en lugar de `DateTimeOffset.UtcNow`.**
Cualquier rama que dependa del reloj (la guarda `when` del switch de patrones) sería imposible de
probar de forma determinista con la hora del sistema. Con `FakeTimeProvider` en los tests, la rama
"lectura del futuro" se verifica sin esperas.

**5. La firma del parser recibe `ReadOnlySpan<char>`, no `string`.**
Es una decisión de diseño, no de estilo: obliga a que cualquier implementación que quiera un
`string` tenga que copiarlo explícitamente, y esa copia aparece en las métricas. El interfaz hace
visible el coste.

**6. Rate limiting sobre el grupo de endpoints pesados.**
`span-demo` reserva megabytes por llamada. Sin límite, un bucle de peticiones tumba el contenedor.
Ventana fija de 20 peticiones / 10 s: holgada para un humano, letal para un bucle.

**7. El endpoint de diagnóstico va en un grupo SIN el filtro de clave compartida.**
El `HEALTHCHECK` del Dockerfile no conoce el secreto. Si el health check exigiera credenciales, el
orquestador marcaría el contenedor como no sano y lo reiniciaría en bucle.

### Retos encontrados y cómo se resolvieron

#### Reto 1 · `NU1903`: vulnerabilidad en una dependencia transitiva

`dotnet build` falló nada más añadir OpenAPI:

```
error NU1903: El paquete "Microsoft.OpenApi" 2.0.0 tiene una vulnerabilidad de gravedad alta
conocida, GHSA-v5pm-xwqc-g5wc
```

`Microsoft.AspNetCore.OpenApi` **10.0.9** (la versión que instala la plantilla) arrastra
`Microsoft.OpenApi` 2.0.0. El primer intento —fijar `Microsoft.OpenApi` a 2.0.1 con una referencia
directa— produjo dos errores nuevos: la 2.0.1 sigue afectada, y además `NU1605` por degradación de
paquete.

**Solución:** elevar `Microsoft.AspNetCore.OpenApi` a **10.0.11**, que ya exige
`Microsoft.OpenApi >= 2.7.5`. Sin referencia directa, sin pin manual.
**Lección:** `TreatWarningsAsErrors` convirtió un aviso que se habría ignorado durante meses en un
bloqueo de dos minutos.

#### Reto 2 · `CA1848` y `CA1873`: el logging estructurado no es gratis

Los analizadores rechazaron todas las llamadas del estilo
`logger.LogInformation("... {Rows} ...", rows, elapsed.TotalMicroseconds, allocated)`:

- **CA1848**: cada llamada parsea la plantilla del mensaje y hace *boxing* de los argumentos.
- **CA1873**: los argumentos se evalúan aunque el nivel de log esté desactivado.

En un endpoint que presume de no asignar memoria, loguear asignando era una contradicción medible.

**Solución:** clase `ApiLog` con métodos parciales `[LoggerMessage]`. El generador de código emite
delegados `LoggerMessage` cacheados: sin boxing, sin parseo de plantilla y con comprobación previa
del nivel. Es el patrón que usa el propio ASP.NET Core internamente.

#### Reto 3 · El ratio de asignaciones era una división por cero disfrazada

La primera versión calculaba `naive.AllocatedBytes / max(span.AllocatedBytes, 1)`. Como la ruta con
spans asigna **exactamente 0 bytes**, el resultado real fue:

```json
{"allocationRatio": 9740344}
```

Un "9.740.344x más rápido" que no significa nada: era el número de bytes de la otra estrategia
disfrazado de cociente.

**Solución:** `AllocationRatio` pasó a `double?`. `null` significa "el cociente no está definido
porque el divisor es cero", y el veredicto se redacta distinto en ese caso:

> *Span no asignó NADA en el Heap (0 B) frente a 11,55 MB de string.Split, y además fue 2,96x más
> rápido sobre 25.000 filas.*

**Lección:** una métrica que no se puede calcular debe viajar como ausente, no como un número
plausible.

#### Reto 4 · Los GUID v7 arruinaron la demo de lifetimes

`/api/di/lifetimes` devolvía esto:

```
singletonFirst  01a04e9f      scopedFirst  01a04e9f      transientFirst  01a04e9f
singletonSecond 01a04e9f      scopedSecond 01a04e9f      transientSecond 01a04e9f
```

Seis instancias distintas con el mismo identificador. La causa: `Guid.CreateVersion7()` codifica un
**timestamp Unix en milisegundos en sus primeros 48 bits**, y el código tomaba
`.ToString("N")[..8]`, es decir, justo esa parte. Todos los objetos creados en el mismo milisegundo
comparten prefijo.

**Solución:** `[^8..]` en lugar de `[..8]` — la cola del GUID es la porción aleatoria. Resultado:

```
singletonFirst  f4a50235      scopedFirst  d58e097e      transientFirst  eb09e003
singletonSecond f4a50235      scopedSecond d58e097e      transientSecond 0d07cf38
```

**Lección:** un identificador "único" solo es único en la parte que lo hace único. Los GUID v7 se
diseñaron para ser ordenables, y esa propiedad es exactamente la que rompe un prefijo corto.

#### Reto 5 · Carrera de datos en la caché del `ValueTask`

La caché del resumen se guardaba en dos campos independientes (`_cachedSummary` y `_cachedKey`) de
un servicio **Singleton**. Dos peticiones concurrentes podían leer una clave ya actualizada junto a
un resumen aún antiguo.

**Solución:** un único `record SummaryCache(Events, Capacity, Summary)` publicado con
`Volatile.Write` y leído con `Volatile.Read`. La referencia se sustituye de forma atómica: clave y
valor no pueden desincronizarse. Sin lock, porque no hay sección crítica que proteger.

#### Reto 6 · El calentamiento del JIT falseaba la comparativa

En las primeras mediciones, la estrategia que se ejecutaba primero salía sistemáticamente peor:
cargaba con el coste de compilación JIT de su propio método.

**Solución:** ambas estrategias procesan un fragmento de 8 KB antes de arrancar el cronómetro. El
mismo problema apareció en los tests: `LaRutaConSpansNoAsignaEnElHeap` era intermitente porque el
JIT sí asigna en la primera ejecución. Se añadió el mismo calentamiento.

#### Reto 7 · La clave `Urls` en `appsettings.json` rompía `dotnet run`

Se había fijado `"Urls": "http://+:8080"` en `appsettings.json` pensando en el contenedor. Efecto
real: `dotnet run` ignoraba el puerto de `launchSettings.json` y fallaba con *address already in
use*.

**Causa:** el orden de precedencia. `ASPNETCORE_URLS` entra en la configuración del **host**, que se
aplica **antes** que `appsettings.json` (configuración de la **app**). La clave del JSON gana.

**Solución:** eliminar `Urls` del JSON. En contenedor el puerto lo fija `ASPNETCORE_HTTP_PORTS`, que
además ya viene definida en las imágenes oficiales de .NET 8+.

#### Reto 8 · "Degraded" mentía en ejecución local

El health check devolvía `Degraded` al ejecutar sin Docker, porque la sonda de cache no respondía.
Pero la cache **no estaba configurada**: no hay dependencia que pueda fallar.

**Solución:** distinguir "caída" de "no configurada" inyectando `IOptions<CacheOptions>` en el
handler. `Degraded` solo si `Cache__Enabled=true` y además no responde. Nunca `Unhealthy`: la API
sirve todos sus endpoints igualmente, y un 503 haría que el orquestador reiniciara un contenedor
perfectamente funcional.

---

## Fase 2 · Frontend Blazor Web App (render mode Auto)

### Qué se implementó

- Host `DotNetLab.Web` (render estático + interactivo de servidor) y cliente `DotNetLab.Web.Client`
  (WebAssembly).
- Portada estática, sección **Modern .NET y C#** con 7 tarjetas y sección **Docker en producción**.
- Siete playgrounds interactivos, uno por concepto.
- Resaltador de sintaxis C# propio.
- Sistema de diseño en un único `app.css` sin frameworks.

### Decisiones y porqués

**1. Las páginas interactivas viven en el proyecto CLIENTE.**
Es un requisito del modo `InteractiveAuto`: un componente solo puede ejecutarse en WebAssembly si su
ensamblado se descarga al navegador. La portada, que no necesita interactividad, se queda en el host
como HTML estático: es la página más rápida posible.

**2. El host hace de proxy hacia la API (`/api/{**path}`).**
Fue la decisión estructural más importante del frontend. Alternativa descartada: que el cliente WASM
llamase directamente a la API. Eso obligaba a exponer la URL interna del contenedor al navegador,
a configurar CORS con preflight en cada llamada y a que el navegador conociera la clave compartida.
Con el proxy: rutas relativas, sin CORS, y el secreto se añade en el servidor.

**3. `HttpCompletionOption.ResponseHeadersRead` en el proxy.**
Sin esto, el proxy esperaría el cuerpo completo antes de responder y el endpoint de Channels dejaría
de emitir eventos progresivamente: la demostración de streaming se vería como una lista que aparece
de golpe. Con streaming real, cada evento llega a la UI cuando se produce.

**4. Un único `LabApiClient` para servidor y WebAssembly.**
El mismo código corre en ambos modos; lo único que cambia es la `BaseAddress` del `HttpClient`
inyectado: el origen de la página en WASM, la URL interna de la API en el servidor.

**5. Resaltador de sintaxis propio en lugar de Prism.js o highlight.js.**
Una librería JS externa implicaría CDN (rompe con CSP estricta y sin red) o empaquetarla en
`wwwroot`. El resaltador propio son ~60 líneas de C# con `[GeneratedRegex]`, funciona igual en
servidor y en WASM, y no añade ninguna descarga.

**6. `[GeneratedRegex]` y no `RegexOptions.Compiled`.**
`Compiled` usa `Reflection.Emit`, que **no está disponible en WebAssembly**. El generador de código
emite el autómata en tiempo de compilación, así que funciona en ambos entornos.

**7. `JsonSerializerContext` generado, compartido por API y cliente.**
En Blazor WASM la publicación aplica *trimming*: la deserialización por reflexión puede perder los
metadatos de los tipos. El contexto generado los conserva y, de paso, elimina la reflexión.

**8. `InvariantGlobalization` en TODAS las configuraciones, no solo Release.**
Detectado al comparar salidas: en local (cultura `es-ES`) la API devolvía `"Value = 21,5"` y en
contenedor `"Value = 21.5"`. Un portal pedagógico no puede enseñar un formato distinto según dónde
se ejecute. Efecto secundario útil: sin ICU, la imagen adelgaza ~30 MB.

### Retos encontrados

#### Reto 9 · `MapStaticAssets` frente a `UseStaticFiles`

El host arrancaba con este aviso:

```
Mapped static asset endpoints not found. Ensure 'MapStaticAssets' is called before
'AddInteractiveWebAssemblyRenderMode'.
```

`UseStaticFiles()` sirve archivos, pero desde .NET 9 el mecanismo correcto es `MapStaticAssets()`,
que además aplica huella de contenido (fingerprinting), compresión previa y cabeceras de caché
inmutables. Es lo que hace funcionar a `@Assets["app.css"]`.

**Solución:** sustituirlo y colocarlo **antes** de `AddInteractiveWebAssemblyRenderMode()`.

#### Reto 10 · `FormattableString.Invariant` no admite concatenación

```csharp
// No compila: la concatenación produce un string, no un FormattableString.
FormattableString.Invariant($"...{a}" + $"...{b}");
```

El error (`CS1503`) es correcto y el motivo importa: al concatenar se pierde la cultura invariante,
y con ella la garantía de que un `double` se serialice con `.` y no con `,` en la query string.

**Solución:** escapar los valores en variables locales y dejar **una sola** expresión interpolada.

#### Reto 11 · La etiqueta del bloque de código decía "C#" para un Dockerfile

Detalle pequeño con impacto pedagógico: la guía de Docker mostraba instrucciones `FROM`/`COPY`
etiquetadas como C#. Se añadió el parámetro `Language` al componente `CodeSnippet`.

---

## Fase 3 · Contenedores

### Qué se implementó

- `Dockerfile` multi-stage para la API y para el frontend (3 etapas cada uno).
- `.dockerignore` en la raíz.
- `docker-compose.yml` con tres servicios (`api`, `web`, `cache`), red bridge propia, secret
  montado en fichero, healthchecks encadenados y límites de recursos.

### Decisiones y porqués

**1. Tres etapas (`restore` → `publish` → `final`), no dos.**
Separar el `restore` del `publish` es lo que hace que la caché de Docker funcione de verdad: se
copian **solo los `.csproj`** antes de restaurar, así que editar un `.cs` no vuelve a descargar
NuGet. Con dos etapas, cualquier cambio en el código invalidaría el restore.

**2. Contexto de build en la raíz del repositorio.**
Ambos proyectos referencian `DotNetLab.Contracts`, que está fuera de su carpeta. El contexto debe
contener el grafo completo de proyectos.

**3. Alpine en la etapa final, con Chiseled documentado como alternativa.**
Alpine (~110 MB) conserva BusyBox, lo que permite un `HEALTHCHECK` con `wget`. La variante
`noble-chiseled` es más segura (sin shell ni gestor de paquetes) pero **por eso mismo no admite
healthcheck dentro del contenedor**: no hay ningún binario que ejecutar. El `Dockerfile` documenta
el intercambio en un comentario en vez de esconderlo.

**4. `USER $APP_UID` en lugar de crear un usuario a mano.**
Las imágenes oficiales de .NET 8+ ya definen el usuario `app` (UID 64198) y exponen `APP_UID`. Un
`adduser` propio, además, tendría sintaxis distinta en Alpine (BusyBox) y en Debian.

**5. `ENTRYPOINT` en forma exec, no en forma shell.**
En forma shell, `/bin/sh` sería PID 1 y no propagaría `SIGTERM` al proceso .NET: el contenedor
moriría por timeout en cada despliegue en lugar de apagarse ordenadamente.

**6. `UseAppHost=false`.**
Sin apphost nativo, el ENTRYPOINT invoca `dotnet App.dll`. Un binario menos en la imagen.

**7. Secrets por fichero, no por variable de entorno.**
Una variable de entorno aparece en `docker inspect`, en los logs del orquestador y a veces en
volcados de error. Compose monta el secret en `/run/secrets/<nombre>`: fuera del sistema de capas y
con permisos restringidos. La aplicación recibe **la ruta**, nunca el valor, y lo lee una sola vez
al arrancar.

**8. La cache NO publica puertos.**
`redis:7-alpine` sin autenticación publicado en el host sería una base de datos abierta a toda la
LAN. Solo es accesible desde la red interna `labnet`.

**9. `depends_on` con `condition: service_healthy`.**
El `depends_on` simple solo espera a que el contenedor arranque, no a que el servicio esté listo.
Con la condición de salud, el frontend no atiende su primera visita mientras la API no responda: el
prerender en servidor no se encuentra con errores de conexión.

**10. Sonda al contenedor auxiliar hablando RESP por TCP, sin librería cliente.**
Añadir `StackExchange.Redis` para hacer un `PING` habría metido una dependencia de ~1 MB para
enviar 14 bytes. La sonda escribe `*1\r\n$4\r\nPING\r\n` sobre un socket y espera `+PONG`. Demuestra
la comunicación entre contenedores sin coste de dependencias.

**11. Comparación en tiempo constante para la clave compartida.**
`CryptographicOperations.FixedTimeEquals` en vez de `==`. Una comparación normal aborta en el primer
byte distinto y filtra información por canal lateral temporal.

---

## Fase 4 · Pruebas

### Qué se implementó

31 pruebas: unitarias sobre los servicios e integración sobre los endpoints con
`WebApplicationFactory<Program>` (host en memoria, sin abrir puertos).

### Decisiones y porqués

**1. Las pruebas de rendimiento verifican ASIGNACIONES, no tiempos.**
Un test del tipo "span debe ser 3x más rápido" es inestable: depende de la máquina, de la carga y
del ruido del sistema. `Assert.Equal(0, result.AllocatedBytes)` es determinista y detecta
exactamente la regresión que importa: que alguien introduzca un `ToString()` en la ruta caliente.

**2. `IClassFixture<WebApplicationFactory<Program>>`.**
Comparte un único host entre los tests de la clase. Arrancar la aplicación por test multiplicaría el
tiempo sin ganar aislamiento real, porque los servicios implicados no guardan estado por test.

**3. `public partial class Program;` al final de `Program.cs`.**
Los *top-level statements* generan una clase `Program` interna. Esta línea la hace visible para
`WebApplicationFactory<Program>`.

**4. `consumerDelayMs=0` en el test del stream.**
El test verifica el **orden y la integridad** de los eventos, no la temporización. Depender de
esperas reales haría el test lento e intermitente.

---

## Verificaciones realizadas

Todo lo siguiente se ejecutó de verdad durante el desarrollo, no se asume:

- `dotnet build` de la solución completa: **0 avisos, 0 errores** (con warnings como errores).
- `dotnet test`: **31 pruebas, 31 correctas**.
- Los 8 endpoints, llamados con `curl` y verificados uno a uno.
- El portal, recorrido en navegador: cambio de pestañas, ejecución del playground de `Span<T>`
  (0 B frente a 11,55 MB, 2,96x más rápido sobre 25.000 filas), resaltado de sintaxis y guía de
  Docker con selección de etapas.
- El proxy `/api/...` del host Blazor, comprobado contra la API real.
- El modo de render **Auto**: la primera visita muestra "ejecutándose en el servidor (circuito
  SignalR)" y, tras descargarse el runtime, la recarga muestra "ejecutándose en WebAssembly".
- `dotnet publish -c Release` de ambos proyectos, **incluido el recorte de IL (trimming)** del
  cliente WebAssembly: sin avisos de trimming.
- La salida publicada en Release, ejecutada y recorrida en el navegador: el playground de `Span<T>`
  funciona desde WebAssembly recortado (0 B frente a 11,55 MB) y el stream de Channels entrega
  20/20 eventos con ocupación máxima de canal 3/3 a través del proxy.

Es la validación más cercana al contenedor que se puede hacer sin Docker: el publish en Release con
trimming es exactamente el paso que ejecuta la etapa `publish` del Dockerfile.

---

## Fase 5 · Integración continua (y cierre de la verificación de Docker)

### El problema

Docker no estaba instalado en el entorno de desarrollo, así que los `Dockerfile` y el
`docker-compose.yml` quedaron escritos pero **sin construir ni ejecutar nunca**. Era el único hueco
real del entregable, y no se podía cerrar sin instalar Docker en la máquina.

### La solución

Un workflow de GitHub Actions (`.github/workflows/ci.yml`) con dos trabajos en paralelo. Los
runners de Ubuntu ya traen Docker, así que la verificación que faltaba se hace en CI:

| Trabajo | Qué comprueba |
|---|---|
| **Build y tests** | `dotnet build -c Release` (con avisos como errores), las 31 pruebas y `dotnet publish` del frontend, que ejercita el recorte de IL del cliente WebAssembly |
| **Imágenes Docker** | Construye las dos imágenes, valida `docker compose config`, **arranca la API en un contenedor** y sondea `/api/diagnostics/health` con reintentos |

Decisiones del workflow:

**1. `docker build` directo, sin acciones de terceros.**
`docker/build-push-action` aportaría caché de capas entre ejecuciones, pero también una dependencia
externa que versionar y auditar. Con dos imágenes que tardan 87 s en total, la caché no compensa.
Es la misma lógica que llevó a resolver la sonda a Redis con un socket crudo.

**2. Arrancar el contenedor, no solo construirlo.**
Que una imagen se construya no demuestra que arranque: un `ENTRYPOINT` mal escrito o un `USER` sin
permisos sobre `/app` compilan igual de bien. El paso levanta la API y sondea el endpoint de salud
con reintentos, porque el `HEALTHCHECK` tarda unos segundos en dar su primer veredicto.

**3. Un secreto desechable generado en el runner.**
`docker compose config` falla si el fichero del secret no existe. En CI se genera con `openssl` y
muere con el runner: nunca se versiona ni sale de la máquina efímera.

**4. `if: always()` en la limpieza y `if: failure()` en los logs.**
Los logs del contenedor solo se vuelcan cuando algo falla (en caso de éxito son ruido), pero el
`docker rm -f` corre siempre, incluso si el sondeo falló.

### Resultado de la primera ejecución

Ambos trabajos en verde a la primera. Build y tests en 63 s, imágenes Docker en 87 s. El contenedor
de la API respondió esto:

```json
{"status":"Healthy","environmentName":"Production","runtimeVersion":".NET 10.0.11",
 "insideContainer":true,"machineName":"11fea8d75bdb",
 "cache":{"name":"cache","reachable":false,"detail":"Sonda deshabilitada (Cache__Enabled=false)."}}
```

Las tres cosas que confirma esa respuesta:

- `insideContainer: true` — la variable `DOTNET_RUNNING_IN_CONTAINER` de la imagen oficial está
  presente, así que el proceso corre dentro del contenedor y no en el runner.
- `machineName: 11fea8d75bdb` — es el id corto del contenedor, no el nombre del host.
- `runtimeVersion: .NET 10.0.11` sobre la imagen `aspnet:10.0-alpine`, ejecutando como el usuario
  `app` sin privilegios.

La sonda a la cache responde "deshabilitada" porque el contenedor se arrancó suelto, sin compose:
es el comportamiento correcto y, de hecho, valida la corrección del **reto 8** (distinguir
"dependencia caída" de "dependencia no configurada"): sin cache configurada, el estado es `Healthy`
y no `Degraded`.

**Aviso resuelto en la segunda iteración:** la primera ejecución avisó de que `actions/checkout@v4`
y `actions/setup-dotnet@v4` fuerzan Node 24 porque Node 20 está obsoleto. Se subieron a `@v7` y
`@v6` respectivamente.

**Lo que sigue sin verificarse:** `docker compose up` completo con los tres servicios en marcha.
CI valida la sintaxis del compose y que cada imagen arranca por separado, pero no la red interna
`labnet` ni la sonda RESP real contra el contenedor de cache. Eso necesita una máquina con Docker.

---

## Fase 6 · Observabilidad, resiliencia y arquitectura dirigida por eventos

### Qué se implementó

| Pieza | Dónde | Qué aporta |
|---|---|---|
| **OpenTelemetry** | `DotNetLab.ServiceDefaults` | Trazas, métricas y logs correlacionados en los tres procesos |
| **Polly v8** | `ResilienceDefaults` + `ResilienceShowcaseService` | Retry con backoff y jitter, circuit breaker, timeouts |
| **MassTransit + RabbitMQ** | `DotNetLab.Analysis`, API, frontend | Publish/subscribe con dos consumidores independientes |
| **Worker Service** | `DotNetLab.Worker` | Consume el trabajo pesado y lo ejecuta con `Span<T>` |
| **SignalR** | `DotNetLab.Web/Realtime` | Notifica al navegador el fin del trabajo, sin polling |
| **Endpoints nuevos** | `/api/resilience/demo`, `/api/analysis/jobs` | Playgrounds reales de ambos conceptos |

El caso de uso asíncrono completo: el navegador pulsa un botón → el frontend hace POST al proxy →
la API valida, publica `TelemetryAnalysisRequested` y responde **202 Accepted** con la `Location`
del recurso futuro → el Worker consume, genera el buffer, lo parsea con `Span<T>` y publica
`TelemetryAnalysisCompleted` → **dos** suscriptores independientes reaccionan: la API actualiza su
registro y el frontend lo empuja al navegador por SignalR.

### Decisiones y porqués

**1. Un proyecto `ServiceDefaults` compartido, no configuración copiada en cada `Program.cs`.**
Los tres procesos configuran OpenTelemetry y los health checks con **una línea**:
`builder.AddLabServiceDefaults("nombre-del-servicio")`. La alternativa —copiar cincuenta líneas de
configuración en cada uno— garantiza que a los seis meses los tres muestreen distinto y nadie sepa
por qué faltan spans en uno de ellos.

**2. Los servicios exportan SOLO OTLP, contra un OpenTelemetry Collector.**
Es la decisión de observabilidad más importante del proyecto. Las aplicaciones no conocen a Jaeger,
ni a Prometheus, ni a Azure Monitor: hablan el protocolo estándar contra un colector, y es el
colector el que reparte. Consecuencias prácticas:
- Cambiar de backend (o enviar a dos a la vez) es editar un YAML, no recompilar tres servicios.
- Migrar a Application Insights en Azure es cambiar el exporter del colector.
- Se evita el paquete `OpenTelemetry.Exporter.Prometheus.AspNetCore`, que **sigue en beta** (única
  versión publicada: `1.18.0-beta.1`). Meter una dependencia beta en la ruta de observabilidad de
  un repositorio de portfolio no compensa.

El precio es un contenedor más. Se acepta porque es el patrón que se encuentra en cualquier
plataforma seria y el que se replica luego en Kubernetes.

**3. Extraer `DotNetLab.Analysis` como librería de dominio.**
El Worker necesitaba los mismos parsers que la API. Las dos alternativas eran peores: que el Worker
referenciara el proyecto web entero de la API (arrastrando Kestrel, OpenAPI y el rate limiter para
usar una clase) o duplicar el parser (y garantizar que las dos copias divergieran). La librería
contiene los parsers, el generador, la fábrica **y el consumidor de MassTransit**: el consumidor es
el mismo caso de uso que los endpoints síncronos, solo que disparado por un mensaje.

**4. El hub de SignalR vive en el FRONTEND, no en la API.**
Parecía natural ponerlo en la API, que es quien conoce el estado de los trabajos. Se descartó por
una razón concreta: el navegador tendría que abrir el WebSocket contra la API, lo que obligaría a
exponerla públicamente y a que el proxy soportase negociación de WebSocket (que no es un simple
reenvío de GET). Con el hub en el frontend, el navegador se conecta a su **propio origen** y la API
sigue sin ser accesible desde fuera. De regalo, queda una demostración limpia de publish/subscribe:
dos servicios distintos consumen el mismo evento, cada uno con su cola y para algo distinto.

**5. Notificación por SignalR **y** respaldo por consulta periódica.**
No es cinturón y tirantes: es lo que hace un sistema real. Un WebSocket se cae (un proxy con
timeout, un despliegue, una red móvil). El componente arranca ambos caminos y el primero que
llegue gana; la tabla muestra por cuál llegó cada resultado (`SignalR` o `consulta`). Además, es
lo que permite que el playground funcione en local sin broker.

**6. MassTransit 8.5.10, no 9.x.**
Descubierto al comparar los `.nuspec`: la rama 9 pasó a **licencia comercial** (Massient, Inc.,
`massient.com/license`), mientras que la 8.5.10 sigue siendo **Apache-2.0**. Para un repositorio
público de portfolio, la 8 es la única opción defendible. Es exactamente el tipo de comprobación
que separa elegir una dependencia de arrastrarla.

**7. Tres modos de transporte, no dos.**
`rabbitmq` (Docker y Kubernetes), `inmemory` (desarrollo local: la API aloja también el consumidor,
así que el flujo asíncrono completo funciona sin broker) y `disabled` (503 explicativo). El modo en
memoria no es un juguete: es lo que permite que la **prueba de integración recorra el flujo
completo** sin levantar RabbitMQ en CI.

**8. La demostración de Polly ejecuta políticas reales contra una dependencia determinista.**
`FlakyDependency` falla exactamente las N primeras llamadas de cada correlación. El determinismo es
el punto: con fallos aleatorios, cada ejecución contaría una historia distinta y la cronología no
se podría explicar. Los tiempos que muestra la UI están **medidos con Stopwatch**, no calculados a
partir de la configuración — que es la única forma de que se vea el jitter.

### Retos encontrados

#### Reto 12 · El calentamiento del JIT falseaba las métricas del Worker

El síntoma apareció en el navegador: el playground de trabajos asíncronos mostraba **7.840 bytes**
asignados en una ruta que el portal entero presume de mantener en 0. Tres ejecuciones seguidas del
mismo trabajo (50.000 filas, estrategia `span`) dieron:

| Ejecución | Bytes asignados | Duración |
|---|---|---|
| 1.ª | 7.840 B | 12.323 µs |
| 2.ª | 3.320 B | 3.265 µs |
| 3.ª | **0 B** | 3.713 µs |

Mientras tanto, el endpoint **síncrono** con las mismas 50.000 filas reportaba 0 bytes desde la
primera llamada.

**Causa:** el endpoint síncrono hacía un calentamiento antes de medir; el consumidor de mensajes,
no. La primera ejecución de un método paga la compilación del JIT por niveles y, en un bucle largo,
el reemplazo en pila (OSR). Ese trabajo lo hace el runtime **en el hilo que ejecuta el código**, así
que `GC.GetAllocatedBytesForCurrentThread()` lo contabiliza como si lo hubiera asignado el parser.

**Solución:** un método `Warmup(ReadOnlySpan<char>)` en el interfaz `ITelemetryParser`. Ejecuta la
misma ruta (el `ParseCore` privado que ambos comparten) sin medir ni publicar métricas. Se llama
desde el endpoint y desde el consumidor con un fragmento de 8 KB.

**Por qué no valía llamar a `Parse` para calentar:** desde que los parsers publican métricas de
OpenTelemetry, un `Parse` de calentamiento metería en los histogramas muestras de un buffer de 8 KB
que nadie pidió procesar, sesgando el p50 de `dotnetlab.parse.duration` hacia abajo. El
calentamiento tenía que existir como operación explícita y **sin instrumentar**.

**Resultado tras el arreglo:** el primer trabajo de un proceso recién arrancado reporta **0 bytes y
3.031 µs**.

**Lección:** cuando se mide con precisión de bytes, el runtime forma parte del experimento. El
número no estaba mal calculado — estaba midiendo algo distinto de lo que el portal afirmaba.

#### Reto 13 · El proxy solo reenviaba GET

El flujo asíncrono necesitaba `POST /api/analysis/jobs`, y el proxy del frontend estaba mapeado con
`MapGet`. La corrección no fue solo cambiar el verbo:

- El método se copia del original (`new HttpMethod(context.Request.Method)`), no se fija.
- El cuerpo se envuelve con `StreamContent(context.Request.Body)`, **sin leerlo a memoria**: un
  cuerpo grande no se bufferiza en el proxy.
- El `Content-Type` se propaga con `TryAddWithoutValidation`, o la API no sabría deserializar.
- Se limitó la lista de verbos a `GET` y `POST`: un proxy abierto a todos los métodos es superficie
  de ataque regalada.

#### Reto 14 · `FrameworkReference` no es una propiedad

`ServiceDefaults` necesita tipos de ASP.NET Core (health checks, `WebApplication`) siendo una
librería. El primer intento puso `<FrameworkReference Include="Microsoft.AspNetCore.App" />` dentro
de `<PropertyGroup>` y MSBuild respondió `MSB4066: No se reconoce el atributo "Include"`. Es un
**item**, no una propiedad: va en `<ItemGroup>`. Detalle menor, pero ilustra por qué el mensaje de
error de MSBuild merece leerse entero.

#### Reto 15 · La sobrecarga de `AddOpenTelemetry` que no aparecía

`builder.Logging.AddOpenTelemetry(logging => ...)` no compilaba: *"Ninguna sobrecarga toma 1
argumento"*. El compilador encontraba la extensión sin parámetros y no la que acepta el delegado de
configuración, que vive en el espacio de nombres `Microsoft.Extensions.Logging` (no en
`OpenTelemetry.Logs`, que fue el primer intento). Un `using` de más y compiló.

#### Reto 16 · CA1822 forzó un diseño mejor

Al mover `TelemetrySampleGenerator` a la librería, el analizador exigió marcar `Generate` como
`static` porque no tocaba estado de instancia. Hacerlo habría dejado sin sentido su registro en el
contenedor de DI. La salida fue darle estado **real**: la semilla pasó de constante a parámetro del
constructor primario. Ahora la reproducibilidad es una decisión de configuración, no una propiedad
grabada en el algoritmo, y un test puede verificar que semillas distintas producen documentos
distintos. El analizador tenía razón, pero la solución correcta no era la que sugería.

#### Reto 17 · `TestContext` es de xUnit v3

Los tests nuevos se escribieron con `TestContext.Current.CancellationToken`, que es API de **xUnit
v3**. El proyecto usa xUnit 2.9.3 y el compilador no encontró el tipo. Se sustituyó por
`CancellationToken.None` donde el parámetro es obligatorio y por las sobrecargas sin token en el
resto.

---

## Fase 7 · Kubernetes, Helm e infraestructura como código

### Qué se implementó

```
k8s/           namespace, configmap, secret, deployment, service, ingress, hpa,
               networkpolicy y dependencies (RabbitMQ + Redis para laboratorio)
helm/dotnetlab Chart parametrizado con values.yaml, values-dev.yaml y values-prod.yaml
infrastructure main.bicep + módulos (ACR, AKS, Key Vault, monitoring, Container Apps)
               y ficheros .bicepparam por entorno
observability  Configuración del colector OTel, Prometheus y Grafana (orígenes y dashboard)
```

### Decisiones y porqués

**1. Manifiestos sueltos **y** chart de Helm, no uno u otro.**
No es duplicación: cumplen funciones distintas. Los manifiestos de `/k8s` son el material de
estudio para CKAD — se leen de arriba abajo y cada campo está comentado. El chart es la herramienta
de despliegue real, con valores por entorno, historial de releases y `helm rollback`. Quien estudia
lee `/k8s`; quien despliega usa `/helm`.

**2. El chart genera los tres Deployments con un único `range`.**
Un solo `templates/deployment.yaml` recorre `.Values.components`. Añadir un cuarto servicio es
añadir una entrada en `values.yaml`, no copiar 120 líneas. El precio —que las diferencias entre
componentes tienen que expresarse como **valores** y no como YAML a medida— es precisamente la
disciplina que se busca.

**3. Con HPA activo, el Deployment NO emite `replicas`.**
Es un error clásico y silencioso: si la plantilla emite `replicas`, cada `helm upgrade` devuelve el
número de pods al valor del chart y **deshace el trabajo del autoescalador**. Hay un paso de CI que
comprueba justamente esto sobre el render de producción.

**4. `checksum/config` en las anotaciones del pod.**
Sin esta anotación, `helm upgrade` actualiza el ConfigMap pero los pods siguen ejecutándose con los
valores viejos hasta que alguien los reinicia a mano. El despliegue parece haber funcionado y no ha
cambiado nada. El hash del ConfigMap renderizado fuerza el rollout.

**5. Tres sondas por pod, no una.**
`startupProbe` da margen al arranque en frío **sin** relajar la de liveness (que si no habría que
configurar con un `initialDelaySeconds` enorme, dejando ciega la detección de cuelgues durante ese
tiempo). `livenessProbe` apunta a `/health/live`, que **no consulta dependencias**: si comprobara
RabbitMQ, una caída del broker reiniciaría todos los pods a la vez en vez de esperar a que vuelva.
`readinessProbe` apunta a `/health/ready`, que sí incluye el bus.

**6. Escalar el Worker por CPU es una aproximación, y está documentado como tal.**
La señal correcta para un consumidor de colas es la **longitud de la cola**, no el uso de CPU del
pod que ya existe: un único pod saturado con 10.000 mensajes pendientes marca el mismo 75% que con
10. La solución real es KEDA con su scaler de RabbitMQ (que además escala a cero). Se deja fuera
del repositorio porque exige instalar el operador, y el objetivo es que los manifiestos funcionen
en un Kind recién creado — pero el `hpa.yaml` incluye el `ScaledObject` equivalente comentado.

**7. Bicep con nombres deterministas (`uniqueString`).**
ACR y Key Vault exigen nombres globalmente únicos. `uniqueString(resourceGroup().id)` genera un
hash determinista: el mismo despliegue produce siempre los mismos nombres (idempotencia), pero dos
suscripciones no chocan. Lo mismo con `guid()` para las asignaciones de rol: sin un GUID
determinista, cada despliegue crearía una asignación duplicada.

**8. Identidad gestionada en todo, cero contraseñas de infraestructura.**
El kubelet de AKS descarga imágenes del ACR con el rol `AcrPull` sobre su propia identidad: no hay
`imagePullSecret` que crear ni rotar. El CSI driver lee Key Vault con la suya. El `adminUserEnabled`
del ACR se deja explícitamente en `false`: es el atajo más común y la vulnerabilidad más habitual
en un registro privado.

**9. AKS **y** Container Apps, para poder comparar.**
El mismo servicio se despliega en ambos. Container Apps no tiene nodos que parchear, integra KEDA y
escala a cero; a cambio, no da acceso al plano de control (nada de DaemonSets, operadores ni CRDs).
Para esta API concreta, Container Apps cubre el 90% con una fracción del trabajo operativo. AKS se
mantiene porque el objetivo del repositorio incluye practicar Kubernetes de verdad.

**10. El código no cambia entre Docker, Kubernetes y Azure.**
La aplicación lee sus secretos por **ruta de fichero** (`SharedKey__File`,
`Messaging__PasswordFile`). En Docker esa ruta la monta un docker secret; en Kubernetes, un
`Secret` montado como volumen; en AKS, el Secrets Store CSI Driver sincronizando desde Key Vault
con rotación automática. Son tres tecnologías distintas y **cero líneas de C# diferentes**.

### Retos encontrados

#### Reto 18 · Un `Secret` de Kubernetes no está cifrado

Merece constar porque es el malentendido más extendido: `stringData` se guarda en etcd codificado
en **base64**, que no es cifrado. Cualquiera con permiso de lectura sobre el namespace lo
descodifica en un segundo. El fichero `k8s/secret.yaml` lo documenta y enumera las tres salidas
reales, de menos a más recomendable: cifrado en reposo de etcd, SOPS/Sealed Secrets (el fichero
cifrado sí puede vivir en git) y el CSI Driver con Key Vault (el secreto **nunca llega a etcd**).

#### Reto 19 · Las NetworkPolicies rompen las sondas si se olvida el kubelet

La política `default-deny-ingress` deja el namespace sin tráfico entrante, y las siguientes van
abriendo lo justo. El detalle que se olvida siempre: **el kubelet ejecuta las sondas desde la IP del
nodo**, no desde un pod, así que no encaja en ningún `podSelector`. Sin una regla explícita para
él, las sondas fallan, Kubernetes reinicia los pods en bucle y el diagnóstico es de los que cuestan
una tarde. Está resuelto y comentado en `networkpolicy.yaml`.

#### Reto 20 · El Worker necesita ser una aplicación web

Un Worker Service puro (SDK `Microsoft.NET.Sdk.Worker`) no expone ningún endpoint HTTP, y sin
endpoint **no puede tener sondas de liveness ni readiness**: el kubelet no tendría forma de
distinguir un consumidor colgado de uno simplemente ocioso. El proyecto usa el SDK `Web` con
`CreateSlimBuilder` y publica exactamente dos rutas, `/health/live` y `/health/ready`. Ninguna de
negocio. La consecuencia es que su imagen parte de `aspnet` y no de `runtime` (unos 20 MB más), que
es un precio razonable por poder detectar un pod muerto.

---

## Verificación de la Fase 6 y 7

Todo lo siguiente se ejecutó de verdad:

- `dotnet build` de la solución completa (8 proyectos): **0 avisos, 0 errores**.
- `dotnet test`: **49 pruebas, 49 correctas** (31 previas + 18 nuevas).
- Flujo asíncrono completo en local con transporte en memoria: 202 → bus → consumidor → parseo con
  `Span<T>` → evento de completado → registro actualizado. Latencia de cola 1,49 ms, procesamiento
  16,23 ms, **0 bytes asignados**.
- `/api/resilience/demo?scenario=retry&failures=2`: 3 intentos, esperas medidas de 16,7 → 188,9 →
  76,3 ms (el jitter en acción: **no** son 150/300 exactos ni crecientes).
- `/api/resilience/demo?scenario=circuit-breaker`: 4 fallos reales, circuito **abierto**, 6 llamadas
  cortadas sin salir, 7,6 ms en total.
- Portal en navegador: la página de la Fase 02 renderiza sus 5 tarjetas, pasa a **WebAssembly** en
  la segunda visita, el playground de circuit breaker devuelve `circuito: open` con 10 intentos, y
  el de trabajos asíncronos completa un trabajo de 50.000 filas mostrando por qué canal llegó.
- `POST /api/analysis/jobs` a través del proxy del frontend: **202**.

### Verificación en CI del stack completo

Docker Desktop está instalado en la máquina de desarrollo pero su demonio no estaba en ejecución,
así que la verificación del stack de contenedores la hace el workflow. Estos son los datos reales
de la ejecución en verde:

```
{"status":"Healthy","runtimeVersion":".NET 10.0.11","insideContainer":true,
 "machineName":"51a9113d780e",
 "cache":{"reachable":true,"latencyMs":3.23,"detail":"Respondió +PONG."}}

202 Accepted: {"jobId":"01a0805b-...","rowCount":50000,"strategy":"span"}
Procesado por 'db782036507c'; la API es '51a9113d780e'.
Bytes asignados en el Heap: 0
GET directo a la API sin cabecera: HTTP 401
Prometheus tiene 1 serie(s) de dotnetlab_parse_operations_total.
Jaeger conoce los servicios: ["dotnetlab-web","dotnetlab-api","dotnetlab-worker"]
```

Cada línea cierra una duda distinta:

- **La sonda RESP funciona por la red interna** (`+PONG` en 3,23 ms). Era el punto que quedaba
  pendiente desde la Fase 5.
- **El trabajo lo procesó OTRO contenedor**: `db782036507c` frente a `51a9113d780e` de la API. Si
  coincidieran, significaría que el bus no se usó y todo ocurrió en un proceso.
- **0 bytes asignados** en la ruta con `Span<T>`, ya en el primer trabajo del proceso: el arreglo
  del calentamiento del JIT funciona también dentro del contenedor.
- **401 sin cabecera**: la clave compartida se está aplicando de verdad.
- **Los tres servicios aparecen en Jaeger** y las métricas llegan a Prometheus: la tubería
  aplicación → OTLP → colector → backends está completa.

### Retos que destapó la propia CI

#### Reto 21 · RabbitMQ 4 rechaza las variables `*_FILE`

El stack no arrancaba: `dependency failed to start: container dotnetlab-rabbitmq is unhealthy`. En
los logs del contenedor, repetido en bucle:

```
error: RABBITMQ_DEFAULT_PASS_FILE is set but deprecated
error: deprecated environment variables detected
Please use a configuration file instead
```

La imagen oficial **eliminó** el soporte de las variables `*_FILE`. La salida evidente —usar
`RABBITMQ_DEFAULT_PASS` con el valor— rompía la propiedad que el proyecto enseña: el secreto
volvería a viajar como variable de entorno (visible en `docker inspect`) y habría **dos** valores
que mantener sincronizados, el del broker y el del fichero que leen los tres servicios.

**Solución:** generar `rabbitmq.conf` al arrancar el contenedor a partir del mismo docker secret.
Se mantiene una única fuente de verdad. Detalle no obvio: el comando no usa **ningún `$`**, porque
Compose interpola variables en el fichero y obligaría a escribir `$$`, dejando ambiguo qué recibe
realmente el contenedor. Con `echo` y `cat` el resultado es literal.

#### Reto 22 · `kubectl --dry-run=client` no valida sin cluster

```
error: unable to recognize "k8s/configmap.yaml": Get "http://localhost:8080/api?timeout=32s":
dial tcp [::1]:8080: connect: connection refused
```

Pese a llamarse "client", el dry-run contacta con el servidor de API para **descubrir los tipos de
recurso** a través del RESTMapper. Sin cluster no funciona, y `--validate=false` no lo evita.

**Solución:** `kubeconform`, que valida contra los esquemas JSON publicados de Kubernetes sin red
ni cluster. Con `-strict` además rechaza campos desconocidos: un `livenessProb` mal escrito pasaría
la validación normal y luego **no haría nada** en el cluster, que es de los errores más difíciles
de ver.

#### Reto 23 · Un 401 que era un acierto del sistema

El paso de integración fallaba con 401 en bucle al consultar el estado del trabajo. El fallo estaba
en la prueba: sondeaba la API directamente (`:8080`), que exige la clave compartida, en vez de ir
por el proxy del frontend (`:8081`), que es quien la añade. El sistema se comportaba exactamente
como debía.

Se corrigió el sondeo y, ya que el hallazgo era útil, se convirtió en una **aserción explícita**:
un endpoint protegido llamado sin cabecera debe responder 401. Si algún día respondiera 200, la
protección estaría desactivada y nadie se habría enterado.

---

## Career & Resume Impact

Sección para traducir el trabajo técnico en argumentos de candidatura. Los logros están redactados
en inglés, en el formato que espera un reclutador suizo: acción, tecnología concreta y **resultado
medible**.

### Logros cuantificables para el CV

> **Eliminated 100% of heap allocations in a hot parsing path** by rewriting a `string.Split`-based
> parser with `ReadOnlySpan<char>` slicing, cutting 11.55 MB of Gen0 pressure per 25k-row batch to
> **0 bytes** and reducing latency by 66% (8,081 µs → 2,728 µs). Enforced by an automated test
> asserting exactly zero allocations, preventing regressions.

> **Diagnosed a measurement artifact that misreported memory usage by 7.8 KB** in an asynchronous
> worker: tiered JIT compilation and on-stack replacement were being attributed to the measured
> thread. Introduced an explicit uninstrumented warm-up path, restoring accurate zero-allocation
> reporting from the first request of a process.

> **Designed and shipped an event-driven workflow** (ASP.NET Core Minimal APIs, MassTransit,
> RabbitMQ) replacing a blocking request with **202 Accepted** semantics: a dedicated worker
> consumes the message and two independent subscribers react to the completion event, with results
> pushed to the browser over SignalR plus a polling fallback for connection loss.

> **Implemented vendor-neutral observability** with OpenTelemetry across three services, exporting
> OTLP-only to a Collector that fans out to Jaeger and Prometheus. Backend changes (including a
> migration to Azure Monitor) require a configuration change, **not a code change or redeployment**
> of the services.

> **Hardened service-to-service communication with Polly v8**: exponential backoff with jitter,
> circuit breaking at a 50% failure ratio over a 30 s window, and layered timeouts. Verified with a
> deterministic fault-injection harness showing 6 of 10 calls short-circuited in microseconds once
> the breaker opened, preventing retry amplification against a failing dependency.

> **Automated the full delivery pipeline** with multi-stage Docker builds (three services, ~110 MB
> Alpine runtime images, non-root, read-only root filesystem), a parameterised Helm chart for
> dev/prod, and Bicep IaC provisioning AKS, ACR, Key Vault and Container Apps with managed
> identities and **zero stored registry credentials**.

> **Built a CI pipeline that verifies the system, not just the code**: it compiles with
> warnings-as-errors, runs 49 tests, builds all container images, boots the full nine-service
> stack and asserts the asynchronous job was processed by a *separate* container, with telemetry
> reaching Prometheus and Jaeger.

> **Reduced supply-chain risk** by auditing dependency licences and advisories: replaced a
> transitively vulnerable package flagged by NU1903 and pinned MassTransit to the last Apache-2.0
> release after v9 moved to a commercial licence.

### Cómo defenderlo en una entrevista

| Si preguntan… | El ejemplo del repositorio |
|---|---|
| "¿Cómo depuras un problema de rendimiento?" | El reto 12: síntoma en la UI, tres mediciones sucesivas, hipótesis del JIT, contraste con la ruta síncrona y verificación tras el arreglo |
| "¿Cómo diseñas para el fallo?" | Retry **con** circuit breaker, y por qué el retry solo amplifica una caída |
| "¿Liveness o readiness?" | Una reinicia el contenedor, la otra solo lo despublica; por qué liveness no debe consultar dependencias |
| "¿Cómo gestionas secretos?" | La aplicación lee una **ruta**; docker secret, Secret de K8s y Key Vault son la misma línea de C# |
| "¿Cuándo NO usarías Kubernetes?" | La comparación AKS vs Container Apps del módulo de IaC |
| "¿Cómo eliges una dependencia?" | La auditoría de licencia de MassTransit 9 y la vulnerabilidad NU1903 |

### Mapa con el temario de AZ-204 (Azure Developer Associate)

| Área del examen | Dónde se practica en el repositorio |
|---|---|
| **Develop Azure compute solutions** — Container Apps, contenedores | `infrastructure/modules/containerapps.bicep`, los tres `Dockerfile` multi-stage |
| Crear y gestionar imágenes en ACR | `infrastructure/modules/acr.bicep`, rol `AcrPull` con identidad gestionada |
| **Implement Azure security** — Key Vault, identidades gestionadas | `keyvault.bicep` con RBAC, Workload Identity y CSI driver en `aks.bicep` |
| Autenticación sin secretos | `adminUserEnabled: false`, asignaciones de rol con GUID determinista |
| **Monitor, troubleshoot and optimize** — Application Insights | `monitoring.bicep` (workspace-based), OpenTelemetry en `ServiceDefaults` |
| Instrumentación y métricas personalizadas | `LabTelemetry`: Counter, Histogram y UpDownCounter con su justificación |
| **Connect to and consume services** — mensajería | MassTransit sobre RabbitMQ, abstraído para Azure Service Bus |
| Procesamiento asíncrono y 202 Accepted | `AnalysisEndpoints`, `TelemetryAnalysisConsumer` |
| Caching | `CachePingProbe` (RESP sobre socket), Redis en compose y en `k8s/dependencies.yaml` |
| **Develop for Azure storage** | ⚠️ **No cubierto.** Es la laguna consciente del repositorio: no hay Blob Storage ni Cosmos DB. Es lo primero que habría que añadir para cubrir el temario entero |

### Mapa con el temario de CKAD (Certified Kubernetes Application Developer)

| Dominio (peso en el examen) | Dónde se practica |
|---|---|
| **Application Design and Build (20%)** | Los tres `Dockerfile` multi-stage, usuario no root, `k8s/deployment.yaml` |
| Jobs y CronJobs | ⚠️ **No cubierto.** El trabajo asíncrono usa un Deployment permanente, no un `Job` |
| **Application Deployment (20%)** | `RollingUpdate` con `maxSurge`/`maxUnavailable`, chart de Helm, `helm rollback` |
| Estrategias blue/green y canary | ⚠️ Solo rolling update; no hay manifiestos de canary |
| **Application Observability and Maintenance (15%)** | Las tres sondas por pod, `/health/live` vs `/health/ready`, OpenTelemetry |
| Depuración (`kubectl logs`, `describe`) | Documentado en `CLAUDE.md` |
| **Application Environment, Configuration and Security (25%)** | ConfigMap, Secret, `securityContext`, `ServiceAccount` sin token, `ResourceQuota`, `LimitRange` |
| SecurityContexts y capacidades | `runAsNonRoot`, `readOnlyRootFilesystem`, `drop: ["ALL"]`, seccomp |
| CRDs y operadores | ⚠️ **No cubierto.** Se menciona KEDA pero no se instala |
| **Services and Networking (20%)** | Services ClusterIP, Ingress con TLS, **NetworkPolicies** con default-deny |

**Cobertura honesta: unos dos tercios del temario de CKAD y algo más de la mitad del de AZ-204.**
Lo que falta —Jobs/CronJobs, canary, CRDs, Blob Storage, Cosmos DB— está identificado arriba y es
la lista de trabajo de la siguiente fase, no un hueco que el repositorio disimule.

---

## Métricas de referencia

Medidas en el equipo de desarrollo (Windows 11, .NET 10, Debug). Sirven como orden de magnitud,
no como benchmark formal:

| Escenario | `Span<T>` | `string.Split` |
|---|---|---|
| 25.000 filas (1,26 MB de texto) | 0 B asignados, 2.728 µs | 11,55 MB asignados, 8.081 µs |
| Recolecciones Gen0 | 0 | 1 |

Pipeline de Channels, 20.000 eventos con capacidad 8: 6.313 esperas por canal lleno en 10,4 ms.
El número de esperas **es** la evidencia del backpressure: el productor se detuvo 6.313 veces a
esperar que el consumidor liberase hueco.

### Fase 2 · Resiliencia y trabajo asíncrono

| Escenario | Resultado medido |
|---|---|
| Retry, 2 fallos configurados | 3 intentos, esperas de 16,7 → 188,9 → 76,3 ms, resuelto en 282 ms |
| Circuit breaker, dependencia caída | 4 fallos reales, circuito **abierto**, 6 de 10 llamadas cortadas, 7,6 ms totales |
| Trabajo asíncrono, 50.000 filas (local, bus en memoria) | Latencia de cola 1,5 ms, procesamiento 3,0 ms, **0 bytes** asignados |
| Mismo trabajo **sin** calentamiento del JIT | 7.840 bytes y 12.323 µs en la primera ejecución (ver reto 12) |
| Mismo trabajo en contenedores separados (CI, RabbitMQ real) | Procesado por otro contenedor, **0 bytes** asignados |
| Sonda RESP a la cache por la red interna de Docker | `+PONG` en 3,23 ms |

Las esperas del retry no son 150/300 ms exactos ni crecen una a una: con `UseJitter`, Polly aplica
jitter decorrelacionado, que aleatoriza cada espera alrededor de una media que sí crece de forma
exponencial. Es lo que evita que mil clientes reintenten en el mismo milisegundo.

---

## Próximos pasos sugeridos

1. Levantar el stack de nueve contenedores en una máquina con Docker y comparar los paneles de
   Grafana con las cifras de este documento.
2. Desplegar en un Kind local (`kind create cluster` + `kind load docker-image`) y comprobar el
   comportamiento del HPA con `metrics-server` instalado.
3. **Sustituir el registro de trabajos en memoria por Redis**: es la única pieza que impide escalar
   la API a varias réplicas sin que una consulta de estado pueda dar 404 (ver `JobRegistry`).
4. Añadir KEDA para escalar el Worker por longitud de cola en vez de por CPU (el `ScaledObject`
   equivalente ya está esbozado en `k8s/hpa.yaml`).
5. Cubrir las lagunas de certificación identificadas arriba: un `CronJob` de Kubernetes y un módulo
   de Azure Blob Storage / Cosmos DB.
6. Publicar en Release con `PublishTrimmed` para medir el tamaño real de la imagen final y probar
   la variante `noble-chiseled` como perfil alternativo.
