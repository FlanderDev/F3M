"""
Uploads every mod under modFiles/ to an F3M server through its admin-only import endpoint.

    python -I tools/upload.py                                  # from the import folder; http://localhost:62445, user F3M
    python -I tools/upload.py --server https://example.org --user someadmin
    python -I tools/upload.py --only post-19194156-hunted,framework-custom-missions-v2
    python -I tools/upload.py --dry-run                        # show what would be sent, without signing in

Signs in with an admin account (password from F3M_IMPORT_PASSWORD, or asked for). Reads modFiles/<key>/import.json
(written by build_manifests.py) and sends each version as one multipart POST api/import/upload, oldest first, after
the mods it depends on. Each mod carries the F95zone user who posted it: the server gives it to that user's account,
or keeps it unclaimed until they sign in through F95. A mod that already exists on the server (same name and author,
GET api/import/mod) only gets the versions it is missing, so the script can run again after a failure.

Files are sent with type "auto" unless the plan names one; the server then recognises the type, install path and
archive folder itself. Files that don't fit their type are held back and listed in UPLOAD-WARNINGS.md; once checked,
run again with --accept-warnings to store them anyway.
"""
import argparse
import getpass
import http.cookiejar
import json
import os
import mimetypes
import pathlib
import ssl
import sys
import urllib.error
import urllib.parse
import urllib.request
import uuid

ROOT = pathlib.Path(__file__).resolve().parent.parent
MODS = ROOT / "modFiles"
WARNINGS_FILE = MODS.parent / "UPLOAD-WARNINGS.md"


class Server:
    def __init__(self, base, insecure):
        self.base = base.rstrip("/")
        context = ssl._create_unverified_context() if insecure else None
        # The sign-in cookie is kept for all later requests.
        self.opener = urllib.request.build_opener(
            urllib.request.HTTPCookieProcessor(http.cookiejar.CookieJar()),
            urllib.request.HTTPSHandler(context=context))

    def login(self, user, password):
        request = urllib.request.Request(
            f"{self.base}/login", method="POST", headers={"Content-Type": "application/json"},
            data=json.dumps({"usernameOrEmail": user, "password": password}).encode())
        try:
            with self.opener.open(request, timeout=60):
                pass
        except urllib.error.HTTPError as error:
            reason = "too many failed attempts, wait a moment" if error.code == 429 else "wrong user name or password"
            raise SystemExit(f"Sign-in as {user} failed ({error.code}): {reason}.") from None

    def find(self, name, author):
        query = urllib.parse.urlencode({"name": name, "author": author})
        try:
            with self.opener.open(f"{self.base}/api/import/mod?{query}", timeout=60) as response:
                return json.load(response)
        except urllib.error.HTTPError as error:
            if error.code == 404:
                return None
            if error.code in (401, 403):
                raise SystemExit(f"The server refused the import ({error.code}): the account must be an admin.") from None
            raise

    def upload(self, fields, files):
        """fields: [(name, value)]; files: [(field, path, file name)]. Returns the group id."""
        boundary = uuid.uuid4().hex
        body = bytearray()
        for name, value in fields:
            body += f'--{boundary}\r\nContent-Disposition: form-data; name="{name}"\r\n\r\n'.encode()
            body += str(value).encode("utf-8") + b"\r\n"
        for field, path, file_name in files:
            mime = mimetypes.guess_type(file_name)[0] or "application/octet-stream"
            quoted = urllib.parse.quote(file_name)
            body += (f'--{boundary}\r\nContent-Disposition: form-data; name="{field}"; filename="{quoted}"; '
                     f"filename*=UTF-8''{quoted}\r\nContent-Type: {mime}\r\n\r\n").encode()
            body += path.read_bytes() + b"\r\n"
        body += f"--{boundary}--\r\n".encode()

        request = urllib.request.Request(f"{self.base}/api/import/upload", data=bytes(body), method="POST",
                                         headers={"Content-Type": f"multipart/form-data; boundary={boundary}"})
        try:
            with self.opener.open(request, timeout=600) as response:
                return int(response.read())
        except urllib.error.HTTPError as error:
            text = error.read().decode("utf-8", "replace").strip()
            if error.code == 422:
                raise WarningsError(json.loads(text).get("warnings", [])) from None
            raise UploadError(f"HTTP {error.code}: {text[:500]}") from None


class UploadError(Exception):
    pass


class WarningsError(Exception):
    """The files don't fit their types; the server stores them only with --accept-warnings."""

    def __init__(self, warnings):
        super().__init__(f"{len(warnings)} warnings")
        self.warnings = warnings


def write_warnings(held_back):
    lines = ["# Held back for warnings", "",
             "The server found that these files don't fit their types. Check them (and fix the plan where it is wrong),",
             "then send them with `--accept-warnings`.", ""]
    for key, name, version, warnings in held_back:
        lines += [f"## {name} {version} (`{key}`)", ""] + [f"- {w}" for w in warnings] + [""]
    WARNINGS_FILE.write_text("\n".join(lines), encoding="utf-8", newline="\n")


def load_manifests():
    manifests = {}
    for path in sorted(MODS.glob("*/import.json")):
        manifest = json.loads(path.read_text(encoding="utf-8"))
        manifests[path.parent.name] = manifest
    return manifests


