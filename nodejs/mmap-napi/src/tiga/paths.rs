use dirs::data_local_dir;
use std::path::PathBuf;

const FILE_PREFIX: &str = "DmCommunication_";
const TEMP_SUBDIR: &str = "tiga-ipc";

pub(super) fn resolve_tiga_prefix(name: &str) -> Option<PathBuf> {
    let root = get_mmap_root_path()?;
    Some(root.join(format!("{FILE_PREFIX}{name}")))
}

fn get_mmap_root_path() -> Option<PathBuf> {
    if let Ok(dir) = std::env::var("TIGA_IPC_DIR") {
        return ensure_directory(PathBuf::from(dir));
    }

    if let Ok(temp_dir) = std::env::var("TEMP") {
        let path = PathBuf::from(temp_dir).join(TEMP_SUBDIR);
        if let Some(existing) = ensure_directory(path) {
            return Some(existing);
        }
    }

    let local_app_data_path = data_local_dir()?;
    let mmap_root_path = local_app_data_path.join("innodealing").join(".cache");
    ensure_directory(mmap_root_path)
}

fn ensure_directory(path: PathBuf) -> Option<PathBuf> {
    if path.exists() || std::fs::create_dir_all(&path).is_ok() {
        Some(path)
    } else {
        None
    }
}
