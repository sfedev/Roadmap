# =============================================================================
# Web API — build multi-stage.
# El contexto de build es la RAÍZ del repositorio (ver docker-compose.yml), porque
# la API depende del proyecto DotNetLab.Contracts, que vive fuera de su carpeta.
# =============================================================================

# --- Etapa 1: restore -------------------------------------------------------
# Se separa del build para aprovechar la caché de capas: mientras no cambien los
# .csproj, Docker reutiliza esta capa y no vuelve a descargar NuGet.
FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS restore

# Directorio de trabajo dentro de la imagen; todas las rutas siguientes son relativas a él.
WORKDIR /src

# Los archivos que gobiernan la compilación van primero: cambian muy poco y su capa se reutiliza.
COPY ["Directory.Build.props", "global.json", "./"]

# SOLO los .csproj, no el código. Es lo que permite que 'dotnet restore' se cachee aparte:
# editar un .cs no invalida esta capa, editar una dependencia sí.
COPY ["src/DotNetLab.Api/DotNetLab.Api.csproj", "src/DotNetLab.Api/"]
COPY ["src/DotNetLab.Contracts/DotNetLab.Contracts.csproj", "src/DotNetLab.Contracts/"]

# Restaura el grafo completo de dependencias del proyecto y sus referencias.
RUN dotnet restore "src/DotNetLab.Api/DotNetLab.Api.csproj"

# --- Etapa 2: publish -------------------------------------------------------
# Parte de la etapa anterior, así que hereda los paquetes ya restaurados.
FROM restore AS publish

# Ahora sí, el código fuente. El .dockerignore evita que entren bin/, obj/ y .git.
COPY . .

# --no-restore: los paquetes ya están; repetir el restore sería tiempo tirado.
# UseAppHost=false: no genera el ejecutable nativo, el ENTRYPOINT invoca 'dotnet app.dll'.
#   Ahorra ~70 KB y elimina un binario más de la imagen final.
RUN dotnet publish "src/DotNetLab.Api/DotNetLab.Api.csproj" \
    -c Release \
    -o /app/publish \
    --no-restore \
    /p:UseAppHost=false

# --- Etapa 3: final (runtime) -----------------------------------------------
# Imagen de RUNTIME, no de SDK: sin compilador, sin MSBuild y sin código fuente.
# Alpine pesa ~110 MB frente a los ~220 MB de la variante Debian.
#
# Alternativa aún más restringida para producción:
#   FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled AS final
# No tiene shell ni gestor de paquetes (menor superficie de ataque), pero por eso mismo
# tampoco admite el HEALTHCHECK de abajo, que necesita el wget de BusyBox.
FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine AS final

WORKDIR /app

# Puerto no privilegiado: un proceso sin root no puede abrir puertos por debajo del 1024.
# Las imágenes oficiales de .NET 8+ ya traen ASPNETCORE_HTTP_PORTS=8080.
ENV ASPNETCORE_HTTP_PORTS=8080 \
    # Producción por defecto: si alguien necesita Development, lo pasa por compose.
    ASPNETCORE_ENVIRONMENT=Production \
    # Desactiva la telemetría del SDK/CLI dentro del contenedor.
    DOTNET_NOLOGO=1

# Copia SOLO el resultado publicado. Todo lo demás (SDK, fuentes, NuGet) se queda en las
# etapas anteriores, que nunca se suben al registro.
# --chown asigna la propiedad al usuario 'app' que ya define la imagen oficial (UID 64198).
COPY --from=publish --chown=app:app /app/publish .

# EXPOSE es documentación del contrato de red; no publica el puerto por sí solo.
EXPOSE 8080

# Ejecutar como usuario sin privilegios: si alguien logra ejecución dentro del contenedor,
# no es root. $APP_UID lo define la propia imagen base de .NET.
USER $APP_UID

# Sonda de salud: el orquestador la usa para saber si el contenedor puede recibir tráfico.
# --spider hace que wget no escriba nada: solo interesa el código de estado.
# start-period da margen al arranque en frío del runtime antes de contar fallos.
HEALTHCHECK --interval=15s --timeout=3s --start-period=10s --retries=3 \
    CMD wget --quiet --spider http://localhost:8080/api/diagnostics/health || exit 1

# Forma exec (array), no shell: el proceso dotnet es PID 1 y recibe directamente SIGTERM,
# de modo que ASP.NET Core puede hacer un apagado ordenado en vez de morir por timeout.
ENTRYPOINT ["dotnet", "DotNetLab.Api.dll"]
