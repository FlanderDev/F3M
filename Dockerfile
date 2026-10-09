# syntax=docker/dockerfile:1

# One .NET version for both stages. Override with --build-arg DOTNET_VERSION=...
ARG DOTNET_VERSION=10.0

FROM mcr.microsoft.com/dotnet/sdk:${DOTNET_VERSION}-alpine AS build
ARG CONFIGURATION=Release

COPY ./src /source

WORKDIR /source/F3M.Server

RUN dotnet publish --configuration ${CONFIGURATION} --self-contained false -o /app

FROM mcr.microsoft.com/dotnet/aspnet:${DOTNET_VERSION}-alpine AS final
WORKDIR /app

# Plain HTTP inside the container. TLS is terminated by the reverse proxy in front of it.
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080

COPY --from=build /app .

RUN mkdir -p /app/Storage && chown -R $APP_UID:$APP_UID /app

USER $APP_UID

ENTRYPOINT ["dotnet", "F3M.Server.dll"]
