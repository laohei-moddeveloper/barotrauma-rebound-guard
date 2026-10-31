"""Compile and isolate audited managed game methods using a .NET 8 SDK.

Works on Linux and Windows. Never starts Barotrauma/DedicatedServer Main, installs
the mod, changes game files, or treats managed fixtures as gameplay validation.
All copied DLLs, source snapshots, logs and raw reports remain in ignored build/.
"""
import argparse
import hashlib
import json
from pathlib import Path
import shutil
import struct
import subprocess
import sys
import time
import uuid
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]
TESTS = (
    "Bootstrap DeepRegression QueueMembershipTests FrameHealthTests BucketStampTests "
    "PhysicsBenchmark ConstraintSolverTests ConstraintPreparationTests NearbyItemIndexTests "
    "ContainerEffectTests WorldReplayTests WorldPerformanceTests PluginLifecycleTests "
    "FieldIterationTests NativeModHotPathTests LuaBridgeTests LoadingLifecycleTests LiveStatusTagTests CompatibilityTests LuaDispatchSemanticsTests LuaDispatchStressTests LuaScaffoldBenchmarkTests QueueChurnTests"
).split()
SUITES = ("Bridge", "StatusTags", "Nearby", "Loading", "Native", "Lifecycle", "All", "Queue", "Compatibility", "Dispatch", "Scaffold", "Stress")


