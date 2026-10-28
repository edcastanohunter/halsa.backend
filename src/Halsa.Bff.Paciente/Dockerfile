# syntax=docker/dockerfile:1
# The BFF of the patient portal (SPEC 12), shipped as localhost/halsa-bff-paciente by ci.yml (SPEC 14).
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
# curl for the compose healthcheck; setpriv (util-linux) for deploy/scripts/arrancar-dotnet.sh.
USER root
RUN apt-get update \
 && apt-get install -y --no-install-recommends curl \
 && rm -rf /var/lib/apt/lists/*
WORKDIR /app
EXPOSE 8080
USER $APP_UID

# The SDK runs on the builder's architecture: the output (UseAppHost=false) is portable IL, so ci.yml builds the
# arm64 image without emulating the compiler.
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src

COPY Directory.Build.props Directory.Packages.props ./
COPY src/Halsa.Bff.Paciente/*.csproj src/Halsa.Bff.Paciente/
RUN dotnet restore src/Halsa.Bff.Paciente/Halsa.Bff.Paciente.csproj

COPY src/Halsa.Bff.Paciente/ src/Halsa.Bff.Paciente/
RUN dotnet publish src/Halsa.Bff.Paciente/Halsa.Bff.Paciente.csproj \
    -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "Halsa.Bff.Paciente.dll"]
