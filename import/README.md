# Debug import

Real mods from the F95zone community list, for filling a local F3M server with test data.

| Path | What it is |
|---|---|
| `f95-mod-list.json` | Every entry of the community list (post #3,706 of the Custom Missions thread), one per mod, with a stable `key`. |
| `modFiles/<key>/` | One folder per mod; `<key>` is the mod's `key` in `f95-mod-list.json`. The two `framework-custom-missions-v*` folders hold the Custom Missions framework itself and `tool-texture-replacer` the Texture Replacer plugin that texture packs need; the list contains neither. |
| `modFiles/<key>/files/<version>/` | The original files of that version, unchanged: F95zone attachments, or for two mods a pinned archive of their gitgud/GitHub repository. |
| `modFiles/<key>/preview.*` | A screenshot from the post, used as the mod's preview image. |
| `modFiles/<key>/import.json` | What `tools/upload.py` sends, including who posted the mod on F95zone: name, author, category, description, dependencies, and per version the files with their type (`template`), install path and archive folder. Type `auto` with an empty install path lets the server work both out. Missing when the mod can't be imported yet. |
| `IMPORT-REPORT.md` | Mods that are not uploaded, and files that were left out of imported ones. |
| `UPLOAD-WARNINGS.md` | Written by `tools/upload.py` when the server held mods back for warnings (see below); removed once nothing is held back. |
| `tools/` | The scripts that produced all of this; `posters.json` and `posts.json` hold who posted each post and when. |

## Importing

Sign in is done by the script with an **admin** account; it works against any build, local or live. From this folder:

```bash
python -I tools/upload.py --server http://localhost:62445
```

- The default account is the debug admin `F3M` that Debug builds create. For another server use `--user <admin>`; the password is read from `F3M_IMPORT_PASSWORD` or asked for.
- `--insecure` accepts the HTTPS development certificate, `--only key1,key2` uploads just those mods (their dependencies must already be on the server), `--dry-run` shows what would be sent without signing in.
- Each version is one multipart `POST api/import/upload` (admin only), after the mods it depends on (the framework first), versions oldest first, each with the date it appeared on F95zone. On the server it goes through the same checks, file inspection and catalog publishing as the upload form.
- Before uploading a mod, the script asks `GET api/import/mod?name=&author=` whether it exists and sends only the missing versions, so it can run again after a failure. It prints `uploaded`, `updated`, `exists`, `held back`, `partial` or `failed` per mod, and exits with 1 if anything was not uploaded.

## Types and warnings

Every file has a **type** (the server's `ModTemplates`: BepInEx plugin, Custom Missions v1/v2, cosplay, texture pack, or custom). Files are sent with type `auto`: the server reads the archive and picks the type, and, where the plan sets no install path, also the install path and archive folder, with the same rules the upload form uses for its suggestions.

The server then checks each file against its type (a mission folder without `.code` files, readmes dropped into the game folder, BepInEx itself bundled, a required framework or loader missing from the dependencies, …). A mod with warnings is **held back**: nothing is stored, and the warnings are listed in `UPLOAD-WARNINGS.md`. Read them, fix the plan where it is wrong, and then store the rest anyway:

```bash
python -I tools/upload.py --server http://localhost:62445 --accept-warnings
```

With the current plan, six mods are held back for harmless reasons (readmes or screenshots in the game folder, an extra notes file next to a mission).

## Who owns an imported mod

Each `import.json` names the F95zone user who **posted** the files (`f95Owner`, from `tools/posters.json`), which is not always the first name in the list's credits (`author`).

- If that F95 user already has an F3M account, the mod is theirs right away.
- Otherwise it is **unclaimed**. It goes to them the next time they sign in (F95 verification or password); matching is by F95 user id, never by name. The mod page shows "Imported from F95zone · not yet claimed" with a link to the original post, and a "This is mine" button.
- Once theirs, they can edit it, upload new versions, or remove the whole mod.
- Admins can give any mod to another user, or make it unclaimed again, from the "Owner (admin)" box on the mod page.

## Rebuilding

```bash
python -I tools/download.py
python -I tools/build_manifests.py
```

`modFiles/` is not in git (about 110 MB), so after a fresh clone run both scripts once before importing.

`tools/plan.py` says which attachments belong to which mod and version, and why the rest were left out; edit it to change what is imported. A file there can name an **archive folder** (`root`): only that folder of the archive is installed, without the folder itself. That handles archives packed with an extra top folder and archives that carry more than the mod (a modpack, a repository); `import.json` then lists it as `archiveRoot` and the server stores it on the file. A file can also set its `install` path and its `template` (type); entries can correct the list's `category` and `frameworks`. `download.py` skips files that are already on disk. `build_manifests.py` checks with 7-Zip (it never extracts anything) that each archive folder exists, and rewrites every `import.json` and `IMPORT-REPORT.md`.

The downloaded files come from forum attachments. Nothing here runs or extracts them; the scripts send them as they are and the server only reads the archives' file lists and hashes.
