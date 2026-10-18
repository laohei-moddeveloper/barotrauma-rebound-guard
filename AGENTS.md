# Rebound Guard project instructions

- Canonical source: `LocalMod/ReboundGuard`. Current gameplay code is 0.4.0, tested against Windows Barotrauma 1.13.4.0 and matching LuaCs/C# methods.
- Read `docs/HANDOFF.md` and the Chinese README before changing code. A private migration bundle may additionally contain `handoff/PROJECT_HANDOFF.zh-CN.md` with field evidence.
- User priorities: substantial general improvements to stutter, FPS, rubberbanding, desync and loading, plus useful multicore work. Preserve observable gameplay. Imperceptible reductions may be considered with evidence.
- Do not equate local benchmark speedups, CPU usage, correction-branch counts or reported allocation with whole-game improvement, visible rubberbanding, or leaks.
- Preserve method fingerprint and competing-patch guards. Do not force unknown builds through. Position projection and shared ordered velocity-solver research stay disabled until justified by realistic evidence.
- Do not blindly parallelize shared AI, Lua VM, water, power, physics-body writes or side effects. Maintain order/barriers and validate actual state/callback equivalence.
- Test changes using the existing isolated real-game harness, appropriate to the change. Do not interrupt an active game or replace game DLLs just to run tests.
- Tools accept `-GameDir`; old computer paths in scripts are defaults only. Verify the new machine's actual files. Migrated `build/net8-harness-*` are cached old runtimes; do not mix them silently with a different installed build.
- Public GitHub/Workshop content must exclude private reports, raw traces, saves, installed game DLLs, decompiled third-party trees, credentials and migration-private material. Use explicit publication allowlists.
- Workshop item already exists: **3822098302**, app **602960**. Update that item when publication is requested; do not create duplicates. Publishing must verify the currently logged-on account owns it.
- `workshop/Publish-Workshop.ps1 -Action Create` was a one-time operation and must not be repeated for this project. `publish-state.json` persists the existing target. Localized metadata uses `Update-Localizations.ps1`.
- User currently permits Edge browser control only for browser UI work; do not use desktop/native UI automation. Shell file/build operations remain part of the project work.

## Codex Cloud source synchronization

- This is a separate Git repository inside the local Codex project `潜渊症模组开发`. The user has authorized cloud access to its public Git source alongside the BaroDock repository. This does not authorize uploading the private migration bundle or publishing a new Steam Workshop item.
- Keep `migration-private/`, `handoff/`, cached game and LuaCs binaries, raw reports, local research, credentials, and ignored files out of Git. Stage explicit paths and review `git diff --cached --name-only` before every cloud-related push.
- After creating or updating a local branch for cloud work, commit the intended public files, push that branch to `origin`, and verify the remote ref. A local-only branch or uncommitted file is not available in a new cloud task. Do not merge branches merely to make them visible.
- Use the Windows game and isolated harness only when available. Cloud review of source does not replace real-game validation or the release checks in `docs/HANDOFF.md`.
