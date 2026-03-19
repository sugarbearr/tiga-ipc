use napi::bindgen_prelude::Buffer;

use crate::{TigaEntry, TigaReadResult};

use super::common::napi_error;
use super::paths::resolve_tiga_prefix;
use crate::tiga_channel::TigaChannel;

pub fn tiga_read_impl(name: String, last_id: Option<i64>) -> Result<TigaReadResult, napi::Error> {
    let last_id = last_id.unwrap_or(0);
    let prefix = resolve_tiga_prefix(&name).ok_or_else(|| napi_error("mmap root not found"))?;

    let mut channel = TigaChannel::open(prefix)?;
    let (logbook, _) = match channel.read_logbook() {
        Some(value) => value,
        None => {
            return Ok(TigaReadResult {
                last_id,
                entries: Vec::new(),
            });
        }
    };

    let mut entries = Vec::new();
    let mut max_id = last_id;
    for entry in &logbook.entries {
        if entry.id <= last_id {
            continue;
        }

        max_id = max_id.max(entry.id);
        entries.push(TigaEntry {
            id: entry.id,
            message: Buffer::from(entry.message.clone()),
            media_type: entry.media_type.clone(),
        });
    }

    Ok(TigaReadResult {
        last_id: max_id,
        entries,
    })
}
