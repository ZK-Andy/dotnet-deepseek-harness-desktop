// Layout self-check for the companion JS test leg.
//
// The plugin keeps one layout: test files are the regular `*.test.js` files
// under `test/`, support files (DOM stub, clock, Cordis context, React stub)
// live in `testkit/`. This file states that layout as the plugin's own policy
// and checks it; Node's discovery rules are deliberately not restated here —
// they live in the Node documentation and change with the version.
//
// Why it matters: a support file that Node picks up as a test file still
// counts as one passing case, so a misplaced support file inflates the
// reported count while the job stays green.

import assert from 'node:assert/strict';
import { readdirSync } from 'node:fs';
import { dirname, join, relative, sep } from 'node:path';
import { fileURLToPath } from 'node:url';
import { test } from 'node:test';

const pluginRoot = dirname(dirname(fileURLToPath(import.meta.url)));

function findEntries(dir) {
  const entries = [];
  for (const entry of readdirSync(dir, { withFileTypes: true })) {
    if (entry.name === 'node_modules') {
      continue;
    }
    const path = join(dir, entry.name);
    entries.push({
      path,
      name: entry.name,
      isFile: entry.isFile(),
      isDirectory: entry.isDirectory(),
      isSymbolicLink: entry.isSymbolicLink(),
    });
    if (entry.isDirectory()) {
      entries.push(...findEntries(path));
    }
  }
  return entries;
}

function toPosix(path) {
  return path.split(sep).join('/');
}

function findTestDirectories(dir) {
  return findEntries(dir)
    .filter((entry) => entry.isDirectory && entry.name === 'test')
    .map((entry) => toPosix(relative(pluginRoot, entry.path)));
}

test('test/ 树内只允许常规的 *.test.js 文件，且 test/ 之外没有 *.test.js', () => {
  const violations = [];

  for (const entry of findEntries(pluginRoot)) {
    const path = toPosix(relative(pluginRoot, entry.path));
    if (path.startsWith('test/')) {
      if (!entry.isDirectory && (!entry.isFile || !entry.name.endsWith('.test.js'))) {
        const reason = entry.isSymbolicLink ? 'symbolic link' : 'not a *.test.js file';
        violations.push(`${path} (${reason})`);
      }
    } else if (!entry.isDirectory && entry.name.endsWith('.test.js')) {
      violations.push(`${path} (*.test.js outside test/)`);
    }
  }

  assert.deepEqual(violations, []);
});

test('插件内只有一个名为 test 的目录', () => {
  assert.deepEqual(findTestDirectories(pluginRoot), ['test']);
});
