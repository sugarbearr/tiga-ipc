use std::time::{Duration, Instant};

use crate::tiga_channel::TigaChannel;
use crate::tiga_logbook::LogEntry;
use crate::tiga_notify::{signal_updated, wait_for_listener, NotificationListener};
use crate::tiga_protocol::{build_invoke_message, parse_response_message};
use crate::tiga_sys::now_timestamp_ticks;

use super::common::{
    make_instance, napi_error, push_log_entry, read_logbook_or_default,
    serialize_logbook_with_limit,
};
use super::paths::resolve_tiga_prefix;

pub fn tiga_invoke_impl(
    request_name: String,
    response_name: String,
    method: String,
    data: String,
    timeout_ms: Option<i64>,
) -> Result<String, napi::Error> {
    let timeout_ms = timeout_ms.unwrap_or(30_000) as u64;
    let request_prefix =
        resolve_tiga_prefix(&request_name).ok_or_else(|| napi_error("mmap root not found"))?;
    let response_prefix =
        resolve_tiga_prefix(&response_name).ok_or_else(|| napi_error("mmap root not found"))?;
    let wait_budget = Duration::from_millis(timeout_ms);

    let mut request_channel = TigaChannel::open(request_prefix.clone())?;
    let mut response_channel = TigaChannel::open(response_prefix.clone())?;
    let response_listener = NotificationListener::register(&response_prefix)
        .map_err(|_| napi_error("failed to register response notification listener"))?;
    let mut last_seen = response_channel
        .read_logbook()
        .map(|(logbook, _)| logbook.last_id)
        .unwrap_or(0);

    if !wait_for_listener(&request_prefix, wait_budget) {
        return Err(napi_error(
            "server not ready (request listener not registered)",
        ));
    }

    let request_id = uuid::Uuid::new_v4().to_string();
    let invoke_payload = build_invoke_message(&method, &data, &request_id);
    let (mut logbook, schema) = read_logbook_or_default(&mut request_channel);
    let entry = LogEntry {
        id: logbook.last_id + 1,
        instance: make_instance(),
        timestamp: now_timestamp_ticks(),
        message: invoke_payload,
        media_type: Some("application/x-msgpack".to_string()),
    };
    push_log_entry(&mut logbook, entry);

    let payload = serialize_logbook_with_limit(
        &mut logbook,
        schema,
        request_channel.max_size(),
        "failed to serialize request logbook",
    )?;

    request_channel
        .write_raw_shared(&payload, Duration::from_secs(1))
        .map_err(|_| napi_error("failed to write invoke request"))?;
    let _ = signal_updated(&request_prefix);

    let deadline = Instant::now() + Duration::from_millis(timeout_ms);
    loop {
        if Instant::now() > deadline {
            return Err(napi_error("invoke timeout"));
        }

        if let Some(response) = read_matching_response(
            &mut response_channel,
            &request_id,
            &mut last_seen,
        )? {
            return Ok(response);
        }

        let remaining = deadline.saturating_duration_since(Instant::now());
        if remaining.is_zero() {
            return Err(napi_error("invoke timeout"));
        }

        let _ = response_listener.wait(remaining);
    }
}

fn read_matching_response(
    response_channel: &mut TigaChannel,
    request_id: &str,
    last_seen: &mut i64,
) -> Result<Option<String>, napi::Error> {
    let Some((logbook, _)) = response_channel.read_logbook() else {
        return Ok(None);
    };

    for entry in &logbook.entries {
        if entry.id <= *last_seen {
            continue;
        }

        *last_seen = (*last_seen).max(entry.id);
        let Some(response) = parse_response_message(&entry.message) else {
            continue;
        };

        if response.id != request_id {
            continue;
        }

        if response.code < 0 {
            return Err(napi_error(&response.data));
        }

        return Ok(Some(response.data));
    }

    Ok(None)
}
