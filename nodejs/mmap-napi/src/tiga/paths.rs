use std::path::PathBuf;

const FILE_PREFIX: &str = "tiga_";

pub(crate) fn resolve_tiga_prefix(name: &str, mapping_directory: Option<&str>) -> Result<PathBuf, String> {
    let root = get_mmap_root_path(mapping_directory)?;
    Ok(root.join(format!("{FILE_PREFIX}{name}")))
}

fn get_mmap_root_path(mapping_directory: Option<&str>) -> Result<PathBuf, String> {
    let mapping_directory = mapping_directory
        .map(str::trim)
        .filter(|value| !value.is_empty())
        .ok_or_else(|| {
            "mappingDirectory option is required for file-backed tiga channels".to_string()
        })?;

    ensure_directory(PathBuf::from(mapping_directory))
}

fn ensure_directory(path: PathBuf) -> Result<PathBuf, String> {
    if path.exists() || std::fs::create_dir_all(&path).is_ok() {
        Ok(path)
    } else {
        Err(format!(
            "failed to create mappingDirectory '{}'",
            path.display()
        ))
    }
}

#[cfg(test)]
mod tests {
    use super::resolve_tiga_prefix;

    #[test]
    fn resolve_tiga_prefix_uses_mapping_directory_from_options() {
        let base_dir =
            std::env::temp_dir().join(format!("mmap_napi_paths_{}", uuid::Uuid::new_v4()));

        let prefix = resolve_tiga_prefix("sample", Some(&base_dir.to_string_lossy()))
            .expect("resolve prefix");

        assert_eq!(prefix, base_dir.join("tiga_sample"));

        let _ = std::fs::remove_dir_all(base_dir);
    }

    #[test]
    fn resolve_tiga_prefix_requires_mapping_directory_option() {
        let error = resolve_tiga_prefix("sample", None).expect_err("missing mappingDirectory");
        assert!(error.contains("mappingDirectory"), "actual error: {error}");
    }
}
