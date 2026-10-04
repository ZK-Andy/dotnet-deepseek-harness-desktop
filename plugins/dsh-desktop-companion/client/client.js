/**
 * dsh-desktop-companion client half.
 *
 * Loaded by the dsh web shell's module system (window.__ModuleLoader__) once
 * per app boot — before React mounts, and again after every full document
 * load, so a plain capture-phase listener registered here survives all SPA
 * re-renders without any host-side re-injection machinery.
 *
 * The web kernel adopts each boot-manifest module as a Cordis loader entry and
 * applies its exports: the factory MUST take `require` and return an object
 * carrying apply(ctx) (the dsh-client-* convention). An exports object without
 * apply fails the entry — and one failed entry aborts the whole web boot.
 *
 * Features:
 * - Self-update button in the sidebar footer (beside Settings): renders ONLY
 *   while the host reports status=ready; hover expands「更新 vX.Y.Z」;
 *   click installs+restarts via desktop.update.install.
 * - Self-update section in Settings (settings.section): current version,
 *   manual check entry, full status line incl. host-reported error reason;
 *   when the query fails it distinguishes "the host has no update stack (dev)"
 *   from "the page-to-host command channel is down" (e.g. the page origin moved
 *   to a new loopback port after the window was created) and points at the fix.
 * - Tray event relay: forwards Ryn tray plugin events (tray.clicked,
 *   tray.menuItemClicked) to the host via desktop.tray.event; show /
 *   check-update / restart / quit semantics are resolved on the host side.
 */
