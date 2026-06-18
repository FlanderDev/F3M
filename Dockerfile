# syntax=docker/dockerfile:1

FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build

COPY ./src /source

WORKDIR /source/F3M.Server

ARG TARGETARCH

RUN dotnet publish -a ${TARGETARCH/amd64/x64} --self-contained false -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine AS final
WORKDIR /app

COPY --from=build /app .

# Create the persistent asset directories so the volume mount point exists.
# Uploads (mod files) and previews (thumbnails) live here, outside wwwroot.
RUN mkdir -p /assets/uploads /assets/previews

USER $APP_UID

ENTRYPOINT ["dotnet", "F3M.Server.dll"]
