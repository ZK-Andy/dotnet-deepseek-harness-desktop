# Agent Note: pnpm-caret-spec-rejection（registry spec 显式拒 `^` 前缀）

Status: implemented

Review: FULL/2026-09-25#3/R1=ok R2=ok R3=ok

## Problem

竞品构造的 caret spec（`dshmarket@^x.y.z` 类）致 pnpm `ERR_PNPM_SPEC_NOT_SUPPORTED` 全员安装失败。我方两入口不对称：① `MarketInstallHelper` registry 面：`MarketSpec` 常量（`dshmarket@latest`）安全，但 `IsValidRegistrySpec` 对 caret 只是顺带拒（`VersionPattern` 首字符类不含 `^`，落"形状不合法"通用原因）——正则一旦放宽即静默失守，且日志给不出竞品指向；② `DshSpec`：`appsettings.json` 可配，零校验裸进 `npm install -g`（`RuntimeBootstrap.cs:315`），配错要等 npm 跑完（分钟级）才失败。

## Decision

- `IsValidRegistrySpec` 在 `SplitSpec` 后、形状校验前显式拒 version `^` 前缀：独立原因串点名 `ERR_PNPM_SPEC_NOT_SUPPORTED`，caret 证据优先于通用形状错误。
- `DshSpec` 合约收紧为 registry 形态：`RunAsync` 的 `InstallDsh` 步前经 `IsValidRegistrySpec`（同程序集 internal 调用，零新增跨工程边），不合法即 `Fail`（`UiCopy.BootstrapInvalidDshSpec`，引导页错误框 fail loud），一次 npm 都不跑。
- 单测钉死：caret 变体（裸名/scoped/tag 位）+ 合法形（`@alpha`/`@latest`/精确版/rc）不受影响 + 非法 `DshSpec` 零 spawn + 中英文案分支。

## Alternatives considered

- **`~` / `>=` / `x` 等 range 一并收紧**：落败——要求只点了 `^`（竞品实证位），npm 对 `1.x` 等部分接受，过度收紧误伤合法配置；后续有实证再扩。
- **`DshSpec` 非法时回退默认 `@alpha`**：落败——静默改写用户显式配置意图，fail-safe 用错地方（缺省回退只给"没写"，写错必须 fail loud）。
- **共享守卫下沉 Core**：落败——两调用方同属 Infrastructure 程序集，internal 调用零新增边；为 3 行逻辑新增跨工程 API 面，账不平。

## Consequences

- 既有合法 `DshSpec`（`@alpha`/`@latest`/精确版）与全部合法 registry 形行为零变更。
- 非法配置从"npm 跑数分钟失败"变为"秒级 fail loud + 可读原因"。

## Testing

- `MarketInstallHelperSpecTests` caret 用例；`RuntimeBootstrapTests` 非法 `DshSpec` 零 spawn + 中英文案；`dotnet test` 全绿 0 警告。