;(function () {
  if (typeof window === 'undefined' || !window.__ModuleLoader__) return
  window.__ModuleLoader__.load({
    id: 'dsh-desktop-companion',
    // factory receives the module-table require (react & host modules resolve
    // through it) — forgetting the parameter silently breaks every require below.
    factory: function (require) {
      /**
       * Register the client half against the client cordis context.
       * @param {object} ctx - Client cordis context. No services acquired.
       */
      function apply(ctx) {
        var TAG = '[dsh-desktop-companion]'

        // 托盘事件中继（批次三，ADR shell-tray-hide-to-tray）：Ryn 托盘插件把点击事件发到
        // Web 层（window.__ryn.on）；页面是常驻层（隐藏到托盘后仍存活），在此把白名单事件
        // 原样转发回宿主命令，语义解析在宿主纯函数里。只做哑中继：不判断动作、不改载荷。
        try {
          if (window.__ryn && window.__ryn.on && !window.__dshDesktopCompanionTrayRelay) {
            window.__dshDesktopCompanionTrayRelay = true
            var rynTray = window.__ryn
            var relayToHost = function (name) {
              return function (data) {
                // 中继失败静默吞：宿主路由缺失（旧壳）或窗口销毁中，托盘动作本就有菜单兜底
                rynTray.invoke('desktop.tray.event', { event: name, data: data === undefined ? null : data })
                  .catch(function () {})
              }
            }
            window.__ryn.on('tray.clicked', relayToHost('tray.clicked'))
            window.__ryn.on('tray.menuItemClicked', relayToHost('tray.menuItemClicked'))
          }
        } catch (e) { /* no __ryn bridge: nothing to relay */ }

        // invoke 响应归一化：Ryn 桥接对非空响应 resolve 的是 JSON.parse 后的值（通常是对象），
        // 空响应是 undefined；字符串分支只为兼容手工构造的帧。所有命令响应一律先过这里，
        // 禁止直接 JSON.parse——v0.3.0 实机自启开关/诊断导出两缺陷的根因就是对对象二次 parse。
        var parseFrame = function (raw) {
          if (raw && typeof raw === 'object') return raw
          try { return JSON.parse(raw) } catch (e) { return null }
        }

        var setupUpdateUI = function () {
          if (!(window.__ryn && ctx.slots && typeof require === 'function')) {
            console.warn(TAG, 'update UI skipped: guards')
            return false
          }
          var reactMod = null
          try { reactMod = require('react') } catch (e1) { console.warn(TAG, 'react require threw', e1) }
          if (!reactMod || !reactMod.createElement || !reactMod.useState || !reactMod.useEffect) {
            console.warn(TAG, 'update UI skipped: react unusable')
            return false
          }
          var h = reactMod.createElement

          var style = document.createElement('style')
          style.id = 'dsh-desktop-companion-update-css'
          style.textContent =
            // 配色对齐 dsh 设计系统语义：图标/文字=success-primary（两主题恒定绿），
            // 圆片底色=success-tertiary（亮浅绿/暗深绿，随主题自适应）；hover 主次反转
            '.ddc-upd{display:inline-flex;align-items:center;height:26px;border-radius:9999px;' +
            'background:transparent;border:none;cursor:pointer;padding:0;color:inherit}' +
            '.ddc-upd:disabled{cursor:default;opacity:.7}' +
            '.ddc-upd .ddc-ic{flex:none;width:26px;height:26px;border-radius:50%;display:flex;' +
            'align-items:center;justify-content:center;background:rgba(127,127,127,.15);' +
            'background:var(--dsw-alias-state-success-tertiary,rgba(127,127,127,.15));' +
            'color:#22c55e;color:var(--dsw-alias-state-success-primary,#22c55e);' +
            'transition:background .15s ease,color .15s ease}' +
            '.ddc-upd:hover .ddc-ic,.ddc-upd:focus-visible .ddc-ic{' +
            'background:#22c55e;background:var(--dsw-alias-state-success-primary,#22c55e);' +
            'color:#e6fff2;color:var(--dsw-alias-state-success-tertiary,#e6fff2)}' +
            '.ddc-lb{max-width:0;overflow:hidden;white-space:nowrap;font-size:12px;opacity:0;' +
            'color:#22c55e;color:var(--dsw-alias-state-success-primary,#22c55e);' +
            'transition:max-width .15s ease,opacity .15s ease,padding .15s ease}' +
            '.ddc-upd:hover .ddc-lb,.ddc-upd:focus-visible .ddc-lb{max-width:150px;opacity:1;padding:0 8px 0 2px}' +
            '.ddc-rail .ddc-lb{display:none}.ddc-rail:hover .ddc-lb{display:none}' +
            '.ddc-spin{width:14px;height:14px;border-radius:50%;border:2px solid #22c55e;' +
            'border:2px solid var(--dsw-alias-state-success-primary,#22c55e);' +
            'animation:ddc-rot 1s linear infinite}' +
            '@keyframes ddc-rot{to{transform:rotate(360deg)}}' +
            // 设置页视觉对齐原生设置区块（settings-plugins/general 的 token 规格）：
            // 分组标题 16px/600；卡片 = bg-layer-3 底 + border-l2 描边 + 12px 圆角；
            // 行内「标题/描述 左、控件 右」，行间以 border-l2 分隔；按钮为原生
            // save/discard 两态（主=反色填充，次=描边幽灵）；开关为官方 Switch 同规格克隆
            // （36×20 胶囊，Switch.module.css 逐项对照，见下方 nativeSwitch 探测）。
            '.ddc-page{max-width:760px;display:flex;flex-direction:column;gap:12px;' +
            'color:var(--dsw-alias-label-primary)}' +
            '.ddc-group{display:flex;flex-direction:column;gap:10px}' +
            '.ddc-gtitle{margin:0;font-size:16px;font-weight:600;line-height:1.5}' +
            '.ddc-list{border:1px solid var(--dsw-alias-border-l2);' +
            'background:var(--dsw-alias-bg-layer-3);border-radius:12px;padding:4px 16px}' +
            '.ddc-row2{display:flex;align-items:center;gap:12px;padding:12px 0}' +
            '.ddc-row2 + .ddc-row2{border-top:1px solid var(--dsw-alias-border-l2)}' +
            '.ddc-copy{flex:1;display:flex;flex-direction:column;gap:4px;min-width:0}' +
            '.ddc-title{color:var(--dsw-alias-label-primary);font-size:13px;font-weight:500;line-height:1.5}' +
            '.ddc-desc{color:var(--dsw-alias-label-tertiary);font-size:12px;font-weight:400;line-height:1.5;margin:0}' +
            '.ddc-ctl{display:flex;justify-content:flex-end;align-items:center;gap:8px;flex:none}' +
            '.ddc-err{color:#ef4444;color:var(--dsw-alias-state-error-primary,#ef4444)}' +
            '.ovn-btn{appearance:none;font:inherit;cursor:pointer;border:1px solid transparent;' +
            'border-radius:8px;padding:5px 14px;font-size:13px;line-height:1.5;' +
            'background:var(--dsw-alias-label-primary);color:var(--dsw-alias-bg-layer-3);' +
            'transition:filter .15s ease}' +
            '.ovn-btn:hover:not(:disabled){filter:brightness(1.08)}' +
            '.ovn-btn:disabled{opacity:.4;cursor:default}' +
            '.ovn-btn--ghost{background:transparent;border-color:var(--dsw-alias-border-l2);' +
            'color:var(--dsw-alias-label-secondary)}' +
            '.ovn-btn--ghost:hover:not(:disabled){color:var(--dsw-alias-label-primary);' +
            'border-color:var(--dsw-alias-label-dimmed);filter:none}' +
            '.ddc-sw{position:relative;flex:none;width:36px;height:20px;padding:2px;border:0;' +
            'border-radius:10px;background:var(--dsw-alias-border-l3,#3a3a4a);cursor:pointer;' +
            'transition:background 120ms ease}' +
            '.ddc-sw[aria-checked="true"]{background:#22c55e;' +
            'background:var(--dsw-alias-brand-primary,#22c55e)}' +
            '.ddc-sw[disabled]{opacity:.5;cursor:default}' +
            '.ddc-sw:focus-visible{outline:2px solid var(--dsw-alias-brand-primary,#22c55e);outline-offset:2px}' +
            '.ddc-sw i{display:block;width:16px;height:16px;border-radius:50%;' +
            'background:var(--dsw-alias-label-primary-foreground,#fff);' +
            'transform:translateX(0);transition:transform 120ms ease}' +
            '.ddc-sw[aria-checked="true"] i{transform:translateX(16px)}'
          if (!document.getElementById(style.id)) document.head.append(style)

          function UpdateButton(props) {
            // t 来自 slot 声明的 locale: NS——renderer 自动注入 props.t 并按 revision 跟随，
            // 切语言时由 SlotOutlet 重渲染本组件、t 随之读到新语言。无 t（独立渲染/测试）时
            // 退化显示 key（fail loud 而非空白）。
            var p = props || {}
            var th = p.t || function (k) { return k }
            var pair = reactMod.useState(null)
            var state = pair[0]
            var setState = pair[1]
            reactMod.useEffect(function () {
              var onEvt = function (e) { setState(e.detail || null) }
              document.addEventListener('dsh-desktop-update', onEvt)
              // 第二个参数必须传（空对象即可）：空参数体的 invoke 在宿主分发层会 500
              window.__ryn.invoke('desktop.update.getState', {}).then(function (s) {
                var parsed = parseFrame(s)
                if (parsed && (parsed.status === 'ready' || parsed.status === 'installing')) setState(parsed)
              }).catch(function (e3) { console.warn(TAG, 'getState failed', e3 && e3.message) })
              return function () { document.removeEventListener('dsh-desktop-update', onEvt) }
            }, [])
            var wide = !(p.wide === false)
            var installing = !!state && state.status === 'installing'
            if (!state || (state.status !== 'ready' && !installing)) return null
            var cls = 'ddc-upd' + (wide ? '' : ' ddc-rail')
            return h('button', {
              className: cls,
              type: 'button',
              disabled: installing,
              title: state.version ? th('updateTitle') + ' ' + state.version : th('updateTitle'),
              'aria-label': state.version ? th('installPrefix') + ' ' + state.version : th('installPrefix'),
              onClick: function () {
                window.__ryn.invoke('desktop.update.install', {}).catch(function () {})
              },
            }, h('span', { className: 'ddc-ic' },
              installing
                ? h('span', { className: 'ddc-spin' })
                : h('svg', { width: 14, height: 14, viewBox: '0 0 14 14', fill: 'none', 'aria-hidden': true },
                    h('path', { d: 'M7 11V3M3.5 7.63128L7 11L10.5 7.63128', stroke: 'currentColor' })),
              ), h('span', { className: 'ddc-lb' },
                installing ? th('installingNow') : (th('updateTitle') + ' ' + (state.version || ''))))
          }

          // 客户端文案本地化：随 dsh 语言切换（中⇄英）。dsh 的语言表不是配置文件，
          // 而是纯 JS 字典 + 运行时注册（@deepseek-ai/dsh-client-locale 的 ctx.locale）。
          // zh 为中文基线（原 STR 对象逐项迁移），en 为对应英文；key 用扁平字符串，
          // 模板占位为 {name} 格式（与 dsh t() 的 translate 运行时一致，本插件文案暂不使用
          // 占位符）。注册走 untyped 重载（命名空间不进 @deepseek-ai/dsh-client-ui-slots 的
          // LocaleNamespaceMap 明文合并表）。
          var NS = 'desktop-companion'
          var zh = {
            label: '\u684c\u9762\u8bbe\u7f6e',
            cur: '\u5f53\u524d\u7248\u672c',
            idle: '\u5c1a\u672a\u68c0\u67e5\u66f4\u65b0',
            checking: '\u6b63\u5728\u68c0\u67e5\u66f4\u65b0\u2026',
            dl: '\u6b63\u5728\u4e0b\u8f7d',
            readySuffix: '\u5c31\u7eea\uff0c\u53ef\u5b89\u88c5',
            installing: '\u6b63\u5728\u5b89\u88c5\uff0c\u5e94\u7528\u5373\u5c06\u91cd\u542f\u2026',
            uptodate: '\u5df2\u662f\u6700\u65b0\u7248\u672c',
            errPrefix: '\u68c0\u67e5\u5931\u8d25\uff1a',
            unknown: '\u672a\u77e5\u539f\u56e0',
            unavail: '\u684c\u9762\u81ea\u66f4\u65b0\u5728\u5f53\u524d\u8fd0\u884c\u65f6\u4e0d\u53ef\u7528\uff08\u5f00\u53d1\u8fd0\u884c\u65f6\u53ef\u8bbe DSH_DESKTOP_UPDATE_FORCE=1 \u5f00\u542f\uff09',
            // 页面→壳命令通道整体失效（非 dev 门禁）：端口漂移后 origin 变化、命令被 CORS 拦下时的文案
            unavailChannel: '\u65e0\u6cd5\u8fde\u63a5\u684c\u9762\u5bbf\u4e3b\uff1a\u672c\u4f1a\u8bdd\u7684\u9875\u9762\u547d\u4ee4\u901a\u9053\u4e0d\u53ef\u7528\uff0c\u91cd\u542f\u5e94\u7528\u53ef\u6062\u590d\u81ea\u66f4\u65b0',
            // opencode settings-v2 同款行的文案
            checkTitle: '检查更新',
            checkDesc: '检查是否有可用的新版本',
            actCheck: '立即检查',
            actChecking: '检查中…',
            actDownloading: '下载中…',
            actInstall: '安装并重启',
            actInstalling: '安装中…',
            diagTitle: '导出诊断信息',
            diagBtn: '导出',
            autostart: '\u5f00\u673a\u81ea\u542f',
            autostartDesc: '登录后自动启动 DeepSeek Harness 桌面端',
            closeTitle: '关闭时最小化到托盘',
            closeDesc: '勾选后点击关闭按钮会隐藏到系统托盘，取消则直接退出应用。',
            closeUnavailable: '当前运行环境无系统托盘，开关不可用。',
            // 命令通道失败（非「宿主答不可用」）：状态未知 + 可重试，见 useSwitchState 的三态；
            // 反复出现指向托盘菜单重启——通道失效时托盘原生链路仍可达（ADR app-restart-native-switch）
            hostUnreachableDesc: '状态未知，请重试；若反复出现请重启应用（可用托盘菜单）',
            retry: '\u91cd\u8bd5',
            // 重启应用行（宿主命令 desktop.app.restart；受理即静候，失败回页内）
            restartTitle: '重启应用',
            restartDesc: '退出并重新启动桌面壳，会话数据不受影响。',
            restartBtn: '重启',
            restarting: '正在重启…',
            restartFail: '重启失败',
            // 组标题（原散落在各 Section 的 '.ddc-gtitle' 字面量）
            updGroup: '\u66f4\u65b0',
            diagGroup: '\u8bca\u65ad',
            desktopGroup: '\u684c\u9762',
            // 更新按钮 hover/aria（原 UpdateButton 的 title/aria-label 字面量）
            updateTitle: '\u66f4\u65b0',
            installPrefix: '\u5b89\u88c5\u5e76\u91cd\u542f',
            installingNow: '\u5b89\u88c5\u4e2d\u2026',
            // 诊断导出（原 DIAG 对象）
            diagHint: '\u4ec5\u5305\u542b\u65e5\u5fd7\u4e0e\u8fd0\u884c\u72b6\u6001\uff0c\u4e0d\u542b\u4f1a\u8bdd\u4e0e\u51ed\u636e',
            diagSavedPrefix: '\u5df2\u4fdd\u5b58\u81f3\uff1a',
            diagFail: '\u5bfc\u51fa\u5931\u8d25',
            diagFailSep: '\uff1a',
            // 外部链接打开失败 toast（宿主导航层推 desktop.externalLinkOpenerFailed）
            linkFailTitle: '\u6253\u5f00\u5916\u90e8\u94fe\u63a5\u5931\u8d25',
            linkFailBody: '\u7cfb\u7edf\u672a\u80fd\u6253\u5f00\u8be5\u94fe\u63a5\uff0c\u8bf7\u590d\u5236\u5730\u5740\u5230\u6d4f\u89c8\u5668',
          }
          var en = {
            label: 'Desktop Settings',
            cur: 'Current version',
            idle: 'Not checked for updates yet',
            checking: 'Checking for updates\u2026',
            dl: 'Downloading',
            readySuffix: 'ready, installable',
            installing: 'Installing, the app will restart shortly\u2026',
            uptodate: 'Already up to date',
            errPrefix: 'Check failed: ',
            unknown: 'Unknown reason',
            unavail: 'Desktop self-update is unavailable in this runtime (set DSH_DESKTOP_UPDATE_FORCE=1 to enable in dev)',
            unavailChannel: 'Cannot reach the desktop host: the page command channel is unavailable this session; restart the app to restore self-update',
            // opencode settings-v2 同款行的文案
            checkTitle: 'Check for updates',
            checkDesc: 'Check whether a new version is available',
            actCheck: 'Check now',
            actChecking: 'Checking\u2026',
            actDownloading: 'Downloading\u2026',
            actInstall: 'Install and restart',
            actInstalling: 'Installing\u2026',
            diagTitle: 'Export diagnostics',
            diagBtn: 'Export',
            autostart: 'Launch at sign-in',
            autostartDesc: 'Launch DeepSeek Harness Desktop after sign-in',
            closeTitle: 'Minimize to tray on close',
            closeDesc: 'When checked, closing the window hides it to the tray; otherwise the app quits.',
            closeUnavailable: 'No system tray in this environment; the switch is unavailable.',
            // Command-channel failure (NOT "host answered unavailable"): state unknown + retryable.
            // Repeated failures point at the tray menu — the native tray chain still works when the
            // page-to-host channel is down (ADR app-restart-native-switch).
            hostUnreachableDesc: 'State unknown — retry; if it keeps happening, restart the app (tray menu → Restart)',
            retry: 'Retry',
            // Restart row (host command desktop.app.restart; accepted = hold "restarting", failure inline)
            restartTitle: 'Restart app',
            restartDesc: 'Quit and relaunch the desktop shell; session data is unaffected.',
            restartBtn: 'Restart',
            restarting: 'Restarting…',
            restartFail: 'Restart failed',
            // 组标题
            updGroup: 'Update',
            diagGroup: 'Diagnostics',
            desktopGroup: 'Desktop',
            // 更新按钮 hover/aria
            updateTitle: 'Update',
            installPrefix: 'Install and restart',
            installingNow: 'Installing\u2026',
            // 诊断导出
            diagHint: 'Contains only logs and runtime state, no sessions or credentials',
            diagSavedPrefix: 'Saved to: ',
            diagFail: 'Export failed',
            diagFailSep: ': ',
            // External-link open failure toast (host navigation layer pushes desktop.externalLinkOpenerFailed)
            linkFailTitle: 'Failed to open external link',
            linkFailBody: 'The link could not be opened. Copy the address into your browser.',
          }
          // 注册双字典并绑定翻译函数：t() 读调用时刻的 active locale。
          // ctx.effect 使注册随本插件 fiber 卸载而撤销（dshmarket 同款）。
          ctx.effect(function () { return ctx.locale.register(NS, { zh: zh, en: en }) }, 'dsh-desktop-companion: dictionaries')
          var t = ctx.locale.bind(NS)

          // 宿主 UI 语言桥接（desktop.companion.setLocale）：dsh locale runtime 在插件激活与
          // 每次切换时把 <html lang> 指向当前 locale（zh-CN/en），监听该属性即拿到切换时刻——
          // 宿主据此重建托盘菜单/选横幅文案。上报失败静默（旧宿主无此命令是合法形态）：
          // locale 桥是增强能力，绝不影响安装/更新主链路。
          ctx.effect(function () {
            var report = function () {
              var lang = (document.documentElement.getAttribute('lang') || '').toLowerCase()
              if (!lang) return
              try { window.__ryn.invoke('desktop.companion.setLocale', { locale: lang }).catch(function () {}) } catch (e) { /* __ryn 未就绪：下个 lang 变更再试 */ }
            }
            report()
            var localeObserver = new MutationObserver(report)
            localeObserver.observe(document.documentElement, { attributes: true, attributeFilter: ['lang'] })
            return function () { localeObserver.disconnect() }
          }, 'dsh-desktop-companion: locale bridge')

          var statusText = function (s) {
            switch (s && s.status) {
              case 'checking': return t('checking')
              case 'downloading': return t('dl') + (s.version ? ' ' + s.version : '') + '\u2026'
              case 'ready': return (s.version ? s.version + ' ' : '') + t('readySuffix')
              case 'installing': return t('installing')
              case 'uptodate': return t('uptodate')
              default: return t('idle')
            }
          }

          // 页面侧降级积压：命令通道失败时本页的失败只活在内存里，host.log 上零证据
          // （2026-10-01 实机：通道健康、开关不动、日志空白）——先积压，等本页下一次成功调用
          // （开关重试 / 更新重试 / 探针，即经 invokeWithTimeout 的命令）补报给宿主
          // desktop.companion.report。补报自身也接结算兜底：桥接 invoke 可能既不成功也不失败
          // （同 invokeWithTimeout 的依据），悬挂即回队——否则「不丢」不成立。
          // 有意取舍：队列是页面内存态，页面卸载即消失（跨页持久化不在本批，见 ADR Consequences）。
          var pendingReports = []
          var sendReport = function (item) {
            var settled = false
            var requeue = function () {
              if (settled) return
              settled = true
              pendingReports.push(item)
            }
            try {
              window.__ryn.invoke('desktop.companion.report', item).then(function () { settled = true }, requeue)
            } catch (e) { requeue() }
            setTimeout(requeue, 8000)
          }
          var flushReports = function () {
            if (!pendingReports.length) return
            var batch = pendingReports
            pendingReports = []
            for (var i = 0; i < batch.length; i++) sendReport(batch[i])
          }
          // 同一 scope+消息只积压一条：重试/重挂载把同一缺陷重复上报没有新增信息，只刷日志。
          var reportDegradation = function (scope, message) {
            var item = { scope: scope, message: message }
            for (var i = 0; i < pendingReports.length; i++) {
              if (pendingReports[i].scope === scope && pendingReports[i].message === message) { flushReports(); return }
            }
            pendingReports.push(item)
            flushReports()
          }

          // 命令通道调用：invoke + 结算锁 + 4s 兜底超时。桥接的 invoke 在 __ryn 不完整时会同步抛，
          // 包一层 catch 转 reject——「既不成功也不失败」与同步抛都收敛到失败，不留悬挂 Promise。
          // 成功路径顺带补报积压（通道恢复即落痕）；args 缺省 {}——空参数体的 invoke 在宿主分发层会 500。
          var invokeWithTimeout = function (command, args) {
            return new Promise(function (resolve, reject) {
              var settled = false
              var once = function (fn) {
                return function (v) { if (!settled) { settled = true; fn(v) } }
              }
              try {
                window.__ryn.invoke(command, args || {}).then(
                  once(function (v) { flushReports(); resolve(v) }), once(reject))
              } catch (e) { once(reject)(e) }
              setTimeout(once(reject), 4000)
            })
          }

          // 取帧：通道调用 + 形状校验。合法对象帧直接给；宿主错误帧（{"error":…}，如 autostart
          // getState 读盘失败）单独抛，让宿主给的原因进上报与日志，不被并成「unexpected frame」；
          // 其余（没答案 / 答了但不是帧）一律抛——调用方按「通道失败」处置，绝不把坏帧当结论。
          var invokeFrame = function (command, args) {
            return invokeWithTimeout(command, args).then(function (res) {
              var frame = parseFrame(res)
              if (frame && typeof frame === 'object') {
                if (typeof frame.error === 'string') throw new Error('host error: ' + frame.error)
                return frame
              }
              throw new Error('bad frame: ' + command)
            })
          }

          var queryState = function () { return invokeWithTimeout('desktop.update.getState') }

          // 开关行初值装载：首拉 + 800ms 重试一次，两次都失败才判定「通道失败」并补报。
          // 只有合法帧才敢下结论——把「没答案」渲染成「无系统托盘 / 已关闭」正是 2026-10-01 实机缺陷。
          var loadSwitchState = function (command, accept, settle) {
            var cancelled = false
            var attempts = 0
            var run = function () {
              invokeFrame(command).then(function (frame) {
                if (cancelled) return
                var state = accept(frame)
                if (state) { settle(state); return }
                fail('unexpected frame: ' + JSON.stringify(frame).slice(0, 120))
              }, function (e) {
                if (cancelled) return
                console.warn(TAG, command + ' failed:', e && e.message)
                fail(e && e.message ? String(e.message) : 'invoke failed')
              })
            }
            var fail = function (detail) {
              attempts += 1
              if (attempts >= 2) {
                reportDegradation(command, 'state unavailable after retry: ' + detail)
                settle({ channelDown: true })
                return
              }
              setTimeout(run, 800)
            }
            run()
            return function () { cancelled = true }
          }

          // 开关状态 hook：null=装载中；对象=已结算（{enabled,...} 或 {channelDown:true}）。
          // 返回 [state, setState, retry]——retry 递增 nonce 重跑装载，失败后可自愈，不必重开设置页。
          var useSwitchState = function (command, accept) {
            var sp = reactMod.useState(null)
            var state = sp[0]
            var setState = sp[1]
            var np = reactMod.useState(0)
            var nonce = np[0]
            var setNonce = np[1]
            reactMod.useEffect(function () {
              return loadSwitchState(command, accept, setState)
            }, [nonce])
            var retry = function () { setState(null); setNonce(nonce + 1) }
            return [state, setState, retry]
          }

          var channelDown = function (state) { return !!(state && state.channelDown) }

          // 官方 Switch 探测（ADR app-restart-native-switch）：dsh 模块表自带
          // @deepseek-ai/dsh-client-ui-primitives，但 Switch 导出在 0.1.1-rc.2 尚未出现、
          // 更新的 dsh 才有——取到即用原生（零漂移），取不到回退 .ddc-sw 克隆
          // （按官方 Switch.module.css 规格重画，上方样式块）。
          var nativeSwitch = null
          try {
            var primitivesMod = require('@deepseek-ai/dsh-client-ui-primitives')
            if (primitivesMod && typeof primitivesMod.Switch === 'function') nativeSwitch = primitivesMod.Switch
            else console.warn(TAG, 'ui-primitives has no Switch export; using clone fallback')
          } catch (e1b) {
            // 模块不在模块表：老 dsh，走克隆（留痕便于「为何开关长得不一样」的排障，评审 R2 采纳项）
            console.warn(TAG, 'ui-primitives module unavailable; using clone fallback', e1b && e1b.message)
          }

          // 开关行控件：官方 Switch 可用时直接消费（label 是官方组件的必填 aria 名，
          // 由调用方传行标题）；否则出克隆。通道失败仍优先给重试按钮。
          // disabled 谓词由调用方给（关闭到托盘另有一条 available=false），缺省 = 尚未结算。
          var switchControl = function (th, state, onToggle, retry, disabled, label) {
            if (channelDown(state)) {
              return h('button', {
                className: 'ovn-btn ovn-btn--ghost',
                type: 'button',
                onClick: retry,
              }, th('retry'))
            }
            var off = disabled === undefined ? !state : !!disabled
            var checked = !!(state && state.enabled)
            if (nativeSwitch) {
              return h(nativeSwitch, {
                checked: checked,
                disabled: off,
                onChange: onToggle,
                label: label || '',
              })
            }
            return h('button', {
              className: 'ddc-sw',
              type: 'button',
              role: 'switch',
              'aria-checked': checked ? 'true' : 'false',
              'aria-label': label || '',
              disabled: off,
              onClick: function () {
                if (!off && onToggle) onToggle(!checked)
              },
            }, h('i'))
          }

          // 开关行的 desc：通道失败 → 「状态未知 + 重试」；否则用调用方给的常规文案
          // （关闭到托盘在 available=false 时自带「无系统托盘」追加行）。两行共用同一映射。
          var switchDesc = function (th, state, normalDesc) {
            return channelDown(state) ? th('hostUnreachableDesc') : normalDesc
          }

          // 失败分流（ADR port-drift-ipc-origin-mismatch）：getState 失败有两种因、处置不同——
          // ① 宿主无自更新栈（dev 门禁，路由未注册）：给 dev 提示；② 页面→壳命令通道整体失效
          // （如端口漂移后页面 origin 变化、命令被浏览器 CORS 拦下）：给「重启应用可恢复」提示。
          // 判别不解析错误文案（那是桥接/宿主的实现细节）：用无条件注册的桌面命令探同一通道，
          // 探通即说明通道好、只有自更新路由缺失。只在失败路径多发一次 invoke。
          var probeCommandChannel = function () {
            return invokeWithTimeout('desktop.autostart.getState').then(function () { return true }, function (e) {
              console.warn(TAG, 'command channel probe failed:', e && e.message)
              return false
            })
          }

          // 设置页区块：undefined=查询中不渲染；{unavailable:'nostack'|'channel'}=页内提示；
          // 对象（宿主状态帧）=正常渲染。宿主推送的事件帧晚到会覆盖提示态（见 pushed 守卫）。
          // 通道失败不再锁死：首次挂载失败自动重查一次（3s），并给手动重试入口（重挂载整段 effect）。
          function UpdateSection(props) {
            var p = props || {}
            var th = p.t || function (k) { return k }
            var pair = reactMod.useState(undefined)
            var state = pair[0]
            var setState = pair[1]
            var np = reactMod.useState(0)
            var nonce = np[0]
            var setNonce = np[1]
            reactMod.useEffect(function () {
              // 宿主推送的状态帧优先：失败查询的异步结论不得覆盖已经到达的真实状态；
              // cancelled 挡住卸载后到达的结论（两个 4s 定时器都不随卸载取消）
              var pushed = false
              var cancelled = false
              var onEvt = function (e) { if (e.detail) { pushed = true; setState(e.detail) } }
              document.addEventListener('dsh-desktop-update', onEvt)
              queryState().then(function (s) {
                if (!pushed && !cancelled) setState(parseFrame(s) || { unavailable: 'nostack' })
              }).catch(function (e3) {
                console.warn(TAG, 'update getState failed:', e3 && e3.message)
                probeCommandChannel().then(function (channelOk) {
                  if (pushed || cancelled) return
                  if (!channelOk) {
                    reportDegradation('desktop.update.getState', 'command channel down (probe failed)')
                  }
                  setState({ unavailable: channelOk ? 'nostack' : 'channel' })
                  // 自动重查只给一次（nonce 0→1）：通道持续不通时把节奏交回用户，不无限重试
                  if (!channelOk && nonce === 0) {
                    setTimeout(function () { if (!cancelled) setNonce(1) }, 3000)
                  }
                })
              })
              return function () {
                cancelled = true
                document.removeEventListener('dsh-desktop-update', onEvt)
              }
            }, [nonce])
            if (state === undefined) return null
            if (state.unavailable) {
              return h('div', { className: 'ddc-group' },
                h('div', { className: 'ddc-gtitle' }, th('updGroup')),
                h('div', { className: 'ddc-list' },
                  h('div', { className: 'ddc-row2' },
                    h('div', { className: 'ddc-copy' },
                      h('div', { className: 'ddc-desc' }, th(state.unavailable === 'channel' ? 'unavailChannel' : 'unavail'))),
                    h('div', { className: 'ddc-ctl' },
                      h('button', {
                        className: 'ovn-btn ovn-btn--ghost',
                        type: 'button',
                        onClick: function () { setState(undefined); setNonce(nonce + 1) },
                      }, th('retry'))))))
            }
            var busy = state.status === 'checking' || state.status === 'downloading' || state.status === 'installing'
            // 按钮标签随状态机切换（opencode updater-action 同款）：ready 即安装入口，不再单设主按钮
            var actionLabel =
              state.status === 'checking' ? th('actChecking') :
              state.status === 'downloading' ? th('actDownloading') :
              state.status === 'ready' ? th('actInstall') :
              state.status === 'installing' ? th('actInstalling') : th('actCheck')
            var statusLine = state.status === 'error'
              ? h('span', { className: 'ddc-err' }, th('errPrefix') + (state.message || th('unknown')))
              : statusText(state)
            return h('div', { className: 'ddc-group' },
              h('div', { className: 'ddc-gtitle' }, th('updGroup')),
              h('div', { className: 'ddc-list' },
                h('div', { className: 'ddc-row2' },
                  h('div', { className: 'ddc-copy' },
                    h('div', { className: 'ddc-title' }, th('cur')),
                    h('div', { className: 'ddc-desc' }, state.current || '—', ' · ', statusLine)),
                  h('div', { className: 'ddc-ctl' })),
                h('div', { className: 'ddc-row2' },
                  h('div', { className: 'ddc-copy' },
                    h('div', { className: 'ddc-title' }, th('checkTitle')),
                    h('div', { className: 'ddc-desc' }, th('checkDesc'))),
                  h('div', { className: 'ddc-ctl' },
                    h('button', {
                      // 原生 save/discard 两态：ready=主按钮（安装入口），其余=描边幽灵
                      className: state.status === 'ready' ? 'ovn-btn' : 'ovn-btn ovn-btn--ghost',
                      type: 'button',
                      disabled: busy,
                      onClick: function () {
                        if (state.status === 'ready') {
                          window.__ryn.invoke('desktop.update.install', {}).catch(function () {})
                        } else {
                          window.__ryn.invoke('desktop.update.check', {}).catch(function () {})
                        }
                      },
                    }, actionLabel)))))
          }

          // 设置页「诊断」区块（order 51）：一键导出诊断 zip。点击即隐私确认——
          // 包内容为白名单日志与运行状态，不含会话/凭据；宿主无此命令时失败转页内提示
          function DiagnosticsSection(props) {
            var p = props || {}
            var th = p.t || function (k) { return k }
            var pair = reactMod.useState(null)
            var result = pair[0]
            var setResult = pair[1]
            return h('div', { className: 'ddc-group' },
              h('div', { className: 'ddc-gtitle' }, th('diagGroup')),
              h('div', { className: 'ddc-list' },
                h('div', { className: 'ddc-row2' },
                  h('div', { className: 'ddc-copy' },
                    h('div', { className: 'ddc-title' }, th('diagTitle')),
                    result === null
                      ? h('div', { className: 'ddc-desc' }, th('diagHint'))
                      : typeof result === 'string'
                        ? h('div', { className: 'ddc-desc' }, th('diagSavedPrefix') + result)
                        : h('div', { className: 'ddc-desc ddc-err' }, th('diagFail') + th('diagFailSep') + result.error)),
                  h('div', { className: 'ddc-ctl' },
                    h('button', {
                      className: 'ovn-btn ovn-btn--ghost',
                      type: 'button',
                      disabled: !!result,
                      onClick: function () {
                        window.__ryn.invoke('desktop.diagnostics.export', {}).then(function (res) {
                          var parsed = parseFrame(res)
                          if (parsed && parsed.path) setResult(parsed.path)
                          else setResult({ error: (parsed && parsed.error) || th('diagFail') })
                        }, function () {
                          setResult({ error: th('diagFail') })
                        })
                      },
                    }, th('diagBtn'))))))
          }

          // 「桌面」区块：开机自启 + 关闭时最小化到托盘 + 重启应用（开关行为官方发行说明同款行）。
          // 两行的状态一律经 useSwitchState 装载：合法帧才算数，「没答案」渲染成可重试的通道失败。
          function DesktopSection(props) {
            var p = props || {}
            var th = p.t || function (k) { return k }
            var asp = useSwitchState('desktop.autostart.getState', function (frame) {
              return typeof frame.enabled === 'boolean' ? { enabled: frame.enabled } : null
            })
            var state = asp[0]
            var setState = asp[1]
            var retryAutostart = asp[2]
            var toggleAutostart = function (next) {
              if (channelDown(state)) return
              setState({ enabled: next })
              invokeWithTimeout('desktop.autostart.set', { enabled: next }).then(function (res) {
                var frame = parseFrame(res)
                if (frame && typeof frame.enabled === 'boolean') setState({ enabled: frame.enabled })
                else { reportDegradation('desktop.autostart.set', 'unexpected frame; reverted'); setState({ enabled: !next }) }
              }, function (e) {
                reportDegradation('desktop.autostart.set', (e && e.message) || 'invoke failed; reverted')
                setState({ enabled: !next })
              })
            }
            return h('div', { className: 'ddc-group' },
              h('div', { className: 'ddc-gtitle' }, th('desktopGroup')),
              h('div', { className: 'ddc-list' },
                h('div', { className: 'ddc-row2' },
                  h('div', { className: 'ddc-copy' },
                    h('div', { className: 'ddc-title' }, th('autostart')),
                    h('div', { className: 'ddc-desc' }, switchDesc(th, state, th('autostartDesc')))),
                  h('div', { className: 'ddc-ctl' },
                    switchControl(th, state, toggleAutostart, retryAutostart, undefined, th('autostart')))),
                CloseToTrayRow({ t: th }),
                RestartRow({ t: th })))
          }

          // 关闭时最小化到托盘：宿主持久化于 <DSH_HOME>/desktop-preferences.json（默认开启，
          // 与历史行为一致）。available=false（宿主明确答）才表示无系统托盘——隐藏无从谈起，开关禁用；
          // 「没答案」走 channelDown，与「答了不可用」处置不同（重试 vs 换环境）。
          function CloseToTrayRow(props) {
            var p = props || {}
            var th = p.t || function (k) { return k }
            // 装载与 set 共用同一把形状尺：两字段齐才算合法帧——缺字段不得被当成「无系统托盘」
            var closeAccept = function (frame) {
              return typeof frame.enabled === 'boolean' && typeof frame.available === 'boolean'
                ? { enabled: frame.enabled, available: frame.available }
                : null
            }
            var csp = useSwitchState('desktop.closeToTray.getState', closeAccept)
            var st = csp[0]
            var setSt = csp[1]
            var retryClose = csp[2]
            var toggle = function (next) {
              if (!st || channelDown(st) || !st.available) return
              setSt({ enabled: next, available: true })
              invokeWithTimeout('desktop.closeToTray.set', { enabled: next }).then(function (res) {
                var frame = parseFrame(res)
                if (frame && typeof frame.enabled === 'boolean' && typeof frame.available === 'boolean') {
                  setSt({ enabled: frame.enabled, available: frame.available })
                } else {
                  reportDegradation('desktop.closeToTray.set', 'unexpected frame; reverted')
                  setSt({ enabled: !next, available: true })
                }
              }, function (e) {
                reportDegradation('desktop.closeToTray.set', (e && e.message) || 'invoke failed; reverted')
                setSt({ enabled: !next, available: true })
              })
            }
            var noTray = !!st && !channelDown(st) && st.available === false
            var desc = switchDesc(th, st, noTray
              ? h('span', null, th('closeDesc'), h('br'), th('closeUnavailable'))
              : th('closeDesc'))
            return h('div', { className: 'ddc-row2' },
              h('div', { className: 'ddc-copy' },
                h('div', { className: 'ddc-title' }, th('closeTitle')),
                h('div', { className: 'ddc-desc' }, desc)),
              h('div', { className: 'ddc-ctl' },
                switchControl(th, st, toggle, retryClose, !st || st.available === false, th('closeTitle'))))
          }

          // 重启应用（ADR app-restart-native-switch）：受理即静候——宿主 200ms 冲刷后回收进程
          // 重启，页面随进程退出销毁；失败转页内错误（含 4s 超时兜底误报，见 invokeWithTimeout）。
          // 通道整体失效时本按钮同样发不出，hostUnreachableDesc 已把该场景指向托盘菜单。
          function RestartRow(props) {
            var p = props || {}
            var th = p.t || function (k) { return k }
            var bpair = reactMod.useState(false)
            var busy = bpair[0]
            var setBusy = bpair[1]
            var epair = reactMod.useState(null)
            var err = epair[0]
            var setErr = epair[1]
            var doRestart = function () {
              if (busy) return
              setBusy(true)
              setErr(null)
              invokeWithTimeout('desktop.app.restart', {}).then(function () {
                // 受理成功即保持「正在重启」态：后续结论（成功销毁/失败回显）都不需要本组件再动作
              }, function (e) {
                setBusy(false)
                var detail = (e && e.message) ? String(e.message) : th('restartFail')
                setErr(detail)
                reportDegradation('desktop.app.restart', detail)
              })
            }
            return h('div', { className: 'ddc-row2' },
              h('div', { className: 'ddc-copy' },
                h('div', { className: 'ddc-title' }, th('restartTitle')),
                err !== null
                  ? h('div', { className: 'ddc-desc ddc-err' }, th('restartFail') + th('diagFailSep') + err)
                  : h('div', { className: 'ddc-desc' }, th('restartDesc'))),
              h('div', { className: 'ddc-ctl' },
                h('button', {
                  className: 'ovn-btn ovn-btn--ghost',
                  type: 'button',
                  disabled: busy,
                  onClick: doRestart,
                }, busy ? th('restarting') : th('restartBtn'))))
          }

          ctx.slots.inject('sidebar.footer.action', function () {
            return ctx.slots.register({
              name: 'sidebar.footer.action',
              id: 'dsh-desktop-companion-update',
              label: function () { return t('label') },
              locale: NS,
            }, function (props) {
              return h(UpdateButton, props)
            })
          })
          // 设置页「桌面设置」（order 50）：更新 / 诊断 / 开机自启三块合一页——用户拍板
          // 不为每块单开导航页（ADR companion-settings-consolidation）；
          // 无自更新栈时由 UpdateSection 自行降级为不可用提示，其余块不受影响
          ctx.slots.inject('settings.section', function () {
            return ctx.slots.register({
              name: 'settings.section',
              id: 'dsh-desktop-companion-update',
              order: 50,
              label: function () { return t('label') },
              locale: NS,
            }, function (props) {
              return h('div', { className: 'ddc-page' },
                h(UpdateSection, { t: props.t }),
                h(DiagnosticsSection, { t: props.t }),
                h(DesktopSection, { t: props.t }))
            })
          })
          return true
        }

        try {
          setupUpdateUI()
        } catch (e) {
          console.warn(TAG, 'update UI setup error', e)
        }

        // 外部链接打开失败 toast（R2 N2）：宿主导航层拦截站外链接、经系统浏览器打开失败时，
        // 推 desktop.externalLinkOpenerFailed 事件（见 PageBridge/RynNavigationCallbacks）。页面侧
        // 用纯 DOM 渲染一个短暂 toast（不经 dsh slot 树——它是页面级浮动层；文案经 locale 随语言切换）。
        try {
          if (window.__ryn && window.__ryn.on && !window.__dshDesktopCompanionLinkFailToast) {
            window.__dshDesktopCompanionLinkFailToast = true
            var toastT = ctx.locale.bind('desktop-companion')
            var toastTimer = null
            var toastStyle = document.getElementById('dsh-desktop-companion-linkfail-css')
            if (!toastStyle) {
              toastStyle = document.createElement('style')
              toastStyle.id = 'dsh-desktop-companion-linkfail-css'
              toastStyle.textContent =
                '#ddc-linkfail-toast{position:fixed;left:50%;bottom:24px;transform:translateX(-50%) translateY(20px);' +
                'max-width:640px;padding:10px 16px;border-radius:10px;' +
                // toast 生于 dsh 页面内，--dsw-alias-* 必然已定义；回退值仅防御性兜底，随主题自适应
                'background:var(--dsw-alias-bg-layer-3,#22222e);border:1px solid var(--dsw-alias-border-l2,#3a3a4a);' +
                'color:var(--dsw-alias-label-primary,#e6e6ea);' +
                'font:13px/1.5 system-ui,sans-serif;box-shadow:0 6px 24px rgba(0,0,0,.4);' +
                'opacity:0;pointer-events:none;transition:opacity .18s ease,transform .18s ease;z-index:2147483646}' +
                '#ddc-linkfail-toast.ddc-linkfail-show{opacity:1;transform:translateX(-50%) translateY(0)}'
              document.head.appendChild(toastStyle)
            }
            window.__ryn.on('desktop.externalLinkOpenerFailed', function (data) {
              try {
                var url = data && data.url ? String(data.url) : ''
                var box = document.getElementById('ddc-linkfail-toast')
                if (!box) {
                  box = document.createElement('div')
                  box.id = 'ddc-linkfail-toast'
                  box.setAttribute('role', 'alert')
                  document.body.appendChild(box)
                }
                // 有 url 显示标题+地址，无 url 退化显示正文提示（默认编码器转义，textContent 零注入）
                box.textContent = url ? toastT('linkFailTitle') + '\uff1a' + url : toastT('linkFailBody')
                box.classList.add('ddc-linkfail-show')
                if (toastTimer) clearTimeout(toastTimer)
                toastTimer = setTimeout(function () { box.classList.remove('ddc-linkfail-show') }, 5000)
              } catch (e2) { console.warn(TAG, 'link fail toast error', e2) }
            })
          }
        } catch (e) { /* no __ryn bridge: nothing to toast */ }
      }

      // inject 声明本插件要访问的宿主服务：不声明时访问 ctx.slots / ctx.locale 会被
      // cordis 以 "cannot get property without inject" 拒绝（dshmarket 同款）。
      // locale 用于客户端文案随 dsh 语言切换（中⇄英），见 dsh-client-locale 机制。
      return { apply: apply, inject: ['slots', 'locale'] }
    },
  })
})()
