//! The C ABI the managed host calls.
//!
//! Every entry point catches panics. Unwinding across an FFI boundary is undefined behaviour, and
//! the usual remedy for a cdylib is `panic = "abort"` - which is wrong for this system. The host
//! process holds the restore journal and is the only thing that knows how to put a forced adapter
//! back, so aborting would turn a recoverable engine bug into a NIC stranded at whatever speed the
//! run set it to. Catching converts the same bug into a failed run that the host can recover from.

use std::panic::{catch_unwind, AssertUnwindSafe};
use std::sync::atomic::{AtomicU32, Ordering};

use crate::engine::{Engine, RunConfig, StartError};
use crate::TelemetrySample;

/// Result codes. Zero is success; everything else is a reason the host can report.
pub const ELT_OK: i32 = 0;
pub const ELT_ERR_NULL_ARGUMENT: i32 = -1;
pub const ELT_ERR_BAD_UTF8: i32 = -2;
pub const ELT_ERR_OPEN_FAILED: i32 = -3;
pub const ELT_ERR_PANIC: i32 = -4;
/// The handle is stopped, or another thread is stopping or draining it.
pub const ELT_ERR_NOT_RUNNING: i32 = -5;
/// Transmit and receive name the same adapter, so no frame would cross a cable.
pub const ELT_ERR_SAME_DEVICE: i32 = -6;
/// The frame length is outside what a legal Ethernet frame can carry.
pub const ELT_ERR_FRAME_LENGTH: i32 = -7;

const STATE_RUNNING: u32 = 0;
const STATE_BUSY: u32 = 1;
const STATE_STOPPED: u32 = 2;

/// Opaque handle. The host never dereferences this.
///
/// Carries its own state word because the C ABI cannot stop a caller from draining a handle that
/// another thread is freeing, or from stopping the same handle twice. Both were reachable and both
/// were fatal: drain-after-stop faulted with an access violation, double-stop corrupted the heap.
/// Neither is something `catch_unwind` can help with - the process is already gone.
///
/// That matters more here than in most libraries. The whole reason this crate unwinds rather than
/// aborts is that the managed host holds the restore journal and is the only thing that can put a
/// forced adapter back; an access violation strands the adapter exactly as thoroughly as an abort
/// would, and takes the recovery with it.
pub struct EngineHandle {
    state: AtomicU32,
    engine: Engine,
}

/// # Safety
/// `tx_device` and `rx_device` must be NUL-terminated UTF-8. `tx_mac` and `rx_mac` must each point
/// to six readable bytes. `out_handle` must be a writable pointer.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn elt_engine_start(
    tx_device: *const std::ffi::c_char,
    rx_device: *const std::ffi::c_char,
    tx_mac: *const u8,
    rx_mac: *const u8,
    frame_len: u32,
    link_bits_per_second: u64,
    out_handle: *mut *mut EngineHandle,
) -> i32 {
    if tx_device.is_null()
        || rx_device.is_null()
        || tx_mac.is_null()
        || rx_mac.is_null()
        || out_handle.is_null()
    {
        return ELT_ERR_NULL_ARGUMENT;
    }

    let result = catch_unwind(AssertUnwindSafe(|| {
        let tx = match unsafe { std::ffi::CStr::from_ptr(tx_device) }.to_str() {
            Ok(text) => text.to_owned(),
            Err(_) => return ELT_ERR_BAD_UTF8,
        };
        let rx = match unsafe { std::ffi::CStr::from_ptr(rx_device) }.to_str() {
            Ok(text) => text.to_owned(),
            Err(_) => return ELT_ERR_BAD_UTF8,
        };

        let mut tx_address = [0u8; 6];
        let mut rx_address = [0u8; 6];
        unsafe {
            std::ptr::copy_nonoverlapping(tx_mac, tx_address.as_mut_ptr(), 6);
            std::ptr::copy_nonoverlapping(rx_mac, rx_address.as_mut_ptr(), 6);
        }

        let config = RunConfig {
            tx_device: tx,
            rx_device: rx,
            tx_mac: tx_address,
            rx_mac: rx_address,
            frame_len: frame_len as usize,
            link_bits_per_second,
        };

        match Engine::start(config) {
            Ok(engine) => {
                let handle = Box::into_raw(Box::new(EngineHandle {
                    state: AtomicU32::new(STATE_RUNNING),
                    engine,
                }));
                unsafe { *out_handle = handle };
                ELT_OK
            }
            Err(error) => {
                // Cleared so a caller that ignores the code cannot mistake whatever was on the
                // stack for a handle.
                unsafe { *out_handle = std::ptr::null_mut() };
                match error {
                    StartError::SameDevice => ELT_ERR_SAME_DEVICE,
                    StartError::FrameLength(_) => ELT_ERR_FRAME_LENGTH,
                    StartError::Capture(_) => ELT_ERR_OPEN_FAILED,
                }
            }
        }
    }));

    result.unwrap_or(ELT_ERR_PANIC)
}

