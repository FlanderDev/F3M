"""
Writes modFiles/<mod key>/import.json for every downloaded mod, and IMPORT-REPORT.md with what was left out.

    python -I tools/build_manifests.py      # from the import folder, after tools/download.py

Where a file goes and what type it is (a mission, a plugin, ...) is decided by the server when it is imported: the
manifest sends type "auto" and no install path, and the server reads the archive with the same rules the upload form
uses (ModTemplates). Only what the plan sets itself is written: an install path, an archive folder, a type.
Archives are listed with 7-Zip (nothing is extracted) to check that an archive folder from the plan exists.
"""
import datetime
import json
import pathlib
import re
import subprocess
import sys

sys.path.insert(0, str(pathlib.Path(__file__).parent))
from download import file_name, mod_keys, spec  # noqa: E402
from plan import FRAMEWORKS, PLAN, SKIPPED  # noqa: E402

ROOT = pathlib.Path(__file__).resolve().parent.parent
MODS = ROOT / "modFiles"
SEVEN_ZIP = r"C:\Program Files\7-Zip\7z.exe"

AUTO = "auto"  # the server recognises the file's type, install path and archive folder


def list_archive(path):
    """Paths of the files in an archive, '/' separated, without folders."""
    out = subprocess.run([SEVEN_ZIP, "l", "-slt", "-ba", "-sccUTF-8", str(path)],
                         capture_output=True, check=False).stdout.decode("utf-8", "replace")
    files, current = [], {}
    for line in out.splitlines() + [""]:
        if not line.strip():
            if "Path" in current and current.get("Folder") != "+" and "D" not in current.get("Attributes", "")[:1]:
                files.append(current["Path"].replace("\\", "/"))
            current = {}
            continue
        key, _, value = line.partition(" = ")
        current[key.strip()] = value
    return files


def version_date(posted, file, earlier):
    """When a version appeared: the month its F95 attachment was uploaded (from its path), never before the post or an
    earlier version. Files from elsewhere count from the post."""
    candidates = [posted or ""]
    if path := spec(file).get("path"):
        year, month = path.split("/")[:2]
        candidates.append(f"{year}-{month}-01T12:00:00")
    if earlier:
        last = datetime.datetime.fromisoformat(earlier[-1]["uploadedAt"].rstrip("Z"))
        candidates.append((last + datetime.timedelta(minutes=1)).isoformat())
    return max(candidates) + "Z"


def describe(mod, entry):
    lines = []
    if mod.get("note"):
        lines.append(mod["note"])
    if mod.get("nameHint"):
        lines.append(f"({mod['nameHint']})")
    authors = mod.get("authorsRaw")
    if authors:
        lines.append(f"Authors: {authors}.")
    if mod.get("tag"):
        lines.append(f"Type in the list: {mod['tag']}.")
    lines.append(f"Source: {entry.get('sourceUrl') or mod['source']['url']}")
    if mod.get("obsolete"):
        lines.append("Marked as outdated in the community list.")
    return "\n".join(lines)


