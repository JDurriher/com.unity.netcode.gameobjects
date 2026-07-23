# N3 — Rebase onto release/1.15.1 + dependency reconciliation

**Session:** Project Tether, Track N, Session N3 (2026-07-22)
**Branch produced:** `abyssal/1.15.1-patched` = `upstream/release/1.15.1` (`81ff9074`) + Category A (`968869ec`) + Category C (`a8880d5b`). **No B, no D** — dropped per the N2 verdict (`N2_SPIKE_VERDICT.md`).
**`abyssal/1.11.0-patched` is untouched** — it remains the 1.11 record (A→C→B→D).

---

## 1. The rebase

Both category commits were cherry-picked from `abyssal/1.11.0-patched` onto `upstream/release/1.15.1` and **auto-merged cleanly** — none of the anticipated hand-resolution was needed. The predicted conflict surface (§12 churn table) resolved as follows:

| File | Upstream churn 1.11→1.15.1 | Outcome |
|---|---|---|
| `NetworkSceneManager.cs` | 274 lines total file churn | Auto-merged. **63(+)/20(−)**, changed-line set **byte-identical** to the 1.11 application (verified by interdiff). Upstream's additive `ScenePathFromHash` `sceneHash == 0` guard (new since 1.11) **carried forward**: our external-path check prepends, the guard sits in the final else branch — both survive. |
| `ISceneManagerHandler.cs` | zero | Trivial. **1(+)/1(−)**. |
| `SceneEventProgress.cs` | 6 lines (3 hunks) | Auto-merged. **15(+)/9(−)**, byte-identical to the 1.11 application. All three upstream hunks preserved alongside our hook refactor: null-safe `HasTimedOut`, `ConnectionManager.ConnectedClientIds` enumeration, null-guarded `StopCoroutine`. Upstream is still `AsyncOperation`/`.isDone`-based at 1.15.1 — C2 re-applied whole, no absorption. |

**Proof of purity:** `git diff upstream/release/1.15.1..abyssal/1.15.1-patched` (at the C commit) = exactly 3 files, 79(+)/30(−), all in `Runtime/SceneManagement/`. Interdiffs of the sorted changed-line sets vs the 1.11 applications returned empty for both A and C. B's and D's conflict surfaces (incl. `RpcTarget.cs`'s 77-line churn) were never paid — dropped commits, per the N2 verdict.

The end tree was reproduced identically when the commit messages were amended (tree `e6e54f04` both before and after) — message-only rewrites.

## 2. Dependency reconciliation (Ruling 3)

Sandbox ("Network Testing") resolution with the manifest's NGO `file:` ref serving this branch, transport/collections **deliberately unpinned**:

| Package | Resolved | Note |
|---|---|---|
| `com.unity.netcode.gameobjects` | **1.15.1** (Local `file:`) | the rebased fork |
| `com.unity.transport` | **1.5.0** (Registry) | **Ruling 3's exact target** — NGO 1.15.1's declared dependency, resolved transitively, no pin needed |
| `com.unity.collections` | **1.4.0** (Registry) | 1.x lineage; floor raised to 1.4.0 by `com.archon-industries.networking`'s declared dep (transport alone would allow 1.2.4) |
| `com.unity.nuget.mono-cecil` | **1.11.6** (Registry) | the 1.15.1 cecil bump — rode along inside the fork's `package.json`, no manifest change |
| `com.unity.burst` | 1.8.21 | no cascade |
| `com.unity.mathematics` | 1.2.6 | no cascade |

No `collections`/`burst` cascade from the transport alignment. Note: the Tether package (`com.archon-industries.networking`) still declares `com.unity.netcode.gameobjects: 1.11.0` — overridden by the sandbox's direct `file:` ref; bump it when the fork consumption pattern is finalized (T3/N4 territory).

## 3. Category-D check at 1.15.1

The `NativeHashSet<T>` surface at 1.15.1 is **six** runtime files, not the two §12 named: `RpcTarget.cs` (×2 sites), `NetworkMessageManager.cs`, `CollectionSerializationUtility.cs`, `NetworkVariableSerialization.cs`, `FastBufferReader.cs`, `FastBufferWriter.cs`. All compile **clean, unmodified** against collections **1.4.0** (via the 1.x obsolete rename shim). Zero console errors or warnings after full import.

The **API Updater consent dialog** appeared at first import, offering to rewrite exactly the two Messaging sites (`RpcTarget.cs`, `NetworkMessageManager.cs`) — i.e. to re-create Category D. **Declined** (standing sandbox note; fork working tree verified clean after). Expect it on any fresh import of this branch under collections 1.x; keep declining.

⚠ **N4 residual (unchanged from §12):** Abyssal pins collections **2.5.3** (where `NativeHashSet<T>` is a real current type — the N2 D-probe proved that combination compiles at the 1.11 sites). Re-verify the full six-file surface against Abyssal's actual pins at cutover; do not assume from the sandbox's 1.4.0 result.

## 4. Suite results (sandbox, NGO 1.15.1 fork, 2026-07-22)

| Suite | Result |
|---|---|
| Stage 1 compile gate | ✅ clean (`scriptCompilationFailed == false`, zero console errors) |
| N2 spike suite (`Archon.N2SpikeTests`, PlayMode) | ✅ **4/4 + 1 ignored** (the H-A probe, correctly skipped off the scratch branch) |
| Package EditMode (`Archon.Networking.EditModeTests`) | ✅ **15/15** |
| Package PlayMode (`Archon.Networking.PlayModeTests`) | ✅ **22/22** |
| Live host run — `N2Bootstrap` (stock-start flow) | ✅ `n2-installed-and-registered` frame 3 == `n2-start-returned` frame 3 (registration inside stock `StartHost()`); NGO `LoadScene("Layer")` → `Load`/`LoadComplete`/`LoadEventCompleted` **via the Addressables handler**; zero errors, zero warnings |
| Live host run — `Bootstrap` (T1 Stage-3 rig) | ✅ pool prewarm (5), SmoothSync orbit stream, two pooled spawn→return cycles, teleport phase; zero errors, zero warnings. (This rig does not NGO-load Layer by design — the addressable-scene gate is `N2Bootstrap`'s.) |

The client-half ordering test (`RegistrationInsideStockStartClient_BeatsFirstServerSceneEvent`) passing at 1.15.1 confirms the N2 stock-start pattern survives upstream's 1.15 scene-sync changes ("switch connecting clients to the server's active scene before spawning") — the structural margin (message pumping only in `NetworkUpdate`) holds.

**Sandbox state at session end: left on the rebased fork** (manifest `file:` ref + this branch checked out in the clone) — suites green, so no restore per the session rule. The N2Bootstrap scene is open for the user's staged two-process clone run, which remains the only open Track-N residue besides N4.

## 5. Evidence inventory

- Branch pushed: `abyssal/1.15.1-patched` (`origin`), A `968869ec` → C `a8880d5b` on `81ff9074`.
- Test jobs (sandbox MCP, 2026-07-22): spike `6d8d272b`, EditMode `c0cbaf90`, PlayMode `4b0310e6`.
- Editor-time note: NGO's "scene not in build settings" prompt for `N2Bootstrap` was declined (build list unchanged: `Bootstrap` only) — external-scene resolution must stay on the fork's registration path, not the build list.
