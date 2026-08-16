//! Exercises the C ABI's lifetime contract the way a buggy host would.
//!
//! Two P1 defects have lived in `ffi.rs`, and both were about what happens *after* a handle stops:
//! draining one that has been stopped faulted with an access violation, and stopping the same
//! handle twice corrupted the heap. Neither is reachable from the managed host as written - it
//! nulls its pointer after a successful stop - so neither would ever be caught by using the app.
//! That is exactly why this exists: the ABI's promise is to survive misuse, and a promise nothing
//! tests is a promise nothing keeps.
//!
//! Every call here is one a host could legally make through the published header. None of them may
//! crash; all of them must return a code.

use std::ffi::CString;

use ethlink_engine::diag::{device, mac, require_npcap};
use ethlink_engine::ffi::{
    elt_engine_drain, elt_engine_fault, elt_engine_start, elt_engine_stop, EngineHandle,
    ELT_ERR_NOT_RUNNING, ELT_ERR_NULL_ARGUMENT, ELT_OK,
};
use ethlink_engine::TelemetrySample;

fn check(label: &str, actual: i32, expected: i32) -> bool {
    let ok = actual == expected;
    println!(
        "  {:<44} {:>4} {}",
        label,
        actual,
        if ok { "ok" } else { "UNEXPECTED" }
    );
    ok
}

fn main() {
    let args: Vec<String> = std::env::args().collect();
    if args.len() < 5 {
        eprintln!("usage: abicheck <tx-guid> <rx-guid> <tx-mac> <rx-mac>");
        std::process::exit(2);
    }
    require_npcap();

    let tx_name = CString::new(device(&args[1]).expect("no device matching the TX GUID")).unwrap();
    let rx_name = CString::new(device(&args[2]).expect("no device matching the RX GUID")).unwrap();
    let tx_mac = mac(&args[3]).expect("bad TX MAC");
    let rx_mac = mac(&args[4]).expect("bad RX MAC");

    let mut samples = [TelemetrySample::default(); 64];
    let mut dropped = 0u64;
    let mut passed = true;

    println!("null arguments");
    passed &= check(
        "start with a null out-handle",
        unsafe {
            elt_engine_start(
                tx_name.as_ptr(),
                rx_name.as_ptr(),
                tx_mac.as_ptr(),
                rx_mac.as_ptr(),
                1514,
                1_000_000_000,
                std::ptr::null_mut(),
            )
        },
        ELT_ERR_NULL_ARGUMENT,
    );
    passed &= check(
        "drain a null handle",
        unsafe {
            elt_engine_drain(
                std::ptr::null_mut(),
                samples.as_mut_ptr(),
                samples.len() as u32,
                &mut dropped,
            )
        },
        ELT_ERR_NULL_ARGUMENT,
    );
    passed &= check(
        "stop a null handle",
        unsafe { elt_engine_stop(std::ptr::null_mut()) },
        ELT_ERR_NULL_ARGUMENT,
    );
    passed &= check(
        "fault of a null handle",
        unsafe { elt_engine_fault(std::ptr::null_mut()) },
        ELT_ERR_NULL_ARGUMENT,
    );

    println!("\na real run");
    let mut handle: *mut EngineHandle = std::ptr::null_mut();
    passed &= check(
        "start",
        unsafe {
            elt_engine_start(
                tx_name.as_ptr(),
                rx_name.as_ptr(),
                tx_mac.as_ptr(),
                rx_mac.as_ptr(),
                1514,
                1_000_000_000,
                &mut handle,
            )
        },
        ELT_OK,
    );

    if handle.is_null() {
        println!("\nVERDICT : FAILED - start returned no handle");
        std::process::exit(1);
    }

    std::thread::sleep(std::time::Duration::from_millis(600));

    let drained = unsafe {
        elt_engine_drain(
            handle,
            samples.as_mut_ptr(),
            samples.len() as u32,
            &mut dropped,
        )
    };
    println!(
        "  {:<44} {:>4} {}",
        "drain while running",
        drained,
        if drained >= 0 { "ok" } else { "UNEXPECTED" }
    );
    passed &= drained >= 0;

    let fault = unsafe { elt_engine_fault(handle) };
    passed &= check("fault while running", fault, 0);

    println!("\nafter stopping - every one of these was a crash");
    passed &= check("stop", unsafe { elt_engine_stop(handle) }, ELT_OK);
    passed &= check(
        "stop again (double free)",
        unsafe { elt_engine_stop(handle) },
        ELT_ERR_NOT_RUNNING,
    );
    passed &= check(
        "stop a third time",
        unsafe { elt_engine_stop(handle) },
        ELT_ERR_NOT_RUNNING,
    );
    passed &= check(
        "drain after stop (use after free)",
        unsafe {
            elt_engine_drain(
                handle,
                samples.as_mut_ptr(),
                samples.len() as u32,
                &mut dropped,
            )
        },
        ELT_ERR_NOT_RUNNING,
    );
    passed &= check(
        "fault after stop",
        unsafe { elt_engine_fault(handle) },
        ELT_ERR_NOT_RUNNING,
    );

    println!(
        "\nVERDICT : {}",
        if passed {
            "THE ABI SURVIVES MISUSE"
        } else {
            "FAILED - see the unexpected codes above"
        }
    );
    std::process::exit(i32::from(!passed));
}
