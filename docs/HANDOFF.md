# Rebound Guard / 深潜稳帧 — development handoff

Current gameplay code: **0.4.0 experimental**, Windows / Barotrauma **1.13.4.0** / matching LuaCs. Workshop item: https://steamcommunity.com/sharedfiles/filedetails/?id=3822098302 (already published publicly; LuaCs prerequisite linked).

## Read first

`AGENTS.md`, `README.zh-CN.md`, `README-0.4.zh-CN.md`, `研究测试摘要.md`, then code and test reports. A private desktop migration additionally preserves the complete Chinese source-level research report, raw field evidence and old isolated test runtimes; these are excluded from the public repository.

## Implemented

- Generic LuaCs bridge: guards empty phases using the live callback registry, caches MethodBase, forwards typed results into the original generated callback bodies. Callback semantics, reset and restoration tested. Dynamic module builder reference recycling is not proof of immediate CLR metadata collection.
- Loading lifecycle: the trusted reference iterator's Dispose receives the missing operation-count release for states 2/-3. Native MoveNext, inner disposal, ordering, null sentinel and GC remain. The tiny getter could be inlined, so fixing the actual iterator matters.
- Client exit: delays the audited LuaCs disposal until after native Client.Quit and before Steam shutdown, with exception cleanup. This repairs a demonstrated lifetime ordering defect; the latest reported host crash still lacks a matching exception stack.
- Existing queue/index, irrelevant container search and independent-stage parallel work remain. Shared velocity solving and position projection are disabled research paths.
- Optional audited Mudraptor/TSM rule ports preserve live rules and fall back to Lua. They require explicit source replacement and are not automatically applied to other mods.
- Local-only bounded diagnostic writing, coarse per-thread allocation metrics and exception evidence.

## Last complete tests

`research/deep/validation-client.json` and `validation-server.json`: actual bundled .NET 8 isolated methods, no real submarine battle, rendering benchmark or networking session.

| Test | Client | Server |
| --- | ---: | ---: |
| Main state assertions | 1,057,372 | 1,057,374 |
| Generic Lua bridge checks | 41 | 41 |
| 20k wrapper allocations, before → after | 16,480,000 → 7,040,000 bytes | same |
| Wrapper time before → after | 8.4651 → 4.6969 ms | 9.9450 → 8.1520 ms |
| Early-ended-enumerator unload | 2026.2782 → 9.9596 ms | 2033.7391 → 16.9642 ms |
| Loading lifecycle checks | 22 | 22 |
| Optional native-rule checks | 72 | 68 |

Results are local benchmarks with timing variability. Allocation reduction is about 57.3% for the wrapper fixture, not global FPS. The generic fixture has C# callbacks and excludes Lua VM work. The optional IsHuman comparison includes MoonSharp but omits full LuaCs outer wrappers.

PowerShell 7 test commands, with the actual game directory:

```powershell
pwsh -NoProfile -File tools/Verify.ps1 -GameDir 'YOUR_GAME_DIR' -Side Client
pwsh -NoProfile -File tools/Verify-Deep.ps1 -GameDir 'YOUR_GAME_DIR' -Side Client
pwsh -NoProfile -File tools/Verify-Deep.ps1 -GameDir 'YOUR_GAME_DIR' -Side Server
```

Quick suites: `-Suite Bridge`, `-Suite Loading`, `-Suite Native`. The harness uses installed publicized references and bundled compiler/runtime dependencies; it does not need a system SDK. Run each side in a separate pwsh process. Before using a different game build, rename/archive old `build/net8-harness-client` and `build/net8-harness-server` inside the workspace so the current copy-if-missing logic cannot mix runtime DLL versions.

## Next investigation

Obtain same-session **actual server** input/position-queue diagnostics and the latest host crash report/Windows error. Supplied field reports so far are clients; correction counts are not visible rubberband counts. Compare real workloads before expanding optimization scope.

Investigate per-loader full compacting GC during unload, but do not simply remove it: the plugin is disposed before the batch of assembly loaders, collectible ALC/native-library lifetimes matter, and existing manager GC runs too. Generic initial patch creation currently runs the original factory and an additional bridge factory; no initial-loading speed claim is justified.

Test optional native ports against later-arriving foreign hooks and real reload/exit scenarios. The common bridge is installed on future generated wrappers; verify actual wrappers/empty-phase counts in field logs before attributing improvements.

## Publishing

`workshop/` contains prepared localized titles/descriptions, final cover, existing item state, and SDK publication scripts. Steam SDK comes from an installed game and is not distributed publicly. `Publish` updates content; localization updates should change only the requested language metadata. Always read back both languages and preserve the Chinese entry.

The release ZIP is a source mod package, not a game installer. The private migration ZIP is a full development handoff and must not be uploaded wholesale to the Workshop or public GitHub.

Chinese and English Workshop metadata were submitted and independently read back successfully. The bundled SDK's Query.InLanguage stores a field without applying it; `Update-Localizations.ps1` uses its native query interface to set the query language before sending. Content is unchanged at 466467 bytes. English title: **Rebound Guard | Performance**; Simplified Chinese: **深潜稳帧 | Rebound Guard**.
