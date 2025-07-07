# Multi-stage Dockerfile for ModelingEvolution.AutoUpdater supporting ARM64 and x64

# This stage is used when running from VS in fast mode (Default for Debug configuration)
FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS base
RUN apt-get update
RUN apt-get install -y ssh-client wget curl git

ENV DOCKER_VERSION=20.10.24
ENV DOCKER_COMPOSE_VERSION=2.29.1

# Add a script to download Docker binaries based on the architecture
RUN set -eux; \
    ARCH=$(uname -m); \
    if [ "$ARCH" = "x86_64" ]; then \
        ARCH="x86_64"; \
    elif [ "$ARCH" = "aarch64" ]; then \
        ARCH="aarch64"; \
    else \
        echo "Unsupported architecture: $ARCH"; \
        exit 1; \
    fi; \
    curl -fsSL "https://download.docker.com/linux/static/stable/$ARCH/docker-$DOCKER_VERSION.tgz" -o docker.tgz; \
    tar xzvf docker.tgz; \
    mv docker/* /usr/local/bin/; \
    rm -rf docker docker.tgz

# Add a script to download Docker Compose binaries based on the architecture
RUN set -eux; \
    ARCH=$(uname -m); \
    if [ "$ARCH" = "x86_64" ]; then \
        ARCH="x86_64"; \
    elif [ "$ARCH" = "aarch64" ]; then \
        ARCH="aarch64"; \
    else \
        echo "Unsupported architecture: $ARCH"; \
        exit 1; \
    fi; \
    curl -SL "https://github.com/docker/compose/releases/download/v${DOCKER_COMPOSE_VERSION}/docker-compose-$(uname -s)-${ARCH}" -o /usr/local/bin/docker-compose; \
    chmod +x /usr/local/bin/docker-compose

WORKDIR /app
EXPOSE 5000
VOLUME /data

# This stage is used to build the service project
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
ARG BUILD_CONFIGURATION=Debug
WORKDIR /src
COPY ["src/ModelingEvolution.AutoUpdater.Host/ModelingEvolution.AutoUpdater.Host.csproj", "src/ModelingEvolution.AutoUpdater.Host/"]
COPY ["src/ModelingEvolution.AutoUpdater/ModelingEvolution.AutoUpdater.csproj", "src/ModelingEvolution.AutoUpdater/"]
RUN dotnet restore "./src/ModelingEvolution.AutoUpdater.Host/ModelingEvolution.AutoUpdater.Host.csproj"
COPY . .
WORKDIR "/src/src/ModelingEvolution.AutoUpdater.Host"
RUN dotnet build "./ModelingEvolution.AutoUpdater.Host.csproj" -c $BUILD_CONFIGURATION -o /app/build

# This stage is used to publish the service project to be copied to the final stage
FROM build AS publish
ARG BUILD_CONFIGURATION=Debug
RUN dotnet publish "./ModelingEvolution.AutoUpdater.Host.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

# This stage is used in production or when running from VS in regular mode (Default when not using the Debug configuration)
FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "ModelingEvolution.AutoUpdater.Host.dll"]