# PROJECT_DEVLOG

Registro de desarrollo de **DotNetLab**, el portal interactivo de la FASE 01 (Modern .NET y Docker).
Cada entrada recoge **qué se implementó**, **qué decisión de arquitectura se tomó**, **por qué** y
**qué alternativa se descartó**. Los problemas encontrados se documentan con su medición y su
solución, no como anécdota.

Formato: entradas en orden cronológico de implementación. Las versiones citadas son las reales del
repositorio (.NET SDK 10.0.301, C# 14).

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

**Pendiente de verificar en una máquina con Docker:** la construcción de las imágenes y
`docker compose up`. Los `Dockerfile` y el `docker-compose.yml` están escritos y comentados, pero
Docker no estaba disponible en el entorno de desarrollo, así que **no se han construido ni
ejecutado**. Es el primer paso a validar.

---

## Métricas de referencia

Medidas en el equipo de desarrollo (Windows 11, .NET 10.0.9, Debug). Sirven como orden de magnitud,
no como benchmark formal:

| Escenario | `Span<T>` | `string.Split` |
|---|---|---|
| 25.000 filas (1,26 MB de texto) | 0 B asignados, 2.728 µs | 11,55 MB asignados, 8.081 µs |
| Recolecciones Gen0 | 0 | 1 |

Pipeline de Channels, 20.000 eventos con capacidad 8: 6.313 esperas por canal lleno en 10,4 ms.
El número de esperas **es** la evidencia del backpressure: el productor se detuvo 6.313 veces a
esperar que el consumidor liberase hueco.

---

## Próximos pasos sugeridos

1. Construir y levantar las imágenes (`docker compose up --build`) y verificar la sonda a la cache.
2. Publicar en Release con `PublishTrimmed` para medir el tamaño real de la imagen final.
3. Añadir OpenTelemetry: los endpoints ya emiten logging estructurado con `EventId` estables.
4. Añadir la variante `noble-chiseled` como perfil alternativo de compose para comparar tamaños.
