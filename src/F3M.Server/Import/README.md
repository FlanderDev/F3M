# F95zone import

The real community mods from F95zone, imported into F3M as **unclaimed** mods. Each mod goes to the F95zone user who posted it as soon as they sign in.

The import runs on the server, from an admin page: **Admin Panel → F95zone import** (`/admin/import`). Everything it needs is in one file, the **import plan**:

`import-plan.json` (next to this file) lists every mod to import, with its versions, files, owner and dependencies, plus the mods that are left out and why. It ships inside the server. It lives in the server project because only `src/` goes into the Docker build.

## Importing from the admin page

Sign in with an admin account and open **Admin Panel → F95zone import**. Each step only runs when you start it. It keeps running on the server if you leave the page, and the page shows its progress, the current item and a log.

1. **Download the files.** This downloads every file of the plan that is not on the server yet, plus the preview images.
   - Downloads are spaced half a second apart.
   - Files only come from `attachments.f95zone.to`, `gitgud.io` and `codeload.github.com`.
   - Where the plan names an archive folder, the step checks that the archive has that folder.
   - A failed download is listed under **Problems**. Starting the step again retries only what is missing.
2. **Import the mods.**
   - This imports everything that is not fully on the site: dependencies first, missing versions only, oldest version first, each with the date it appeared on F95zone.
   - Each version goes through the same checks, file inspection and catalog publishing as the upload form.
   - A mod counts as imported when a mod with the same name and author credit exists.
3. **Review the held-back mods.**
   - The server checks each file against its type (`ModTemplates`): readmes in the game folder, a mission folder without `.code` files, BepInEx itself bundled, and so on.
   - A mod with warnings is **held back**: nothing is stored, and it's listed with its warnings.
   - Tick the ones that are fine as they are, then click **Accept & import**.
   - If a warning shows the plan is wrong, fix the plan instead.
   - Files in an optional group are not checked: they are not installed unless a player turns the group on.
   - With the shipped plan, three mods are held back, all for harmless reasons (a folder of screenshot notes, a texture next to the missions, a notes file next to a mission).

**Storage:**

- Everything the import keeps is in `Storage/Import/`:
  - `import-plan.json`: the plan in use;
  - `files/<mod key>/<version>/<file>` and `files/<mod key>/preview.*`: the downloads (about 110 MB);
  - `state.json`: the last problem per mod.
- Downloaded files are never run or extracted. The server only lists archives and hashes files, as it does for uploads.

**Changing the plan:**

- **Download JSON** saves the plan in use.
- **Replace…** uploads an edited plan. It is checked first, and refused with the list of problems if anything is wrong.
- **Use the shipped plan** goes back to the plan shipped with the server.
- To change the plan for everyone, edit `src/F3M.Server/Import/import-plan.json` and deploy. A server that still uses the shipped plan picks up the new one on its next start. A server that uses an uploaded plan keeps it until someone clicks **Use the shipped plan**.

## The import plan

```jsonc
{
  "about": "…",
  "source": { … },                        // where the community list came from (informational)
  "mods": [
    {
      "key": "post-18562687-the-price-of-losing-control",   // stable id; also the download folder name
      "name": "The Price of Losing Control",
      "author": "zizheng2019",            // the credit shown on the mod
      "category": "Custom-Missions 2",     // one of Configuration.DefaultCategories
      "description": "…",                  // Markdown, at most 10,000 characters
      "sourceUrl": "https://f95zone.to/posts/18562687/",
      "postedAt": "2025-11-06T02:36:58Z",  // fallback date for versions without uploadedAt
      "f95Owner": { "userId": "9909841", "name": "zizheng2019" },     // who posted the files: the mod goes to this F95 account
      "preview": "https://attachments.f95zone.to/….png",    // optional preview image (.jpg/.png/.webp)
      "dependencies": ["framework-custom-missions-v2"],     // keys of mods this one needs
      "note": "Only the mission folder is installed, …",    // optional, only for people reading the plan
      "optionalGroups": [                  // optional: parts of every version the mod does not need
        { "name": "Example missions", "patterns": ["CustomMissions2/**"] }
      ],
      "versions": [                        // oldest first
        {
          "version": "0.11",
          "uploadedAt": "2025-11-06T02:36:58Z",
          "files": [
            {
              "url": "https://attachments.f95zone.to/2025/11/5440245_The_Price_of_Losing_Control_V0.11.zip",
              "name": "The_Price_of_Losing_Control_V0.11.zip",   // the name it is stored and offered under
              "archiveRoot": "The Price of Losing Control V0.11/The Price of Losing Control", // optional
              "installPath": "CustomMissions2/The Price of Losing Control",                    // optional
              // "member": "…",                // optional, .zip only: keep just this file of the archive
              "template": "auto"               // a mod type id, or "auto"
            }
          ]
        }
      ]
    }
  ],
  "skipped": [ { "key": "…", "name": "…", "url": "…", "reason": "…" } ]  // listed mods that are left out on purpose
}
```

**Rules:**

- **`archiveRoot`:** only that folder of the archive is installed, without the folder itself. It handles:
  - archives packed with an extra top folder;
  - archives that carry more than the mod (a modpack, a repository).
- **`template`:** one of `bepinex-plugin`, `custom-missions-v1`, `custom-missions-v2`, `cosplay`, `texture-pack` or `custom`.
  - `auto` lets the server recognise the type from the archive.
  - With an empty `installPath`, `auto` also works out the install path and the archive folder (inside `archiveRoot`, if one is given), with the same rules the upload form uses for its suggestions.
  - The two Custom Missions frameworks use `custom`: they are a plugin plus example missions.
- **`member`:** keeps one file of a downloaded `.zip` and stores it as `name`, e.g. Texture Replacer's IL2CPP DLL.
- **Keys and names:** `key`, `version` and file `name` must be plain file names. Within their scope they must be unique: keys across the plan, versions within a mod, file names within a version.
- **Dependencies:** they must name keys in the plan, without loops.
- **`optionalGroups`:** named parts of the mod that players don't need, such as example missions.
  - Patterns use the generated-file syntax and are matched against the installed game paths: `*` within a name, `?` one character, `**` any number of folders.
  - F3M Desktop leaves them out unless a player turns the group on in the Library.
  - Readmes and licences (a **Documentation** group) and folders named Examples, Samples, Tutorials or Demos (an **Examples** group) are found on import without being listed. Groups listed here come first.
  - The frameworks use this for the example missions they ship (`CustomMissions/**`, `CustomMissions2/**`).
- **Mods not on the community list:** the Custom Missions frameworks (`framework-custom-missions-v1`/`-v2`) and Texture Replacer (`tool-texture-replacer`) are in the plan as extra mods, because the missions and texture packs need them.

**Who owns an imported mod:**

- If the F95 user in `f95Owner` already has an F3M account (linked F95 id), the mod is theirs right away.
- Otherwise it is **unclaimed**:
  - It goes to them the next time they sign in. Matching is by F95 user id, never by name.
  - The mod page shows "Imported from F95zone · not yet claimed" with a link to the original post, and a "This is mine" button.
- Once theirs, they can edit it, upload new versions, or remove the whole mod.
- Admins can give any mod to another user, or make it unclaimed again, from the "Owner (admin)" box on the mod page.
