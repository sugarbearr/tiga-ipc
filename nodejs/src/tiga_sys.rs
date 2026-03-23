use std::fs::{File, OpenOptions};
use std::path::Path;

#[cfg(windows)]
pub fn open_file_shared(
    path: &Path,
    read: bool,
    write: bool,
    create: bool,
) -> std::io::Result<File> {
    use std::os::windows::fs::OpenOptionsExt;
    const FILE_SHARE_READ: u32 = 0x00000001;
    const FILE_SHARE_WRITE: u32 = 0x00000002;
    const FILE_SHARE_DELETE: u32 = 0x00000004;

    let mut opts = OpenOptions::new();
    opts.read(read).write(write).create(create);
    opts.share_mode(FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE);
    opts.open(path)
}

#[cfg(not(windows))]
pub fn open_file_shared(
    path: &Path,
    read: bool,
    write: bool,
    create: bool,
) -> std::io::Result<File> {
    let mut opts = OpenOptions::new();
    opts.read(read).write(write).create(create);
    opts.open(path)
}

pub fn now_timestamp_ticks() -> i64 {
    #[cfg(windows)]
    unsafe {
        use windows_sys::Win32::System::Performance::QueryPerformanceCounter;
        let mut value: i64 = 0;
        let _ = QueryPerformanceCounter(&mut value);
        return value;
    }

    #[cfg(not(windows))]
    {
        use std::sync::OnceLock;
        use std::time::Instant;
        static START: OnceLock<Instant> = OnceLock::new();
        let start = START.get_or_init(Instant::now);
        start.elapsed().as_nanos() as i64
    }
}
