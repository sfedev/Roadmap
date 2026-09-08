# =============================================================================
# Worker Service — build multi-stage.
# El contexto de build es la RAÍZ del repositorio: el Worker referencia a DotNetLab.Analysis,
# DotNetLab.Contracts y DotNetLab.ServiceDefaults, todos fuera de su carpeta.
# =============================================================================

# --- Etapa 1: restore -------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS restore

WORKDIR /src

# Los ficheros que gobiernan la compilación primero: cambian poco y su capa se reutiliza.
COPY ["Directory.Build.props", "global.json", "./"]

# SOLO los .csproj. Es lo que permite cachear el restore aparte: editar un .cs no invalida
# esta capa, cambiar una dependencia sí.
COPY ["src/DotNetLab.Worker/DotNetLab.Worker.csproj", "src/DotNetLab.Worker/"]
COPY ["src/DotNetLab.Analysis/DotNetLab.Analysis.csproj", "src/DotNetLab.Analysis/"]
COPY ["src/DotNetLab.Contracts/DotNetLab.Contracts.csproj", "src/DotNetLab.Contracts/"]
COPY ["src/DotNetLab.ServiceDefaults/DotNetLab.ServiceDefaults.csproj", "src/DotNetLab.ServiceDefaults/"]

RUN dotnet restore "src/DotNetLab.Worker/DotNetLab.Worker.csproj"

# --- Etapa 2: publish -------------------------------------------------------
FROM restore AS publish

COPY . .

# --no-restore: los paquetes ya están. UseAppHost=false evita generar el ejecutable nativo,
# porque el ENTRYPOINT invoca 'dotnet app.dll' de todas formas.
RUN dotnet publish "src/DotNetLab.Worker/DotNetLab.Worker.csproj" \
    -c Release \
    -o /app/publish \
    --no-restore \
    /p:UseAppHost=false

# --- Etapa 3: final (runtime) -----------------------------------------------
# Imagen aspnet y no runtime: el Worker expone /health/live y /health/ready para las sondas de
# Kubernetes, así que necesita Kestrel. Un consumidor sin endpoint HTTP no puede tener sondas,
# y el kubelet no sabría distinguir un pod colgado de uno simplemente ocioso.
FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine AS final

WORKDIR /app

ENV ASPNETCORE_HTTP_PORTS=8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_NOLOGO=1

# Solo el resultado publicado. El SDK, las fuentes y la caché de NuGet se quedan en las etapas
# anteriores, que nunca se suben al registro.
COPY --from=publish --chown=app:app /app/publish .

EXPOSE 8080

# Usuario sin privilegios definido por la propia imagen oficial (UID 64198).
USER $APP_UID

# La sonda apunta a /health/live, que no consulta el bus: si comprobara RabbitMQ, una caída del
# broker haría que Docker reiniciara el Worker en bucle en vez de dejarle esperar a que vuelva.
HEALTHCHECK --interval=15s --timeout=3s --start-period=10s --retries=3 \
    CMD wget --quiet --spider http://localhost:8080/health/live || exit 1

# Forma exec: dotnet es PID 1 y recibe SIGTERM directamente, así el consumidor puede TERMINAR
# el mensaje que tiene entre manos antes de morir. En forma shell, /bin/sh sería PID 1 y no
# propagaría la señal: Docker mataría el proceso a mitad de un trabajo.
ENTRYPOINT ["dotnet", "DotNetLab.Worker.dll"]
