"""Update existing item 3822098302 via Valve SteamCMD; no desktop automation.

Credentials come only from explicitly mapped STEAM_* environment variables.
Never creates an item or changes either localized title/description.
"""
import argparse
import base64
import hashlib
import hmac
import html
import json
import os
from pathlib import Path
import re
import struct
import subprocess
import time
import urllib.parse
import urllib.request
import xml.etree.ElementTree as ET

ITEM = "3822098302"
APP = "602960"
OWNER = "76561199532676984"


def details():
    payload = urllib.parse.urlencode({"itemcount": 1, "publishedfileids[0]": ITEM}).encode()
    request = urllib.request.Request("https://api.steampowered.com/ISteamRemoteStorage/GetPublishedFileDetails/v1/", data=payload)
    with urllib.request.urlopen(request, timeout=30) as response:
        item = json.load(response)["response"]["publishedfiledetails"][0]
    if item.get("result") != 1 or str(item.get("consumer_app_id")) != APP or str(item.get("creator")) != OWNER:
        raise RuntimeError("Existing item identity/app/creator verification failed; no upload")
    return item


def titles():
    result = {}
    for language in ("schinese", "english"):
        url = f"https://steamcommunity.com/sharedfiles/filedetails/?id={ITEM}&l={language}"
        with urllib.request.urlopen(url, timeout=30) as response:
            page = response.read().decode("utf-8", "replace")
        match = re.search(r'<div[^>]*class="workshopItemTitle"[^>]*>(.*?)</div>', page, re.S)
        if not match:
            raise RuntimeError("Could not read localized Workshop title: " + language)
        result[language] = html.unescape(re.sub("<[^>]+>", "", match.group(1))).strip()
    return result


def vdf(value):
    return '"' + str(value).replace("\\", "\\\\").replace('"', '\\"').replace("\n", "\\n") + '"'


