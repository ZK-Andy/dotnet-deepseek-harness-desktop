# Agent Note: drop-standalone-zip-artifacts

Status: implemented

## Problem

Windows 打包（12.7 分）第二大热点是「打 Windows 包」（~363s）。根因之一是**对 ~1.5GB 的 `resources/runtime` 闭包做两次满负荷压缩**：先 `zip -r`（deflate）产便携 zip，再 Inno Setup `Compression=lzma` + `SolidCompression=yes` 对同一闭包产安装器 `-setup.exe`。macOS 同样同时产 `zip`（`zip -r` .app）与 `dmg`（`hdiutil UDZO`），也是两份压缩。独立 zip 是历史遗留的「便携/兜底」产物，维护成本（三平台 `create_zip` 回退链、`unzip` 校验）与耗时都高。

## Decision

**三平台一律不单独产出便携 zip，只保留单一安装产物**：

- macOS：只产 `dmg`（`hdiutil`，仅 macOS runner；`create-dmg` 兜底）；缺 `hdiutil/create-dmg` 时 `fail loud` 退出（dmg 为唯一产物，不再有 zip 兜底）。
- Windows：只产安装器 `-setup.exe`（Inno Setup → NSIS → 7z SFX）；删 `create_zip` 链条；缺安装器工具时 `fail loud` 退出（不再「仅保留 zip」）。
- Linux 本就不产 zip（deb + rpm），不变。
- 收尾：`package-*.yml` 的 `upload-artifact` 与 `release.yml` 的 Release `files` 去掉 `*.zip`；README（中英）+ `docs/architecture|development` 的「包格式/产物」更新为只列安装产物。

## Alternatives considered

- **保留 zip 但用 `-0`/store 免压缩**：落败——仍要产并上传一个 ~1.5GB 的 zip（体积等同闭包），带宽/上传无收益，且保留回退链与校验代；用户已定不需要便携 zip。
- **只保留 Windows zip、去安装器**：落败——Windows 用户期待 exe 安装器，且安装器才是交付主形态。
- **继续保留 zip+dmg / zip+exe 双产物**：落败——对比 pilot-harness（Windows 单 NSIS、macOS 单 dmg），双产物造成 ~1.5GB 重复压缩、上传双份，收益仅是少数便携场景，用户决定不要。

## Consequences

- 收益：Windows 打包少一次 1.5GB deflate 压缩、macOS 少一次 zip；Release 体积/上传更小；打包脚本删除 `create_zip`/`unzip` 回退链，显著简化。
- 代价/风险：失去「解压即用」的便携 zip（用户需装安装器或用 dmg 拖拽）；Windows 若 runner 无 Inno Setup/NSIS/7z SFX 将直接失败（`fail loud`，Windows-latest 自带 Inno Setup 6，风险低）；历史发布里的 zip 不追溯删除。
- 验证：本地 `bash -n` + 三平台步骤时间对比待下次 tag 实测（期望 Windows「打 Windows 包」由 ~363s 显著下降）。
