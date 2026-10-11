"""
Downloads the attachments named in plan.py into modFiles/<mod key>/files/<version>/ and modFiles/<mod key>/preview.*.

    python -I tools/download.py            # from the import folder; files already on disk are skipped

The files are only stored, never run. A plan file with a "member" is a .zip of which only that one file is kept.
"""
import io
import json
import pathlib
import re
import sys
import time
import urllib.request
import zipfile

sys.path.insert(0, str(pathlib.Path(__file__).parent))
from plan import A, FRAMEWORKS, PLAN, SKIPPED  # noqa: E402

ROOT = pathlib.Path(__file__).resolve().parent.parent
MODS = ROOT / "modFiles"
USER_AGENT = "Mozilla/5.0 (F3M import)"


def mod_keys():
    """Plan key -> mod key from f95-mod-list.json."""
    doc = json.loads((ROOT / "f95-mod-list.json").read_text(encoding="utf-8"))
    by_source = {}
    for mod in doc["mods"]:
        src = mod["source"]
        ident = str(src["postId"] if src["kind"] == "post" else src["threadId"])
        by_source.setdefault(ident, []).append(mod["key"])

    resolved, problems = {}, []
    for plan_key in list(PLAN) + list(SKIPPED):
        ident, _, part = plan_key.partition("/")
        keys = by_source.get(ident, [])
        matches = [k for k in keys if not part or k.endswith("-" + part) or ("-" + part + "-") in k]
        if len(matches) != 1:
            problems.append(f"{plan_key}: matches {matches or 'nothing'}")
        else:
            resolved[plan_key] = matches[0]

    covered = set(resolved.values())
    for keys in by_source.values():
        for key in keys:
            if key not in covered:
                problems.append(f"{key}: neither planned nor skipped")
    return resolved, problems


def spec(file):
    """A plan file as a dict: an attachment path, or a dict with path/url/name/root/install."""
    return file if isinstance(file, dict) else {"path": file}


def file_name(file):
    """'2026/01/5669464_Anal_Plug_Animation_1.0.zip' -> 'Anal_Plug_Animation_1.0.zip'; a dict's own name if it has one."""
    file = spec(file)
    if file.get("name"):
        return file["name"]
    name = file["path"].rsplit("/", 1)[-1]
    return re.sub(r"^\d+_", "", name).lstrip("_") or name


def source_url(file):
    file = spec(file)
    return file.get("url") or A + file["path"]


def download(file, target):
    if target.exists() and target.stat().st_size > 0:
        return False
    target.parent.mkdir(parents=True, exist_ok=True)
    request = urllib.request.Request(source_url(file), headers={"User-Agent": USER_AGENT})
    with urllib.request.urlopen(request, timeout=120) as response:
        data = response.read()
    if member := spec(file).get("member"):
        with zipfile.ZipFile(io.BytesIO(data)) as archive:
            data = archive.read(member)
    target.write_bytes(data)
    time.sleep(0.5)
    return True


def main():
    resolved, problems = mod_keys()
    if problems:
        print("Plan does not match the mod list:\n  " + "\n  ".join(problems))
        sys.exit(1)

    jobs = []
    for plan_key, entry in PLAN.items():
        jobs.append((resolved[plan_key], entry))
    for key, entry in FRAMEWORKS.items():
        jobs.append((key, entry))

    fetched = skipped = 0
    for key, entry in jobs:
        folder = MODS / key
        for version, files in entry["versions"]:
            for file in files:
                if download(file, folder / "files" / version / file_name(file)):
                    fetched += 1
                else:
                    skipped += 1
        if preview := entry.get("preview"):
            suffix = pathlib.PurePosixPath(preview).suffix.lower()
            if download(preview, folder / f"preview{suffix}"):
                fetched += 1
            else:
                skipped += 1
        print(f"{key}: ok", flush=True)

    print(f"done: {fetched} downloaded, {skipped} already there")


if __name__ == "__main__":
    main()
