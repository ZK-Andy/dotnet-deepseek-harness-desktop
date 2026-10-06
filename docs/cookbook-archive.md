# Cookbook 冷归档 — 已从仓库移除的机制

> [docs/cookbook.md](cookbook.md) 的冻结冷归档层：只收**机制已从仓库移除**的退役条目，迁入允许归并改写、落定后不改（保留原始判别细节供存量 tag 包与历史排查）。
> **冻结**：新踩坑一律进 [cookbook](cookbook.md)，本层不承载在役条目；回迁须原子改两处（[AGENTS.md](../AGENTS.md) 文档纪律：每个事实只有一个家）。
> 条目沿用 cookbook 的阶段标签与条目格式（`scripts/verify-cookbook.py` 契约），不另立规范；预算见 `scripts/doc-budgets.manifest.json`。

## 打包

- **[打包] 闭包形态与 per-arch 瘦身边界（2026-08-20/21；`bundle-runtime*.sh` 已删）**：闭包=**整树 `node_modules`** + `pnpm --allow-build` 原生绑定；per-arch 瘦身只剪平台专属 `prebuilds/*` + `*.map` + 冗 md，**不剪 `.ts`/`.d.ts`/LICENSE**（盲删教训）。旧 0.1.0–0.1.2 坏包已删，仅存量 tag 包有意义。
- **[打包] 打包踩坑全链（0.1.3→0.1.9，2026-08-20）**：`npm→pnpm` 慢→`--allow-build` 缺 `.node`→`WebKitGTK6` 误 `4.1`→`brp-strip` 误伤 `arm64`→`AutoReqProv` 假依赖→`setup-node cache:pnpm` 缺 `pnpm-lock.yaml`→`data.tar.zst` 解压与 `*.rpm` 路径 `maxdepth 1`→`0.1.4` 同步 `market patch` 阻塞 `dsh web:`→`0.1.9` 仅 `dependencies` 有 `dshmarket` 但 `bundles` 无（解法 `bundles` 补写 + `ryn.json:identifier` 对齐 `StartupWMClass`）→`Ryn` 在 `Wayland` 下 `app_id` 未注册致 `shutting down` 立退；`data.tar.zst` 解压与 `*.rpm` 路径按 `find -type f` 递归。

- **[打包] Windows「安装器」实为 SFX 的判别与根治（2026-08-29 冒烟实锤；NSIS/SFX 回退链已随根治删除）**：Git Bash 把 `iscc /Q` 的 `/Q` 当 POSIX 路径转换成第二个脚本文件名（报 `You may not specify more than one script filename`），Inno 编译恒失败，NSIS/SFX 回退链把失败吞成成功——发布资产长期实为 7z 自解压包。判别：装后缺 `unins000.exe` 或进程窗口标题含 `7-Zip self-extracting archive`。根治：`MSYS2_ARG_CONV_EXCL` 排除转换 + iss `cygpath -w` + Inno 唯一链 fail loud（MSYS2 转换课仍活在主档「Windows 打包三连坑」③）。
- **[打包] release「等待产物」空转轮询的判别（2026-08-29 v0.3.12 首发实证；等待/逃生层已随 2026-09-28 三流合一删除）**：等待步骤就绪条件只认**产物名存在**，不查平台 run 状态——平台 job 失败时产物永不出现，失败被当「还没好」空转满 25m。判别：release 卡「等待三平台产物」先查三平台 run 的 conclusion，别干等（保留价值：「只认产物存在性」这类判据的反例；现链路见 [ADR](../.agents/notes/implemented/process/2026-09-28-ci-package-workflow-unification.md)）。

## 调试

- **[调试] DevTools 下 .map 404 刷屏的判别（2026-08-27 实证 + 已修）**：`DSH_DEVTOOLS=1` 打开控制台见 `Failed to load resource ... client.js.map / index-*.js.map / vendor-*.js.map (404)` + 聚合 `Source Map loading errors (x44)`——根因=交付物「JS 带 `//# sourceMappingURL=` 尾注释但 .map 文件不存在」（闭包裁剪删了 .map 没剥注释，上游构建产物自带注释）。修法：`trim_runtime_closure` 追加剥注释（ADR strip-sourcemap-comments-in-closure，提交 `fb41f1b`）；v0.3.10 及更早 tag 包仍会报，属预期。判别要点：凡见 `.js.map` 404 先查「注释是否指向不存在的文件」，不是 WebKit 玄学；companion 自身 client.js 无注释、不背锅（来源是上游 dsh-client-modules/-runtime 产物）。
