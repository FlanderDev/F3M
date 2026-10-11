![Website Preview](https://repository-images.githubusercontent.com/1176540310/0ff85081-472b-4d34-bc6c-293e93d9d75b)

# F3M: Flander's Manaka Mod Manager

F3M is a mod hub for **Secret Flasher Manaka**. It has two parts:

- **The website**, where authors upload mods and players browse and download them. It is live at **[f3m.felix-leander.de](https://f3m.felix-leander.de)**.
- **F3M Desktop**, a mod manager for Windows and Linux. It installs mods, with their dependencies, into the game folder and removes them again cleanly.

> **Status: public alpha.** Expect rough edges, and mods, data or links that may still change.

| | |
|---|---|
| Website | https://f3m.felix-leander.de |
| Desktop app and guide | https://flanderdev.github.io/F3M/ |
| Downloads | [Latest release](https://github.com/FlanderDev/F3M/releases/latest) |
| Bugs and ideas | [GitHub issues](https://github.com/FlanderDev/F3M/issues) |
| Community | [Discord](https://discord.com/invite/eSWfUezM8K) · [FlanderDev on F95zone](https://f95zone.to/members/flanderdev.2601924/) |

## Table of Contents

- [Features](#features)
- [For players](#for-players)
- [For mod authors](#for-mod-authors)
- [Project structure](#project-structure)
- [Development](#development)
  - [Prerequisites](#prerequisites)
  - [Run the website locally](#run-the-website-locally)
  - [Run the desktop app](#run-the-desktop-app)
  - [Tests](#tests)
  - [Database migrations](#database-migrations)
  - [Filling a local server with real mods](#filling-a-local-server-with-real-mods)
- [Hosting your own server](#hosting-your-own-server)
  - [Docker Compose](#docker-compose)
  - [Without Docker](#without-docker)
- [Releases](#releases)
- [Further documentation](#further-documentation)
- [Contributing](#contributing)
- [License](#license)

---

## Features

### Website

- [x] Browse mods with search, category filter, sorting and infinite scroll
- [x] Mod pages with a Markdown description, versions, requirements, statistics, and links to the author's F3M and F95zone profiles
- [x] Download in the browser, or open the mod straight in F3M Desktop (`f3m://` link)
- [x] Accounts with sign-up and login, plus linking an F95zone account (verified through a post on the F95zone profile)
- [x] Upload with versioning and dependencies. Files may be `.dll`, `.zip`, `.rar` or `.7z`.
- [x] **Mod types** for each file: BepInEx plugin, Custom Missions v1/v2, Cosplay Loader outfit, texture pack, or custom. The type fills in the install path and category, a preview shows where every file will land, and checks warn about common packaging mistakes.
- [x] Mods imported from F95zone stay with their original authors, who claim them by signing in with their F95zone account
- [x] Admin page: users and roles, rebuilding the signed catalog, assigning mod owners
- [x] User profiles
- [ ] Mod packs
- [ ] Collections: the collection pages (`/collection`, the builder) and `AddToCollectionButton` exist but are unused for now; no page offers adding a mod to a collection.

### F3M Desktop

- [x] Finds the game: a running game, Steam libraries, or common folders
- [x] One-click install and removal of mods with all their dependencies, with confirmation before anything is overwritten, and rollback
- [x] Profiles: several mod setups for the same game folder
- [x] An editor for BepInEx `.cfg` settings that keeps the comments
- [x] Works offline after the first catalog download. It only trusts a **signed** catalog.
- [x] `f3m://` links from the website, always confirmed in the app first
- [x] Updates itself (Windows installer and Linux AppImage)

## For players

1. **Prepare the game.** Install BepInEx, or use the pre-patched game linked in the [game thread](https://f95zone.to/threads/secret-flasher-manaka-v1-1-3-sheablesoft.256682/).
2. **Get F3M Desktop** from the [latest release](https://github.com/FlanderDev/F3M/releases/latest):
   - Windows installer: `F3M.Desktop-win-Setup.exe`
   - Windows portable: `F3M.Desktop-win-Portable.zip`
   - Linux: the `.AppImage`
3. **Point it at the game.** Start the app and let it find the game, then browse and install mods.
   - Alternatively, click **F3M Desktop** on any mod page on the website.

The [project site](https://flanderdev.github.io/F3M/) has a step-by-step guide and an FAQ.

## For mod authors

1. Sign in at https://f3m.felix-leander.de. Linking your F95zone account is recommended.
2. Open **Upload**, then add your files and pick a **type** for each one.
   - F3M suggests the type, install path and archive folder from the archive's contents.
   - The preview shows where every file will go.
3. Add the mods yours requires.
   - The Custom Missions framework, the Cosplay Loader or Texture Replacer is added automatically for the matching type.
4. Review any warnings, confirm them if they're intended, and publish. New versions are uploaded from the mod's page.

Did someone import your mod from F95zone? Sign in with, or link, the F95zone account that posted it. The mod becomes yours automatically.

## Project structure

```
F3M/
├── src/
│   ├── F3M.Client/         # Blazor WebAssembly website
│   ├── F3M.Server/         # ASP.NET Core: API, hosts the website, signed catalog, SQLite database
│   ├── F3M.Shared/         # Models, API contracts, install-path and mod-type rules (used by all apps)
│   ├── F3M.Shared.Tests/   # xUnit tests
│   ├── F3M.Desktop/        # Avalonia desktop mod manager (Windows/Linux)
│   ├── F3M.sln
│   └── global.json         # .NET SDK pin
├── import/                 # Scripts and data to import community mods from F95zone (see import/README.md)
├── docs/                   # Project site (GitHub Pages)
├── .github/workflows/      # Release builds: server, Windows installer (Velopack), Linux AppImage
├── Dockerfile              # Multi-stage Alpine build of the server
├── compose.yaml            # Docker Compose setup
├── .env.example            # Every setting the container needs
└── MODDING-ECOSYSTEM.md    # Background: the game's modding scene, frameworks, sources
```

| Layer | Technology |
|---|---|
| Website | Blazor WebAssembly, Bootstrap |
| Server | ASP.NET Core Web API, EF Core with SQLite, ASP.NET Core Identity |
| API contracts | Typed `I*Api` interfaces in `F3M.Shared`, with generated controllers and clients (FlanderDev.RouteGen) |
| Desktop | Avalonia, CommunityToolkit.Mvvm, Velopack |
| Archives | SharpCompress (`.zip`, `.rar`, `.7z`) |
| Runtime | .NET 10 |
| Hosting | Docker / Docker Compose |

## Development

### Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- Optional:
  - [Docker](https://www.docker.com/) for container builds;
  - Python 3 and [7-Zip](https://www.7-zip.org/) for the import scripts.

### Run the website locally

```bash
git clone https://github.com/FlanderDev/F3M.git
cd F3M
dotnet run --project src/F3M.Server
```

The server starts at `https://localhost:62444` and `http://localhost:62445` (see `src/F3M.Server/Properties/launchSettings.json`).

In the **Development** environment:
- The database, uploads, catalog and signing key are created on first start under `src/F3M.Server/Storage/` (git-ignored).
- The catalog signing key uses a development password, so no configuration is needed.
- Debug builds create a debug admin with user name and password `F3M`.
- **F95zone linking** needs an F95zone bot account in `F95Bot__*`. Set it in `appsettings.Development.json` or user secrets, and never commit it. Without one, linking fails; everything else works.

### Run the desktop app

```bash
dotnet run --project src/F3M.Desktop
```

The desktop app talks to the public website by default. Change the server address in **Settings** to use your local server.

### Tests

```bash
dotnet test src/F3M.sln
```

### Database migrations

Before the first stable release there is a **single `InitialCreate` migration**:
- When the schema changes, regenerate it instead of adding a new migration.
- Then delete your local database in `src/F3M.Server/Storage/Database/`.

From `src/F3M.Server`:

```bash
dotnet dnx -y dotnet-ef@10.0.12 -- migrations add InitialCreate
```

### Filling a local server with real mods

`import/` contains scripts that download about 200 community mods from F95zone and upload them to an F3M server through the admin import API:

```bash
cd import
python -I tools/download.py
python -I tools/build_manifests.py
python -I tools/upload.py --server http://localhost:62445
```

See [import/README.md](import/README.md) for the options, how imported mods are owned, and how warnings are handled.

## Hosting your own server

### Docker Compose

1. Copy `.env.example` to `.env` and set at least `F3M_CATALOG_KEY_PASSWORD`. Every setting is described there.
2. Run:

```bash
docker compose up --build -d
```

**Network:**
- The container serves plain HTTP on port `8080`, published on the host as `http://localhost:62444` (change it with `F3M_HTTP_PORT`).
- For HTTPS, put a TLS-terminating reverse proxy in front. `F3M_BEHIND_PROXY=true` (the default) makes the app trust its `X-Forwarded-*` headers.

**Data:** the `f3m_Storage` volume holds the database, uploads, the public catalog and the catalog signing key.

> **Back up the signing key (`Storage/Secrets/`) together with its password.** Installed desktop apps trust the first key they see. If the key is lost, they can no longer verify the catalog.

### Without Docker

```bash
dotnet publish src/F3M.Server -c Release -o ./publish
```

Run `publish/F3M.Server.dll` with the ASP.NET Core 10 runtime. Set `Catalog__KeyPassword` and, optionally, `F95Bot__*` as environment variables.

Ready-made server builds are also attached to every release. They come framework-dependent or self-contained, for Linux and Windows.

## Releases

Pushing to the `release` branch runs `.github/workflows/release.yml`. It:
- builds the server for Linux and Windows;
- builds the desktop app as a Windows installer and portable zip (Velopack) and as a Linux AppImage;
- publishes everything as a GitHub release tagged `release/<yyyy.MM.dd.HHmm>`.

Day-to-day work happens on `develop` and feature branches.

## Further documentation

| Document | What's in it |
|---|---|
| [MODDING-ECOSYSTEM.md](MODDING-ECOSYSTEM.md) | How Secret Flasher Manaka is modded: BepInEx, Custom Missions, Cosplay Loader, Texture Replacer, community sources, common packaging pitfalls, and a map of this codebase |
| [import/README.md](import/README.md) | Importing community mods from F95zone |
| [Project site](https://flanderdev.github.io/F3M/) | Player guide, FAQ and downloads |

## Contributing

1. Fork the repository.
2. Create a feature branch from `develop`, for example `git checkout -b feature/my-feature develop`.
3. Commit your changes and push the branch.
4. Open a pull request against `develop`.

Please never commit secrets: `.env`, `appsettings.Development.json` with credentials, or anything in `Storage/`.

## License

[GNU Affero General Public License v3.0](LICENSE)