def dependency_order(manifests):
    ordered, state = [], {}

    def visit(key):
        if key in state or key not in manifests:
            return
        state[key] = False
        for dep in manifests[key].get("dependencies") or []:
            visit(dep)
        state[key] = True
        ordered.append(key)

    for key in sorted(manifests):
        visit(key)
    return ordered


def upload_mod(server, key, manifest, group_ids, dry_run, accept_warnings, held_back):
    """Returns (status, detail). Versions the server holds back for their warnings are added to held_back."""
    deps = manifest.get("dependencies") or []
    missing = [d for d in deps if d not in group_ids]
    if missing:
        return "failed", f"dependencies missing: {', '.join(missing)}"

    existing = None if dry_run else server.find(manifest["name"], manifest["author"])
    group_id = existing["groupId"] if existing else None
    have = set(existing["versions"]) if existing else set()
    folder = MODS / key
    sent = []

    for index, version in enumerate(manifest["versions"]):
        if version["version"] in have:
            continue
        fields = [
            ("Name", manifest["name"]),
            ("Description", manifest["description"]),
            ("Version", version["version"]),
            ("Category", manifest["category"]),
            ("author", manifest["author"]),
        ]
        if uploaded_at := version.get("uploadedAt") or manifest.get("uploadedAt"):
            fields.append(("uploadedAt", uploaded_at))
        if owner := manifest.get("f95Owner"):
            fields += [("f95OwnerUserId", owner["userId"]), ("f95OwnerName", owner["name"])]
        if source := manifest.get("sourceUrl"):
            fields.append(("sourceUrl", source))
        if group_id is not None:
            fields.append(("ModGroupId", group_id))
        fields += [("DependencyGroupIds", group_ids[d]) for d in deps]
        if accept_warnings:
            fields.append(("AcceptWarnings", "true"))

        files = []
        for file in version["files"]:
            path = folder / file["path"]
            files.append(("files", path, path.name))
            fields += [("installPaths", file["installPath"]), ("originalNames", path.name),
                       ("archiveRoots", file.get("archiveRoot", "")), ("templates", file.get("template", "auto"))]
        if manifest.get("preview") and group_id is None:
            preview = folder / manifest["preview"]
            files.append(("previewImage", preview, preview.name))

        if dry_run:
            print(f"  would send {version['version']}: {', '.join(f[2] for f in files)}")
            group_id = group_id or -1
            sent.append(version["version"])
            continue

        try:
            group_id = server.upload(fields, files)
        except WarningsError as error:
            held_back.append((key, manifest["name"], version["version"], error.warnings))
            status = "partial" if sent or have else "held back"
            return status, f"version {version['version']}: {len(error.warnings)} warning(s), see {WARNINGS_FILE.name}"
        except (UploadError, OSError) as error:
            status = "partial" if sent or have else "failed"
            return status, f"version {version['version']}: {error}"
        sent.append(version["version"])

    if group_id is not None:
        group_ids[key] = group_id
    if not sent:
        return "exists", f"group {group_id}"
    return ("updated" if have else "uploaded"), f"group {group_id}, versions {', '.join(sent)}"


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--server", default="http://localhost:62445", help="server base address")
    parser.add_argument("--user", default="F3M", help="admin account to sign in with (default: the debug admin F3M)")
    parser.add_argument("--only", help="comma-separated mod keys (folder names); their dependencies must exist on the server")
    parser.add_argument("--insecure", action="store_true", help="accept the self-signed HTTPS development certificate")
    parser.add_argument("--dry-run", action="store_true", help="print what would be sent")
    parser.add_argument("--accept-warnings", action="store_true",
                        help="store files that don't fit their types too (check the list from a run without it first)")
    args = parser.parse_args()
    sys.stdout.reconfigure(encoding="utf-8")

    manifests = load_manifests()
    if not manifests:
        sys.exit("No modFiles/*/import.json found. Run tools/download.py and tools/build_manifests.py first.")

    server = Server(args.server, args.insecure)
    if not args.dry_run:
        password = os.environ.get("F3M_IMPORT_PASSWORD") or getpass.getpass(f"Password for {args.user}: ")
        server.login(args.user, password)
    wanted = set(args.only.split(",")) if args.only else None
    group_ids, counts, held_back = {}, {}, []
    for key in dependency_order(manifests):
        manifest = manifests[key]
        if wanted is not None and key not in wanted:
            # Still needed as a dependency of a wanted mod: look it up instead of uploading it.
            if args.dry_run:
                group_ids[key] = -1
            elif found := server.find(manifest["name"], manifest["author"]):
                group_ids[key] = found["groupId"]
            continue

        print(f"{key}", flush=True)
        status, detail = upload_mod(server, key, manifest, group_ids, args.dry_run, args.accept_warnings, held_back)
        counts[status] = counts.get(status, 0) + 1
        print(f"  {status}: {detail}", flush=True)

    print("\n" + ", ".join(f"{n} {s}" for s, n in sorted(counts.items())))
    if held_back:
        write_warnings(held_back)
        print(f"{len(held_back)} held back for warnings: see {WARNINGS_FILE}, then run again with --accept-warnings.")
    elif WARNINGS_FILE.exists() and not args.only:
        WARNINGS_FILE.unlink()
    if counts.get("failed") or counts.get("partial") or held_back:
        sys.exit(1)


if __name__ == "__main__":
    main()
