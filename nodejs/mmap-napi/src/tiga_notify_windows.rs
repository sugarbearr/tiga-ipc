use memmap2::MmapMut;
use std::ffi::OsStr;
use std::io;
use std::os::windows::ffi::OsStrExt;
use std::path::Path;
use std::sync::atomic::{AtomicI32, AtomicI64, Ordering};
use std::thread;
use std::time::{Duration, Instant};

use crate::tiga_sys::open_file_shared;
use crate::wyhash_compat::wyhash_hash_compat;

use super::{
    notification_file_len, notification_path, NotificationSlot,
    DOTNET_FILETIME_EPOCH_OFFSET_TICKS, EVENT_PREFIX, NOTIFICATION_EVENT_SUFFIX,
    NOTIFICATION_SLOT_COUNT,
};

pub struct NotificationListener {
    map: MmapMut,
    slot_index: usize,
    slot_token: i64,
    event_handle: windows_sys::Win32::Foundation::HANDLE,
}

impl NotificationListener {
    pub fn register(prefix: &Path) -> io::Result<Self> {
        super::ensure_notification_layout(prefix)?;
        let mut map = open_notification_map(prefix, false)?;
        initialize_notification_state(&mut map);

        let process_id = get_current_process_id();
        let process_start_time_utc_ticks = get_current_process_start_time_utc_ticks();
        let slot_token = create_notification_slot_token(process_id, process_start_time_utc_ticks);
        let slot_index = unsafe {
            register_notification_slot(
                &mut map,
                slot_token,
                process_id,
                process_start_time_utc_ticks,
            )?
        };

        let event_name = get_notification_event_name(prefix, slot_index);
        let event_handle = match create_named_auto_reset_event(&event_name) {
            Ok(handle) => handle,
            Err(err) => {
                unsafe {
                    unregister_notification_slot(&mut map, slot_index, slot_token);
                }

                return Err(err);
            }
        };

        Ok(Self {
            map,
            slot_index,
            slot_token,
            event_handle,
        })
    }

    pub fn wait(&self, timeout: Duration) -> bool {
        unsafe {
            use windows_sys::Win32::Foundation::WAIT_OBJECT_0;
            use windows_sys::Win32::System::Threading::WaitForSingleObject;

            let result = WaitForSingleObject(self.event_handle, duration_to_wait_ms(timeout));
            result == WAIT_OBJECT_0
        }
    }
}

impl Drop for NotificationListener {
    fn drop(&mut self) {
        unsafe {
            unregister_notification_slot(&mut self.map, self.slot_index, self.slot_token);
            windows_sys::Win32::Foundation::CloseHandle(self.event_handle);
        }
    }
}

pub fn signal_updated(prefix: &Path) -> io::Result<bool> {
    let mut map = match open_notification_map(prefix, false) {
        Ok(map) => map,
        Err(err) if err.kind() == io::ErrorKind::NotFound => return Ok(false),
        Err(err) => return Err(err),
    };

    let mut signaled = false;
    let event_scope = create_notification_event_scope(prefix);
    unsafe {
        let slots = slots_ptr(&mut map);
        for slot_index in 0..NOTIFICATION_SLOT_COUNT {
            let slot = slots.add(slot_index);
            let token = slot_token_atomic(slot).load(Ordering::Acquire);
            if token == 0 {
                continue;
            }

            let owner_process_id = slot_owner_process_id_atomic(slot).load(Ordering::Acquire);
            if owner_process_id == 0 {
                if !is_token_owned_by_live_process(token) {
                    clear_notification_slot(slot, token);
                }

                continue;
            }

            let owner_process_start_time_utc_ticks =
                slot_owner_start_atomic(slot).load(Ordering::Acquire);
            if !is_same_live_process(owner_process_id, owner_process_start_time_utc_ticks) {
                clear_notification_slot(slot, token);
                continue;
            }

            let event_name =
                format!("{EVENT_PREFIX}{event_scope}{NOTIFICATION_EVENT_SUFFIX}{slot_index}");
            if let Ok(handle) = create_named_auto_reset_event(&event_name) {
                windows_sys::Win32::System::Threading::SetEvent(handle);
                windows_sys::Win32::Foundation::CloseHandle(handle);
                signaled = true;
            }
        }
    }

    Ok(signaled)
}

pub fn wait_for_listener(prefix: &Path, timeout: Duration) -> bool {
    let deadline = Instant::now() + timeout;
    loop {
        if has_live_listener(prefix).unwrap_or(false) {
            return true;
        }

        if Instant::now() >= deadline {
            return false;
        }

        thread::sleep(Duration::from_millis(50));
    }
}

