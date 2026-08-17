# Contributing

Thanks for taking a look. This document covers the parts of the project that are not obvious
from the code.

## Getting a build

You need the **.NET 10 SDK**. Nothing else, until Phase 3.

```
dotnet build
dotnet test
```

Visual Studio is *not* required — the app builds with the SDK alone. Rust and VS Build Tools are
only needed once the native engine lands in Phase 3.

## You do not need hardware

**Simulation mode runs the entire UI with no NICs, no cable, and no Npcap installed.** This is
deliberate and load-bearing: it is the only way most contributors can work on the app, so it is
treated as a first-class path rather than a test fixture. If you change engine-facing code, make
sure simulation mode still runs.

## Version pinning is intentional

Three files pin the toolchain, and they should be edited deliberately rather than drifted:

| File | Pins |
|---|---|
| `global.json` | .NET SDK feature band |
| `Directory.Packages.props` | Every NuGet version (central package management) |
| `rust-toolchain.toml` | Rust compiler (from Phase 3) |

Because versions are centrally managed, **`PackageReference` entries must not carry a `Version`
attribute** — add the version to `Directory.Packages.props` instead.

## Branching and versions

`main` always holds a tagged, working build. Work happens on phase-named branches and lands via
squash-merged PR.

Versions are `0.<phase>.<pr>` — `0.3.3` means phase 3, third PR. A phase ends when its design
requirements are met, not at a fixed patch number.

## Project layout

```
src/EthLinkTester.Core       platform-neutral models, abstractions, orchestration, grading
src/EthLinkTester.Platform   the Windows half - CIM, NDIS properties, Npcap, the native engine host
src/EthLinkTester.App        WinUI 3 shell
tests/                       xunit
engine/                      Rust packet engine (Phase 3) and its verification binaries
tools/                       PowerShell harnesses that bracket a run with the NICs' own counters
```

`Core` deliberately has **no Windows dependency** so it stays unit-testable without a desktop
session. Anything touching NDIS, WMI, or the registry belongs in the platform layer.

## Things that are easy to get wrong

- **Never test throughput over sockets.** With two NICs in one host, Windows short-circuits
  local-to-local traffic through the loopback path and the packets never reach copper. Everything
  goes through raw L2 injection for this reason.
- **Loss and volume come from NIC hardware counters, not userspace capture.** At rate, capture
  drops frames and you cannot distinguish that from cable loss.
- **Nothing may mutate an adapter property before the write-ahead restore journal has recorded
  the original value.** A crash mid-run can otherwise strand a NIC forced to 100 Half with
  offloads disabled, and the user has no way to discover why their network broke.
- **Verdicts must never be signalled by colour alone.** Always icon plus text. This is a pass/fail
  tool and colour-blind users need the same information.

## Reporting honestly

The project's core claim is that it tells you the truth about a link, so reports must state their
own limits: this measures *behavior*, not TIA-568 physical-layer parameters. It cannot replace a
certifier. Bit-error-rate claims carry the confidence interval actually achieved. A detected
switch downgrades confidence, because a fault cannot be attributed to a specific cable without a
swap test.

## License

MIT. By contributing you agree your contributions are licensed under it.
