#!/usr/bin/env node
// verify-closure-graph.mjs — 一方闭包图静态校验（批次一，ADR artifact-verification-chain）。
// 从闭包内一方包（node_modules/@deepseek-ai/*）出发，沿生产依赖面（dependencies +
// 非 optional 的 peerDependencies）BFS：凡一方传递依赖不可解析即缺件，fail loud
// （exit 1）——把「运行时才发现缺件」提前到构建时。
//
// 解析模型必须对齐 Node 运行时：从**引用方包的真实路径**逐级向上找 node_modules/<dep>
//（pnpm 布局下依赖住在 .pnpm 虚拟存储的兄弟目录，顶层提升目录只有直装入口，
// 按「只查顶层」建模会把健康闭包误报成全图缺件）。每个包先 realpath 再向上走，
// 与 require 的实际解析行为一致。
//
// 边界：只遍历一方边。第三方树的完整性由 pnpm 安装原子性与 bundle-runtime-ci.sh
// 的 `dsh web:` 强自检把关；本校验专钉历史事故类——一方包缺件（0.1.x 时代
// cp -rL 只拷包本体丢 @deepseek-ai/dsh-app-boot 一族）。
//
// 用法：node verify-closure-graph.mjs <closure-dir>
import { existsSync, readdirSync, readFileSync, realpathSync } from 'node:fs';
import { dirname, isAbsolute, join, relative } from 'node:path';

const closureArg = process.argv[2];
if (!closureArg) {
  console.error('error: 用法: node verify-closure-graph.mjs <closure-dir>');
  process.exit(1);
}
if (!existsSync(closureArg)) {
  console.error(`error: 闭包目录不存在: ${closureArg}`);
  process.exit(1);
}
const closureRoot = realpathSync(closureArg);
const nm = join(closureRoot, 'node_modules');
const scope = join(nm, '@deepseek-ai');
if (!existsSync(scope)) {
  console.error(`error: 闭包缺一方作用域目录: ${scope}`);
  process.exit(1);
}

/** 读一个包目录的 package.json；不存在/坏 JSON 视为致命。 */
function readPackageAt(pkgDir) {
  const pkgPath = join(pkgDir, 'package.json');
  try {
    return JSON.parse(readFileSync(pkgPath, 'utf8'));
  } catch (err) {
    console.error(`error: ${pkgPath} 不可读或非合法 JSON: ${err.message}`);
    process.exit(1);
  }
}

/**
 * Node 同款向上解析：fromDir 起逐级父目录找 node_modules/<dep>，越过闭包根即失败。
 * 返回被解析包的真实目录；找不到返回 null。
 */
function resolveDep(fromPkgRealDir, dep) {
  let dir = fromPkgRealDir;
  for (;;) {
    const cand = join(dir, 'node_modules', ...dep.split('/'));
    if (existsSync(cand)) {
      const real = realpathSync(cand);
      // 解析结果越出闭包 = 闭包边界破损（运行时同样会炸），按缺件处理。
      // 用 relative 判定而非 startsWith：Windows 分隔符是 `\`，硬编码 '/' 在
      // win runner 上永不匹配，会把健康闭包误报成全图缺件（CI 实测抓到）。
      const rel = relative(closureRoot, real);
      if (isAbsolute(rel) || rel.startsWith('..')) return null;
      return real;
    }
    if (dir === closureRoot) return null;
    dir = dirname(dir);
  }
}

// 根 = 顶层作用域下的直装一方包（pnpm 对 workspace 直接依赖只在此处建链）
const queue = [];
for (const dir of readdirSync(scope)) {
  const link = join(scope, dir);
  if (!existsSync(link)) {
    console.error(`error: 一方入口包符号链接悬空: ${link}`);
    process.exit(1);
  }
  queue.push({ realDir: realpathSync(link), name: `@deepseek-ai/${dir}`, requiredBy: '(root)' });
}
if (!queue.some((e) => e.name === '@deepseek-ai/dsh')) {
  console.error('error: 闭包缺入口包 @deepseek-ai/dsh');
  process.exit(1);
}

// BFS：visited 以真实路径去重；missing 汇总后统一 fail loud。
const visited = [];
const seen = new Set();
const missing = [];
while (queue.length > 0) {
  const { realDir, name, requiredBy } = queue.shift();
  const key = realDir;
  if (seen.has(key)) continue;
  seen.add(key);

  const pkg = readPackageAt(realDir);
  if (pkg.name !== name) {
    console.error(`warn: ${realDir} 的 name(${pkg.name}) 与引用名(${name})不一致，以引用名为准`);
  }
  visited.push(name);

  const peersMeta = pkg.peerDependenciesMeta ?? {};
  const edges = [
    ...Object.keys(pkg.dependencies ?? {}),
    ...Object.keys(pkg.peerDependencies ?? {}).filter((d) => peersMeta[d]?.optional !== true),
  ];
  for (const dep of edges) {
    if (!dep.startsWith('@deepseek-ai/')) continue;
    const depDir = resolveDep(realDir, dep);
    if (depDir === null) {
      missing.push({ name: dep, requiredBy: name });
      continue;
    }
    if (!seen.has(depDir)) queue.push({ realDir: depDir, name: dep, requiredBy: name });
  }
}

console.log(`一方闭包图校验（${closureArg}）`);
for (const name of [...new Set(visited)].sort()) console.log(`  ok: ${name}`);
if (missing.length > 0) {
  const unique = [...new Map(missing.map((m) => [m.name, m])).values()];
  console.error('error: 一方闭包图缺件——以下 @deepseek-ai/* 包在生产依赖图中被引用但不可解析:');
  for (const { name, requiredBy } of unique.sort((a, b) => a.name.localeCompare(b.name))) {
    console.error(`  missing: ${name}（被 ${requiredBy} 引用）`);
  }
  console.error('构建继续只会把「运行时才发现缺件」带给用户，终止。');
  process.exit(1);
}
console.log(`闭包图完整：${seen.size} 个一方包实例全供给。`);
