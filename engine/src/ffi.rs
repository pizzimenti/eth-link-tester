//! The C ABI the managed host calls.
//!
//! Every entry point catches panics. Unwinding across an FFI boundary is undefined behaviour, and
//! the usual remedy for a cdylib is `panic = "abort"` - which is wrong for this system. The host
//! process holds the restore journal and is the only thing that knows how to put a forced adapter
//! back, so aborting would turn a recoverable engine bug into a NIC stranded at whatever speed the
//! run set it to. Catching converts the same bug into a failed run that the host can recover from.

use std::panic::{catch_unwind, AssertUnwindSafe};
use std::sync::atomic::{AtomicU32, Ordering};
use std::sync::Mutex;

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
const STATE_STOPPED: u32 = 2;

/// Opaque handle. The host never dereferences this.
///
/// # Why the allocation is never freed
///
/// The C ABI cannot stop a caller from draining a handle that has been stopped, or from stopping
/// the same handle twice. Both were reachable and both were fatal: drain-after-stop faulted with an
/// access violation, double-stop corrupted the heap.
///
/// A state word alone does not fix that, which was the mistake in the first attempt. `stop` freed
/// the allocation the moment it won the transition - and the state word lives *inside* that
/// allocation, so every later call read a freed byte to decide whether the handle was alive. The
/// check and the thing it was checking died together.
///
/// So `stop` drops the [`Engine`] - the threads, the capture devices, everything expensive - and
/// leaves this struct allocated as a tombstone. A late or repeated call then reads a valid
/// `STATE_STOPPED` and gets an error code. The leak is about a hundred bytes per run, which after
/// ten thousand runs is under a megabyte; the alternative is an access violation, and on this
/// system an access violation is worse than it sounds. The whole reason this crate unwinds rather
/// than aborts is that the managed host holds the restore journal and is the only thing that can
/// put a forced adapter back - a crash strands the adapter exactly as thoroughly as an abort would,
/// and takes the recovery with it.
///
/// The mutex, not the state word, is what makes concurrent access safe. The state word only
/// answers "has this been stopped", which the `Option` inside confirms authoritatively.
pub struct EngineHandle {
    state: AtomicU32,
    engine: Mutex<Option<Engine>>,
}

impl EngineHandle {
    /// Runs `body` against the live engine, or returns a code when there is none.
    ///
    /// A poisoned mutex means a previous call panicked while holding it, so the engine's state is
    /// unknown; refusing is the only honest answer.
    fn with_engine(&self, body: impl FnOnce(&Engine) -> i32) -> i32 {
        if self.state.load(Ordering::Acquire) != STATE_RUNNING {
            return ELT_ERR_NOT_RUNNING;
        }

        match self.engine.lock() {
            Ok(guard) => match guard.as_ref() {
                Some(engine) => body(engine),
                None => ELT_ERR_NOT_RUNNING,
            },
            Err(_) => ELT_ERR_PANIC,
        }
    }
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
                    engine: Mutex::new(Some(engine)),
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

    // Sound because the allocation is never freed - see EngineHandle.
    let engine_handle = unsafe { &*handle };

    let result = catch_unwind(AssertUnwindSafe(|| {
        engine_handle.with_engine(|engine| {
            let out = unsafe { std::slice::from_raw_parts_mut(samples, capacity as usize) };
            // The mutex held across this call is what satisfies the ring's single-consumer
            // requirement: two hosts draining at once are serialised rather than interleaved.
            let drained = engine.ring().drain(out);

            if !out_dropped.is_null() {
                unsafe { *out_dropped = drained.dropped };
            }

            // Non-negative is a count; negative is a code. The host checks the sign.
            i32::try_from(drained.count).unwrap_or(i32::MAX)
        })
    }));

    result.unwrap_or(ELT_ERR_PANIC)
}

