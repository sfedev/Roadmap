# =============================================================================
# Blazor Web App (host) — build multi-stage.
# Publica DOS ensamblados a la vez: el host de servidor y el cliente WebAssembly,
# porque el render mode Auto necesita ambos. El contexto es la raíz del repositorio.
# =============================================================================

FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS restore

WORKDIR /src

COPY ["Directory.Build.props", "global.json", "./"]

# Los tres .csproj del frontend: host, cliente WASM y contratos compartidos.
COPY ["src/DotNetLab.Web/DotNetLab.Web.csproj", "src/DotNetLab.Web/"]
COPY ["src/DotNetLab.Web.Client/DotNetLab.Web.Client.csproj", "src/DotNetLab.Web.Client/"]
COPY ["src/DotNetLab.Contracts/DotNetLab.Contracts.csproj", "src/DotNetLab.Contracts/"]
COPY ["src/DotNetLab.ServiceDefaults/DotNetLab.ServiceDefaults.csproj", "src/DotNetLab.ServiceDefaults/"]

# Restaurar el host arrastra a sus referencias, incluido el workload de WebAssembly.
RUN dotnet restore "src/DotNetLab.Web/DotNetLab.Web.csproj"

FROM restore AS publish

COPY . .

# Publicar el HOST publica también el cliente: el SDK compila el ensamblado WASM, lo recorta
# (trimming) y deja los .wasm/.dat bajo wwwroot/_framework listos para servir.
RUN dotnet publish "src/DotNetLab.Web/DotNetLab.Web.csproj" \
    -c Release \
    -o /app/publish \
    --no-restore \
    /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine AS final

WORKDIR /app

ENV ASPNETCORE_HTTP_PORTS=8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_NOLOGO=1

COPY --from=publish --chown=app:app /app/publish .

EXPOSE 8080

USER $APP_UID

# Se sondea la raíz del sitio: si responde 200, Kestrel sirve y los assets estáticos están
# mapeados. No se usa el endpoint de la API porque este contenedor no debe depender de ella
# para considerarse sano (si lo hiciera, una API caída reiniciaría también el frontend).
HEALTHCHECK --interval=15s --timeout=3s --start-period=10s --retries=3 \
    CMD wget --quiet --spider http://localhost:8080/ || exit 1

ENTRYPOINT ["dotnet", "DotNetLab.Web.dll"]
