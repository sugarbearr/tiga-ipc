use napi::bindgen_prelude::{Buffer, Either};
use std::time::Duration;

use crate::TigaWriteOptions;
use crate::tiga_channel::TigaChannel;
use crate::tiga_logbook::LogEntry;
use crate::tiga_notify::signal_updated;
use crate::tiga_protocol::build_publisher_message;
use crate::tiga_sys::now_timestamp_ticks;

use super::common::{
    make_instance, napi_error, push_log_entry, read_logbook_or_default,
    serialize_logbook_with_limit,
};
use super::paths::resolve_tiga_prefix;

pub fn tiga_write_impl(
    name: String,
    message: Either<String, Buffer>,
    options: Option<TigaWriteOptions>,
) -> Result<String, napi::Error> {
    let prefix =
        resolve_tiga_prefix(&name, options.as_ref().and_then(|value| value.mapping_directory.as_deref()))
            .map_err(|message| napi_error(&message))?;
    let mut channel = TigaChannel::open(prefix.clone())?;
    let media_type = options.and_then(|value| value.media_type);

    let (payload, resolved_media_type) = match message {
        Either::A(text) => (
            build_publisher_message(&text),
            Some("application/x-msgpack".to_string()),
        ),
        Either::B(buffer) => (buffer.to_vec(), media_type),
    };

    let (mut logbook, schema) = read_logbook_or_default(&mut channel);
    let entry = LogEntry {
        id: logbook.last_id + 1,
        instance: make_instance(),
        timestamp: now_timestamp_ticks(),
        message: payload,
        media_type: resolved_media_type,
    };
    push_log_entry(&mut logbook, entry);

    let payload = serialize_logbook_with_limit(
        &mut logbook,
        schema,
        channel.max_size(),
        "failed to serialize logbook",
    )?;

    let (written, reset) = channel
        .write_raw_shared(&payload, Duration::from_secs(1))
        .map_err(napi_error)?;
    let signaled = signal_updated(&prefix);

    Ok(format!(
        "written: {written} bytes | reset: {reset} | signaled: {signaled}"
    ))
}
