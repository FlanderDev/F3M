![Website Preview](https://repository-images.githubusercontent.com/1176540310/0ff85081-472b-4d34-bc6c-293e93d9d75b)

# F3M

F3M is a mod hub for a BepInEx-modded Unity game. It has two parts:

- **The website**, where authors upload mods and players browse and download them.
- **F3M Desktop**, a mod manager for Windows and Linux. It installs mods, with their dependencies, into the game folder and removes them again cleanly.

> **Status: public alpha.** Expect rough edges, and mods, data or links that may still change.

| | |
|---|---|
| Desktop app and guide | https://flanderdev.github.io/F3M/ |
| Downloads | [Latest release](https://github.com/FlanderDev/F3M/releases/latest) |
| Bugs and ideas | [GitHub issues](https://github.com/FlanderDev/F3M/issues) |

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
  - [Setup script](#setup-script)
  - [Backups and updates](#backups-and-updates)
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
- [x] **Tags:** a mod can have several, from a list admins manage. A tag can include others (*Clothing* includes *Lingerie*), so filtering by a tag also finds mods tagged with the tags it includes. A filtered list's address can be shared.
- [x] Mod pages with a Markdown description, versions, requirements, statistics, and links to the author's F3M and community forum profiles
- [x] Download in the browser, or open the mod straight in F3M Desktop (`f3m://` link)
- [x] Accounts with sign-up and login, plus linking a community forum account (verified through a post on the forum profile)
- [x] Upload with versioning and dependencies. Files may be `.dll`, `.zip`, `.rar` or `.7z`.
- [x] **Optional files:** named groups for parts a mod works without (examples, tutorials, readmes). The form suggests them and they can be marked in the install preview; F3M Desktop installs them only when a player turns a group on.
- [x] **Mod types** for each file: BepInEx plugin, Custom Missions v1/v2, Cosplay Loader outfit, texture pack, or custom. The type fills in the install path and category, a preview shows where every file will land, and checks warn about common packaging mistakes.
- [x] Mods imported from the community forum stay with their original authors, who claim them by signing in with their forum account
- [x] Admin page: users and roles, rebuilding the signed catalog, assigning mod owners, managing tags (the tag tree can't get loops)
- [x] Site setup (admin): a checklist of what a new site still needs, the forum bot account for account linking, and a one-click import of the frameworks
- [x] Community import (admin): downloads about 200 community mods and imports them step by step, with progress, problems and a review of held-back mods
- [x] User profiles
- [ ] Mod packs
- [ ] Collections: the collection pages (`/collection`, the builder) and `AddToCollectionButton` exist but are unused for now; no page offers adding a mod to a collection.

### F3M Desktop

- [x] Finds the game: a running game, Steam libraries, or common folders
- [x] One-click install and removal of mods with all their dependencies, with confirmation before anything is overwritten, and rollback
- [x] Optional parts of a mod (examples, documentation) stay out of the game folder unless turned on per mod in the Library
- [x] Profiles: several mod setups for the same game folder
- [x] An editor for BepInEx `.cfg` settings that keeps the comments
- [x] Works offline after the first catalog download. It only trusts a **signed** catalog.
- [x] `f3m://` links from the website: registered at every start, and the first link from a site asks whether to trust it (revocable in Settings)
- [x] Updates itself (Windows installer and Linux AppImage)

## For players

1. **Prepare the game.** Install BepInEx into the game folder.
2. **Get F3M Desktop** from the [latest release](https://github.com/FlanderDev/F3M/releases/latest):
   - Windows installer: `F3M.Desktop-win-Setup.exe`
   - Windows portable: `F3M.Desktop-win-Portable.zip`
   - Linux: the `.AppImage`
3. **Point it at the game.** Start the app and let it find the game, then browse and install mods.
   - Alternatively, click **F3M Desktop** on any mod page on the website.

The [project site](https://flanderdev.github.io/F3M/) has a step-by-step guide and an FAQ.

## For mod authors

1. Sign in on the website. Linking your community forum account is recommended.
2. Open **Upload**, then add your files and pick a **type** for each one.
   - F3M suggests the type, install path and archive folder from the archive's contents.
   - The preview shows where every file will go.
3. Add the mods yours requires.
   - The Custom Missions framework, the Cosplay Loader or Texture Replacer is added automatically for the matching type.
4. Review any warnings, confirm them if they're intended, and publish. New versions are uploaded from the mod's page.

Did someone import your mod from the community forum? Sign in with, or link, the forum account that posted it. The mod becomes yours automatically.

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
├── docs/                   # Project site (GitHub Pages)
├── .github/workflows/      # Release builds: server, Windows installer (Velopack), Linux AppImage
├── Dockerfile              # Multi-stage Alpine build of the server
├── compose.yaml            # Docker Compose setup
├── setup.sh                # Interactive server setup: writes .env, builds and starts the container
├── backup.sh               # Backs up the server's data and .env, or restores a backup
├── update.sh               # Pulls the newest code, rebuilds and restarts the server
├── admin-updates.sh        # Installs or removes the service that lets the admin page run update.sh
├── scripts/                # Helpers shared by the scripts, and the update service's runner
└── .env.example            # Every setting the container needs
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
- Optional: [Docker](https://www.docker.com/) for container builds.

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
- `launchSettings.json` sets a local admin with user name and password `F3M` (`Admin__UserName`, `Admin__Password`).
- **Forum account linking** needs a forum bot account. Enter it on the setup page (`/admin/setup`), or set `F95Bot__*` in `appsettings.Development.json` or user secrets, and never commit it. Without one, linking is off; everything else works.

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

Servers upgrade their database on start, so every schema change gets **a new migration**:
- Never change or regenerate migrations that already exist.
- Name the new one after the change.

From `src/F3M.Server`:

```bash
dotnet dnx -y dotnet-ef@10.0.12 -- migrations add AddSomething --context AppDbContext
```

### Filling a local server with real mods

Sign in as the local admin and open the import page in the **Admin Panel** (`/admin/import`). It works from the import plan shipped with the server (`src/F3M.Server/Import/import-plan.json`), one step at a time:

1. **Download the files:** about 200 community mods (about 110 MB), only what is missing.
2. **Import the mods:** dependencies first, as unclaimed mods that go to their original uploaders when they sign in.
3. **Review the held-back mods:** tick the ones whose warnings are fine, then import them.

See [src/F3M.Server/Import/README.md](src/F3M.Server/Import/README.md) for every field of the plan and how imported mods are owned.

## Hosting your own server

### Setup script

On a Linux host with Docker, `./setup.sh` does the steps below interactively: it asks for the settings (and can generate the catalog key password), writes `.env`, builds and starts the container, and waits until the server is healthy. Run it again to change settings; the values in `.env` are offered as defaults.

```bash
./setup.sh
```

### Docker Compose

1. Copy `.env.example` to `.env` and set at least `F3M_CATALOG_KEY_PASSWORD`, `F3M_ADMIN_USERNAME` and `F3M_ADMIN_PASSWORD`. Every setting is described there.
2. Run:

```bash
docker compose up --build -d
```

3. Sign in as that admin and open **Admin Panel → Site setup** (`/admin/setup`). Its checklist shows what is left: the forum bot account that account linking needs, and the frameworks to import.

**The admin account** is created at the first start, or an existing account of that name becomes admin. It is made admin again at every start and can't be demoted on the site. Its password is only set when the account is created; `F3M_ADMIN_PASSWORD_OVERWRITE=true` resets it at every start, for when it was lost.

**Network:**
- The container serves plain HTTP on port `8080`, published on the host as `http://localhost:62444` (change it with `F3M_HTTP_PORT`).
- For HTTPS, put a TLS-terminating reverse proxy in front. `F3M_BEHIND_PROXY=true` (the default) makes the app trust its `X-Forwarded-*` headers.

**Data:** the `f3m_Storage` volume holds the database, uploads, the public catalog and the catalog signing key.

> **Back up the signing key (`Storage/Secrets/`) together with its password.** Installed desktop apps trust the first key they see. If the key is lost, they can no longer verify the catalog.

**Health:** `/healthz` answers `Healthy` (200) while the database opens, the storage takes writes and the signing key opens with its password, and `Unhealthy` (503) otherwise; the reason is in the log. The image's Docker `HEALTHCHECK` uses it, so `docker ps` shows the server as healthy or unhealthy.

### Backups and updates

`./backup.sh` writes one `.tar.gz` into `backups/` with the storage volume (database, uploads, catalog, signing key) and `.env` (the key's password). The server stops for the few seconds the copy takes, so the database is consistent. Keep the archive private: it holds the passwords.

```bash
./backup.sh                      # make a backup
./backup.sh --keep 14            # ... and keep only the 14 newest
./backup.sh --restore backups/f3m-backup-20261012-040000.tar.gz
```

The import's downloads are left out unless `--with-downloads` is given; they can be downloaded again. Making a backup asks nothing, so it can run from cron (`0 4 * * * /path/to/F3M/backup.sh --keep 14`). `./backup.sh --help` lists every option.

`./update.sh` fetches the branch's newest commits, offers a backup, rebuilds and restarts the container, and waits until it is healthy. If the new version doesn't start, it prints how to go back to the previous one. `--yes` asks nothing, makes the backup, and only rebuilds when there is something new.

#### Updating from the admin page

`./admin-updates.sh` installs a small systemd service on the host (it asks for sudo), after which the admin page shows the running version, the new commits waiting on the branch, and an **Update now** button. The page only leaves a request in the storage volume; the service runs `./update.sh --yes` on the host (backup, newest code, rebuild, restart) as the owner of the F3M folder, and reports its progress and log back to the page. It also looks for new commits every 6 hours. The container gets no access to Docker or the host.

```bash
./admin-updates.sh           # shows the status and asks: install, reinstall or remove
./admin-updates.sh install   # or remove, or status, without questions
```

Reinstall it after moving the F3M folder. `journalctl -u f3m-admin-update.service` shows what the service did.

### Without Docker

```bash
dotnet publish src/F3M.Server -c Release -o ./publish
```

Run `publish/F3M.Server.dll` with the ASP.NET Core 10 runtime. Set `Catalog__KeyPassword`, `Admin__UserName` and `Admin__Password` (optionally `Admin__OverwritePassword` and `F95Bot__*`) as environment variables.

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
| [src/F3M.Server/Import/README.md](src/F3M.Server/Import/README.md) | Importing community mods: the admin page, the import plan and its fields (optional groups included), ownership of imported mods |
| [Project site](https://flanderdev.github.io/F3M/) | Player guide, FAQ and downloads |

## Contributing

1. Fork the repository.
2. Create a feature branch from `develop`, for example `git checkout -b feature/my-feature develop`.
3. Commit your changes and push the branch.
4. Open a pull request against `develop`.

Please never commit secrets: `.env`, `appsettings.Development.json` with credentials, or anything in `Storage/`.

## License

[GNU Affero General Public License v3.0](LICENSE)
