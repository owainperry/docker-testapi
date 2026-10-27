# syntax=docker/dockerfile:1

# ---- Build stage ----
# Runs natively on the builder and cross-publishes for the target architecture, so
# multi-arch builds do not need emulation.
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build

ARG TARGETARCH

WORKDIR /src

# Restore first so this layer caches independently of source edits.
COPY DemoApi/DemoApi.csproj DemoApi/
RUN dotnet restore DemoApi/DemoApi.csproj -a $TARGETARCH

COPY DemoApi/ DemoApi/

# Must be a valid NuGet/semver version (e.g. 1.0.42 or 0.0.0-dev): the SDK rejects anything else.
ARG VERSION=0.0.0-dev
RUN dotnet publish DemoApi/DemoApi.csproj \
      -c Release \
      -a $TARGETARCH \
      --no-restore \
      -p:Version=${VERSION} \
      -o /out

# ---- Runtime stage ----
FROM mcr.microsoft.com/dotnet/aspnet:10.0

WORKDIR /app

COPY --from=build /out .

# Non-root user that ships with the .NET images.
USER $APP_UID

# appsettings.json binds localhost:5080 for local runs. A plain (unprefixed) env var
# overrides it; ASPNETCORE_URLS would not, because appsettings is read after it.
ENV Urls=http://+:8080
EXPOSE 8080

# Frontegg__Audience is deliberately not set: the API refuses to start without it.
ENTRYPOINT ["dotnet", "DemoApi.dll"]
