# 检索通道路由表

> 由根 AGENTS.md「检索通道路由」引用。AnySearch 是唯一 web 搜索后端（插件已 Provider 级接管内置 `web_search`/`web_fetch`）；本表只区分工具面。

## 路由规则

| 意图 | 通道 | 要点 |
|---|---|---|
| GitHub 项目/仓库/代码 | gh CLI | 见 [github-research.md](github-research.md)，不再重复搜索 |
| 库/框架官方文档与用法 | `code.doc` | params.library 必填；返回 context7 结构化摘要 + 官方文档定点链接 |
| 真实代码实现示例 | `code.snippet` 或 `gh search code` | params.repo/lang/path 过滤 |
| 一般 web 检索（中文） | `general.general`，zone `"cn"` | 资讯、社区讨论、实测文 |
| 一般 web 检索（英文） | `general.general`，zone `"intl"` | 新闻、官方博客、发布说明 |
| 无区域/纵向诉求的快查 | 内置 `web_search` | 同后端简化面，仅 query/max_results |

## 输出纪律

- 多个独立查询合并单次 batch 调用（anysearch 最多五路并发）
- 单步输出超约 50 行先截断再进上下文
