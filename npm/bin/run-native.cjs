'use strict';
const path = require('node:path');
const { spawnSync } = require('node:child_process');

module.exports = function runNative(filename) {
  if (process.platform !== 'win32' || process.arch !== 'x64') {
    console.error('Codex Switch requires Windows x64 and an x64 Node.js runtime.');
    process.exitCode = 1;
    return;
  }
  const result = spawnSync(path.join(__dirname, '..', 'native', filename), process.argv.slice(2), {
    stdio: 'inherit',
    shell: false
  });
  if (result.error) {
    console.error('Could not start ' + filename + ': ' + result.error.code);
    process.exitCode = 1;
  } else {
    process.exitCode = result.status === null ? 1 : result.status;
  }
};
