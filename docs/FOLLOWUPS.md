# Follow-ups

Known, deliberate, and not yet done. Everything here was found by a review, verified against the
source, and judged not worth blocking a merge — which is a different statement from "not worth
doing". Anything that would publish a wrong measurement is fixed rather than listed.

Each entry says what is wrong, what it costs today, and what would settle it. An item with no
stated cost is a smell: if nobody can say what it breaks, it probably belongs in the bin rather
than in this file.

Sources: `~/.claude/pair/eth-link-tester/findings-0N.md` (adversarial review), plus the Codex and
CodeRabbit passes on PR #9.

---

## Measurement correctness

### The end-of-run drain never happens *(from findings-03 F22 — pre-existing, Phase 3)*

`NativePacketEngine.StopAsync` claims the handle with an `Interlocked.Exchange` **before** the
500 ms quiesce, so every telemetry poll during that window sees a zeroed handle and drains nothing.
The samples carrying the settled receive totals are therefore never read, which is the exact
undercount the quiesce window was built to remove — 0.3% at 1518 B when Phase 3 measured it. Both
the C# comment and the Rust ABI doc describe a "drain once more, then stop" contract the code no
longer honours.

Not fixed with the Phase 4 review because it is Phase 3 code and out of that PR's scope; it is the
highest-value item here.

**Settles it:** a Lab hardware run at 1518 B, comparing the app's final RX total at Stop against
both NICs' hardware counters. Expect a shortfall near 0.3%. `enginerun` has its own drain loop and
is unaffected, which is why the rig runs have never shown it.

### Tagged LLDP is invisible to every shipped listen *(findings-03 F16)*

`LLDP_TAGGED` exists, is tested, and is used by nothing. The docs say "a separate listen with
`LLDP_TAGGED` is the way to cover tagged frames" — no such listen exists, so a switch announcing
itself on a tagged VLAN is heard by nothing while the documentation reads as covered.

The reason it is not simply folded into `all()` is real and recorded there: libpcap's `vlan`
keyword is a compile-time offset shift, not a predicate, and parentheses do not reliably contain
it, so a tagged term ahead of the STP and CDP terms breaks both. A second capture handle is the
fix, not a bigger filter.

### `unclassified` never crosses the FFI *(findings-03 F17)*

`Heard.unclassified` counts frames that passed the kernel filter and did not classify — which the
module doc calls "worth looking at", because the filter and the classifier disagreeing means one of
them is wrong. `passivecheck` prints it; `elt_passive_listen` returns three counts and drops it, so
the app is structurally blind to it.

---

## Reporting and honesty

### The forced-speed opt-in never mentions the default route *(findings-03 F18)*

The project constraint is that any adapter may be tested including the default-route one, "on
condition the warning is loud and the confirmation explicit". Lab Mode implements that with
`TargetsDefaultRoute` and `DefaultRouteAcknowledged` gating its start. Topology detection's
checkbox states the link-drop cost and never the default-route hazard, and if *both* adapters carry
a default route the selection falls through to forcing the transmit adapter with no acknowledgement
at all. The single-carry case is handled correctly.

### `AllowDisruptive` is re-read after the await *(findings-03 F11)*

The request captures it once; the grading note reads the property again after the longest await in
the app. Untick the box mid-detection and a not-attributable result prints "the forced-speed test,
which is off" beside an observations row showing it ran — the report contradicting itself about its
own configuration. Either branch on the captured request value or disable the checkbox while busy.

### `Forget()` leaves pair-specific warnings standing *(findings-03 F12)*

`Caveat` and preflight-shaped `ErrorMessage`s are written per pair and cleared only at the start of
the next detection, so after a pair change the panel says "Topology not checked yet" underneath the
*old* adapter's vendor-binding caveat. Note the restore warning is correctly kept: it describes the
journal and an adapter this run pinned, not the cable.

### Post-force failures are all blamed on the driver *(findings-03 F13)*

The inner catch wraps both `ForceSpeedAsync` and the first `SettleAsync`, and always reports "would
not take a forced 100 Mbps". A CIM failure inside the settle poll — after the force landed —
therefore blames the driver for refusing a write it accepted, and a user reasonably stops retrying a
test that would work.

### `topology.rs`'s module doc still states the pre-correction claim *(findings-03 F19)*

It opens with "802.1Q makes each reserved address a permanent filtering-database entry… so a
conforming relay component drops them", which is what this phase disproved: the block is *not*
uniform, S-VLAN and TPMR components conformantly forward `-00`, and that is why `-00` was dropped.
The managed side says it correctly. The standard set by `6a6824f` — sweep the module docs in the
same commit as the README — is the one this line missed.

---

## Robustness, latent

