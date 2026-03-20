use std::path::PathBuf;

const FILE_PREFIX: &str = "tiga_";

pub(crate) fn resolve_tiga_prefix(
    name: &str,
    ipc_directory: Option<&str>,
) -> Result<PathBuf, String> {
    let root = get_mmap_root_path(ipc_directory)?;
    Ok(root.join(format!("{FILE_PREFIX}{name}")))
}

fn get_mmap_root_path(ipc_directory: Option<&str>) -> Result<PathBuf, String> {
    let ipc_directory = ipc_directory
        .map(str::trim)
        .filter(|value| !value.is_empty())
        .ok_or_else(|| {
            "ipcDirectory option is required for file-backed tiga channels".to_string()
        })?;

    ensure_directory(PathBuf::from(ipc_directory))
}

fn ensure_directory(path: PathBuf) -> Result<PathBuf, String> {
    if path.exists() || std::fs::create_dir_all(&path).is_ok() {
        Ok(path)
    } else {
        Err(format!(
            "failed to create ipcDirectory '{}'",
            path.display()
        ))
    }
}

#[cfg(test)]
mod tests {
    use super::resolve_tiga_prefix;

    #[test]
    fn resolve_tiga_prefix_uses_ipc_directory_from_options() {
        let ipc_directory =
            std::env::temp_dir().join(format!("mmap_napi_paths_{}", uuid::Uuid::new_v4()));

        let prefix = resolve_tiga_prefix("sample", Some(&ipc_directory.to_string_lossy()))
            .expect("resolve prefix");

        assert_eq!(prefix, ipc_directory.join("tiga_sample"));

        let _ = std::fs::remove_dir_all(ipc_directory);
    }

    #[test]
    fn resolve_tiga_prefix_requires_ipc_directory_option() {
        let error = resolve_tiga_prefix("sample", None).expect_err("missing ipcDirectory");
        assert!(error.contains("ipcDirectory"), "actual error: {error}");
    }
}
