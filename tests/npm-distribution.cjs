'use strict';
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const { spawnSync } = require('node:child_process');
const [work, npmCli, npxCli, mode, requestedVersion] = process.argv.slice(2);
const real = mode === '--real';
const version = real ? requestedVersion : '0.4.0';
assert.ok(typeof version === 'string' && /^\d{1,3}\.\d{1,3}\.\d{1,5}$/.test(version), 'Expected an explicit valid version for real native smoke tests.');
const tarball = path.join(work, 'veiluna-codex-switch-' + version + '.tgz');
const prefix = path.join(work, 'global prefix');
const cache = path.join(work, 'cache');
const cwd = path.join(work, 'empty project');
fs.mkdirSync(cwd, { recursive: true });
fs.writeFileSync(path.join(cwd, 'package.json'), JSON.stringify({ name: 'isolated-npm-test', version: '1.0.0', private: true }));
// Every npm invocation is offline, including install (without --ignore-scripts).
const env = { ...process.env, npm_config_cache: cache, npm_config_prefix: prefix, npm_config_offline: 'true',
  npm_config_registry: 'http://127.0.0.1:9', npm_config_audit: 'false', npm_config_fund: 'false',
  npm_config_update_notifier: 'false', npm_config_userconfig: path.join(work, 'no-user-npmrc'),
  npm_config_globalconfig: path.join(work, 'no-global-npmrc') };
function run(command, args, options = {}) {
  const result = spawnSync(command, args, { cwd, env, encoding: 'utf8', input: 'interactive-input\n', timeout: 30000, ...options });
  assert.ifError(result.error);
  return result;
}
function npm(args) { console.log('CHECK: npm ' + args[0]); return run(process.execPath, [npmCli, ...args]); }
let result = npm(['install', '--global', '--prefix', prefix, '--offline', '--no-audit', '--no-fund', tarball]);
assert.equal(result.status, 0, result.stderr);
const installed = path.join(prefix, 'node_modules', '@veiluna', 'codex-switch');
const manifest = JSON.parse(fs.readFileSync(path.join(installed, 'package.json')));
assert.equal(manifest.name, '@veiluna/codex-switch');
assert.equal(manifest.version, version);
assert.deepEqual(manifest.os, ['win32']);
assert.deepEqual(manifest.cpu, ['x64']);
for (const key of ['scripts', 'dependencies', 'optionalDependencies', 'devDependencies', 'bundledDependencies']) assert.equal(manifest[key], undefined, key);
const files = [];
function walk(dir, relative = '') {
  for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
    const name = relative + entry.name;
    if (entry.isDirectory()) walk(path.join(dir, entry.name), name + '/');
    else files.push(name);
  }
}
walk(installed);
assert.deepEqual(files.sort(), ['README.md', 'bin/codex-switch-install.cjs', 'bin/codex-switch.cjs', 'bin/run-native.cjs', 'native/codex-switch.exe', 'native/installer.exe', 'package.json'].sort());
assert.ok(!fs.readFileSync(path.join(installed, 'README.md'), 'utf8').includes('fixture-secret'));
function probe(result, args, executable, code = 0) {
  assert.equal(result.status, code, result.stderr);
  const record = JSON.parse(result.stdout.trim());
  assert.deepEqual(record.args, args);
  assert.equal(record.exe, executable);
  assert.equal(record.input, 'interactive-input');
  assert.ok(result.stderr.includes('probe-stderr'));
}
const cli = path.join(installed, manifest.bin['codex-switch']);
const installer = path.join(installed, manifest.bin['codex-switch-install']);
if (real) {
  result = run(process.execPath, [cli, 'help']);
  assert.equal(result.status, 0, result.stderr);
  assert.ok(result.stdout.includes('codex-switch'));
  result = run(process.execPath, [cli, '--not-a-real-option']);
  assert.equal(result.status, 2, result.stderr);
  // Invalid installer arguments exercise the shipped entry point without changing installation.
  result = run(process.execPath, [installer, '--not-a-real-option']);
  assert.notEqual(result.status, 0);
  assert.ok(!result.stderr.includes('ENOENT'));
  result = npm(['exec', '--yes', '--offline', '--', 'file:' + tarball, 'help']);
  assert.equal(result.status, 0, result.stderr);
  assert.ok(result.stdout.includes('codex-switch'));
  result = run(process.execPath, [npxCli, '--yes', '--offline', 'file:' + tarball, '--not-a-real-option']);
  assert.equal(result.status, 2, result.stderr);
  result = npm(['exec', '--yes', '--offline', '--package=' + tarball, '--', 'codex-switch-install', '--not-a-real-option']);
  assert.notEqual(result.status, 0);
  assert.ok(!result.stderr.includes('ENOENT'));
} else {
  const argv = ['名称 with spaces', '', 'quote"inside', 'C:\\trailing space\\', '&echo unexecuted', '--fail'];
  probe(run(process.execPath, [cli, ...argv]), argv, 'codex-switch.exe', 37);
  probe(run(process.execPath, [installer]), [], 'installer.exe');
  for (const action of ['install', 'uninstall']) probe(run(process.execPath, [installer, action, '--quiet', '--fail']), [action, '--quiet', '--fail'], 'installer.exe', 37);
  // Actual npm-generated Windows .cmd entry points, in an isolated global prefix.
  for (const name of ['codex-switch', 'codex-switch-install']) {
    const command = '"' + path.join(prefix, name + '.cmd') + '" --fail';
    probe(run(process.env.ComSpec, ['/d', '/s', '/c', '"' + command + '"'], { windowsVerbatimArguments: true }), ['--fail'], name === 'codex-switch' ? 'codex-switch.exe' : 'installer.exe', 37);
  }
  // Local tarball infers codex-switch despite two bins, exactly as npx @veiluna/codex-switch would.
  probe(npm(['exec', '--yes', '--offline', '--', 'file:' + tarball, '--fail']), ['--fail'], 'codex-switch.exe', 37);
  probe(run(process.execPath, [npxCli, '--yes', '--offline', 'file:' + tarball, '--fail']), ['--fail'], 'codex-switch.exe', 37);
  probe(npm(['exec', '--yes', '--offline', '--package=' + tarball, '--', 'codex-switch-install', 'install', '--quiet']), ['install', '--quiet'], 'installer.exe');
  // Missing native binary is a launch error, never a successful run.
  fs.renameSync(path.join(installed, 'native', 'codex-switch.exe'), path.join(installed, 'native', 'hidden.exe'));
  result = run(process.execPath, [cli]);
  assert.notEqual(result.status, 0);
  assert.ok(result.stderr.includes('ENOENT'));
}
console.log('PASS: ' + (real ? 'real native package smoke' : 'offline global install, npm exec/npx, exact files, argv, inherited stdio, exit codes'));
