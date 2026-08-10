FROM node:22-bookworm-slim AS ui-build
WORKDIR /src

COPY src/virtua-agent-app/package*.json ./src/virtua-agent-app/
RUN npm ci --prefix src/virtua-agent-app

COPY assets ./assets
COPY src/virtua-agent-app ./src/virtua-agent-app
RUN npm run build --prefix src/virtua-agent-app

FROM node:22-bookworm-slim AS codex-runtime
ARG CODEX_VERSION=0.147.0
RUN npm install --global "@openai/codex@${CODEX_VERSION}" \
    && codex --version \
    && mkdir -p /codex-home /run/codex /work
ENV CODEX_HOME=/codex-home
ENTRYPOINT ["codex"]

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS api-build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src

COPY src/virtua-agent-api/VirtuaAgent.Api/VirtuaAgent.Api.csproj src/virtua-agent-api/VirtuaAgent.Api/
RUN dotnet restore src/virtua-agent-api/VirtuaAgent.Api/VirtuaAgent.Api.csproj

COPY src/virtua-agent-api ./src/virtua-agent-api
COPY --from=ui-build /src/src/virtua-agent-api/VirtuaAgent.Api/wwwroot/app ./src/virtua-agent-api/VirtuaAgent.Api/wwwroot/app
WORKDIR /src/src/virtua-agent-api/VirtuaAgent.Api
RUN dotnet publish VirtuaAgent.Api.csproj -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
EXPOSE 8080

COPY --from=api-build /app/publish .

RUN mkdir -p /data
VOLUME ["/data"]

ENV ASPNETCORE_HTTP_PORTS=8080
ENV TraceStore__ConnectionString="Data Source=/data/virtua-agent.db"

ENTRYPOINT ["dotnet", "VirtuaAgent.Api.dll"]