def sha(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def managed(path):
    """Check the PE CLI directory; native Windows DLLs cannot be loaded here."""
    with path.open("rb") as stream:
        if stream.read(2) != b"MZ":
            return False
        stream.seek(0x3C)
        offset = struct.unpack("<I", stream.read(4))[0]
        stream.seek(offset)
        if stream.read(4) != b"PE\0\0":
            return False
        stream.seek(offset + 24)
        magic = struct.unpack("<H", stream.read(2))[0]
        directory = 96 if magic == 0x10B else 112 if magic == 0x20B else None
        if directory is None:
            return False
        stream.seek(offset + 24 + directory + 14 * 8)
        data = stream.read(8)
        return len(data) == 8 and struct.unpack("<II", data)[0] != 0


def project(path, sources, references, side, executable):
    root = ET.Element("Project", Sdk="Microsoft.NET.Sdk")
    props = ET.SubElement(root, "PropertyGroup")
    for key, value in dict(TargetFramework="net8.0", OutputType="Exe" if executable else "Library",
                           EnableDefaultCompileItems="false", ImplicitUsings="disable", Nullable="disable",
                           Optimize="true", AssemblyName="TestRunner" if executable else "ReboundGuard." + side).items():
        ET.SubElement(props, key).text = value
    items = ET.SubElement(root, "ItemGroup")
    for source in sources:
        ET.SubElement(items, "Compile", Include=str(source))
    for reference in references:
        node = ET.SubElement(items, "Reference", Include=reference.stem)
        ET.SubElement(node, "HintPath").text = str(reference)
        # Runtime always uses the original DLL, never the publicized reference.
        ET.SubElement(node, "Private").text = "false"
    if executable:
        ET.SubElement(items, "ProjectReference", Include="../mod/Mod.csproj")
        ET.SubElement(items, "RuntimeHostConfigurationOption", Include="System.Runtime.TieredCompilation", Value="false")
    ET.indent(root)
    ET.ElementTree(root).write(path, encoding="unicode")


def run(command, log, cwd):
    with log.open("w", encoding="utf-8") as stream:
        process = subprocess.run(command, cwd=cwd, stdout=stream, stderr=subprocess.STDOUT, timeout=600)
    if process.returncode:
        print(log.read_text(encoding="utf-8", errors="replace")[-5000:])
        raise RuntimeError(f"Command failed ({process.returncode}); {log}")


def verify(args):
    dotnet = shutil.which(args.dotnet)
    if not dotnet:
        raise RuntimeError(".NET SDK executable unavailable; supply --dotnet PATH")
    sdk = subprocess.check_output([dotnet, "--version"], text=True).strip()
    stamp = time.strftime("%Y%m%dT%H%M%SZ", time.gmtime()) + "-" + uuid.uuid4().hex[:8]
    output = ROOT / "build" / ("managed-" + stamp)
    source = output / "source"
    source.mkdir(parents=True)
    public_sources = []
    for part in ("Shared", "Client", "Server"):
        public_sources.extend((ROOT / "LocalMod/ReboundGuard/CSharp" / part).glob("*.cs"))
    public_sources.extend(ROOT / "tests" / (name + ".cs") for name in TESTS)
    public_sources.extend((ROOT / "compat").glob("*.lua"))
    fingerprints = {}
    for path in public_sources:
        relative = path.relative_to(ROOT)
        destination = source / relative
        destination.parent.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(path, destination)
        fingerprints[relative.as_posix()] = sha(destination)
    report = dict(schema=1, success=False, os=sys.platform, sdk=sdk, output=str(output), source_sha256=fingerprints,
                  results=[], limitations=[
                      "Isolated managed methods in SDK-provided .NET 8; not the installed game's bundled Windows runtime.",
                      "No game entry point, submarine, live networking, rendering, GPU or end-to-end loading benchmark.",
                      "Only requested suites run. All still enforces existing physics guards and can reject an unaudited dependency."])
    selected = [("Client", args.client_dir), ("Server", args.server_dir)]
    errors = []
    try:
        for side, directory in selected:
            if directory is None:
                continue
            game = Path(directory).resolve(strict=True)
            side_root = output / side
            for folder in ("mod", "test"):
                (side_root / folder).mkdir(parents=True)
            game_name = "Barotrauma.dll" if side == "Client" else "DedicatedServer.dll"
            refs = [game / "Publicized" / name for name in (game_name, "BarotraumaCore.dll")]
            refs += [game / name for name in ("0Harmony.dll", "Farseer.NetStandard.dll",
                                             "XNATypes.dll", "MoonSharp.Interpreter.dll", "FluentResults.dll", "Microsoft.CodeAnalysis.dll")]
            if side == "Client":
                refs.append(game / "MonoGame.Framework.Windows.NetStandard.dll")
            if any(not path.is_file() for path in refs):
                raise RuntimeError(f"Missing {side} compile reference(s): " + ", ".join(str(p) for p in refs if not p.is_file()))
            dlls = [p for p in game.glob("*.dll") if not p.name.startswith(("System.", "Microsoft.Win32.", "Microsoft.CSharp", "Microsoft.VisualBasic"))
                    and p.name not in ("mscorlib.dll", "netstandard.dll", "WindowsBase.dll") and managed(p)]
            dependencies = {str(path.relative_to(game)).replace("\\", "/"): sha(path) for path in set(refs + dlls)}
            record = dict(side=side, dependency_sha256=dependencies, suites=[])
            report["results"].append(record)
            sources = sorted((source / "LocalMod/ReboundGuard/CSharp/Shared").glob("*.cs"))
            sources += sorted((source / "LocalMod/ReboundGuard/CSharp" / side).glob("*.cs"))
            project(side_root / "mod/Mod.csproj", sources, refs, side, False)
            project(side_root / "test/Tests.csproj", [source / "tests" / (name + ".cs") for name in TESTS], refs, side, True)
            run([dotnet, "build", str(side_root / "test/Tests.csproj"), "-c", "Release", "--ignore-failed-sources", "-v", "minimal"],
                side_root / "compile.log", ROOT)
            record["compiled"] = True
            runtime = side_root / "test/bin/Release/net8.0"
            for dll in dlls:
                shutil.copyfile(dll, runtime / dll.name)
            shutil.copytree(source / "compat", runtime / "compat")
            print(f"{side}: compiled against actual publicized references; SDK {sdk}", flush=True)
            for suite in args.suite:
                result = dict(name=suite, passed=False)
                record["suites"].append(result)
                path = side_root / (suite + ".json")
                try:
                    run([dotnet, str(runtime / "TestRunner.dll"), str(game), str(path), suite], side_root / (suite + ".log"), runtime)
                    result.update(passed=True, report=path.name, sha256=sha(path))
                    print(f"{side}/{suite}: PASS", flush=True)
                except RuntimeError as error:
                    errors.append(str(error))
            after = {name: sha(game / name) for name in dependencies}
            record["dependencies_unchanged"] = dependencies == after
            if dependencies != after:
                errors.append(f"{side} dependency changed during validation")
        report["success"] = not errors
        report["errors"] = errors
    except Exception as error:
        report["errors"] = errors + [str(error)]
        raise
    finally:
        (output / "manifest.json").write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
        print(f"Managed validation manifest: {output / 'manifest.json'}", flush=True)
    if errors:
        raise RuntimeError("One or more managed checks failed; see manifest/logs")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--dotnet", default="dotnet")
    parser.add_argument("--client-dir", type=Path)
    parser.add_argument("--server-dir", type=Path)
    parser.add_argument("--suite", choices=SUITES, action="append", help="Repeat for several suites; defaults to Bridge, StatusTags, Nearby, Loading")
    args = parser.parse_args()
    if args.client_dir is None and args.server_dir is None:
        parser.error("Supply --client-dir and/or the ACTUAL LuaCs --server-dir")
    args.suite = args.suite or ["Bridge", "StatusTags", "Nearby", "Loading"]
    verify(args)


if __name__ == "__main__":
    main()
