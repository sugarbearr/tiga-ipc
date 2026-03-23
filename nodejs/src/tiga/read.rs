use napi::bindgen_prelude::Buffer;

use crate::{TigaEntry, TigaReadOptions, TigaReadResult};

use super::common::napi_error;
use super::paths::resolve_tiga_prefix;
use crate::tiga_channel::TigaChannel;

pub fn tiga_read_impl(
    name: String,
    options: Option<TigaReadOptions>,
) -> Result<TigaReadResult, napi::Error> {
    let last_id = options
        .as_ref()
        .and_then(|value| value.last_id)
        .unwrap_or(0);
    let prefix = resolve_tiga_prefix(
        &name,
        options
            .as_ref()
            .and_then(|value| value.ipc_directory.as_deref()),
    )
    .map_err(|message| napi_error(&message))?;

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
