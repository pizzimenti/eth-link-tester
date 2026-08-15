# eth-link-tester

A Windows 11 tool that characterizes Ethernet **links** by running real traffic across a physical
loop between two NICs in one machine — then tells you honestly how much of the result is the cable
and how much is everything else in the path.

> **Status: pre-alpha.** Phase 3 complete — the engine puts real frames on real copper and Lab Mode
> plots them live. Topology detection, the RFC 2544 orchestration, and grading are still ahead.
> See [Build phases](#build-phases) and [What has actually been measured](#what-has-actually-been-measured).

## Why this exists

Plugging a cable between two NICs on the same computer and running `iperf3` against yourself
measures **nothing about the cable**. Windows' TCP/IP stack recognizes both addresses as local and
short-circuits the traffic through the loopback path — the packets never reach copper. You get a
number that looks like a great result and is actually RAM bandwidth.

`eth-link-tester` injects raw Ethernet frames through Npcap's NDIS lightweight filter, which sits
*below* the TCP/IP stack. There is no routing decision to short-circuit, so the frames genuinely
leave the PHY, cross the cable, and come back in the other port.

It also refuses to pretend throughput alone grades a cable. A marginal cable frequently shows full
throughput because the PHY silently error-corrects. The signals that actually matter are
negotiated speed vs. mutual capability, CRC error rates from NIC hardware counters, time-to-link,
and link retrains under sustained load.

## What it measures

- **Link characterization** — time-to-link, downshift detection, master/slave resolution, MDI/MDI-X,
  pause capability, EEE state, forced 10/100 half and full duplex sweeps
- **Topology detection** — whether a switch is sitting in the middle, using a reserved-multicast
  probe that conforming 802.1D bridges are required *not* to forward, backed by passive LLDP/CDP/STP
  observation and latency-vs-frame-size slope analysis
- **RFC 2544 frame sweep** — zero-loss throughput by binary search, latency distribution
  (p50/p99/p99.9, not mean), frame loss rate, back-to-back burst tolerance
- **Soak testing** — sustained load watching error-counter deltas, link flaps, and PHY retrains.
  Cables that pass idle and fail under load are the classic marginal case, and only this finds them.
- **A diagnostic ladder** — the highest speed a link will negotiate is itself a coarse
  length-and-health estimate, free of extra hardware

Supported: 10BASE-T (as a reachability probe only), 100BASE-TX, 1000BASE-T, 2.5GBASE-T, 5GBASE-T,
10GBASE-T. The test matrix is derived from probed adapter capability, so the app only offers tiers
your hardware can actually reach.

## What has actually been measured

The central claim — that Npcap injection reaches copper — is not taken on trust. `engine/src/bin/`
holds the three tools that established it, and their results on the reference rig (a Killer E2400
and a Realtek USB GbE adapter joined by one cable) are these:

| | Result |
|---|---|
| Frames crossing the wire | 1000 sent, 1000 received, confirmed on **both** NICs' own hardware counters, with no reverse traffic |
| 1518-byte frames | 900 Mbps sustained, p50 520 µs, p99 780 µs, 99.7% delivered |
| 64-byte frames | 188 Mbps (280k frames/s), p50 3.7 ms, p99 5.9 ms |
| A link dropped mid-run | Reported as a fault within half a second, naming the cause, and the run torn down |

Three caveats the app repeats wherever it shows these. The 64-byte figures describe the
**transmitting NIC**, not the cable: the same cable measured in the other direction is four times
faster, because the limit is the Killer's packet path. The latency figures still include the
driver's own send buffer, so they bound cable latency rather than measuring it — closing that gap
needs NIC hardware timestamping, which neither adapter here provides. And throughput is understated
by roughly 3.5%, because the latency probe is sent separately from the bulk traffic and that leaves
a bubble in the driver's pipeline; RFC 2544 measures the two in separate tests for exactly this
reason, which is what Phase 5 will do.

## What it is not

This measures **behavior**, not physical-layer parameters. It does not and cannot replace a
certifier like a Fluke DSX — there is no NEXT, return loss, or TIA-568 certification here. Reports
say so explicitly.

## Requirements

- Windows 11
- **Two Ethernet interfaces** connected to each other. A crossover cable is *not* needed —
  Auto-MDI-X is mandatory in 1000BASE-T, so straight-through works direct NIC-to-NIC.
- [Npcap](https://npcap.com) — installed separately, with *WinPcap-compatible mode off*. Npcap's
  license does not permit redistribution, so it cannot be bundled; the app checks for it on first
  run and guides you through installing it.
- Administrator rights (raw packet injection and NDIS property changes require them)

A **simulation mode** runs the entire UI with no hardware and no Npcap installed, so you can work
on the app without a rig.

### Hardware notes

No single NIC pair covers the full 10M–10G range: 10G-class adapters such as the Intel X550-T2
dropped 10BASE-T entirely, and 1G adapters obviously cannot reach 10G. Full coverage is two
purpose-built rigs — a low-speed/long-run rig and a high-speed/short-cable rig — which is less of a
burden than it sounds, because long cables are inherently slow and short cables are where 10G
matters.

## Build phases

| Phase | Version | Contents |
|---|---|---|
| 0 | — | Environment, repo foundation, version pinning |
| 1 | v0.1.0 | WinUI 3 shell + simulation mode |
| 2 | v0.2.0 | Adapter layer, capability probe, write-ahead restore journal |
| 3 | v0.3.0 | Rust engine, Npcap raw L2 — *proves frames reach the wire* |
| 4 | v0.4.0 | Topology detection |
| 5 | v0.5.0 | Test orchestrator, RFC 2544 phases |
| 6 | v0.6.0 | Grading, SQLite persistence, cable library |
| 7 | v0.7.0 | Reporting and export |
| 8 | v0.8.0 | Lab Mode, soak testing |
| 9 | v0.9.0 | PoE placeholder + working voltage-drop calculator |
| — | v2.0.0 | PoE instrumentation (INA228 + RP2040 probes) |

`main` always holds a tagged, working build. Everything after v0.1.0 arrives as a branch and PR.

## Building

Requires the .NET 10 SDK. Rust and VS Build Tools are only needed from Phase 3 onward.

```
dotnet build
```

Versions are pinned in `global.json`, `Directory.Packages.props`, and (from Phase 3)
`rust-toolchain.toml`, so the build is reproducible regardless of what the host machine's
toolchain has drifted to.

## License

MIT — see [LICENSE](LICENSE).