fn has_live_listener(prefix: &Path) -> io::Result<bool> {
    let mut map = match open_notification_map(prefix, false) {
        Ok(map) => map,
        Err(err) if err.kind() == io::ErrorKind::NotFound => return Ok(false),
        Err(err) => return Err(err),
    };

    unsafe {
        let slots = slots_ptr(&mut map);
        for slot_index in 0..NOTIFICATION_SLOT_COUNT {
            let slot = slots.add(slot_index);
            let token = slot_token_atomic(slot).load(Ordering::Acquire);
            if token == 0 {
                continue;
            }

            let owner_process_id = slot_owner_process_id_atomic(slot).load(Ordering::Acquire);
            if owner_process_id == 0 {
                if is_token_owned_by_live_process(token) {
                    return Ok(true);
                }

                clear_notification_slot(slot, token);
                continue;
            }

            let owner_process_start_time_utc_ticks =
                slot_owner_start_atomic(slot).load(Ordering::Acquire);
            if is_same_live_process(owner_process_id, owner_process_start_time_utc_ticks) {
                return Ok(true);
            }

            clear_notification_slot(slot, token);
        }
    }

    Ok(false)
}

unsafe fn register_notification_slot(
    map: &mut MmapMut,
    slot_token: i64,
    process_id: i32,
    process_start_time_utc_ticks: i64,
) -> io::Result<usize> {
    let slots = slots_ptr(map);
    for slot_index in 0..NOTIFICATION_SLOT_COUNT {
        let slot = slots.add(slot_index);
        let token = slot_token_atomic(slot).load(Ordering::Acquire);
        if token == 0 {
            if slot_token_atomic(slot)
                .compare_exchange(0, slot_token, Ordering::AcqRel, Ordering::Acquire)
                .is_ok()
            {
                slot_owner_start_atomic(slot)
                    .store(process_start_time_utc_ticks, Ordering::Release);
                slot_owner_process_id_atomic(slot).store(process_id, Ordering::Release);
                return Ok(slot_index);
            }

            continue;
        }

        let owner_process_id = slot_owner_process_id_atomic(slot).load(Ordering::Acquire);
        if owner_process_id == 0 {
            if is_token_owned_by_live_process(token) {
                continue;
            }

            if slot_token_atomic(slot)
                .compare_exchange(token, slot_token, Ordering::AcqRel, Ordering::Acquire)
                .is_ok()
            {
                slot_owner_start_atomic(slot)
                    .store(process_start_time_utc_ticks, Ordering::Release);
                slot_owner_process_id_atomic(slot).store(process_id, Ordering::Release);
                return Ok(slot_index);
            }

            continue;
        }

        let owner_process_start_time_utc_ticks =
            slot_owner_start_atomic(slot).load(Ordering::Acquire);
        if is_same_live_process(owner_process_id, owner_process_start_time_utc_ticks) {
            continue;
        }

        if slot_token_atomic(slot)
            .compare_exchange(token, slot_token, Ordering::AcqRel, Ordering::Acquire)
            .is_ok()
        {
            slot_owner_start_atomic(slot)
                .store(process_start_time_utc_ticks, Ordering::Release);
            slot_owner_process_id_atomic(slot).store(process_id, Ordering::Release);
            return Ok(slot_index);
        }
    }

    Err(io::Error::new(
        io::ErrorKind::Other,
        "no notification slots are available",
    ))
}

unsafe fn unregister_notification_slot(map: &mut MmapMut, slot_index: usize, slot_token: i64) {
    if slot_index >= NOTIFICATION_SLOT_COUNT {
        return;
    }

    let slot = slots_ptr(map).add(slot_index);
    let current_token = slot_token_atomic(slot).load(Ordering::Acquire);
    if current_token != slot_token {
        return;
    }

    slot_owner_start_atomic(slot).store(0, Ordering::Release);
    slot_owner_process_id_atomic(slot).store(0, Ordering::Release);
    let _ = slot_token_atomic(slot).compare_exchange(
        slot_token,
        0,
        Ordering::AcqRel,
        Ordering::Acquire,
    );
}

unsafe fn clear_notification_slot(slot: *mut NotificationSlot, token: i64) {
    let current_token = slot_token_atomic(slot).load(Ordering::Acquire);
    if current_token != token {
        return;
    }

    slot_owner_start_atomic(slot).store(0, Ordering::Release);
    slot_owner_process_id_atomic(slot).store(0, Ordering::Release);
    let _ = slot_token_atomic(slot).compare_exchange(
        token,
        0,
        Ordering::AcqRel,
        Ordering::Acquire,
    );
}

unsafe fn slots_ptr(map: &mut MmapMut) -> *mut NotificationSlot {
    map.as_mut_ptr() as *mut NotificationSlot
}

