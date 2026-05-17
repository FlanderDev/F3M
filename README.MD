## Table of Contents

- [Intro](#intro)
	- [Planned features/changes](#planned-features/changes)
- [F3M](#F3M)
	- [Project Structure](#project-structure)
	- [Prerequisites](#prerequisites)
	- [Getting Started](#getting-started)
		- [Building for Production](#building-for-production)
- [Contributing](#contributing)

---

# Intro

**F**lander's **M**anaka **M**od **M**anager is a Website for managing mods for a certain game.
Feel free to test the demo-site however you want, just know I might wipe it anytime: http://f3m-dev.runasp.net

## Planned features/changes
- Rich Text Editor for mod descriptions.
- Mod dependencies
- Mod packs
- A Desktop application working in tandem with this server.

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
├── F3M.Client/       # Blazor WebAssembly frontend
├── F3M.Server/       # ASP.NET Core backend (hosts the client + API)
├── F3M.Shared/       # Shared models, DTOs, and contracts
├── Dockerfile        # Multi-stage Docker build (Alpine-based)
├── compose.yaml      # Docker Compose configuration
├── F3M.sln           # Visual Studio solution file
└── global.json       # .NET SDK version pin (10.0, latestMinor)
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

dotnet restore
dotnet run --project F3M.Server
```

The application will be available at `https://localhost:5001` (or the port configured in `launchSettings.json`).

### Run with Docker Compose

```bash
docker compose up --build
```

The server will be exposed on:
- `http://localhost:8081` → container port `8080`
- `https://localhost:443`

---

## Building for Production

```bash
dotnet publish F3M.Server -c Release -o ./publish
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