### A cancelled detection loses its restore outcome *(findings-03 F9)*

If an `OperationCanceledException` unwinds through the cleanup, the restore runs correctly but the
`TopologyDetection` carrying its outcome is never constructed — so a restore that *failed* is
invisible and the adapter stays pinned with only "the operation was canceled" on screen. Unreachable
today because no caller passes a token; it goes live the day anyone adds a Cancel button, which is a
one-attribute change on `AsyncRelayCommand`.

The un-cancellable tail also includes a second `SettleAsync` on `CancellationToken.None` — up to
20 s of polling after a "cancelled" detection.

### The hardware claim is per-process *(Codex, PR #9)*

`HardwareSession` is a static gate, so two copies of this unpackaged app each have their own. One
instance can start a disruptive detection while another is running a Lab measurement. The restore
journal already anticipates concurrent instances with a named mutex, so the precedent and the
mechanism both exist.

### `sweep::listen` sleeps the full window regardless *(findings-03 F14, CodeRabbit)*

The coordinator watches only the clock, so a `listen_one` that fails to open its device returns
immediately and is not joined until the deadline — a three-minute listen with a dead instrument
costs three minutes before the error appears. `pcap` 2.4 marks `Capture` as `Send`, so the captures
could be opened and filtered before the window starts and the failure reported at once.

### No teardown path exists *(findings-03 F21)*

`LabPage.Dispose` and `RigViewModel.Dispose` have no callers — there is no `Window.Closed` handler —
so closing the window mid-run relies on process death to release engine threads, NPF handles and the
journal's named mutex. Consequence today is close to nil, since the OS reclaims all of it and Lab
runs journal nothing; the cost is that several carefully written drain paths are dead code guarding
comments that are false.

Related, same finding: `HardwareSession` is single-thread-safe rather than thread-safe (`_holder` is
written after the CAS, and the idempotence guard is a plain bool). Safe today because every
claim and release site is on the UI thread — all traced — and its remark about the MVVM toolkit
marshalling notifications is wrong and should be corrected whether or not the class changes.

---

## Test fidelity

### `AlreadyAtTarget` is unreachable through the detector tests *(findings-03 F20)*

The `Rig` fake returns only `Applied` or throws, so the stranded-adapter case cannot be produced at
the detector level. Mitigated rather than dangerous: it is properly covered one level down, in
`ForcedSpeedAsymmetrySignalTests` and `GuardedAdapterConfiguratorTests`, and the detector only pipes
the outcome through.

### Three Platform files have no tests *(findings-03 F20)*

`NativeEngineLibrary`, `NativeTopologyProbe` and `WindowsSoftwareBridgeProbe`. The positional zip in
`SweepAsync` is protected only transitively, by the cross-language fixture pin.

### The vanished-adapter fake models the seam backwards *(findings-03 F3, partly fixed)*

The classification bug is fixed. The fake still has `ReadPropertiesAsync` always succeed and
`WriteAsync` throw `AdapterNotFoundException`, which is the opposite of the real platform stack — a
CIM query for an absent adapter returns zero rows without throwing. Worth correcting so the test
certifies a behaviour the real stack can actually produce.

---

## Cosmetic, deliberately deferred

- **Dead public members in the engine** *(F16)*: `Heard::total` and `Protocol::name` have no
  callers — both `ffi.rs` and `passivecheck.rs` hand-sum and hand-write the strings `name()` exists
  to provide. `LLDP_AND_STP_SECONDS`'s only use is its own const assertion. `pub` items do not trip
  `dead_code`, so clippy stays quiet.
- **`sweep.rs` derives the control index by hardcoding `arrived[0]`** while the discriminator index
  is derived from its flag *(CodeRabbit)*. Correct today and pinned by `the_control_is_swept_first`.
- **`addressed_to` searches `SWEEP` by name to recover an index it already had** *(CodeRabbit)*.
  Returning the index would remove a redundant lookup and make position rather than string equality
  the identity.
- **`[DllImport]` → `[LibraryImport]`**: verified mechanical and safe, but the payoff is AOT and
  trimming and nothing here publishes AOT. Bundle it with that change, not before.
- **The Core test project's fixture include has no `Link`** *(CodeRabbit)*, so the output path is
  inferred from the file name while `SweepSeamTests` asserts that exact location.

---

## Physical, needs the bench

### The bridged half of topology detection has never been run against copper

Every "something is in the path" result is verified by unit tests over synthetic inputs and nothing
else. The reference NETGEAR GS308 is a Broadcom BCM53128, which in unmanaged mode should drop
`01:80:C2:00:00:02`–`0F` and flood unknown multicast — so the expected result is a control frame
through and all three reserved probes absorbed.

**Until this has been run, a bridged verdict is untested.** The README says so.