unsafe fn slot_token_atomic(slot: *mut NotificationSlot) -> &'static AtomicI64 {
    &*(std::ptr::addr_of_mut!((*slot).token) as *mut AtomicI64)
}

unsafe fn slot_owner_start_atomic(slot: *mut NotificationSlot) -> &'static AtomicI64 {
    &*(std::ptr::addr_of_mut!((*slot).owner_process_start_time_utc_ticks) as *mut AtomicI64)
}

unsafe fn slot_owner_process_id_atomic(slot: *mut NotificationSlot) -> &'static AtomicI32 {
    &*(std::ptr::addr_of_mut!((*slot).owner_process_id) as *mut AtomicI32)
}

fn initialize_notification_state(map: &mut MmapMut) {
    unsafe {
        let slots = slots_ptr(map);
        for slot_index in 0..NOTIFICATION_SLOT_COUNT {
            let slot = slots.add(slot_index);
            let token = slot_token_atomic(slot).load(Ordering::Acquire);
            if token == 0 {
                slot_owner_process_id_atomic(slot).store(0, Ordering::Release);
                slot_owner_start_atomic(slot).store(0, Ordering::Release);
            }
        }
    }
}

fn open_notification_map(prefix: &Path, create: bool) -> io::Result<MmapMut> {
    let path = notification_path(prefix);
    let file = open_file_shared(&path, true, true, create)?;
    let expected_len = notification_file_len() as u64;
    if create && file.metadata()?.len() != expected_len {
        file.set_len(expected_len)?;
    }

    if file.metadata()?.len() < expected_len {
        return Err(io::Error::new(
            io::ErrorKind::UnexpectedEof,
            "notification file is smaller than expected",
        ));
    }

    unsafe { MmapMut::map_mut(&file) }
}

fn create_notification_event_scope(prefix: &Path) -> String {
    let normalized_identity = normalize_notification_identity(&notification_path(prefix));
    let hash = wyhash_hash_compat(normalized_identity.as_bytes());
    format!("file_{hash:016x}")
}

fn normalize_notification_identity(path: &Path) -> String {
    let absolute = if path.is_absolute() {
        path.to_path_buf()
    } else if let Ok(current_dir) = std::env::current_dir() {
        current_dir.join(path)
    } else {
        path.to_path_buf()
    };

    absolute.to_string_lossy().to_uppercase()
}

fn get_notification_event_name(prefix: &Path, slot_index: usize) -> String {
    let event_scope = create_notification_event_scope(prefix);
    format!("{EVENT_PREFIX}{event_scope}{NOTIFICATION_EVENT_SUFFIX}{slot_index}")
}

fn create_named_auto_reset_event(name: &str) -> io::Result<windows_sys::Win32::Foundation::HANDLE>
{
    unsafe {
        use windows_sys::Win32::System::Threading::CreateEventW;

        let wide_name: Vec<u16> = OsStr::new(name)
            .encode_wide()
            .chain(std::iter::once(0))
            .collect();
        let handle = CreateEventW(std::ptr::null(), 0, 0, wide_name.as_ptr());
        if handle.is_null() {
            return Err(io::Error::last_os_error());
        }

        Ok(handle)
    }
}

fn duration_to_wait_ms(timeout: Duration) -> u32 {
    timeout.as_millis().min(u32::MAX as u128) as u32
}

fn get_current_process_id() -> i32 {
    unsafe { windows_sys::Win32::System::Threading::GetCurrentProcessId() as i32 }
}

fn get_current_process_start_time_utc_ticks() -> i64 {
    unsafe {
        use windows_sys::Win32::Foundation::FILETIME;
        use windows_sys::Win32::System::Threading::{GetCurrentProcess, GetProcessTimes};

        let process_handle = GetCurrentProcess();
        let mut creation_time: FILETIME = std::mem::zeroed();
        let mut exit_time: FILETIME = std::mem::zeroed();
        let mut kernel_time: FILETIME = std::mem::zeroed();
        let mut user_time: FILETIME = std::mem::zeroed();

        if GetProcessTimes(
            process_handle,
            &mut creation_time,
            &mut exit_time,
            &mut kernel_time,
            &mut user_time,
        ) == 0
        {
            return 0;
        }

        filetime_to_datetime_utc_ticks(filetime_to_ticks(creation_time))
    }
}

fn is_same_live_process(process_id: i32, expected_start_time_utc_ticks: i64) -> bool {
    if process_id <= 0 {
        return false;
    }

    unsafe {
        use windows_sys::Win32::System::Threading::{
            OpenProcess, PROCESS_QUERY_LIMITED_INFORMATION,
        };
        const SYNCHRONIZE: u32 = 0x0010_0000;

        let process_handle = OpenProcess(
            PROCESS_QUERY_LIMITED_INFORMATION | SYNCHRONIZE,
            0,
            process_id as u32,
        );
        if process_handle.is_null() {
            return false;
        }

        let live = process_start_time_matches(process_handle, expected_start_time_utc_ticks);
        windows_sys::Win32::Foundation::CloseHandle(process_handle);
        live
    }
}

