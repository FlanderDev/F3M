![Website Preview](https://repository-images.githubusercontent.com/1176540310/0ff85081-472b-4d34-bc6c-293e93d9d75b)

## Table of Contents

- [Intro](#intro)
	- [Planned Features](#planned-features)
- [F3M](#f3m)
	- [Project Structure](#project-structure)
	- [Prerequisites](#prerequisites)
	- [Getting Started](#getting-started)
		- [Run locally](#run-locally)
		- [Run with Docker Compose](#run-with-docker-compose)
	- [Building for Production](#building-for-production)
- [Contributing](#contributing)

---

# Intro
**F**lander's **M**anaka **M**od **M**anager is a Website for hosting mods for any game.  

## Planned Features
- [x] User registration/login
- [x] Mod upload/download
- [x] Mod versioning
- [x] Mod dependencies
- [x] Markdown for mod descriptions
- [ ] Mod packs
- [x] User profiles

# F3M
**F3M** is a full-stack web application built with **Blazor WebAssembly** on .NET 10. It follows the standard Blazor Hosted architecture, splitting responsibilities across three projects: a browser-side Blazor client, an ASP.NET Core server, and a shared class library for common models and contracts.

| Layer | Technology |
|---|---|
| Frontend | Blazor WebAssembly (C#, HTML, CSS) |
| Backend | ASP.NET Core Web API |
| Shared | .NET Class Library |
| Runtime | .NET 10 |
| Containerization | Docker / Docker Compose |

---

## Project Structure
```
F3M/
├── src/
│   ├── F3M.Client/   # Blazor WebAssembly frontend
│   ├── F3M.Server/   # ASP.NET Core backend (hosts the client + API)
│   ├── F3M.Shared/   # Shared models, DTOs, and contracts
│   ├── F3M.sln       # Visual Studio solution file
│   └── global.json   # .NET SDK version pin
├── Dockerfile        # Multi-stage Docker build (Alpine-based)
├── compose.yaml      # Docker Compose configuration
```

---

## Prerequisites
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) (preview or stable)
- [Docker](https://www.docker.com/) (optional, for containerized deployment)

---

## Getting Started
### Run locally

```bash
git clone https://github.com/FlanderDev/F3M.git
cd F3M

dotnet restore src/F3M.sln
dotnet run --project src/F3M.Server
```

The application will be available at `https://localhost:5001` (or the port configured in `launchSettings.json`).

### Run with Docker Compose
Copy `.env.example` to `.env` and set at least `F3M_CATALOG_KEY_PASSWORD`. Every setting the container needs is listed there.

```bash
docker compose up --build
```

The container serves plain HTTP on port `8080`, published on the host as `http://localhost:62444` (change it with `F3M_HTTP_PORT`).
For HTTPS, put a TLS-terminating reverse proxy in front; `F3M_BEHIND_PROXY=true` (the default) makes the app trust its `X-Forwarded-*` headers.

The `f3m_Storage` volume holds the database, uploads, the public catalog and the catalog signing key. Back up the key together with its password.

---

## Building for Production
```bash
dotnet publish src/F3M.Server -c Release -o ./publish
```

Or build and push a Docker image:

```bash
docker build -t f3m .
```

The Dockerfile uses a multi-stage Alpine build targeting the server project. The final image is based on `mcr.microsoft.com/dotnet/aspnet:10.0-alpine` and runs `F3M.Server.dll` as the entrypoint.

---

# Contributing
1. Fork the repository
2. Create a feature branch (`git checkout -b feature/my-feature`)
3. Commit your changes (`git commit -m 'Add my feature'`)
4. Push to the branch (`git push origin feature/my-feature`)
5. Open a Pull Request
