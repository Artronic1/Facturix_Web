# ==========================================
# Etapa 1: Compilación y Publicación
# ==========================================
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Copiar el archivo del proyecto y restaurar dependencias
COPY ["FacturixWeb.csproj", "./"]
RUN dotnet restore "FacturixWeb.csproj"

# Copiar el resto del código y compilar en modo Release
COPY . .
RUN dotnet publish "FacturixWeb.csproj" -c Release -o /app/publish /p:UseAppHost=false

# ==========================================
# Etapa 2: Entorno de Ejecución (Producción)
# ==========================================
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app

# Instalar dependencias para QuestPDF (SkiaSharp requiere libfontconfig1 y fontconfig para renderizar fuentes)
RUN apt-get update && \
    apt-get install -y --no-install-recommends \
        libfontconfig1 \
        fontconfig \
        fonts-dejavu \
    && rm -rf /var/lib/apt/lists/*

# Crear carpeta de datos para SQLite y asignar permisos adecuados
RUN mkdir -p /data

# Variables de entorno
# FACTURIX_DATA_DIR le indica a la app dónde almacenar las bases de datos de SQLite
ENV FACTURIX_DATA_DIR=/data
# Configurar la aplicación para que escuche en el puerto 8080 (predeterminado en .NET 8)
ENV ASPNETCORE_URLS=http://+:8080
ENV ASPNETCORE_ENVIRONMENT=Production

# Copiar los binarios compilados
COPY --from=build /app/publish .

# Exponer el puerto
EXPOSE 8080

# Comando para iniciar la aplicación web
ENTRYPOINT ["dotnet", "FacturixWeb.dll"]
