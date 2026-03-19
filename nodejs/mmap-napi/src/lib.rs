mod tiga;
mod tiga_channel;
mod tiga_logbook;
mod tiga_notify;
mod tiga_protocol;
mod tiga_sys;
mod wyhash_compat;

use napi_derive::napi;
use tiga::{tiga_invoke_impl, tiga_read_impl, tiga_write_impl};

#[napi]
pub fn initialized() -> String {
    "mmap initialized.".to_string()
}

#[napi(object)]
pub struct TigaReadResult {
    #[napi(js_name = "lastId")]
    pub last_id: i64,
    pub entries: Vec<TigaEntry>,
}

#[napi(object)]
pub struct TigaEntry {
    pub id: i64,
    #[napi(ts_type = "Buffer")]
    pub message: napi::bindgen_prelude::Buffer,
    #[napi(js_name = "mediaType")]
    pub media_type: Option<String>,
}

#[napi(object)]
pub struct TigaChannelOptions {
    #[napi(js_name = "mappingDirectory")]
    pub mapping_directory: Option<String>,
}

#[napi(object)]
pub struct TigaWriteOptions {
    #[napi(js_name = "mediaType")]
    pub media_type: Option<String>,
    #[napi(js_name = "mappingDirectory")]
    pub mapping_directory: Option<String>,
}

#[napi(object)]
pub struct TigaReadOptions {
    #[napi(js_name = "lastId")]
    pub last_id: Option<i64>,
    #[napi(js_name = "mappingDirectory")]
    pub mapping_directory: Option<String>,
}

#[napi(object)]
pub struct TigaInvokeOptions {
    #[napi(js_name = "timeoutMs")]
    pub timeout_ms: Option<i64>,
    #[napi(js_name = "mappingDirectory")]
    pub mapping_directory: Option<String>,
}

#[napi(js_name = "tigaWrite")]
pub fn tiga_write(
    #[napi(ts_arg_type = "string")] name: String,
    #[napi(ts_arg_type = "Buffer | string")] message: napi::bindgen_prelude::Either<
        String,
        napi::bindgen_prelude::Buffer,
    >,
    options: Option<TigaWriteOptions>,
) -> Result<String, napi::Error> {
    tiga_write_impl(name, message, options)
}

#[napi(js_name = "tigaRead")]
pub fn tiga_read(
    #[napi(ts_arg_type = "string")] name: String,
    options: Option<TigaReadOptions>,
) -> Result<TigaReadResult, napi::Error> {
    tiga_read_impl(name, options)
}

#[napi(js_name = "tigaInvoke")]
pub fn tiga_invoke(
    #[napi(ts_arg_type = "string")] request_name: String,
    #[napi(ts_arg_type = "string")] response_name: String,
    #[napi(ts_arg_type = "string")] method: String,
    #[napi(ts_arg_type = "string")] data: String,
    options: Option<TigaInvokeOptions>,
) -> Result<String, napi::Error> {
    tiga_invoke_impl(request_name, response_name, method, data, options)
}
