use memmap2::MmapMut;
use std::path::PathBuf;
use std::sync::atomic::{AtomicU32, AtomicU64, Ordering};
use std::thread;
use std::time::Duration;

use crate::tiga_logbook::{parse_logbook, LogBook};
use crate::tiga_notify::ensure_notification_layout;
use crate::tiga_sys::open_file_shared;
use crate::wyhash_compat::compute_checksum_compat;

const DATA_SIZE_BITS: usize = 39;
const DATA_CHECKSUM_BITS: usize = 24;
const DEFAULT_MAX_FILE_SIZE: u64 = 1024 * 1024;

#[repr(C)]
struct State {
    version: AtomicU64,
    idx_readers: [AtomicU32; 2],
}

pub struct TigaChannel {
    state_map: MmapMut,
    data_maps: [MmapMut; 2],
    max_size: usize,
}

fn state_path(prefix: &PathBuf) -> PathBuf {
    PathBuf::from(format!("{}{}", prefix.display(), "_state"))
}

fn data_path(prefix: &PathBuf, idx: usize) -> PathBuf {
    let suffix = if idx == 0 { "_data_0" } else { "_data_1" };
    PathBuf::from(format!("{}{}", prefix.display(), suffix))
}

fn ensure_file_layout(prefix: &PathBuf) -> std::io::Result<()> {
    let state = state_path(prefix);
    let state_file = open_file_shared(&state, true, true, true)?;
    let state_len = std::mem::size_of::<State>() as u64;
    if state_file.metadata()?.len() != state_len {
        state_file.set_len(state_len)?;
    }

    for idx in 0..2 {
        let path = data_path(prefix, idx);
        let file = open_file_shared(&path, true, true, true)?;
        let len = file.metadata()?.len();
        if len < DEFAULT_MAX_FILE_SIZE {
            file.set_len(DEFAULT_MAX_FILE_SIZE)?;
        }
    }

    ensure_notification_layout(prefix)?;
    Ok(())
}

fn decode_version(v: u64) -> Option<(usize, usize, u64)> {
    if v == 0 {
        return None;
    }
    let idx = (v & 1) as usize;
    let size = ((v >> 1) & ((1u64 << DATA_SIZE_BITS) - 1)) as usize;
    let checksum = v >> (DATA_SIZE_BITS + 1);
    Some((idx, size, checksum))
}

fn encode_version(idx: usize, size: usize, checksum: u64) -> u64 {
    let idx_part = (idx as u64) & 1;
    let size_part = ((size as u64) & ((1u64 << DATA_SIZE_BITS) - 1)) << 1;
    let checksum_part = (checksum & ((1u64 << DATA_CHECKSUM_BITS) - 1)) << (DATA_SIZE_BITS + 1);
    idx_part | size_part | checksum_part
}

fn compute_checksum(data: &[u8]) -> u64 {
    compute_checksum_compat(data, DATA_CHECKSUM_BITS)
}

fn napi_error(message: &str) -> napi::Error {
    napi::Error::from_reason(message.to_string())
}

impl TigaChannel {
    pub fn open(prefix: PathBuf) -> Result<Self, napi::Error> {
        ensure_file_layout(&prefix).map_err(|_| napi_error("failed to prepare mmap files"))?;
        let state_file = open_file_shared(&state_path(&prefix), true, true, false)
            .map_err(|_| napi_error("failed to open state file"))?;
        // SAFETY: memory map is valid for the lifetime of TigaChannel.
        let state_map = unsafe {
            MmapMut::map_mut(&state_file).map_err(|_| napi_error("failed to map state"))?
        };
        if state_map.len() < std::mem::size_of::<State>() {
            return Err(napi_error("invalid state size"));
        }
        let data0 = open_file_shared(&data_path(&prefix, 0), true, true, false)
            .map_err(|_| napi_error("failed to open data file 0"))?;
        let data1 = open_file_shared(&data_path(&prefix, 1), true, true, false)
            .map_err(|_| napi_error("failed to open data file 1"))?;
        // SAFETY: data files are kept open for the lifetime of the maps.
        let map0 =
            unsafe { MmapMut::map_mut(&data0).map_err(|_| napi_error("failed to map data 0"))? };
        let map1 =
            unsafe { MmapMut::map_mut(&data1).map_err(|_| napi_error("failed to map data 1"))? };
        let max_size = data0
            .metadata()
            .map_err(|_| napi_error("failed to stat data file"))?
            .len() as usize;
        Ok(Self {
            state_map,
            data_maps: [map0, map1],
            max_size,
        })
    }

    pub fn read_logbook(&mut self) -> Option<(LogBook, Option<i64>)> {
        let bytes = self.read_raw()?;
        parse_logbook(&bytes)
    }

    pub fn write_raw_shared(
        &mut self,
        payload: &[u8],
        grace: Duration,
    ) -> Result<(usize, bool), &'static str> {
        if self.state_map.len() < std::mem::size_of::<State>() {
            return Err("invalid state size");
        }
        let state_ptr = self.state_map.as_mut_ptr() as *mut State;
        let current = unsafe { (*state_ptr).version.load(Ordering::SeqCst) };
        let next_idx = match decode_version(current) {
            Some((idx, _, _)) => (idx + 1) % 2,
            None => 0,
        };

        let deadline = std::time::Instant::now() + grace;
        let mut reset = false;
        while unsafe { (*state_ptr).idx_readers[next_idx].load(Ordering::SeqCst) } > 0 {
            if grace.is_zero() || std::time::Instant::now() >= deadline {
                unsafe { (*state_ptr).idx_readers[next_idx].store(0, Ordering::SeqCst) };
                reset = true;
                break;
            }
            thread::sleep(Duration::from_millis(1));
        }

        if payload.len() > self.max_size() {
            return Err("payload exceeds mmap size");
        }

        let map = &mut self.data_maps[next_idx];
        map[..payload.len()].copy_from_slice(payload);
        map.flush().map_err(|_| "failed to flush data")?;

        let checksum = compute_checksum(payload);
        let version = encode_version(next_idx, payload.len(), checksum);
        unsafe { (*state_ptr).version.store(version, Ordering::SeqCst) };

        Ok((payload.len(), reset))
    }

    pub fn max_size(&self) -> usize {
        if self.max_size > 0 {
            self.max_size
        } else {
            DEFAULT_MAX_FILE_SIZE as usize
        }
    }

    fn read_raw(&mut self) -> Option<Vec<u8>> {
        if self.state_map.len() < std::mem::size_of::<State>() {
            return None;
        }
        let state_ptr = self.state_map.as_mut_ptr() as *mut State;
        let version = unsafe { (*state_ptr).version.load(Ordering::SeqCst) };
        let (idx, size, checksum) = decode_version(version)?;

        unsafe {
            (*state_ptr).idx_readers[idx].fetch_add(1, Ordering::SeqCst);
        }
        let bytes = self.data_maps[idx].get(..size)?.to_vec();
        unsafe {
            (*state_ptr).idx_readers[idx].fetch_sub(1, Ordering::SeqCst);
        }

        let actual = compute_checksum(&bytes);
        let expected = checksum & ((1u64 << DATA_CHECKSUM_BITS) - 1);
        if actual != expected {
            return None;
        }
        Some(bytes)
    }
}
