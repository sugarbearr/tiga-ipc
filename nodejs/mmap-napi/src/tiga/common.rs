use std::sync::OnceLock;

use crate::tiga_channel::TigaChannel;
use crate::tiga_logbook::{serialize_logbook, LogBook, LogEntry};

pub(crate) fn napi_error(message: &str) -> napi::Error {
    napi::Error::from_reason(message.to_string())
}

pub(super) fn make_instance() -> String {
    static INSTANCE: OnceLock<String> = OnceLock::new();
    INSTANCE
        .get_or_init(|| uuid::Uuid::new_v4().to_string())
        .clone()
}

pub(super) fn read_logbook_or_default(channel: &mut TigaChannel) -> (LogBook, Option<i64>) {
    channel.read_logbook().unwrap_or((
        LogBook {
            last_id: 0,
            entries: Vec::new(),
        },
        None,
    ))
}

pub(super) fn push_log_entry(logbook: &mut LogBook, entry: LogEntry) {
    logbook.last_id = entry.id;
    logbook.entries.push(entry);
}

pub(super) fn serialize_logbook_with_limit(
    logbook: &mut LogBook,
    schema: Option<i64>,
    max_size: usize,
    error_message: &str,
) -> Result<Vec<u8>, napi::Error> {
    let mut payload =
        serialize_logbook(logbook, schema).ok_or_else(|| napi_error(error_message))?;

    while payload.len() > max_size && !logbook.entries.is_empty() {
        logbook.entries.remove(0);
        logbook.last_id = logbook.entries.last().map(|entry| entry.id).unwrap_or(0);
        payload = serialize_logbook(logbook, schema).ok_or_else(|| napi_error(error_message))?;
    }

    Ok(payload)
}
