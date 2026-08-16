//! Argument parsing shared by the diagnostic binaries.
//!
//! Lives in the library rather than being copied into each `bin` because three copies of "turn
//! `AA-BB-CC-DD-EE-FF` into six bytes" is three chances to disagree about separators, and because
//! a `mod` shared between binaries needs a `#[path]` attribute in every one of them - more
//! machinery than twenty lines of parsing is worth. Nothing here is exported over the C ABI, so
//! the cdylib is unaffected.

/// Loads Npcap's libraries by absolute path, so a diagnostic binary runs from any shell.
///
/// Npcap installs into `System32\Npcap` rather than `System32`, and that directory is not on the
/// default library search path. Without this the tools die the moment they first touch pcap, with
/// exit code 53, no output, and no diagnostic - which is exactly how long it took to work out the
/// first time. The managed host does the same thing for the same reason; see `NpcapLoader`.
///
/// Returns false when Npcap is not installed, which the caller should report in words.
#[cfg(windows)]
pub fn ensure_npcap() -> bool {
    use std::os::windows::ffi::OsStrExt;

    unsafe extern "system" {
        fn LoadLibraryW(name: *const u16) -> *mut core::ffi::c_void;
    }

    let Some(root) = std::env::var_os("SystemRoot") else {
        return false;
    };

    // Packet.dll first: wpcap.dll depends on it, so loading it explicitly resolves that from a
    // known path rather than from whatever the search order turns up.
    ["Packet.dll", "wpcap.dll"].into_iter().all(|library| {
        let path = std::path::Path::new(&root)
            .join("System32")
            .join("Npcap")
            .join(library);

        let wide: Vec<u16> = path
            .as_os_str()
            .encode_wide()
            .chain(std::iter::once(0))
            .collect();

        // The handle is deliberately not kept: Windows reference-counts loaded modules and nothing
        // here ever wants to unload one.
        !unsafe { LoadLibraryW(wide.as_ptr()) }.is_null()
    })
}

/// Exits with a readable message when Npcap is missing.
#[cfg(windows)]
pub fn require_npcap() {
    if !ensure_npcap() {
        eprintln!(
            "Npcap is not installed, or not where this expects it \
             (%SystemRoot%\\System32\\Npcap).\n\
             Install it from https://npcap.com with WinPcap-compatible mode OFF."
        );
        std::process::exit(3);
    }
}

/// Parses `AA-BB-CC-DD-EE-FF` or `aa:bb:cc:dd:ee:ff`.
pub fn mac(text: &str) -> Option<[u8; 6]> {
    let mut out = [0u8; 6];
    let mut parts = text.split(['-', ':']);

    for byte in out.iter_mut() {
        *byte = u8::from_str_radix(parts.next()?, 16).ok()?;
    }

    // A seventh group means this was not a MAC address, and silently using the first six of
    // something else is how you spend an afternoon wondering where the frames went.
    parts.next().is_none().then_some(out)
}

/// Finds the Npcap device name containing `guid`, case-insensitively.
///
/// Callers pass the adapter GUID because the full device name (`\Device\NPF_{...}`) is awkward to
/// type and the GUID is what every Windows tool shows.
pub fn device(guid: &str) -> Option<String> {
    let wanted = guid.to_uppercase();

    pcap::Device::list()
        .ok()?
        .into_iter()
        .map(|found| found.name)
        .find(|name| name.to_uppercase().contains(&wanted))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn parses_both_separators() {
        assert_eq!(
            mac("00-1B-21-AA-BB-CC"),
            Some([0x00, 0x1B, 0x21, 0xAA, 0xBB, 0xCC])
        );
        assert_eq!(
            mac("00:1b:21:aa:bb:cc"),
            Some([0x00, 0x1B, 0x21, 0xAA, 0xBB, 0xCC])
        );
    }

    #[test]
    fn rejects_anything_that_is_not_six_bytes() {
        assert_eq!(mac("00-1B-21-AA-BB"), None);
        assert_eq!(mac("00-1B-21-AA-BB-CC-DD"), None);
        assert_eq!(mac("not a mac"), None);
        assert_eq!(mac("00-1B-21-AA-BB-ZZ"), None);
    }
}
