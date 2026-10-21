"""Exercise PowerShell staging with synthetic files; never launch a game."""
import json
import os
import pathlib
import shutil
import subprocess
import tempfile
import unittest


REPO = pathlib.Path(__file__).resolve().parents[1]
PWSH = os.environ.get("REBOUND_TEST_PWSH") or shutil.which("pwsh")
COMMON = (
    "BarotraumaCore.dll", "Publicized/BarotraumaCore.dll",
    "coreclr.dll", "hostfxr.dll", "hostpolicy.dll",
    "System.Private.CoreLib.dll", "Microsoft.CodeAnalysis.dll", "Microsoft.CodeAnalysis.CSharp.dll",
    "Basic.Reference.Assemblies.Net80.dll", "0Harmony.dll", "Farseer.NetStandard.dll",
    "XNATypes.dll", "MoonSharp.Interpreter.dll", "FluentResults.dll",
)
CLIENT = COMMON + ("Barotrauma.dll", "Publicized/Barotrauma.dll", "Barotrauma.exe", "Barotrauma.runtimeconfig.json", "Barotrauma.deps.json", "MonoGame.Framework.Windows.NetStandard.dll")
SERVER = COMMON + ("DedicatedServer.dll", "Publicized/DedicatedServer.dll", "DedicatedServer.exe", "DedicatedServer.runtimeconfig.json", "DedicatedServer.deps.json")


