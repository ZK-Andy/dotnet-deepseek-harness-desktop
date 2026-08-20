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

- 收益（v0.1.18 实测）：macOS 打包从 88–94s 降到 **49–59s**（zip+dmg 双压缩→只 dmg，省掉一次 zip）；Windows 上传 34–39s → **5s**、Release 体积更小；发布从 11 项资产降为 **8 项**（deb×2+rpm×2+dmg×2+setup.exe+SHA256SUMS），脚本删掉 `create_zip`/`unzip` 回退链。
- **修正预期（Windows「打 Windows 包」墙体时间未降，~373s ≈ 原 363s）**：Windows 打包真正的瓶颈不是独立 zip，而是 Inno Setup `LZMA`+`SolidCompression` 对 ~1.5GB 闭包的那一次压缩（约 6 分钟）——去 zip 只省下相对快的 deflate，墙钟基本不变。zip 移除在 Windows 的收益主要体现在上传/体积，不体现在打包步骤耗时。
- 代价/风险：失去「解压即用」便携 zip（需装安装器/拖拽 dmg）；Windows 若 runner 无 Inno Setup/NSIS/7z SFX 将直接失败（`fail loud`，Windows-latest 自带 Inno Setup 6，未触发）。
- 验证：`v0.1.18` tag 三平台全绿（Linux 3.6–3.7 分 / macOS 2.6 分 / Windows 12.1 分 run、14.1 分 job）。
