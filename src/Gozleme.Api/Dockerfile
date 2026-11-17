# syntax=docker/dockerfile:1.7
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /repo
COPY global.json Directory.Build.props Directory.Packages.props .editorconfig ./
COPY src/Gozleme.Api/Gozleme.Api.csproj src/Gozleme.Api/
RUN dotnet restore src/Gozleme.Api/Gozleme.Api.csproj
COPY src/Gozleme.Api/ src/Gozleme.Api/
ARG GOZLEME_BUILD=0.0.0-local
RUN dotnet publish src/Gozleme.Api/Gozleme.Api.csproj -c Release -o /app --no-restore \
      -p:InformationalVersion="${GOZLEME_BUILD}" -p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
# tini is PID 1 so dotnet is not: Linux ignores a PID 1's own abort(), so a crashed runtime would spin forever
# instead of exiting for the restart policy (GOZ-0005).
RUN apt-get update && apt-get install -y --no-install-recommends tini && rm -rf /var/lib/apt/lists/*
WORKDIR /app
ENV ASPNETCORE_HTTP_PORTS=8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_NOLOGO=1
COPY --from=build /app ./
USER $APP_UID
EXPOSE 8080
HEALTHCHECK --interval=5s --timeout=3s --start-period=10s --retries=6 \
  CMD ["dotnet", "Gozleme.Api.dll", "healthcheck"]
ENTRYPOINT ["tini", "--", "dotnet", "Gozleme.Api.dll"]
