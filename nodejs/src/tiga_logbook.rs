use rmpv::{decode, encode, Value};
use std::io::Cursor;

#[derive(Clone)]
pub struct LogEntry {
    pub id: i64,
    pub instance: String,
    pub timestamp: i64,
    pub message: Vec<u8>,
    pub media_type: Option<String>,
}

#[derive(Clone)]
pub struct LogBook {
    pub last_id: i64,
    pub entries: Vec<LogEntry>,
}

fn value_to_i64(value: &Value) -> Option<i64> {
    match value {
        Value::Integer(i) => i.as_i64(),
        _ => None,
    }
}

fn parse_guid(value: &Value) -> Option<String> {
    match value {
        Value::Ext(_ty, data) if data.len() == 16 => {
            let mut bytes = [0u8; 16];
            bytes.copy_from_slice(data);
            Some(guid_string_from_dotnet_bytes(bytes))
        }
        Value::Binary(data) if data.len() == 16 => {
            let mut bytes = [0u8; 16];
            bytes.copy_from_slice(data);
            Some(guid_string_from_dotnet_bytes(bytes))
        }
        Value::String(s) => {
            let text = s.as_str()?.to_string();
            if uuid::Uuid::parse_str(&text).is_ok() {
                Some(text)
            } else {
                None
            }
        }
        _ => None,
    }
}

fn guid_string_from_dotnet_bytes(bytes: [u8; 16]) -> String {
    let mut be = bytes;
    be[0..4].reverse();
    be[4..6].reverse();
    be[6..8].reverse();
    uuid::Uuid::from_bytes(be).to_string()
}

fn parse_entries(arr: &[Value]) -> Option<Vec<LogEntry>> {
    let mut entries = Vec::with_capacity(arr.len());
    for entry_val in arr {
        let entry_arr = entry_val.as_array()?;
        if entry_arr.len() < 4 {
            return None;
        }
        let id = value_to_i64(&entry_arr[0])?;
        let instance = parse_guid(&entry_arr[1])?;
        let timestamp = value_to_i64(&entry_arr[2])?;
        let message = match &entry_arr[3] {
            Value::Binary(bytes) => bytes.clone(),
            _ => return None,
        };
        let media_type = match entry_arr.get(4) {
            Some(Value::String(s)) => s.as_str().map(|v| v.to_string()),
            _ => None,
        };
        entries.push(LogEntry {
            id,
            instance,
            timestamp,
            message,
            media_type,
        });
    }
    Some(entries)
}

pub fn parse_logbook(bytes: &[u8]) -> Option<(LogBook, Option<i64>)> {
    let mut cursor = Cursor::new(bytes);
    let value = decode::read_value(&mut cursor).ok()?;
    let arr = value.as_array()?;
    if arr.len() != 2 {
        return None;
    }

    // Detect envelope: [schemaVersion, [lastId, entries]]
    if let Some(inner) = arr[1].as_array() {
        if inner.len() == 2 && inner[1].is_array() {
            let schema = value_to_i64(&arr[0])?;
            let last_id = value_to_i64(&inner[0])?;
            let entries = parse_entries(inner[1].as_array()?)?;
            return Some((LogBook { last_id, entries }, Some(schema)));
        }
    }

    // Fallback to plain logbook: [lastId, entries]
    let last_id = value_to_i64(&arr[0])?;
    let entries = parse_entries(arr[1].as_array()?)?;
    Some((LogBook { last_id, entries }, None))
}

fn entry_to_value(entry: &LogEntry) -> Value {
    let instance_val = Value::String(entry.instance.clone().into());
    let media_val = entry
        .media_type
        .as_ref()
        .map(|s| Value::String(s.clone().into()))
        .unwrap_or(Value::Nil);
    Value::Array(vec![
        Value::from(entry.id),
        instance_val,
        Value::from(entry.timestamp),
        Value::Binary(entry.message.clone()),
        media_val,
    ])
}

fn build_logbook_value(logbook: &LogBook, schema: Option<i64>) -> Value {
    let entries = Value::Array(logbook.entries.iter().map(entry_to_value).collect());
    let logbook_val = Value::Array(vec![Value::from(logbook.last_id), entries]);
    if let Some(schema_version) = schema {
        Value::Array(vec![Value::from(schema_version), logbook_val])
    } else {
        logbook_val
    }
}

pub fn serialize_logbook(logbook: &LogBook, schema: Option<i64>) -> Option<Vec<u8>> {
    let mut buf = Vec::new();
    let value = build_logbook_value(logbook, schema);
    encode::write_value(&mut buf, &value).ok()?;
    Some(buf)
}
