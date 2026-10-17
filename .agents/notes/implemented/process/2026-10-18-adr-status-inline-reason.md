# Agent Note: adr-status-inline-reason

Status: implemented
Review: FULL/2026-10-18#4/R1=ok R2=ok R3=ok

## Problem

`verify-adr-format.py` 的 `STATUS_RE` 写成 `^Status: (proposed|implemented|rejected(?: — .+)?)$`，行内理由被括进同一个捕获组；而校验用 `status_m.group(1) != lifecycle` 比较状态词。结果：`.agents/notes/README.md` 明文允许的 `Status: rejected — 理由` 恒被判「状态与目录不一致」——契约允许的写法在门禁上根本走不通，且报错信息里看不出是理由被并进了状态词（打印出整行）。2026-10-18 的销账批踩到：把一条 proposed 转 rejected 并写上级理由后门禁即红。

## Decision

- 状态词单独成组，理由作非捕获尾缀：`^Status: (proposed|implemented|rejected)(?: — .+)?$`。理由与状态词解耦是同一个判定，不做状态特判。
- 同批把 `.agents/notes/README.md` 的 `Status:` 契约改写成「状态词 + 可选 ` — 理由`（三种状态通用）」——契约唯一家与门禁同源，不再是「理由只属 rejected」的歧义写法。放宽到三态是判定形态的自然结果：状态词与理由在正则里分属两段，只放行 rejected 需要额外加状态分支，而理由内容不参与校验、分支换不来内容约束力。**依据只到这一层**：仓内 220 篇现状没有 `implemented`/`proposed` 带行内理由的实例（`git log -S'Status: implemented —'` 与 `-S'Status: proposed —'` 零命中），本次是为契约去歧义，不是为已发生的写法背书。
- `--self-test` 增四格夹具：rejected / implemented / proposed 带行内理由均 pass，「状态词与目录不一致 + 带行内理由」仍 fail。

## Alternatives considered

- **反向修契约（README 删掉「— 理由」允许）**：落败——`rejected` 的一句理由（为什么不做）是笔记里最有价值的一行，砍掉是降低信息密度。
- **只在 rejected 上允许理由（状态特判）**：落败——判定里多一条状态分支，却不对理由内容产生任何约束；理由既然不校验，状态特判只剩形态成本。
- **让写作者绕开（理由另起一行）**：落败——门禁与契约打架时应修门禁；把规避写法当规范，会让下一个写作者再踩一次。
- **把理由纳入校验（限定长度/字符集）**：落败——理由的自然语言形态无客观边界，约束只会制造假红。

## Consequences

- 状态词仍与所在目录强校验。
- 写作面：需要解释「为什么不做/为什么现在做」时可直接写在状态行，不必挪进正文。

## Testing

- `python3 scripts/verify-adr-format.py --self-test` 全绿（四格新夹具，回退到旧正则时 rejected 格必红）；全仓笔记零改动即通过——门禁计数以实跑输出为准，不抄进 ADR。