/// Copies available samples into the caller's buffer.
///
/// Copying rather than lending a view into the ring. The plan called for the host to read the ring
/// directly through a span over a raw pointer, which at 60 Hz and 64 bytes a sample saves four
/// kilobytes a second - not worth one lifetime hazard reaching across an FFI boundary. A bulk copy
/// of a whole batch still satisfies the constraint that mattered: no per-sample marshalling.
///
/// `out_dropped` receives the number of samples overwritten before the host reached them, so a gap
/// in the history is visible rather than drawn across as though it were continuous.
///
/// # Safety
/// `handle` must come from `elt_engine_start`. `samples` must be writable for `capacity` samples.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn elt_engine_drain(
    handle: *mut EngineHandle,
    samples: *mut TelemetrySample,
    capacity: u32,
    out_dropped: *mut u64,
) -> i32 {
    if handle.is_null() || samples.is_null() {
        return ELT_ERR_NULL_ARGUMENT;
    }

    let result = catch_unwind(AssertUnwindSafe(|| {
        let engine_handle = unsafe { &*handle };

        // Claims the handle for the duration. A concurrent stop cannot free it underneath us, and
        // a concurrent drain cannot advance the read cursor in parallel - the ring is
        // single-consumer, and two drains would deliver overlapping samples to both callers.
        if engine_handle
            .state
            .compare_exchange(
                STATE_RUNNING,
                STATE_BUSY,
                Ordering::Acquire,
                Ordering::Relaxed,
            )
            .is_err()
        {
            return ELT_ERR_NOT_RUNNING;
        }

        let out = unsafe { std::slice::from_raw_parts_mut(samples, capacity as usize) };
        // Sound because the state word above guarantees this is the only drain in flight.
        let drained = unsafe { engine_handle.engine.ring().drain(out) };

        if !out_dropped.is_null() {
            unsafe { *out_dropped = drained.dropped };
        }

        engine_handle.state.store(STATE_RUNNING, Ordering::Release);

        // Non-negative is a count; negative is a code. The host checks the sign.
        i32::try_from(drained.count).unwrap_or(i32::MAX)
    }));

    result.unwrap_or(ELT_ERR_PANIC)
}

/// Stops the engine and frees the handle. Safe to call once per handle.
///
/// # Safety
/// `handle` must come from `elt_engine_start` and must not be used afterwards.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn elt_engine_stop(handle: *mut EngineHandle) -> i32 {
    if handle.is_null() {
        return ELT_ERR_NULL_ARGUMENT;
    }

    let result = catch_unwind(AssertUnwindSafe(|| {
        let engine_handle = unsafe { &*handle };

        // Only the transition from Running frees. A second stop, or one racing a drain, is refused
        // rather than double-freeing - the difference between an error code and heap corruption.
        if engine_handle
            .state
            .compare_exchange(
                STATE_RUNNING,
                STATE_STOPPED,
                Ordering::Acquire,
                Ordering::Relaxed,
            )
            .is_err()
        {
            return ELT_ERR_NOT_RUNNING;
        }

        // Dropping stops the threads and joins them; the engine's Drop does the work.
        drop(unsafe { Box::from_raw(handle) });
        ELT_OK
    }));

    result.unwrap_or(ELT_ERR_PANIC)
}

/// Why the run stopped producing: 0 none, 1 receive, 2 transmit, 3 a worker panicked.
///
/// The host has to be able to ask. A dead worker leaves transmit running and the receive count
/// frozen, which renders as 100% packet loss on a healthy cable - the most alarming thing this app
/// can display, and, when it happened, entirely false. Without this the only signal was silence.
///
/// Returns a negative result code for a null or stopped handle, which cannot collide with a fault
/// value.
///
/// # Safety
/// `handle` must come from `elt_engine_start` and must not have been stopped.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn elt_engine_fault(handle: *mut EngineHandle) -> i32 {
    if handle.is_null() {
        return ELT_ERR_NULL_ARGUMENT;
    }

    let result = catch_unwind(AssertUnwindSafe(|| {
        let engine_handle = unsafe { &*handle };

        // Same claim the drain takes, for the same reason: a concurrent stop must not free the
        // engine out from under this read.
        if engine_handle
            .state
            .compare_exchange(
                STATE_RUNNING,
                STATE_BUSY,
                Ordering::Acquire,
                Ordering::Relaxed,
            )
            .is_err()
        {
            return ELT_ERR_NOT_RUNNING;
        }

        let fault = engine_handle.engine.fault() as i32;
        engine_handle.state.store(STATE_RUNNING, Ordering::Release);
        fault
    }));

    result.unwrap_or(ELT_ERR_PANIC)
}

/// The size the host must agree on. Checked at startup so a layout drift fails loudly at load
/// rather than silently producing misaligned telemetry.
#[unsafe(no_mangle)]
pub extern "C" fn elt_sample_size() -> u32 {
    core::mem::size_of::<TelemetrySample>() as u32
}
