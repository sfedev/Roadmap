# DotNetLab · Fase 01

Portal interactivo que explica **cómo funcionan por dentro** las tecnologías de la FASE 01 del plan
de estudio: **Modern .NET / C# 14** y **Docker en producción**.

No es una demo decorativa. Cada tarjeta del portal llama a una Web API real, que ejecuta el concepto
y devuelve métricas medidas en su propio proceso: bytes asignados en el Heap, microsegundos de
ejecución y recolecciones de Gen0.

> **Ejemplo real de salida:** sobre 25.000 filas (1,26 MB de texto), la ruta con `Span<T>` asignó
> **0 bytes** y tardó 2.728 µs; `string.Split` asignó **11,55 MB**, provocó una recolección de Gen0
> y tardó 8.081 µs.

---

## Contenido

**Módulo 1 · Modern .NET y C#** — `Span<T>`, records, primary constructors, pattern matching
avanzado, `System.Threading.Channels`, `ValueTask<T>` y Keyed Services. Cada concepto trae teoría
(qué es, cómo funciona internamente, cuándo usarlo), un playground que llama a la API y el código
real del backend con resaltado de sintaxis.

**Módulo 2 · Docker en producción** — el `Dockerfile` multi-stage de este repositorio explicado
etapa por etapa, comparación entre Alpine y Chiseled/Distroless, y gestión de `.dockerignore`,
variables de entorno y secrets.

---

## Arranque rápido

### Con Docker

```bash
openssl rand -base64 32 > secrets/lab_shared_key.txt
```

```bash
docker compose up --build
```

Portal en **http://localhost:8081**.

### Sin Docker

Dos terminales. Primero la API:

```bash
dotnet run --project src/DotNetLab.Api
```

Después el portal:

```bash
dotnet run --project src/DotNetLab.Web
```

Portal en **http://localhost:5120**, API en **http://localhost:5280**.

---

## Estructura

```
DotNetLab.slnx
Directory.Build.props        Estándar de compilación común a todos los proyectos
global.json                  Banda del SDK fijada (10.0.3xx)
docker-compose.yml           api + web + cache, con secret y healthchecks encadenados
.dockerignore
secrets/                     Ficheros de secreto (fuera del control de versiones)

src/
  DotNetLab.Api/             Web API con Minimal APIs — ejecuta las demostraciones
    Endpoints/               Un archivo por módulo pedagógico
    Services/                La lógica real que el portal enseña
    Infrastructure/          Logging generado y seguridad de la clave compartida
    Dockerfile               Multi-stage: restore → publish → runtime Alpine
  DotNetLab.Contracts/       Records compartidos entre API y frontend
  DotNetLab.Web/             Host Blazor: render de servidor, estáticos y proxy hacia la API
    Dockerfile
  DotNetLab.Web.Client/      Ensamblado WebAssembly: páginas y playgrounds interactivos

tests/
  DotNetLab.Api.Tests/       31 pruebas: unitarias de servicios + integración de endpoints
```

---

## Endpoints

| Ruta | Demuestra |
|---|---|
| `GET /api/performance/span-demo` | `Span<T>` frente a `string.Split`, con métricas de memoria |
| `GET /api/concurrency/channel-stream` | Channels con backpressure, respuesta en streaming |
| `GET /api/concurrency/channel-summary` | `ValueTask<T>`: ruta síncrona cacheada vs async |
| `GET /api/language/records` | Igualdad por valor, `with`, deconstrucción |
| `GET /api/language/pattern-match` | Property, relational, logical y list patterns |
| `GET /api/di/keyed-services` | Keyed Services + Factory |
| `GET /api/di/lifetimes` | Singleton / Scoped / Transient en una misma petición |
| `GET /api/diagnostics/health` | Estado del proceso y sonda al contenedor auxiliar |

---

## Verificación

```bash
dotnet build
```

```bash
dotnet test
```

El repositorio compila con **avisos como errores** y las 31 pruebas deben pasar.

---

## Documentación

| Archivo | Para qué |
|---|---|
| [PROJECT_DEVLOG.md](PROJECT_DEVLOG.md) | Registro de desarrollo: decisiones, alternativas descartadas y los once problemas reales que aparecieron, con su medición y su solución |
| [CLAUDE.md](CLAUDE.md) | Comandos, estándares de estilo y reglas de trabajo en el repositorio |
| [AGENTS.md](AGENTS.md) | Mapa de la arquitectura y guías paso a paso para extender el sistema |
