//! Makes `wpcap.dll` a delay-loaded import rather than a load-time one.
//!
//! Two problems, one fix.
//!
//! Npcap's runtime licence forbids redistribution, so CI links against the SDK's import library
//! with no capture driver installed. That worked only by accident: nothing in the test binary
//! referenced pcap, so the linker dropped the import entirely. The moment a test called
//! `Engine::start` - to check that it refuses one adapter for both directions, which never reaches
//! pcap - the import came back, and every test binary began failing at *load* with
//! STATUS_DLL_NOT_FOUND. A test that does not call pcap should not need pcap present.
//!
//! At run time it is what the managed host already assumes. Npcap installs into `System32\Npcap`,
//! which is not on the default search path, so `NpcapLoader` loads the libraries by absolute path
//! before the engine is touched. A load-time import is resolved before any managed code runs, so
//! that pre-load happens too late to help; a delay-loaded one is resolved on first call, by which
//! point the module is already in the process and `LoadLibrary` returns the existing handle without
//! searching anywhere.
//!
//! The cost is that a missing Npcap surfaces as an exception on first use rather than a failure to
//! load, which is strictly better: the app's preflight check reports it in words, where the load
//! failure was exit code 53 with no output and no diagnostic.

fn main() {
    println!("cargo::rerun-if-changed=build.rs");

    // MSVC only. The GNU toolchain has no delay-load mechanism, and this crate has no reason to be
    // built with it - Npcap is Windows-only and the managed host is MSVC-linked.
    if std::env::var("CARGO_CFG_TARGET_ENV").as_deref() == Ok("msvc") {
        println!("cargo::rustc-link-arg=/DELAYLOAD:wpcap.dll");
        // The helper the delay-load stubs call into.
        println!("cargo::rustc-link-arg=delayimp.lib");
    }
}