/// Stops sending while receive and the sampler keep running, so delivery can be counted honestly.
///
/// The host must call this, wait for the wire and the sampler to settle, drain once more, and only
/// then call [`elt_engine_stop`]. Counting the moment transmit ends is always short and always in
/// the same direction: the driver's send queue still holds frames already counted as sent, and the
/// receive thread folds its kernel counts into the shared totals once per sample. On the reference
/// rig that gap was 0.3% at 1518 bytes - the same size as the loss it was being read as, and the
/// reason a healthy cable reported 99.7% delivered instead of 100.00%.
///
/// Safe to call more than once, and safe on a handle that is already stopped, which answers
/// `ELT_ERR_NOT_RUNNING`.
///
/// # Safety
/// `handle` must come from `elt_engine_start`.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn elt_engine_stop_transmit(handle: *mut EngineHandle) -> i32 {
    if handle.is_null() {
        return ELT_ERR_NULL_ARGUMENT;
    }

    // Sound because the allocation is never freed - see EngineHandle.
    let engine_handle = unsafe { &*handle };

    let result = catch_unwind(AssertUnwindSafe(|| {
        engine_handle.with_engine(|engine| {
            engine.stop_transmit();
            ELT_OK
        })
    }));

    result.unwrap_or(ELT_ERR_PANIC)
}

/// Stops the engine. Safe to call more than once, and safe to call while a drain is in flight.
///
/// The handle's allocation deliberately outlives this - see [`EngineHandle`] - so a host that
/// drains once more after stopping gets `ELT_ERR_NOT_RUNNING` rather than an access violation.
///
/// # Safety
/// `handle` must come from `elt_engine_start`.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn elt_engine_stop(handle: *mut EngineHandle) -> i32 {
    if handle.is_null() {
        return ELT_ERR_NULL_ARGUMENT;
    }

    // Sound because the allocation is never freed - see EngineHandle.
    let engine_handle = unsafe { &*handle };

    let result = catch_unwind(AssertUnwindSafe(|| {
        // Only one caller wins the transition, so a second stop is refused rather than joining the
        // worker threads twice.
        if engine_handle
            .state
            .compare_exchange(
                STATE_RUNNING,
                STATE_STOPPED,
                Ordering::AcqRel,
                Ordering::Relaxed,
            )
            .is_err()
        {
            return ELT_ERR_NOT_RUNNING;
        }

        match engine_handle.engine.lock() {
            // Taking the engine out and dropping it stops the threads and joins them; the engine's
            // own Drop does the work. The handle itself stays allocated as a tombstone, so a late
            // drain reads a valid stopped state instead of freed memory.
            Ok(mut guard) => {
                drop(guard.take());
                ELT_OK
            }
            // Poison is a previous panic inside a locked section, which is remote. What it must not
            // do is leave the engine running: the CAS above already moved this handle to
            // STATE_STOPPED, so every later call answers ELT_ERR_NOT_RUNNING and no caller can ever
            // reach the engine again - three worker threads would go on saturating a physical
            // adapter until the process exits, with nothing left able to stop them.
            //
            // The data behind a poisoned mutex is still there and this is the last caller that will
            // ever see it, so take the engine out and drop it. The code still reports the panic;
            // the difference is that the NIC goes quiet.
            Err(poisoned) => {
                drop(poisoned.into_inner().take());
                ELT_ERR_PANIC
            }
        }
    }));

    result.unwrap_or(ELT_ERR_PANIC)
}

/// Why the run stopped producing: 0 none, 1 receive, 2 transmit, 3 a worker panicked,
/// 4 transmit exceeded line rate.
///
/// Code 4 is the one a host is most likely to mishandle, because it is not a stopped worker: the
/// run is still producing, and producing figures that are impossible. A driver whose link has gone
/// accepts frames at memory speed and discards them, which read as 11,336 Mbps on a gigabit cable
/// beside a receive count that looked like 92% loss. It is reported rather than clamped, so the
/// host must treat it as a fault and not as a measurement.
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

    // Sound because the allocation is never freed - see EngineHandle.
    let engine_handle = unsafe { &*handle };

    let result = catch_unwind(AssertUnwindSafe(|| {
        engine_handle.with_engine(|engine| engine.fault() as i32)
    }));

    result.unwrap_or(ELT_ERR_PANIC)
}

/// The size the host must agree on. Checked at startup so a layout drift fails loudly at load
/// rather than silently producing misaligned telemetry.
#[unsafe(no_mangle)]
pub extern "C" fn elt_sample_size() -> u32 {
    core::mem::size_of::<TelemetrySample>() as u32
}
