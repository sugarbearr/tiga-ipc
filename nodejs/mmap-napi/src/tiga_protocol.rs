use rmpv::{decode, encode, Value};
use std::io::Cursor;

pub struct ResponseMessage {
    pub id: String,
    pub code: i32,
    pub data: String,
}

fn value_to_i64(value: &Value) -> Option<i64> {
    match value {
        Value::Integer(i) => i.as_i64(),
        _ => None,
    }
}

pub fn build_publisher_message(data: &str) -> Vec<u8> {
    let id = uuid::Uuid::new_v4().to_string();
    let message = Value::Array(vec![
        Value::String(id.into()),
        Value::from(0i32),
        Value::String(data.to_string().into()),
    ]);
    let mut buf = Vec::new();
    let _ = encode::write_value(&mut buf, &message);
    buf
}

pub fn build_invoke_message(method: &str, data: &str, id: &str) -> Vec<u8> {
    let message = Value::Array(vec![
        Value::String(id.to_string().into()),
        Value::from(1i32),
        Value::String(method.to_string().into()),
        Value::String(data.to_string().into()),
    ]);
    let mut buf = Vec::new();
    let _ = encode::write_value(&mut buf, &message);
    buf
}

pub fn parse_response_message(bytes: &[u8]) -> Option<ResponseMessage> {
    let mut cursor = Cursor::new(bytes);
    let value = decode::read_value(&mut cursor).ok()?;
    let arr = value.as_array()?;
    if arr.len() < 4 {
        return None;
    }

    let id = arr[0].as_str()?.to_string();
    let protocol = value_to_i64(&arr[1])?;
    if protocol != 2 {
        return None;
    }
    let data = arr[2].as_str()?.to_string();
    let code = value_to_i64(&arr[3])? as i32;

    Some(ResponseMessage { id, code, data })
}
