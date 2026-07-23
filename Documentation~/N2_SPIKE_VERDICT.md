# N2 Fork-Elimination Spike — Verdict

**Session:** Project Tether, Track N, Session N2 (2026-07-22)
**Branch under test:** `abyssal/spike/n2-a-c` = `release/1.11.0` + Category A (`e2c11dd4`) + Category C (`712a2a5c`). **No B, no D.**
**Test bed:** "Network Testing" sandbox (2022.3.62f2), NGO consumed via `file:` from this repo, new sandbox test assembly `Archon.N2SpikeTests` (`Assets/N2Spike/` — 2 NetcodeIntegrationTest fixtures over real UnityTransport loopback, 2 standalone host-rig fixtures with real Addressables content, plus the `N2Starter`/`N2StockStartFlow` prototype that is the N4 game-side code).
Category definitions: `ARCHON_NETWORKING_SCOPING.md` §4 (Abyssal repo).

---

## H-B — Category B (NetworkManager start-split) can be dropped: **PROVEN**

**Mechanism validated** (the §5 pre-work design, unchanged):
1. `Addressables.LoadResourceLocationsAsync("NetworkScene")` pre-cached BEFORE any start call (pure Addressables work, no NGO dependency — nothing forces it inside the start path).
2. **Stock** `StartHost()` / `StartClient()` / `StartServer()`.
3. Handler install + `RegisterExternalScenes(cached)` synchronously in the **stock** `OnServerStarted`/`OnClientStarted` callbacks — which fire *inside* the start call, after `Initialize()` has created `SceneManager`, before the call returns.

