# Agent Note: release-artifact-wait-glob（release 等待产物就绪——通配竞态）

Status: implemented

中文（双语暂不启用；启用时恢复 .md + .zh.md 配对 + .i18n.yaml）

## Problem

v0.4.0 发版：tag 触发后三平台 package-* 全绿（linux amd64/arm64、macos arm64/x64、windows x64 六资产均 success），但 `release.yml` 的 preflight 总检位失败——**缺 linux arm64 deb + linux aarch64 rpm**，release 未建立。日志显示已下载资产仅含 `linux-amd64.deb`、`linux-x86_64.rpm`（amd64/x86_64），arm64/aarch64 缺失。

**实证**：`linux-arm64-packages` 产物实际已生成（`workflow_run.head_sha=c8224ba`，未过期）。故非打包失败，而是 release 聚合缺了它。

## Decision

`release.yml`「等待三平台产物就绪」的判定**改为六资产逐名精确匹配**：

```bash
for want in linux-amd64-packages linux-arm64-packages \
           macos-arm64-packages macos-x64-packages windows-x64-packages; do
  [[ " $resp " == *" $want "* ]] || ok=0
done
```

以取代原宽松通配：

```bash
for want in linux- macos- windows-; do
  [[ "$resp" == *"${want}"*-packages* ]] || ok=0
done
```

- **`linux-` 通配在 linux-amd64-packages 就绪即放行**：wait 判定只要求「存在任一 linux 产物」，而 arm64 leg 跑在慢 runner（`ubuntu-24.04-arm`）上、产物上载较晚——wait 提前通过 → 下载步在 arm64 产物上传前运行，只逮到 amd64 缺 arm64 → preflight 缺 linux-arm64/aarch64（release 失败）。
- tag 构建产物恒为 `actions/artifacts` 列表最新（分页首 100 内），故无需分页；`?per_page=100` 够用。

## Alternatives considered

- **`gh api --paginate` 拉全产物**：被否决——`--paginate --jq` 对**每一页**分别执行 jq 并逐行输出，下载步的 `python json.load` 会因多行 JSON 直接炸；即便单行 JSON，也会带多余空页。且 tag 构建产物恒在列表最前（首 100），无分页收益。**待产物持续累积后可另行加固。**
- **保留宽松通配**：已实证会竞态漏掉晚到产物，否决。
- **`workflow_dispatch` 手动重跑 release**：release.yml 仅 `tags: ['v*']` 触发、无 dispatch，不可用；且根因未修则手动重跑仍失败。

## Consequences

- release 聚合前**必须六资产全部就绪**（任一架构晚到即等待，不再提前下载）。
- v0.4.0 首发因本竞态在 preflight 红；修复后删 tag 重打（无 Release 对象、客户端从未见过 v0.4.0，区别于 v0.3.4 已发布重打教训——见 release-flow 已知坑位）。
- 下载步维持非分页（`?per_page=100`），对新构建安全（最新产物恒在首 100）；若仓库产物长期累积超阈值需另行处理。

## Related

- [release-flow](../../../workflows/release-flow.md)：已知坑位「同一 wait 判定只认产物存在性」已展开为本条竞态；「任一红先修再发、勿人工绕过」与本修复一致。
