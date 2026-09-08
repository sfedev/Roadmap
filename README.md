# DotNetLab

Portal interactivo que explica **cómo funcionan por dentro** las tecnologías de un stack .NET de
nivel empresarial: **Modern .NET / C# 14**, **Docker**, **observabilidad con OpenTelemetry**,
**resiliencia con Polly**, **arquitectura dirigida por eventos** y **Kubernetes**.

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

**Módulo 3 · Cloud y producción** — OpenTelemetry (trazas, métricas y logs correlacionados),
Polly (retry con backoff y jitter, circuit breaker), arquitectura dirigida por eventos
(202 Accepted + MassTransit + SignalR), Kubernetes (sondas, recursos, HPA) y IaC (Helm + Bicep).
Los tres primeros tienen playground ejecutable.

---

## Arranque rápido

### Con Docker (stack completo)

```bash
openssl rand -base64 32 > secrets/lab_shared_key.txt && openssl rand -base64 24 > secrets/rabbitmq_password.txt
```

```bash
docker compose up --build
```

| Servicio | URL |
|---|---|
| **Portal** | http://localhost:8081 |
| Web API (JSON crudo) | http://localhost:8080 |
| Jaeger (trazas) | http://localhost:16686 |
| Prometheus (métricas) | http://localhost:9090 |
| Grafana (paneles, admin/admin) | http://localhost:3000 |
| RabbitMQ (colas) | http://localhost:15672 |

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

Sin Docker el bus arranca en modo **`inmemory`**: la API aloja también el consumidor, así que el
flujo asíncrono completo funciona igual. Lo que no habrá es notificación por SignalR ni trazas en
Jaeger, y la UI cae al respaldo por consulta periódica.

### Kubernetes

```bash
helm upgrade --install lab ./helm/dotnetlab -n dotnetlab --create-namespace -f helm/dotnetlab/values-dev.yaml
```

### Azure

```bash
az deployment group create -g rg-dotnetlab-dev -f infrastructure/main.bicep -p infrastructure/parameters/dev.bicepparam
```

---

## Estructura

```
DotNetLab.slnx
Directory.Build.props        Estándar de compilación común a todos los proyectos
global.json                  Banda del SDK fijada (10.0.3xx)
docker-compose.yml           9 servicios: app + mensajería + observabilidad
secrets/                     Ficheros de secreto (fuera del control de versiones)

src/
  DotNetLab.ServiceDefaults/ OpenTelemetry, health checks y resiliencia. Una línea por proceso.
  DotNetLab.Analysis/        Dominio compartido API + Worker: parsers y consumidor del bus
  DotNetLab.Contracts/       Records compartidos (HTTP y mensajes). Sin lógica
  DotNetLab.Api/             Web API con Minimal APIs. Acepta trabajos y los publica
  DotNetLab.Worker/          Consume del bus y parsea con Span<T>. Solo expone /health/*
  DotNetLab.Web/             Host Blazor: render, proxy y hub de SignalR
  DotNetLab.Web.Client/      Ensamblado WebAssembly: páginas y playgrounds

tests/
  DotNetLab.Api.Tests/       49 pruebas: unitarias, integración de endpoints y del bus

k8s/                         Manifiestos comentados campo a campo (estudio CKAD)
helm/dotnetlab/              Chart parametrizado para dev y prod
infrastructure/              Bicep: AKS, ACR, Key Vault, monitoring, Container Apps
observability/               Colector OTel, Prometheus y provisioning de Grafana
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
| `GET /api/resilience/demo` | Polly: retry con backoff y jitter, o circuit breaker |
| `POST /api/analysis/jobs` | **202 Accepted** → bus → Worker → SignalR |
| `GET /api/analysis/jobs/{id}` | Estado del trabajo (respaldo de SignalR) |
| `GET /api/diagnostics/health` | Estado del proceso y sonda al contenedor auxiliar |
| `GET /health/live` · `/health/ready` | Sondas de Kubernetes (liveness y readiness) |

---

## Verificación

```bash
dotnet build
```

```bash
dotnet test
```

El repositorio compila con **avisos como errores** y las 49 pruebas deben pasar.

Cada push a `main` dispara un workflow de CI con cuatro trabajos:

| Trabajo | Qué verifica |
|---|---|
| **Build y tests** | Compila en Release, ejecuta las 49 pruebas y publica el frontend (ejercita el trimming del WASM) |
| **Imágenes Docker** | Construye las tres imágenes, valida el compose y comprueba que el Worker vive sin broker |
| **Manifiestos, Helm y Bicep** | `kubectl --dry-run`, `helm lint` + render de dev y prod, y `az bicep build` |
| **Stack completo** | Levanta siete contenedores y recorre el flujo asíncrono de punta a punta, comprobando que el trabajo lo procesó **otro contenedor** y que la telemetría llega a Prometheus y Jaeger |

---

## Documentación

| Archivo | Para qué |
|---|---|
| [PROJECT_DEVLOG.md](PROJECT_DEVLOG.md) | Registro de desarrollo: decisiones, alternativas descartadas y los veintitrés problemas reales que aparecieron, con su medición y su solución. Incluye la sección **Career & Resume Impact** con logros para CV en inglés y el mapeo con los temarios de AZ-204 y CKAD |
| [CLAUDE.md](CLAUDE.md) | Comandos (dotnet, Docker, Kubernetes, Helm, Bicep), estándares de estilo y patrones de observabilidad, resiliencia y mensajería |
| [AGENTS.md](AGENTS.md) | Mapa de la arquitectura, guías paso a paso para extender el sistema (nueva métrica, nuevo consumidor, nuevo manifiesto) y limitaciones conocidas |