def guard_code(secret):
    digest = hmac.new(base64.b64decode(secret), struct.pack(">Q", int(time.time()) // 30), hashlib.sha1).digest()
    offset = digest[-1] & 15
    number = struct.unpack(">I", digest[offset:offset + 4])[0] & 0x7fffffff
    alphabet = "23456789BCDFGHJKMNPQRTVWXY"
    code = ""
    for _ in range(5):
        code += alphabet[number % len(alphabet)]
        number //= len(alphabet)
    return code


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--steamcmd", type=Path, required=True)
    parser.add_argument("--package", type=Path, required=True)
    args = parser.parse_args()
    steamcmd, package = args.steamcmd.resolve(), args.package.resolve()
    if not os.environ.get("STEAM_USERNAME"):
        raise RuntimeError("STEAM_USERNAME/STEAM_USER/STEAM_ACCOUNT_NAME was not mapped; no upload attempted")
    if not os.environ.get("STEAM_PASSWORD") and not os.environ.get("STEAM_CONFIG_VDF"):
        raise RuntimeError("No mapped STEAM_PASSWORD or cached STEAM_CONFIG_VDF; no upload attempted")
    content = ET.parse(package / "filelist.xml").getroot()
    if content.get("steamworkshopid") != ITEM or content.get("modversion") != "0.4.1":
        raise RuntimeError("Package identity/version mismatch")
    for path in package.rglob("*"):
        if path.is_file() and path.suffix.lower() in {".dll", ".exe", ".pdb", ".save", ".sub", ".log", ".nettrace", ".key", ".pem"}:
            raise RuntimeError("Forbidden binary/private file in source package")
    manifest = json.loads((package / "PUBLIC-PACKAGE.json").read_text(encoding="utf-8-sig"))
    for entry in manifest["files"]:
        relative = Path(entry["path"])
        if relative.is_absolute() or ".." in relative.parts:
            raise RuntimeError("Invalid manifest path")
        if hashlib.sha256((package / relative).read_bytes()).hexdigest().lower() != entry["sha256"].lower():
            raise RuntimeError("Workshop package manifest mismatch: " + str(relative))
    before, localized = details(), titles()
    # Complete bootstrap updates before generating a short-lived Steam Guard code.
    try:
        bootstrap = subprocess.run([str(steamcmd), "+quit"], cwd=steamcmd.parent,
                                   stdout=subprocess.PIPE, stderr=subprocess.STDOUT, timeout=240)
    except subprocess.TimeoutExpired:
        raise RuntimeError("SteamCMD bootstrap timed out before authentication") from None
    if bootstrap.returncode:
        raise RuntimeError("Valve SteamCMD bootstrap failed before authentication")
    cached = os.environ.get("STEAM_CONFIG_VDF")
    if cached:
        configuration = cached if cached.lstrip('\ufeff \t\r\n').startswith('"') else base64.b64decode(cached).decode("utf-8")
        folder = steamcmd.parent / "config"
        folder.mkdir(exist_ok=True)
        (folder / "config.vdf").write_text(configuration, encoding="utf-8")
        (folder / "config.vdf").chmod(0o600)
    # Omit title/description keys: uploading content must not overwrite either
    # localized entry with an assumed SteamCMD language setting.
    upload = steamcmd.parent / "rebound-update.vdf"
    entries = {"appid": APP, "publishedfileid": ITEM, "contentfolder": package, "previewfile": package / "preview.jpg",
               "visibility": "0", "changenote": "0.4.1: audited Barotrauma 1.13.5.0 support; cached native-compatible LuaCs argument conversion and independent feature fallback. No whole-game 10x claim. See GitHub release for measurements."}
    upload.write_text('"workshopitem"\n{\n' + "\n".join(vdf(key) + " " + vdf(value) for key, value in entries.items()) + "\n}\n", encoding="utf-8")
    command = [str(steamcmd), "+login", os.environ["STEAM_USERNAME"]]
    if os.environ.get("STEAM_PASSWORD"):
        command.append(os.environ["STEAM_PASSWORD"])
    code = os.environ.get("STEAM_GUARD_CODE")
    if not code and os.environ.get("STEAM_SHARED_SECRET"):
        code = guard_code(os.environ["STEAM_SHARED_SECRET"])
        if os.environ.get("GITHUB_ACTIONS"):
            print("::add-mask::" + code, flush=True)
    if code:
        command.append(code)
    command += ["+workshop_build_item", str(upload), "+workshop_download_item", APP, ITEM, "validate", "+quit"]
    # Valve enforces ownership again at UpdatePublishedFile. The fixed nonzero
    # publishedfileid prevents accidental item creation under another account.
    try:
        result = subprocess.run(command, cwd=steamcmd.parent, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True, timeout=420)
    except subprocess.TimeoutExpired:
        raise RuntimeError("SteamCMD authentication/upload timed out; no credential-bearing command is logged") from None
    log = result.stdout
    # Never print the command (which contains credentials), configuration or logs
    # wholesale. Emit only specific non-sensitive verification outcomes.
    if result.returncode or not re.search(r"Success.*(?:Published|Update|item)|Published File Updated", log, re.I):
        reason = "SteamGuard/authentication or SteamCMD upload failed; inspect account/session configuration without exposing credentials"
        raise RuntimeError(reason)
    downloaded = steamcmd.parent / "steamapps" / "workshop" / "content" / APP / ITEM
    if not (downloaded / "filelist.xml").is_file():
        raise RuntimeError("Upload command returned, but published content could not be downloaded for verification")
    for entry in manifest["files"]:
        path = downloaded / entry["path"]
        if not path.is_file() or hashlib.sha256(path.read_bytes()).hexdigest().lower() != entry["sha256"].lower():
            raise RuntimeError("Published content differs from verified package: " + entry["path"])
    after = details()
    if after["time_updated"] < before["time_updated"] or titles() != localized:
        raise RuntimeError("Post-upload timestamp/localization verification failed")
    print(json.dumps({"item": ITEM, "version": "0.4.1", "published_content_hashes_match": True, "localized_titles_preserved": localized,
                      "time_updated": after["time_updated"]}, ensure_ascii=False))


if __name__ == "__main__":
    main()
