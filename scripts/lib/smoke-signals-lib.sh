#!/usr/bin/env bash
# smoke-signals-lib.sh — 冒烟判定信号正则的唯一家（尺寸闸拆出：原 smoke-wait-lib 信号区随
# companion 判门扩充超 250 行，ADR smoke-gate-tightening 批次独立成库）。
# 消费方：smoke-wait-lib（唯一 source 点；verdict-lib 与三平台脚本/容器腿经其传递拿到）。
# 本文件只含常量，source 无副作用。
# shellcheck disable=SC2034  # 判定串/正则常量由下游库（smoke-wait-lib 判门/循环）与自测消费

# 双信号：①`[host] dsh web =`（注意是等号——`dsh web:` 冒号格式是 dsh 子进程自检输出，壳打印的
# 是等号格式；两串曾因错位致冒烟恒败，CI 实证）= dsh 就绪（传输层）；②`[bootstrap] 引导开始：`
# = 安装链保底（装包→依赖齐→运行时检测→首启引导已启动）——②回退门已砍，仅作观测信号。
FULL_RE='\[host\] dsh web ='
BOOT_RE='\[bootstrap\] 引导开始：'

# 导航到达行：只作诊断回显（FAIL 证据打印），不判门——新链路零 host 导航，
# holder 自 reload 不产生到达回调（dispatch 36300876224 双腿实证：代理流量
# 证明 reload 发生，到达计数 0 新增）。token 第二跳已随转发模型退役
# （token 永不进导航靶点；页面命令通道按顶层 origin 判据校验），此处不再检查。
NAV_RE='\[nav\] 导航已到达'
# 铸币行（ADR loopback-forward-proxy）：`MintAsync` 成功即打印，
# `token 跳 → 303` 是转发路由存在的唯一机器可读信号。
MINT_RE='\[shell\] 铸币：token 跳 → 303'
# 终页裁决（ADR page-verdict-gate）：应用在落定期写下唯一的裁决行；显示腿的"绿"必须由 healthy 背书。
PAGE_LINE_RE='\[nav\] 页面裁决=(healthy|auth|unknown)'
# companion 装配判门（ADR smoke-gate-tightening）：装配在 spawn 前收口，状态即时可判——
# 失败行（三措辞变体：`随包插件安装失败` / `随包插件 spawn 前安装失败` / 引导路径的
# `引导：随包插件安装失败`）/异常跳过行（spec 解析失败、无可用来源——静默形态，行内分隔是
# 空格或中文冒号）/待装未见换入 active，任一命中即红。ACTIVE_RE 取两路径成功行的公共子串
# （非引导「随包插件经事务管线换入 active」/引导「本轮有插件经事务管线换入 active」——
# 完整前缀会导致引导腿首装成功误红，R1/R2 B1 实证）。
COMPANION_FAIL_RE='随包插件.*安装失败'
COMPANION_SKIP_BAD_RE='随包插件.*(spec 解析失败|无可用来源)'
COMPANION_PENDING_RE='随包插件 .+ 未就位，加入待装清单'
COMPANION_ACTIVE_RE='经事务管线换入 active'
