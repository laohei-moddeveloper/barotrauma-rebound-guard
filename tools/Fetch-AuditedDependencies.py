"""Fetch hash-pinned official LuaCs release builds into a NEW private directory.

For isolated verification only. Never publish these game/runtime binaries.
"""
import argparse
import hashlib
import json
from pathlib import Path, PurePosixPath
import urllib.request
import zipfile

ASSETS = {
    "client": ("luacsforbarotrauma_build_windows_client.zip", 497491268,
               "94c3f499ddcfac884529722addbd3abbd4693c4840335567b3546bf5383e5529",
               "Barotrauma.dll", "70d4f424a0db55b6a4ce4781ba840ef443d15ac7060caf16c2543cdaef5c6e40"),
    "server": ("luacsforbarotrauma_build_windows_server.zip", 497491298,
               "4df2316a1be468ab52af268796eb33d15ecc0a1119cf2318804f2f26ee129630",
               "DedicatedServer.dll", "c29090d937cb9e54e38fd3acad6da3b1bc3e74adce4eb091a6d4f2e54dff22bd"),
}


def fetch(destination):
    destination.mkdir(parents=True, exist_ok=False)
    manifest = []
    for side, (name, asset_id, expected, assembly, assembly_hash) in ASSETS.items():
        url = "https://github.com/evilfactory/LuaCsForBarotrauma/releases/download/latest/" + name
        request = urllib.request.Request(url, headers={"User-Agent": "ReboundGuard-Isolated-Verification"})
        archive = destination / name
        digest = hashlib.sha256()
        with urllib.request.urlopen(request, timeout=300) as response, archive.open("xb") as output:
            while block := response.read(1024 * 1024):
                output.write(block)
                digest.update(block)
        if digest.hexdigest() != expected:
            raise RuntimeError("Official release asset changed; re-audit instead of accepting a new binary: " + name)
        with zipfile.ZipFile(archive) as package:
            for entry in package.infolist():
                path = PurePosixPath(entry.filename.replace("\\", "/"))
                if path.is_absolute() or ".." in path.parts or ":" in entry.filename:
                    raise RuntimeError("Unexpected path in audited archive")
            package.extractall(destination / side)
        actual = hashlib.sha256((destination / side / assembly).read_bytes()).hexdigest()
        if actual != assembly_hash:
            raise RuntimeError("Audited game assembly mismatch: " + assembly)
        manifest.append(dict(side=side, upstream="evilfactory/LuaCsForBarotrauma", asset_id=asset_id,
                             url=url, archive_sha256=expected, assembly=assembly, assembly_sha256=actual))
        print(side + ": official release archive and assembly SHA256 verified", flush=True)
    (destination / "provenance.json").write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--destination", type=Path, required=True)
    fetch(parser.parse_args().destination.resolve())
