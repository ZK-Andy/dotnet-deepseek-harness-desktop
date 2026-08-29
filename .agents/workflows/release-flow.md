# 发版流程（release-flow）

> tag 触发的全链路；历史踩坑沉淀于此（v0.1.17~v0.2.1）。

1. **版本基线**：csproj `<Version>` 单一来源 bump + `chore(release)` 提交。
2. **打 tag**：annotated `vX.Y.Z` 推送 origin——触发三平台 package-*.yml + release.yml。
3. **盯四流水线**：package-linux / package-macos / package-windows 全绿 → 统一 Release。
   - 打包时长预期：无闭包组装，各平台 job 以 dotnet publish + 安装器打包为主。
   - 验证链已自动化：布局断言（无闭包残留 + 插件 tgz）在各平台 job、Linux 安装冒烟覆盖「首启引导 → dsh web URL」全链、release preflight 总检位在 release.yml——任一红先修再发，不要人工绕过。
4. **Release 核验**：
   - 标记 = Latest、非 prerelease/draft
   - 资产 8 项齐整：deb×2 + rpm×2 + dmg×2 + setup.exe + SHA256SUMS.txt（无 zip）
   - 正文为 release-notes.sh 结构化输出
5. **实机验收转交**：自更新链路、安装器、首启补装等只有真机能验的项列清单给用户。
6. **收尾**：README 双语核对同步（版本、功能清单、测试徽章与新基线）；HANDOFF 记录版本号、资产数、流水线 run 号；遗留项进待办。

## 已知坑位速查

- pnpm 版本必须用工作区本地 11.7.0 直调（runner 预装版解析 prerelease peer 会炸；online-first 后仅首启引导内的 npm 路径适用，pnpm 冲突面收窄）。
- 供应链政策拒绝时按壳内逻辑重试一次并留痕（v0.2.1 教训）。
- 插件内容变更 → 安装器插件资源随打包现打（无缓存陈旧面）；tgz 供给源唯一 = `scripts/build-companion-tgz.sh`。
- preflight 的资产矩阵/体积下限在 `scripts/release-preflight.sh`（下限 15MB，当前实测包体 26-36MB）；体积随壳变化须同 PR 调整该值。
- 同名 tag 重打不会让已装客户端重新自更新（feed 按版本号判定）——宁跳版本号也不重打已发布的 tag（v0.3.4 三次重打被迫出 v0.3.5 的教训）。
- 改 `plugins/dsh-desktop-companion` 必须 bump version，否则版本感知升级静默不触发。