@unittest.skipUnless(PWSH, "PowerShell 7 required (or set REBOUND_TEST_PWSH)")
class OfflineVerificationTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="rebound offline 测试 ")
        self.addCleanup(self.temp.cleanup)
        self.root = pathlib.Path(self.temp.name)
        self.repo, self.game = self.root / "project", self.root / "game directory"
        self.server = self.root / "actual LuaCs server Binary"
        (self.repo / "tools").mkdir(parents=True)
        shutil.copyfile(REPO / "tools/Verify-Offline.ps1", self.repo / "tools/Verify-Offline.ps1")
        files = {
            "LocalMod/ReboundGuard/CSharp/Shared/Source.cs": "// synthetic source",
            "LocalMod/ReboundGuard/CSharp/Shared/excluded.dll": "private binary",
            "tests/Fixture.cs": "// synthetic test",
            "tests/Portable/bin/excluded.dll": "old generated cache",
            "compat/rule.lua": "-- synthetic compatibility source",
            "handoff/private.txt": "private handoff",
            "research/private.log": "private report",
            "build/net8-harness-client/old.dll": "old runtime",
            "tools/Verify.ps1": "throw 'test must not execute this script'",
            "tools/Verify-Deep.ps1": "throw 'test must not execute this script'",
        }
        for relative, text in files.items():
            path = self.repo / relative
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_text(text, encoding="utf-8")
        # Plain fixture bytes deliberately aren't executable or valid DLLs.
        # PrepareOnly checks presence/hashes, never runtime compatibility.
        for directory, required in ((self.game, CLIENT), (self.server, SERVER)):
            for relative in required:
                path = directory / relative
                path.parent.mkdir(parents=True, exist_ok=True)
                path.write_bytes(("synthetic-not-a-game:" + relative).encode())

    def run_script(self, *options):
        return subprocess.run(
            [PWSH, "-NoProfile", "-NonInteractive", "-File", str(self.repo / "tools/Verify-Offline.ps1"),
             "-GameDir", str(self.game), "-ServerGameDir", str(self.server), *options],
            capture_output=True, text=True, timeout=30)

    def manifests(self):
        return list((self.repo / "build").glob("offline-*/manifest.json"))

    def test_prepare_is_explicitly_not_a_pass_or_execution(self):
        before = {path: path.read_bytes() for directory in (self.game, self.server) for path in directory.rglob("*") if path.is_file()}
        result = self.run_script("-PrepareOnly")
        self.assertEqual(result.returncode, 0, result.stderr)
        manifest_path, = self.manifests()
        data = json.loads(manifest_path.read_text(encoding="utf-8-sig"))
        self.assertEqual(data["status"], "prepared_not_tested")
        self.assertEqual(data["checks"], [])
        self.assertTrue(data["inputs_unchanged"])
        self.assertEqual({entry["side"] for entry in data["dependency_sets"]}, {"Client", "Server"})
        # Windows can expand RUNNER~1 into runneradmin. Compare filesystem
        # identity instead of spelling; still require the distinct input roots.
        for directory in (self.game, self.server):
            self.assertEqual(sum(os.path.samefile(entry["game_dir"], directory) for entry in data["dependency_sets"]), 1)
        self.assertEqual(sum(len(entry["hashes"]) for entry in data["dependency_sets"]), len(CLIENT) + len(SERVER))
        self.assertEqual(before, {path: path.read_bytes() for directory in (self.game, self.server) for path in directory.rglob("*") if path.is_file()})
        self.assertFalse(list(manifest_path.parent.rglob("TestRunner*")))

    def test_snapshot_excludes_binaries_handoffs_reports_and_old_caches(self):
        result = self.run_script("-PrepareOnly")
        self.assertEqual(result.returncode, 0, result.stderr)
        manifest_path, = self.manifests()
        snapshot = manifest_path.parent / "workspace"
        paths = {str(path.relative_to(snapshot)).replace(os.sep, "/") for path in snapshot.rglob("*") if path.is_file()}
        self.assertEqual(paths, {"LocalMod/ReboundGuard/CSharp/Shared/Source.cs", "tests/Fixture.cs",
                                 "compat/rule.lua", "tools/Verify.ps1", "tools/Verify-Deep.ps1"})
        self.assertTrue((self.repo / "build/net8-harness-client/old.dll").is_file())

    def test_each_run_has_an_independent_snapshot_and_manifest(self):
        self.assertEqual(self.run_script("-PrepareOnly").returncode, 0)
        first, = self.manifests()
        self.assertEqual(self.run_script("-PrepareOnly").returncode, 0)
        self.assertEqual(len(self.manifests()), 2)
        self.assertIn(first, self.manifests())

    def test_missing_dependencies_stop_before_staging(self):
        (self.server / "Publicized/DedicatedServer.dll").unlink()
        result = self.run_script("-PrepareOnly")
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("Publicized/DedicatedServer.dll", result.stderr)
        self.assertEqual(self.manifests(), [])

    def test_two_sided_run_requires_explicit_server_directory(self):
        result = subprocess.run(
            [PWSH, "-NoProfile", "-NonInteractive", "-File", str(self.repo / "tools/Verify-Offline.ps1"),
             "-GameDir", str(self.game), "-PrepareOnly"], capture_output=True, text=True, timeout=30)
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("ServerGameDir", result.stderr)
        self.assertEqual(self.manifests(), [])

    def test_server_only_accepts_explicit_game_dir_without_client_files(self):
        result = subprocess.run(
            [PWSH, "-NoProfile", "-NonInteractive", "-File", str(self.repo / "tools/Verify-Offline.ps1"),
             "-GameDir", str(self.server), "-Side", "Server", "-PrepareOnly"], capture_output=True, text=True, timeout=30)
        self.assertEqual(result.returncode, 0, result.stderr)
        manifest_path, = self.manifests()
        data = json.loads(manifest_path.read_text(encoding="utf-8-sig"))
        self.assertEqual(len(data["dependency_sets"]), 1)
        self.assertEqual(data["dependency_sets"][0]["side"], "Server")

    @unittest.skipIf(os.name == "nt", "Non-Windows refusal test")
    def test_native_execution_refuses_non_windows_host(self):
        result = self.run_script()
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("requires Windows", result.stderr)
        self.assertEqual(self.manifests(), [])


if __name__ == "__main__":
    unittest.main()
