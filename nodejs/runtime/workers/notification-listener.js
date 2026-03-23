const { parentPort, workerData } = require('worker_threads');

const { createTigaNotificationListener } = require('../native');

const post = (message) => {
  if (parentPort) {
    parentPort.postMessage(message);
  }
};

const getErrorMessage = (error) => {
  if (error instanceof Error && error.message) {
    return error.message;
  }

  return String(error);
};

let closed = false;
let listener;

if (!parentPort) {
  throw new Error('tiga notification worker requires parentPort');
}

parentPort.on('message', (message) => {
  if (!message || message.type !== 'close') {
    return;
  }

  closed = true;
  if (listener) {
    try {
      listener.close();
    } catch {
      // Ignore close races during shutdown.
    }
  }
});

try {
  listener = createTigaNotificationListener(workerData.name, {
    ipcDirectory: workerData.ipcDirectory,
  });
  post({ type: 'ready' });

  while (!closed && !listener.closed) {
    const signaled = listener.wait(workerData.waitTimeoutMs);
    if (closed || listener.closed) {
      break;
    }

    if (signaled) {
      post({ type: 'signal' });
    }
  }
} catch (error) {
  post({
    type: 'error',
    message: getErrorMessage(error),
  });
} finally {
  if (listener && !listener.closed) {
    try {
      listener.close();
    } catch {
      // Ignore close races during shutdown.
    }
  }

  post({ type: 'closed' });
}
