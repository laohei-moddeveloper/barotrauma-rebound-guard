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
    "client": ("luacsforbarotrauma_build_windows_client.zip", 681357534,
               "b089eaaea806cd0ea34447b1c36c9f1005c4c62ff13be18f0dfb84a0306de9be",
               "Barotrauma.dll", "ef31a7236dde833382f53df71b76c102b0f2bb3bb2a035dedda85ed5c276822a"),
    "server": ("luacsforbarotrauma_build_windows_server.zip", 681357663,
               "00be1d59a61373686ca4587d7c77f426a43e8af10d42523984a2ae7ebd4d3d94",
               "DedicatedServer.dll", "705a4f5511edb16ce59cc0f5b5a7f14bb3eb1ffda3ab4f3c5edcd49e834d4c16"),
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
