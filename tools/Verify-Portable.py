"""Build and run isolated managed-method tests with a .NET SDK, on Linux or Windows.

Supply an extracted, matching LuaCs build and its Publicized references. No game
entry point, Steam session or rendering is started. Game files are never changed.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import platform
import xml.etree.ElementTree as ET


ROOT = Path(__file__).resolve().parents[1]


def project(path, assembly, sources, references, executable=False):
    node = ET.Element("Project", Sdk="Microsoft.NET.Sdk")
    properties = ET.SubElement(node, "PropertyGroup")
    for key, value in dict(TargetFramework="net8.0", AssemblyName=assembly,
                           EnableDefaultCompileItems="false", GenerateAssemblyInfo="false",
                           AssemblySearchPaths="{HintPathFromItem};{RawFileName};{TargetFrameworkDirectory}",
                           Optimize="true", EnableNETAnalyzers="false",
                           OutputType="Exe" if executable else "Library").items():
        ET.SubElement(properties, key).text = value
    items = ET.SubElement(node, "ItemGroup")
    for source in sources:
        ET.SubElement(items, "Compile", Include=str(source))
    for reference in references:
        entry = ET.SubElement(items, "Reference", Include=reference.stem)
        ET.SubElement(entry, "HintPath").text = str(reference)
        ET.SubElement(entry, "Private").text = "false"
    ET.ElementTree(node).write(path, encoding="utf-8", xml_declaration=True)


def digest(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--game-dir", required=True, type=Path)
    parser.add_argument("--reference-dir", type=Path)
    parser.add_argument("--side", choices=["Client", "Server"], default="Client")
    parser.add_argument("--suite", choices=["All", "Bridge", "Native", "Loading", "Queue", "Compatibility", "Dispatch", "Stress", "Scaffold", "StatusTags", "Nearby", "Lifecycle"], default="All")
    parser.add_argument("--dotnet", default="dotnet")
    parser.add_argument("--report", type=Path)
    parser.add_argument("--build-dir", type=Path, help="Separate harness directory for an independent endurance run")
    parser.add_argument("--stress-seconds", type=int, default=30)
    parser.add_argument("--exercise-parallel", action="store_true",
                        help="Explicitly report four logical processors to .NET to exercise guarded parallel paths on smaller CI hosts; not a hardware performance claim")
    args = parser.parse_args()
    game = args.game_dir.resolve()
    refs = (args.reference_dir or game / "Publicized").resolve()
    build = (args.build_dir or ROOT / "build" / ("portable-" + args.side.lower())).resolve()
    build.mkdir(parents=True, exist_ok=True)
    names = ["Barotrauma.dll" if args.side == "Client" else "DedicatedServer.dll", "BarotraumaCore.dll"]
    references = [refs / name for name in names] + [game / name for name in [
        "0Harmony.dll", "Farseer.NetStandard.dll",
        "XNATypes.dll", "MoonSharp.Interpreter.dll", "FluentResults.dll", "Microsoft.CodeAnalysis.dll"]]
    monogame = game / "MonoGame.Framework.Windows.NetStandard.dll"
    if args.side == "Client" or monogame.exists():
        references.append(monogame)
    for path in references + [game / name for name in names]:
        if not path.is_file():
            parser.error(f"Missing matching build dependency: {path}")
    source = ROOT / "LocalMod" / "ReboundGuard" / "CSharp"
    source_manifest = {str(path.relative_to(ROOT)): digest(path) for part in ["Shared", args.side]
                       for path in sorted((source / part).glob("*.cs"))}
    mod = build / "Mod.csproj"
    project(mod, "ReboundGuard." + args.side,
            sorted((source / "Shared").glob("*.cs")) + sorted((source / args.side).glob("*.cs")), references)
    subprocess.run([args.dotnet, "build", str(mod), "-c", "Release", "--nologo", "-v:q"], check=True)
    output = build / "bin" / "Release" / "net8.0"
    tests = build / "Tests.csproj"
    project(tests, "TestRunner", sorted(path for path in (ROOT / "tests").glob("*.cs") if path.name != "Regression.cs"),
            references + [output / ("ReboundGuard." + args.side + ".dll")], True)
    subprocess.run([args.dotnet, "build", str(tests), "-c", "Release", "--nologo", "-v:q"], check=True)
    # Separate immutable game dependencies from compilation output. In particular,
    # never run the publicized reference DLLs in place of the real game assemblies.
    runtime = build / "runtime"
    runtime.mkdir(exist_ok=True)
    installed = {path.name: path for path in game.glob("*.dll")}
    own = {"TestRunner.dll", "ReboundGuard." + args.side + ".dll"}
    for cached in runtime.glob("*.dll"):
        if cached.name in own:
            continue
        if cached.name not in installed or digest(cached) != digest(installed[cached.name]):
            raise RuntimeError(f"Stale harness dependency: {cached}; archive this runtime before testing another build")
    for name, path in installed.items():
        if not (runtime / name).exists():
            shutil.copy2(path, runtime / name)
    for name in own | {"TestRunner.runtimeconfig.json", "TestRunner.deps.json"}:
        shutil.copy2(output / name, runtime / name)
    shutil.copytree(ROOT / "compat", runtime / "compat", dirs_exist_ok=True)
    report = (args.report or build / ("validation-" + args.suite.lower() + ".json")).resolve()
    report.parent.mkdir(parents=True, exist_ok=True)
    environment = dict(os.environ, DOTNET_TieredCompilation="0")
    environment["REBOUND_GUARD_STRESS_SECONDS"] = str(args.stress_seconds)
    environment["REBOUND_GUARD_STRESS_REPORT"] = str(report)
    if args.exercise_parallel:
        environment["DOTNET_PROCESSOR_COUNT"] = "4"
    subprocess.run([args.dotnet, str(runtime / "TestRunner.dll"), str(game), str(report), args.suite],
                   cwd=runtime, env=environment, check=True)
    result = json.loads(report.read_text(encoding="utf-8"))
    result["harness_context"] = dict(
        platform=platform.system(), side=args.side, suite=args.suite,
        logical_processor_override=environment.get("DOTNET_PROCESSOR_COUNT"),
        affinity_cpus=len(os.sched_getaffinity(0)) if hasattr(os, "sched_getaffinity") else None,
        game_binaries={name: digest(game / name) for name in names + ["Farseer.NetStandard.dll"]},
        source_sha256=source_manifest,
        source_changed_during_run=any(digest(ROOT / name) != value for name, value in source_manifest.items()),
        limitation="Managed isolated methods only. Processor override, when set, exercises scheduling despite CI CPU limits; it does not add CPU resources or validate Windows rendering, live networking or whole-game speedup.")
    report.write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding="utf-8")


if __name__ == "__main__":
    main()