def main():
    resolved, problems = mod_keys()
    if problems:
        sys.exit("Plan does not match the mod list; run tools/download.py first.")

    doc = json.loads((ROOT / "f95-mod-list.json").read_text(encoding="utf-8"))
    mods = {m["key"]: m for m in doc["mods"]}
    post_dates = json.loads((ROOT / "tools" / "posts.json").read_text(encoding="utf-8"))
    # Who posted each post or thread on F95zone: [user id, name]. The mod goes to that account.
    posters = json.loads((ROOT / "tools" / "posters.json").read_text(encoding="utf-8"))
    by_post_number = {m["source"].get("postNumber"): m["key"] for m in doc["mods"] if m["source"]["kind"] == "post"}

    report_ok, report_failed = [], []
    for plan_key, entry in list(PLAN.items()) + [(k, v) for k, v in FRAMEWORKS.items()]:
        key = resolved.get(plan_key, plan_key)
        folder = MODS / key
        mod = mods.get(key)
        name = mod["name"] if mod else entry["name"]
        name = entry.get("displayName") or name or (mod or {}).get("nameHint", key).capitalize()
        category = entry.get("category") or (mod["suggestedCategory"] if mod else entry["category"])
        source = mod["source"] if mod else None
        source_id = str(source.get("postId") or source["threadId"]) if source else entry.get("postId", "17601844")
        posted = post_dates.get(source_id)
        poster = posters.get(source_id)

        versions, problem = [], None
        for version, plan_files in entry["versions"]:
            files = []
            for plan_file in plan_files:
                file = spec(plan_file)
                fname = file_name(file)
                local = folder / "files" / version / fname
                if not local.exists():
                    problem = f"'{fname}' was not downloaded."
                    break
                root = file.get("root", "")
                if root:
                    # Only that folder of the archive is installed, without the folder itself.
                    if local.suffix.lower() not in {".zip", ".rar", ".7z"}:
                        problem = f"Version {version}: '{fname}' is not an archive, so it has no folder '{root}'."
                        break
                    prefix = root.strip("/").lower() + "/"
                    if not any(p.lower().startswith(prefix) for p in list_archive(local)):
                        problem = f"Version {version}: '{fname}' has nothing inside '{root}'."
                        break
                entry_file = {"path": f"files/{version}/{fname}", "installPath": file.get("install", ""),
                              "template": file.get("template", entry.get("template", AUTO))}
                if root:
                    entry_file["archiveRoot"] = root
                files.append(entry_file)
            if problem:
                break
            versions.append({"version": version, "uploadedAt": version_date(posted, plan_files[0], versions), "files": files})

        if problem:
            report_failed.append((key, name, problem))
            (folder / "import.json").unlink(missing_ok=True)
            continue

        dependencies = []
        if mod:
            # The plan corrects the list where it names the wrong framework.
            frameworks = entry.get("frameworks", mod.get("frameworks", []))
            needs_framework = "frameworks" in entry or mod["type"] != "plugin"
            if "v2" in frameworks and needs_framework:
                dependencies.append("framework-custom-missions-v2")
            if "v1" in frameworks and needs_framework:
                dependencies.append("framework-custom-missions-v1")
            note = mod.get("note") or ""
            for number in mod.get("relatedPosts", []):
                if re.search(rf"Requires[^#]*#{number}\b", note) and by_post_number.get(number):
                    dependencies.append(by_post_number[number])
            if "Requires NPC Mod Behaviour" in note:
                dependencies.append(next(k for k, m in mods.items() if m["name"] == "NPC Mod Behaviour"))
        # Plan keys of listed mods, or the keys of the extra mods in FRAMEWORKS.
        extra = [resolved.get(d, d) for d in entry.get("dependencies", [])]
        dependencies += [d for d in extra if d not in dependencies]

        preview = next((p.name for p in folder.glob("preview.*")), None)
        manifest = {
            "key": key,
            "name": name,
            "author": ", ".join(mod["authors"]) if mod and mod["authors"] else (mod or entry).get("authorsRaw") or entry.get("author"),
            "category": category,
            "description": describe(mod, entry) if mod else f"{entry['description']}\nSource: {entry['source']}",
            "uploadedAt": posted + "Z" if posted else None,
            "sourceUrl": entry.get("sourceUrl") or (source["url"] if source else entry["source"]),
            "f95Owner": {"userId": poster[0], "name": poster[1]} if poster else None,
            "preview": preview,
            "dependencies": dependencies,
            "versions": versions,
        }
        if entry.get("note"):
            manifest["importNote"] = entry["note"]
        (folder / "import.json").write_text(json.dumps(manifest, ensure_ascii=False, indent=2) + "\n", encoding="utf-8", newline="\n")
        report_ok.append((key, name, len(versions), entry.get("note")))

    write_report(resolved, mods, report_ok, report_failed)
    print(f"{len(report_ok)} manifests written, {len(report_failed)} left out, {len(SKIPPED)} skipped before download")


def write_report(resolved, mods, ok, failed):
    lines = [
        "# Import report",
        "",
        "Generated by `tools/build_manifests.py`. Mods that are not imported yet, and what was left out of the imported ones.",
        "",
        "The import plan (`src/F3M.Server/Import/import-plan.json`) now holds the same skips and notes, and the admin page (Admin Panel → F95zone import) lists them under \"Left out by the plan\". This report comes from the old scripts and does not follow edits to the plan.",
        "",
        f"- Ready to import: **{len(ok)}** mods (`modFiles/<key>/import.json`)",
        f"- Downloaded, but the archive layout needs a decision: **{len(failed)}**",
        f"- No matching files on F95zone: **{len(SKIPPED)}**",
        "",
        "## Downloaded, not importable yet",
        "",
        "| Mod | Folder | Problem |",
        "|---|---|---|",
    ]
    lines += [f"| {name} | `{key}` | {problem} |" for key, name, problem in sorted(failed)]
    lines += ["", "## No matching files", "", "| Mod | Key | Reason |", "|---|---|---|"]
    for plan_key, reason in sorted(SKIPPED.items()):
        key = resolved[plan_key]
        mod = mods[key]
        lines.append(f"| [{mod['name'] or mod.get('nameHint')}]({mod['source']['url']}) | `{key}` | {reason} |")
    lines += ["", "## Imported, with files left out", "", "| Mod | Folder | Note |", "|---|---|---|"]
    lines += [f"| {name} | `{key}` | {note} |" for key, name, _, note in sorted(ok) if note]
    (ROOT / "IMPORT-REPORT.md").write_text("\n".join(lines) + "\n", encoding="utf-8", newline="\n")


if __name__ == "__main__":
    main()
