# Pasos de compilación
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build-env
WORKDIR /app

# Copiar archivos de proyectos para aprovechar el almacenamiento en caché de capas
COPY src/AMRecomen.Domain/AMRecomen.Domain.csproj src/AMRecomen.Domain/
COPY src/AMRecomen.Application/AMRecomen.Application.csproj src/AMRecomen.Application/
COPY src/AMRecomen.Infrastructure/AMRecomen.Infrastructure.csproj src/AMRecomen.Infrastructure/
COPY src/AMRecomen.Web/AMRecomen.Web.csproj src/AMRecomen.Web/

# Restaurar dependencias del proyecto principal y sus dependencias de la solución
RUN dotnet restore src/AMRecomen.Web/AMRecomen.Web.csproj

# Copiar todo el código fuente y compilar la publicación
COPY src/ src/
RUN dotnet publish src/AMRecomen.Web/AMRecomen.Web.csproj -c Release -o out

# Construir la imagen de ejecución
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build-env /app/out .

# Configurar el puerto para Render (10000 es el puerto predeterminado)
ENV ASPNETCORE_URLS=http://+:10000

ENTRYPOINT ["dotnet", "AMRecomen.Web.dll"]
