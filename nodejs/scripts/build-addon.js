const { spawnSync } = require('child_process');
const path = require('path');

const npxCommand = process.platform === 'win32' ? 'npx.cmd' : 'npx';
const packageRoot = path.resolve(__dirname, '..');
const args = ['@napi-rs/cli', 'build', ...process.argv.slice(2)];

const result = spawnSync(npxCommand, args, {
  cwd: packageRoot,
  shell: process.platform === 'win32',
  stdio: 'inherit',
});

if (result.error) {
  throw result.error;
}

if (result.status !== 0) {
  process.exit(result.status || 1);
}

require('./sync-package-types');
