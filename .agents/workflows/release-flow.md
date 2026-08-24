# 发版流程（release-flow）

> tag 触发的全链路；历史踩坑沉淀于此（v0.1.17~v0.2.1）。

1. **版本基线**：csproj `<Version>` 单一来源 bump + `chore(release)` 提交。
2. **打 tag**：annotated `vX.Y.Z` 推送 origin——触发三平台 package-*.yml + release.yml。
3. **盯四流水线**：package-linux / package-macos / package-windows 全绿 → 统一 Release。
   - 缓存预期：tag 是独立 ref，**首 run 必然全量重建**（闭包缓存按 ref 隔离）；Windows ~13 分钟内属正常。
   - 验证链已自动化（批次一）：闭包图静态校验在构建期、Linux 安装冒烟与 mac dmg 挂载断言/win staging 断言在各平台 job、release preflight 总检位在 release.yml——任一红先修再发，不要人工绕过。
4. **Release 核验**：
   - 标记 = Latest、非 prerelease/draft
   - 资产 8 项齐整：deb×2 + rpm×2 + dmg×2 + setup.exe + SHA256SUMS.txt（无 zip）
   - 正文为 release-notes.sh 结构化输出
5. **实机验收转交**：自更新链路、安装器、首启补装等只有真机能验的项列清单给用户。
6. **收尾**：HANDOFF 记录版本号、资产数、流水线 run 号；遗留项进待办。

## 已知坑位速查

- pnpm 版本必须用工作区本地 11.7.0 直调（runner 预装版解析 prerelease peer 会炸）。
- 供应链政策拒绝时按壳内逻辑重试一次并留痕（v0.2.1 教训）。
- 插件内容变更 → 闭包缓存签名与 CI 缓存 key 必须覆盖（v0.2.0 陈旧 tgz 教训）。
- preflight 的资产矩阵/体积下限在 `scripts/release-preflight.sh`；瘦身使正常包逼近 50MB 下限时须同 PR 调整该值。
