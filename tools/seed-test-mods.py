#!/usr/bin/env python3
"""Fills a LOCAL F3M database with fake mods, to test the browse page (filters, infinite scroll).

    tools/seed-test-mods.py --docker [CONTAINER]     # the docker compose setup (the f3m_Storage volume)
    tools/seed-test-mods.py [STORAGE_DIR]            # a plain Storage folder (default: src/F3M.Server/Storage)

    --count N   how many mods to add (default 400)
    --reset     remove the seeded mods first; with --count 0 only remove them

Start the site once first, so it has created its database. Every seeded mod is marked "[seed]" in its description, so
--reset removes exactly those. Never run this against a real site's data. Needs only the Python standard library.

How it is organised:
    PreviewImages    writes small generated PNGs, used as the mods' preview images
    FakeMods         makes up names, descriptions, dates and download counts
    SeedDatabase     the SQLite part: remove seeded mods, add tags, add mods
    DockerStorage    gives the Storage folder of a docker container as a local folder, and puts the changes back
"""
import argparse
import random
import sqlite3
import struct
import subprocess
import tempfile
import zlib
from collections.abc import Sequence
from contextlib import contextmanager
from datetime import UTC, datetime, timedelta
from pathlib import Path

MARK = "[seed]"  # starts the description of every seeded mod
DEFAULT_STORAGE = Path(__file__).resolve().parent.parent / "src/F3M.Server/Storage"

# Ordinary tags (not the mod types, which the server creates itself) added next to the mods.
ORDINARY_TAGS = {
    "Quality of life": "Small improvements that make the game nicer to play.",
    "Graphics": "Changes how the game looks.",
    "Gameplay": "Changes how the game plays.",
    "UI": "Menus and interface.",
    "Audio": "Sounds and music.",
    "Cheats": "Shortcuts for the impatient.",
    "Characters": "New or changed characters.",
    "Tools": "Helpers for modders and players.",
}


class PreviewImages:
    """Small generated gradient PNGs in Storage/PublicContent/Images, so the cards have pictures."""

    PREFIX = "seed-preview-"
    COUNT = 16

    def __init__(self, storage: Path):
        self.folder = storage / "PublicContent" / "Images"

    @classmethod
    def name(cls, number: int) -> str:
        return f"{cls.PREFIX}{number:02}.png"

    def create_all(self):
        """Writes the images that don't exist yet."""
        self.folder.mkdir(parents=True, exist_ok=True)
        for number in range(self.COUNT):
            target = self.folder / self.name(number)
            if not target.exists():
                target.write_bytes(self._gradient_png(number))

    @staticmethod
    def _gradient_png(seed: int, width=320, height=180) -> bytes:
        """A vertical gradient between two random colors, as PNG bytes (no imaging library needed)."""
        rnd = random.Random(seed)
        top = [rnd.randrange(40, 220) for _ in range(3)]
        bottom = [rnd.randrange(40, 220) for _ in range(3)]

        pixels = bytearray()
        for y in range(height):
            pixels.append(0)  # PNG filter type "none" for this row
            row = [int(top[i] + (bottom[i] - top[i]) * y / (height - 1)) for i in range(3)]
            for x in range(width):
                darker = 1.0 - 0.25 * x / width  # a little shading to the right
                pixels += bytes(int(v * darker) for v in row)

        def chunk(kind: bytes, data: bytes) -> bytes:
            body = kind + data
            return struct.pack(">I", len(data)) + body + struct.pack(">I", zlib.crc32(body))

        return (b"\x89PNG\r\n\x1a\n"
                + chunk(b"IHDR", struct.pack(">IIBBBBB", width, height, 8, 2, 0, 0, 0))
                + chunk(b"IDAT", zlib.compress(bytes(pixels), 6))
                + chunk(b"IEND", b""))


