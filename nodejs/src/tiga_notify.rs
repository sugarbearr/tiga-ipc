use std::ffi::OsString;
use std::io;
use std::path::{Path, PathBuf};
use std::time::Duration;

use crate::tiga_sys::open_file_shared;

pub(super) const EVENT_PREFIX: &str = "tiga_wait_handle_";
pub(super) const NOTIFICATION_SUFFIX: &str = "_notify";
pub(super) const NOTIFICATION_EVENT_SUFFIX: &str = "_slot_";
pub(super) const NOTIFICATION_SLOT_COUNT: usize = 128;
// .NET DateTime ticks are based on 0001-01-01, while Win32 FILETIME uses
// 1601-01-01. TigaIpc stores process start times using DateTime.UtcTicks,
// so we must convert FILETIME values into the same epoch for interop.
pub(super) const DOTNET_FILETIME_EPOCH_OFFSET_TICKS: i64 = 504_911_232_000_000_000;

#[repr(C)]
#[derive(Clone, Copy, Default)]
pub(super) struct NotificationSlot {
    pub(super) token: i64,
    pub(super) owner_process_start_time_utc_ticks: i64,
    pub(super) owner_process_id: i32,
    pub(super) reserved: i32,
}

#[cfg(windows)]
#[path = "tiga_notify_windows.rs"]
mod imp;

#[cfg(not(windows))]
mod imp {
    use std::io;
    use std::path::Path;
    use std::time::Duration;

    pub struct NotificationListener;

    impl NotificationListener {
        pub fn register(_prefix: &Path) -> io::Result<Self> {
            Ok(Self)
        }

        pub fn wait(&self, _timeout: Duration) -> bool {
            true
        }
    }

    pub fn signal_updated(_prefix: &Path) -> io::Result<bool> {
        Ok(false)
    }

    pub fn wait_for_listener(_prefix: &Path, _timeout: Duration) -> bool {
        true
    }

    #[cfg(test)]
    pub(super) fn filetime_ticks_to_datetime_utc_ticks(filetime_ticks: i64) -> i64 {
        filetime_ticks
    }
}

pub use imp::NotificationListener;

pub fn ensure_notification_layout(prefix: &Path) -> io::Result<()> {
    let path = notification_path(prefix);
    let file = open_file_shared(&path, true, true, true)?;
    let expected_len = notification_file_len() as u64;
    if file.metadata()?.len() != expected_len {
        file.set_len(expected_len)?;
    }

    Ok(())
}

pub fn signal_updated(prefix: &Path) -> bool {
    imp::signal_updated(prefix).unwrap_or(false)
}

pub fn wait_for_listener(prefix: &Path, timeout: Duration) -> bool {
    imp::wait_for_listener(prefix, timeout)
}

pub(super) fn notification_file_len() -> usize {
    std::mem::size_of::<NotificationSlot>() * NOTIFICATION_SLOT_COUNT
}

pub(super) fn notification_path(prefix: &Path) -> PathBuf {
    append_suffix(prefix, NOTIFICATION_SUFFIX)
}

fn append_suffix(path: &Path, suffix: &str) -> PathBuf {
    let mut value = OsString::from(path.as_os_str());
    value.push(suffix);
    PathBuf::from(value)
}

#[cfg(test)]
mod tests {
    use super::{
        ensure_notification_layout, signal_updated, wait_for_listener, NotificationListener,
    };
    use std::path::PathBuf;
    use std::time::Duration;

    #[cfg(windows)]
    #[test]
    fn notification_listener_receives_signal_and_unregisters_cleanly() {
        let base_dir =
            std::env::temp_dir().join(format!("mmap_napi_notify_{}", uuid::Uuid::new_v4()));
        std::fs::create_dir_all(&base_dir).expect("create temp directory");
        let prefix = PathBuf::from(base_dir.join("tiga_notify_test"));

        ensure_notification_layout(&prefix).expect("ensure notification layout");
        let listener =
            NotificationListener::register(&prefix).expect("register notification listener");
        assert!(wait_for_listener(&prefix, Duration::from_millis(100)));
        assert!(signal_updated(&prefix));
        assert!(listener.wait(Duration::from_secs(1)));
        drop(listener);
        assert!(!wait_for_listener(&prefix, Duration::from_millis(100)));

        let _ = std::fs::remove_file(super::notification_path(&prefix));
        let _ = std::fs::remove_dir_all(base_dir);
    }

    #[cfg(windows)]
    #[test]
    fn converts_filetime_ticks_to_dotnet_datetime_ticks() {
        // 1970-01-01T00:00:00Z in Win32 FILETIME ticks.
        let filetime_ticks = 116_444_736_000_000_000i64;
        // 1970-01-01T00:00:00Z in .NET DateTime.UtcTicks.
        let expected_datetime_ticks = 621_355_968_000_000_000i64;
        assert_eq!(
            super::imp::filetime_ticks_to_datetime_utc_ticks(filetime_ticks),
            expected_datetime_ticks
        );
    }
}