Prototype implementation: sandbox `Assets/N2Spike/N2StockStartFlow.cs` (+ `N2AddressablesSceneManagerHandler.cs`, a byte-faithful copy of Abyssal's handler). This is the code that replaces `AddressablesSceneInitializer`'s coroutine at N4.

### Client half (the part host-only runs cannot prove)

`N2HbClientOrderingTests.RegistrationInsideStockStartClient_BeatsFirstServerSceneEvent` — **PASSED**.
Real late-join client (`CreateAndStartNewClient`) against a running host over UnityTransport loopback, scene management enabled. The synchronize payload was made **load-bearing**: a runtime probe scene (`N2ExternalProbe`, not in build settings, marked NGO-tracked via `ScenesLoaded`) ships its externally-registered hash in the server's Synchronize payload, and the client's `OnClientBeginSync` resolves `SceneNameFromHash(sceneHash)` **before** any validation callback runs (`NetworkSceneManager.cs:1918` vs `:1940`) — an unregistered client throws right there.

Ordering trace (registration seq/frame vs first server-driven scene event):

```
[TETHER][NET] ... event=n2-client-registered-in-onclientstarted frame=254 seq=1
[TETHER][NET] ... event=n2-client-first-scene-event type=SynchronizeComplete frame=261 seq=2 externalEntries=2
[TETHER][NET] ... event=n2-client-ordering-verdict registrationFrame=254 firstSceneEventFrame=261 frameWindow=7 synchronizeCompleted=True
```

- Registration completed **inside** stock `StartClient()` (the `OnClientStarted` callback), with the external table fully populated (`externalEntries=2`) when the first scene event fired.
- The margin is **7 frames**, and it is structural, not lucky: NGO pumps incoming messages only in `NetworkUpdate` stages, never inside the start call stack, and the server's Synchronize cannot arrive before transport connect + connection approval complete a round trip.

**Negative control** — `N2HbClientNoRegistrationControlTests.NoClientRegistration_SynchronizationFails` — **PASSED** (i.e. sync correctly failed):

```
[TETHER][NET] ... event=n2-negative-setup serverPaths=Assets/InitTestScene...unity;Assets/N2ExternalProbe.unity serverExclude=True
[TETHER][NET] ... event=n2-negative-captured-error logType=Exception frame=22 text=Exception: Scene Hash 2446109163 does not exist in the HashToBuildIndex table! ...
```

Identical topology minus the client-side `RegisterExternalScenes` → the client throws during Synchronize processing and never completes synchronization. The positive test passes *because of* the registration, not vacuously.

### Host half ("does the async gap matter on the HOST at all?") — No.

`N2HbHostAddressablesTests.StockStartHost_PostStartRegistration_LoadsAddressableLayer` — **PASSED**, with real Addressables content (the addressable `Layer` scene, label `NetworkScene`):

```
[TETHER][NET] ... event=n2-installed-and-registered serverSide=True frame=269 seq=3 pathCount=1
[TETHER][NET] ... event=n2-host-layer-loaded role=host frame=271 startFrame=269 registrationFrame=269
```

- Handler install + registration + `SetClientSynchronizationMode(Additive)` all completed **on the same frame as, and inside, the stock `StartHost()` call** (`registrationFrame == startFrame`, asserted).
- An NGO `LoadScene("Layer", Additive)` issued in the **same call stack** as the start returned `Started` and completed end-to-end through the Addressables handler (`LoadEventCompleted`, scene loaded, then NGO `UnloadScene` → `UnloadEventCompleted` — the handler's Addressables unload path exercised too).
- Structurally: inside `StartHost()`, the host's local approval never reaches `SceneManager.SynchronizeNetworkObjects` — that call sits behind `ownerClientId != ServerClientId` in `NetworkConnectionManager.HandleConnectionApproval` (line ~833 at 1.11) and runs only when a **remote** client is approved, frames after start. There is no hash→path resolution inside the host start path at all.

### The StartServer asymmetry — Yes, it was the tell.

`N2HbHostAddressablesTests.StockStartServer_PostStartRegistration_LoadsAddressableLayer` — **PASSED** (same evidence shape: `registrationFrame=277 == startFrame`, Layer loaded frame 279). Upstream never split `StartServer()`, and the fork ran fine that way — because the only thing Category B ever bought was a place to `yield` on `LoadResourceLocationsAsync` *between* initialize and transport start. Pre-caching removes the yield; every role then collapses to the `StartServer` shape.

### Two-process confirmation (staged for the user)

In-process NetcodeIntegrationTest clients share the Unity scene space, so the guest's *actual scene load* path is exercised live in the staged run: sandbox scene `Assets/Scenes/N2Bootstrap.unity` + `N2Starter` (auto host in main editor / guest in ParrelSync clone `Network Testing_clone_0`, stock starts, full `[TETHER]` ordering probes; host NGO-loads `Layer` at +4s so a later-joining guest must resolve an external-scene hash from the Synchronize payload and Addressables-load it).

Staged-run steps (user):
1. Focus the "Network Testing" editor and let it finish refresh/compile — at session end its MCP plugin transport was wedged (websocket send loop; editor idle, no import running), which blocked the final remote steps. `N2Bootstrap.unity` was YAML-authored (Bootstrap copy, starter component swapped to `N2Starter`) and imports on this refresh.
2. Optional but recommended: run PlayMode tests for `Archon.N2SpikeTests` once — expected 4 pass / 1 ignored (the manifest is byte-identical to the state of green run job `55a3a1e7`, pre-D-probe; this just re-confirms after the pin restore).
3. Open `N2Bootstrap` in the main editor AND in the ParrelSync clone; press Play in the main editor first (auto-hosts), then in the clone (auto-joins as guest). Watch the guest load `Layer`.
4. Hand both consoles' `[TETHER]` streams to Claude for the log review (owner doc §9).

**Prediction to check in the guest log:** `n2-installed-and-registered` (frame F1, same frame as `n2-start-returned`) strictly precedes the first `n2-scene-event type=Synchronize` (frame F2), F2 − F1 ≥ 1; guest loads `Layer` via the Addressables handler and reaches `SynchronizeComplete` with zero scene-resolution errors.

---

## H-A — replace Category A's ~line-1118 guard relaxation with `VerifySceneBeforeLoading`: **REFUTED**

Scratch branch `abyssal/spike/n2-ha-stock-guard` (= spike branch + ONLY the `ValidateSceneEvent` guard hunk reverted to stock; external tables intact; pushed).

- **Static:** the guard rejects a non-build-list scene and returns `SceneEventProgressStatus.InvalidSceneName` **before** `VerifySceneBeforeLoading` is ever consulted — and the callback is a further-restricting veto, not a loosening hook. It cannot admit a scene the guard already rejected.
- **Empirical:** `N2HaGuardRevertTests.StockGuard_RejectsExternalScene_EvenWithPermissiveVerifyCallback` — **PASSED on the scratch branch**: with a maximally permissive callback (`(idx, name, mode) => true`) installed, `LoadScene("Layer", Additive)` still returned `InvalidSceneName` and logged the stock error `Scene 'Layer' couldn't be loaded because it has not been added to the build settings scenes in build list.`
- Corroborating: on the scratch branch both H-B host tests fail with exactly that stock error — the relaxation is load-bearing for every external-scene load.

**The ~line-1118 guard relaxation stays in Category A.** (Note `VerifySceneBeforeLoading` is still *used* by the integration — as a filter — but it cannot replace the guard.)

## H-A corollary (scoping-doc confirmation)

The A hash/path fallbacks (`HashToExternalScenePath` / `ExternalSceneNameToHash` in `ScenePathFromHash` / `SceneHashFromName`) were exercised as load-bearing on both server (synchronize loop hashes every loaded scene and **throws** on unregistered ones) and client (`OnClientBeginSync` resolution, see the negative control). No public seam exists for any of it. **Category A survives in full.**

---

## D-necessity probe — Collections shims: **DEAD WEIGHT** (at 1.11, against Abyssal's pins)

On the spike branch (**no D** — stock `NativeHashSet` at `NetworkMessageManager.cs:611` and `RpcTarget.cs:439/533`), the sandbox manifest was pinned to mirror Abyssal: `com.unity.transport@2.4.0` + `com.unity.collections@2.5.3` (burst resolved 1.8.21, mathematics 1.3.2).

- **Compile: CLEAN.** Zero errors, `EditorUtility.scriptCompilationFailed == false`, `Unity.Netcode.Runtime.dll` rebuilt against collections 2.5.3. Stock `NativeHashSet<T>` exists and compiles fine in collections 2.x.
- **Runtime smoke: GREEN.** The full N2 suite (4 tests incl. real client join/synchronize — which drives the `NetworkMessageManager` version-set D site — and addressable load/unload) passed under these pins. *Not* exercised at runtime: `RpcTarget`'s excluded-client set paths (no exclusion-list RPCs in the suite) — compile-level evidence only there.
- Conclusion: the `NativeHashSet→NativeParallelHashSet` shims were never necessary at these dependency versions. **Drop D.** N3 must still re-check the two *new* upstream `NativeHashSet` uses at 1.15.1 (`CollectionSerializationUtility.cs`, `NetworkVariableSerialization.cs`, scoping doc §12) against the reconciled collections version — expectation after this probe: they compile as-is.

Sandbox manifest pins were removed after the probe (restored to transitive resolution).

**Addendum — D's origin identified (post-session, user observation).** After the pin restore dropped the sandbox back to the collections **1.x** lineage (NGO 1.11 → transport 1.4.0), the sandbox editor raised Unity's **API Updater** dialog offering to rewrite exactly the two D sites (`RpcTarget.cs`, `NetworkMessageManager.cs`) — `NativeHashSet<T>` is the obsolete-flagged rename shim for `NativeParallelHashSet<T>` in collections 1.x, and the updater's rewrite is byte-for-byte what Category D is. So D was the API updater's fix (or its manual equivalent) for a collections-1.x-era resolution that Abyssal's actual pin (2.5.3, where `NativeHashSet` is a real current type) never needed. **Decline this dialog whenever it reappears in the sandbox** — accepting would edit the fork working tree and re-create D on the spike branch. (This dialog, being modal, is also what wedged the sandbox editor at session end.)

---

## Target patch set for N3

Rebase onto `release/1.15.1` carrying **exactly two category commits**:

| Category | Fate | N3 notes |
|---|---|---|
| **A** — `NetworkSceneManager.cs` external-scene registration/resolution | **KEEP, in full** (incl. the ~1118 guard relaxation — H-A refuted) | Carry upstream's additive `ScenePathFromHash` `sceneHash == 0` guard forward (§4/§12); `SceneHashFromNameOrPath`→`SceneHashFromName` rename territory is churn-free upstream |
| **B** — `NetworkManager.cs` start-split | **DROP** (H-B proven) | Game side moves to pre-cache + stock start + `OnServerStarted`/`OnClientStarted` registration (prototype: sandbox `N2StockStartFlow`); delete `AddressablesSceneInitializer`'s `OnHostInitialized`/`OnClientInitialized`/`Complete*Start` usage at N4; scrub the two comment references (`MainMenuSceneLoader.cs:103`, `TaskManager.cs:1488`) |
| **C** — `ISceneManagerHandler.cs` + `SceneEventProgress.cs` widening/hook | **KEEP** | `ISceneManagerHandler.cs`: zero upstream churn. `SceneEventProgress.cs`: 6 lines churn vs our 24-line refactor — re-apply C2 by hand (upstream still `AsyncOperation`-based at 1.15.1) |
| **D** — Collections shims | **DROP** (dead weight even at 1.11) | 77 lines of upstream `RpcTarget` churn become irrelevant — nothing to re-apply. Re-verify 1.15.1's two new `NativeHashSet` uses compile against the reconciled collections at N3 |

**Net minimal fork: A + C** — both confined to `Runtime/SceneManagement/`, internal-widening/additive, low rebase-conflict. Game-side start code returns to the stock NGO contract.

### N4 game-side notes (from the prototype)
- Pre-cache at `AddressablesSceneInitializer.Start()` (scene-load time — long before any user start action); **gate starting on the cache being ready** (`N2StockStartFlow.IsPrecached`) instead of yielding mid-start.
- Registration callback shape: subscribe both `OnServerStarted` (register + `SetClientSynchronizationMode(Additive)`) and `OnClientStarted` (guarded by `!IsServer` so a host registers exactly once, in the earlier callback). Works unchanged for dedicated `StartServer`.
- `CoreEventsChannel.ExternalScenesRegistered()` moves into the same callback (now fires a few frames *earlier* than under the B flow — anything sequenced on it should be re-checked at N4).

---

## Evidence inventory

- **Branches (pushed):** `abyssal/spike/n2-a-c` (A+C spike, this verdict lives here), `abyssal/spike/n2-ha-stock-guard` (H-A scratch, `7769e7f2`).
- **Sandbox tests:** `Assets/N2Spike/` — `N2HbClientOrderingTests` (+negative control), `N2HbHostAddressablesTests` (host + server), `N2HaGuardRevertTests` (`[Ignore]`d off the scratch branch), `N2StockStartFlow`/`N2AddressablesSceneManagerHandler`/`N2Starter` prototypes.
- **Runs (2026-07-22, sandbox editor via MCP):** spike suite 4/4 green (+1 ignored); scratch branch: H-A probe green + both host tests failing with the stock guard error (expected); D-probe pins: suite 4/4 green.
- **Not rebased:** `abyssal/1.11.0-patched` untouched — N3 rewrites it after this verdict is reviewed.
- **Session caveats:** (1) the final confirmatory suite run after restoring the D-probe manifest pins could not be executed — the sandbox editor's MCP transport wedged after the dependency downgrade resolve; the manifest was verified restored on disk and is identical to the earlier green baseline, and the staged-run step 2 re-confirms. (2) Mid-session, MCP tool calls silently rerouted to the wrong Unity instance after bridge reconnects (lost pin); all evidence runs above were made with routing re-verified (`Application.dataPath` asserted) — treat any future "test job failed to initialize" as a routing symptom first.