class FakeMods:
    """Makes up the data of one mod after the other. The same seed gives the same mods."""

    FIRST_WORDS = ("Crimson", "Silent", "Golden", "Rapid", "Hidden", "Lucky", "Neon", "Velvet", "Iron", "Soft", "Wild",
                   "Tiny", "Grand", "Misty", "Electric", "Lazy", "Brave", "Cosmic", "Sunny", "Midnight")
    SECOND_WORDS = ("Fox", "Harbor", "Garden", "Engine", "Lantern", "Canvas", "Compass", "Signal", "Bridge", "Orchid",
                    "Ember", "Mirror", "Tide", "Anchor", "Spark", "Meadow", "Cipher", "Pixel", "Falcon", "Ribbon")
    AUTHORS = ("Aria", "Bolt", "Cinder", "Dune", "Echo", "Fable", "Gale", "Haze", "Iris", "Juno", "Kite", "Lumen")
    PHRASES = ("A small mod that", "Adds", "tweaks", "the way", "things look", "and feel,", "with a few options",
               "for everyone.", "Works well with", "other mods", "and needs no setup.")

    def __init__(self, seed=2026):
        self.rnd = random.Random(seed)
        self.used_names = set()

    def name(self) -> str:
        """A name nobody got yet; a number is added once all word pairs are used."""
        pairs = len(self.FIRST_WORDS) * len(self.SECOND_WORDS)
        while True:
            name = f"{self.rnd.choice(self.FIRST_WORDS)} {self.rnd.choice(self.SECOND_WORDS)}"
            if len(self.used_names) >= pairs:
                name = f"{name} {len(self.used_names)}"
            if name not in self.used_names:
                self.used_names.add(name)
                return name

    def author(self) -> str:
        return self.rnd.choice(self.AUTHORS)

    def description(self) -> str:
        words = [self.rnd.choice(self.PHRASES) for _ in range(self.rnd.randrange(8, 22))]
        return f"{MARK} " + " ".join(words)

    def version(self) -> str:
        return f"{self.rnd.randrange(1, 4)}.{self.rnd.randrange(0, 10)}.{self.rnd.randrange(0, 10)}"

    def uploaded_at(self, now: datetime) -> datetime:
        """Somewhere within the last 400 days."""
        return now - timedelta(days=self.rnd.randrange(0, 400), minutes=self.rnd.randrange(0, 1440))

    def download_count(self) -> int:
        """Few mods have many downloads, most have few."""
        return int(self.rnd.paretovariate(1.2) * 20)

    def preview_image(self) -> str | None:
        """Most mods have a preview image, some don't (the card then shows its placeholder)."""
        return PreviewImages.name(self.rnd.randrange(PreviewImages.COUNT)) if self.rnd.random() < 0.85 else None

    def pick(self, items: Sequence, how_many: int | None = None):
        """One item, or `how_many` different ones."""
        return self.rnd.choice(items) if how_many is None else self.rnd.sample(items, how_many)

    def tag_count(self) -> int:
        return self.rnd.randrange(1, 4)


class SeedDatabase:
    """The F3M SQLite database: removes seeded mods and adds new ones."""

    def __init__(self, storage: Path):
        path = storage / "Database" / "F3M.db"
        if not path.exists():
            raise SystemExit(f"{path} not found. Start the site once so it creates its database.")

        self.db = sqlite3.connect(path)
        self.db.execute("PRAGMA foreign_keys = ON")
        if not self._scalar("select 1 from sqlite_master where name = 'Mods'"):
            raise SystemExit("The database has no Mods table yet: let the site start once so it migrates it.")

    def _scalar(self, sql: str, *params):
        row = self.db.execute(sql, params).fetchone()
        return row[0] if row else None

    def has_seeded_mods(self) -> bool:
        return self._scalar("select 1 from Mods where Description like ?", MARK + "%") is not None

    def remove_seeded_mods(self) -> int:
        """Deletes the seeded mods with their groups and tag links. Returns how many mods there were."""
        groups = [r[0] for r in self.db.execute(
            "select distinct ModGroupId from Mods where Description like ?", (MARK + "%",))]
        self.db.execute("delete from Mods where Description like ?", (MARK + "%",))
        for group in groups:
            self.db.execute("delete from ModGroupTags where ModGroupId = ?", (group,))
            self.db.execute("delete from ModGroups where Id = ?", (group,))
        self.db.commit()
        return len(groups)

    def type_tag_ids(self) -> list[int]:
        """The mod types (BepInEx plugin, Cosplay Loader, ...), which the site creates at its start."""
        ids = [r[0] for r in self.db.execute("select Id from Tags where TypeId is not null order by Id")]
        if not ids:
            raise SystemExit("The database has no type tags (the site creates them when it starts).")
        return ids

    def ensure_ordinary_tags(self, now: datetime) -> list[int]:
        """Adds the ordinary tags that are missing and returns the ids of all of them."""
        ids = []
        for name, description in ORDINARY_TAGS.items():
            tag_id = self._scalar("select Id from Tags where NormalizedName = ?", name.upper())
            if tag_id is None:
                tag_id = self.db.execute(
                    "insert into Tags (Name, NormalizedName, Description, TypeId, CreatedAt, IsProposed, ProposalScore) "
                    "values (?, ?, ?, null, ?, 0, 0)", (name, name.upper(), description, self._time(now))).lastrowid
            ids.append(tag_id)
        return ids

    def add_mods(self, count: int, fake: FakeMods, now: datetime):
        """Adds `count` mods, each with its own group, one type tag and one to three ordinary tags."""
        type_ids = self.type_tag_ids()
        tag_ids = self.ensure_ordinary_tags(now)

        for _ in range(count):
            # Unclaimed groups (owner -1) need no user account.
            author = fake.author()
            group = self.db.execute("insert into ModGroups (OwnerId, Author) values (-1, ?)", (author,)).lastrowid
            self.db.execute(
                "insert into Mods (ModGroupId, Name, Description, Author, Version, IsLatestVersion, Category, "
                "PreviewImageName, DownloadCount, UploadedAt, IsApproved, UserId) "
                "values (?, ?, ?, ?, ?, 1, 'General', ?, ?, ?, 1, null)",
                (group, fake.name(), fake.description(), author, fake.version(), fake.preview_image(),
                 fake.download_count(), self._time(fake.uploaded_at(now))))

            tags = [fake.pick(type_ids), *fake.pick(tag_ids, fake.tag_count())]
            self.db.executemany("insert into ModGroupTags (ModGroupId, TagId) values (?, ?)", [(group, t) for t in tags])

        self.db.commit()

    def visible_mod_count(self) -> int:
        return int(self._scalar("select count(*) from Mods where IsApproved = 1 and IsLatestVersion = 1") or 0)

    @staticmethod
    def _time(value: datetime) -> str:
        return value.strftime("%Y-%m-%d %H:%M:%S")


