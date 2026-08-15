//! Argument parsing shared by the diagnostic binaries.
//!
//! Lives in the library rather than being copied into each `bin` because three copies of "turn
//! `AA-BB-CC-DD-EE-FF` into six bytes" is three chances to disagree about separators, and because
//! a `mod` shared between binaries needs a `#[path]` attribute in every one of them - more
//! machinery than twenty lines of parsing is worth. Nothing here is exported over the C ABI, so
//! the cdylib is unaffected.

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
