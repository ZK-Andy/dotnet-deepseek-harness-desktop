# PR Reviewer Checklist

> 单人项目亦用 PR 自审，按 `dsh-code-review` 清单执行。

- [ ] 变更范围已用 `scripts/change-scope.sh` 确认，最小证据已贴
- [ ] 非平凡变更携带 `ADR` 且 `Alternatives considered` 已填
- [ ] 编码约定：`fail loud`、`Branded ID`、`try` 单语句、`catch` 命名
- [ ] 测试覆盖边界/错误/并发，行为级有回归
- [ ] 文档单一事实源，无变更史叙事
- [ ] 链接与预算门禁通过