class DockerStorage:
    """
    The Storage folder inside a docker container, as a local folder to work on. The container is stopped meanwhile
    (so SQLite has written everything to its file), and started again afterwards if it was running.
    """

    def __init__(self, container: str | None):
        self.container = container or self._find_compose_container()
        self.running = self._docker("inspect", "--format", "{{.State.Running}}", self.container) == "true"
        self.image = self._docker("inspect", "--format", "{{.Config.Image}}", self.container)
        self.owner = self._docker("inspect", "--format", "{{.Config.User}}", self.container) or "0"

    @contextmanager
    def local_copy(self):
        """Yields a temporary folder with the database; the changes made in it go back into the container."""
        print(f"Container {self.container} ({self.image}), running: {self.running}")
        if self.running:
            self._docker("stop", self.container)
        try:
            with tempfile.TemporaryDirectory() as tmp:
                storage = Path(tmp)
                self._docker("cp", f"{self.container}:/app/Storage/Database", str(storage / "Database"))
                yield storage
                self._copy_back(storage)
        finally:
            if self.running:
                self._docker("start", self.container)

    def _copy_back(self, storage: Path):
        self._docker("cp", f"{storage / 'Database'}/.", f"{self.container}:/app/Storage/Database/")
        images = storage / "PublicContent" / "Images"
        if images.exists():
            self._docker("cp", f"{images}/.", f"{self.container}:/app/Storage/PublicContent/Images/")
        # docker cp writes as root, but the site runs as another user and has to own what it writes to.
        self._docker("run", "--rm", "--user", "root", "--entrypoint", "chown", "--volumes-from", self.container,
                     self.image, "-R", f"{self.owner}:{self.owner}", "/app/Storage")

    @staticmethod
    def _docker(*command: str) -> str:
        return subprocess.run(["docker", *command], check=True, text=True, stdout=subprocess.PIPE).stdout.strip()

    @classmethod
    def _find_compose_container(cls) -> str:
        """The container of the compose service "f3m-server"."""
        found = cls._docker("ps", "-a", "--filter", "label=com.docker.compose.service=f3m-server",
                            "--format", "{{.Names}}").split()
        if len(found) != 1:
            raise SystemExit(f"Could not tell which container is the F3M site ({', '.join(found) or 'none found'}). "
                             "Pass its name: --docker NAME (see `docker ps -a`).")
        return found[0]


def seed(storage: Path, count: int, reset: bool):
    """Removes (with reset) and adds the seeded mods in the Storage folder."""
    database = SeedDatabase(storage)

    if reset:
        print(f"Removed {database.remove_seeded_mods()} seeded mods.")
    elif database.has_seeded_mods():
        raise SystemExit("Seeded mods exist already. Use --reset to replace them.")

    if count > 0:
        PreviewImages(storage).create_all()
        database.add_mods(count, FakeMods(), datetime.now(UTC).replace(tzinfo=None))
        print(f"Added {count} mods ({database.visible_mod_count()} shown on the site).")


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("storage", nargs="?", type=Path, default=DEFAULT_STORAGE, help="a plain Storage folder")
    parser.add_argument("--docker", nargs="?", const="", metavar="CONTAINER",
                        help="seed the Storage volume of a docker container (default: the compose service f3m-server)")
    parser.add_argument("--count", type=int, default=400)
    parser.add_argument("--reset", action="store_true")
    args = parser.parse_args()

    if args.docker is None:
        seed(args.storage, args.count, args.reset)
        print("Restart the site if it is running.")
    else:
        with DockerStorage(args.docker or None).local_copy() as storage:
            seed(storage, args.count, args.reset)
        print("Done. Reload the site.")


if __name__ == "__main__":
    main()
