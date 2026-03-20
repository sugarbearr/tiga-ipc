use std::sync::Mutex;
use std::time::Duration;

use napi_derive::napi;

use crate::tiga::common::napi_error;
use crate::tiga::paths::resolve_tiga_prefix;
use crate::tiga_notify::NotificationListener;
use crate::TigaChannelOptions;

#[napi]
pub struct TigaNotificationListener {
    inner: Mutex<Option<NotificationListener>>,
}

#[napi]
impl TigaNotificationListener {
    #[napi]
    pub fn wait(&self, #[napi(ts_arg_type = "number")] timeout_ms: Option<i64>) -> Result<bool, napi::Error> {
        let timeout = Duration::from_millis(timeout_ms.unwrap_or(30_000).max(0) as u64);
        let guard = self
            .inner
            .lock()
            .map_err(|_| napi_error("notification listener state poisoned"))?;

        Ok(guard
            .as_ref()
            .map(|listener| listener.wait(timeout))
            .unwrap_or(false))
    }

    #[napi]
    pub fn close(&self) -> Result<(), napi::Error> {
        let mut guard = self
            .inner
            .lock()
            .map_err(|_| napi_error("notification listener state poisoned"))?;
        guard.take();
        Ok(())
    }

    #[napi(getter)]
    pub fn closed(&self) -> Result<bool, napi::Error> {
        let guard = self
            .inner
            .lock()
            .map_err(|_| napi_error("notification listener state poisoned"))?;
        Ok(guard.is_none())
    }
}

pub fn create_tiga_notification_listener_impl(
    name: String,
    options: Option<TigaChannelOptions>,
) -> Result<TigaNotificationListener, napi::Error> {
    let prefix = resolve_tiga_prefix(
        &name,
        options.as_ref().and_then(|value| value.mapping_directory.as_deref()),
    )
    .map_err(|message| napi_error(&message))?;
    let listener = NotificationListener::register(&prefix)
        .map_err(|_| napi_error("failed to register request notification listener"))?;

    Ok(TigaNotificationListener {
        inner: Mutex::new(Some(listener)),
    })
}
