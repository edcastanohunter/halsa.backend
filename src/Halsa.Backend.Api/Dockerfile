# syntax=docker/dockerfile:1
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
WORKDIR /app
EXPOSE 8080
USER $APP_UID

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
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