fn process_start_time_matches(
    process_handle: windows_sys::Win32::Foundation::HANDLE,
    expected_start_time_utc_ticks: i64,
) -> bool {
    unsafe {
        use windows_sys::Win32::Foundation::FILETIME;
        use windows_sys::Win32::System::Threading::GetProcessTimes;

        let mut creation_time: FILETIME = std::mem::zeroed();
        let mut exit_time: FILETIME = std::mem::zeroed();
        let mut kernel_time: FILETIME = std::mem::zeroed();
        let mut user_time: FILETIME = std::mem::zeroed();
        if GetProcessTimes(
            process_handle,
            &mut creation_time,
            &mut exit_time,
            &mut kernel_time,
            &mut user_time,
        ) == 0
        {
            return false;
        }

        if expected_start_time_utc_ticks == 0 {
            return true;
        }

        filetime_to_datetime_utc_ticks(filetime_to_ticks(creation_time))
            == expected_start_time_utc_ticks
    }
}

fn filetime_to_ticks(file_time: windows_sys::Win32::Foundation::FILETIME) -> i64 {
    ((file_time.dwHighDateTime as u64) << 32 | file_time.dwLowDateTime as u64) as i64
}

pub(super) fn filetime_ticks_to_datetime_utc_ticks(filetime_ticks: i64) -> i64 {
    filetime_ticks.saturating_add(DOTNET_FILETIME_EPOCH_OFFSET_TICKS)
}

fn filetime_to_datetime_utc_ticks(filetime_ticks: i64) -> i64 {
    filetime_ticks_to_datetime_utc_ticks(filetime_ticks)
}

fn is_token_owned_by_live_process(token: i64) -> bool {
    let Some((process_id, process_start_marker)) = try_get_notification_slot_token_identity(token)
    else {
        return false;
    };

    if process_id <= 0 {
        return false;
    }

    unsafe {
        use windows_sys::Win32::System::Threading::{
            OpenProcess, PROCESS_QUERY_LIMITED_INFORMATION,
        };
        const SYNCHRONIZE: u32 = 0x0010_0000;

        let process_handle = OpenProcess(
            PROCESS_QUERY_LIMITED_INFORMATION | SYNCHRONIZE,
            0,
            process_id as u32,
        );
        if process_handle.is_null() {
            return false;
        }

        let result = if process_start_marker == 0 {
            true
        } else {
            let current_start_time = get_process_start_time_ticks(process_handle);
            create_notification_process_start_marker(current_start_time) == process_start_marker
        };
        windows_sys::Win32::Foundation::CloseHandle(process_handle);
        result
    }
}

fn get_process_start_time_ticks(process_handle: windows_sys::Win32::Foundation::HANDLE) -> i64 {
    unsafe {
        use windows_sys::Win32::Foundation::FILETIME;
        use windows_sys::Win32::System::Threading::GetProcessTimes;

        let mut creation_time: FILETIME = std::mem::zeroed();
        let mut exit_time: FILETIME = std::mem::zeroed();
        let mut kernel_time: FILETIME = std::mem::zeroed();
        let mut user_time: FILETIME = std::mem::zeroed();
        if GetProcessTimes(
            process_handle,
            &mut creation_time,
            &mut exit_time,
            &mut kernel_time,
            &mut user_time,
        ) == 0
        {
            return 0;
        }

        filetime_to_datetime_utc_ticks(filetime_to_ticks(creation_time))
    }
}

fn try_get_notification_slot_token_identity(token: i64) -> Option<(i32, u32)> {
    if token == 0 {
        return None;
    }

    let value = token as u64;
    let process_id = (value >> 32) as u32 as i32;
    let process_start_marker = value as u32;
    if process_id <= 0 {
        return None;
    }

    Some((process_id, process_start_marker))
}

fn create_notification_process_start_marker(process_start_time_utc_ticks: i64) -> u32 {
    if process_start_time_utc_ticks == 0 {
        return 0;
    }

    let value = process_start_time_utc_ticks as u64;
    (value ^ (value >> 32)) as u32
}

fn create_notification_slot_token(process_id: i32, process_start_time_utc_ticks: i64) -> i64 {
    let normalized_process_id = if process_id > 0 { process_id } else { 1 };
    let process_start_marker =
        create_notification_process_start_marker(process_start_time_utc_ticks);
    let token = ((normalized_process_id as u32 as u64) << 32) | process_start_marker as u64;
    if token == 0 {
        1
    } else {
        token as i64
    }
}
