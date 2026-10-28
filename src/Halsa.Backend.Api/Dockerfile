# syntax=docker/dockerfile:1
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
# SPEC 13: curl for the compose healthchecks (/health); setpriv (util-linux) is already in the base image and
# deploy/scripts/arrancar-dotnet.sh uses it to drop to $APP_UID after reading the secrets.
USER root
RUN apt-get update  && apt-get install -y --no-install-recommends curl  && rm -rf /var/lib/apt/lists/*
WORKDIR /app
EXPOSE 8080
USER $APP_UID

# The SDK runs on the builder's architecture: the output (UseAppHost=false) is portable IL, so ci.yml builds the
# arm64 image without emulating the compiler.
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src

COPY Directory.Build.props Directory.Packages.props ./
COPY src/Halsa.Backend.SharedKernel/*.csproj  src/Halsa.Backend.SharedKernel/
COPY src/Halsa.Backend.Mediation/*.csproj     src/Halsa.Backend.Mediation/
COPY src/Halsa.Backend.Domain/*.csproj        src/Halsa.Backend.Domain/
COPY src/Halsa.Backend.Application/*.csproj   src/Halsa.Backend.Application/
COPY src/Halsa.Backend.Infrastructure/*.csproj src/Halsa.Backend.Infrastructure/
COPY src/Halsa.Backend.Api/*.csproj           src/Halsa.Backend.Api/
RUN dotnet restore src/Halsa.Backend.Api/Halsa.Backend.Api.csproj

COPY . .
RUN dotnet publish src/Halsa.Backend.Api/Halsa.Backend.Api.csproj \
    -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "Halsa.Backend.Api.dll"]
