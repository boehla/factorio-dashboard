ARG DOTNET_VERSION="8.0"
FROM node:22-alpine AS ui
WORKDIR /App
COPY web/package*.json ./
RUN npm ci
COPY web/ ./
RUN npm run build
# https://learn.microsoft.com/en-us/dotnet/core/docker/build-container
FROM mcr.microsoft.com/dotnet/sdk:${DOTNET_VERSION} AS backend
WORKDIR /App
COPY ./ ./
RUN dotnet publish -o out
# Build runtime image
FROM mcr.microsoft.com/dotnet/aspnet:${DOTNET_VERSION}
WORKDIR /App
#RUN apt update && apt install -y curl
COPY --from=backend /App/out /App
COPY --from=ui /src/Fdash.Api/wwwroot /App/wwwroot/
COPY src/Fdash.Api/appsettings.Docker.json /App/appsettings.Local.json
ENTRYPOINT ["dotnet", "Fdash.Api.dll"]
#HEALTHCHECK --interval=5m --timeout=5s CMD curl -f http://localhost:5000/api/health || exit 1
