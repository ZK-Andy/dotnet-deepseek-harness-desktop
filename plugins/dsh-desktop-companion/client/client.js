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
 * - External-link takeover: mirrors the retired host-side injected catcher
 *   exactly (top frame only, http(s) + _blank/cross-origin → preventDefault →
 *   window.__ryn.invoke('app.openExternal', { url })).
 * - Self-update button in the sidebar footer (beside Settings): renders ONLY
 *   while the host reports status=ready; hover expands「更新 vX.Y.Z」;
 *   click installs+restarts via desktop.update.install.
 * - Self-update section in Settings (settings.section): current version,
 *   manual check entry, full status line incl. host-reported error reason;
 *   shows an "unavailable" hint when the runtime has no update stack (dev).
 * - Tray event relay: forwards Ryn tray plugin events (tray.clicked,
 *   tray.menuItemClicked) to the host via desktop.tray.event; show /
 *   check-update / quit semantics are resolved on the host side.
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

        // External-link takeover (the feature proper).
        try {
          // Transitional coexistence: released shells (≤ the version that still
          // ships the injected catcher) may register their own capture listener
          // guarded by window.__ryn_externalLinkCatcher. Whichever of the two
          // claims first wins and sets BOTH flags, so the latecomer's own guard
          // makes it bail — exactly one handler per document either way.
          // Remove this flag dance once no released shell injects the catcher.
          // 无 __ryn 桥（纯浏览器标签页）时不注册 capture：拦截后 invoke 无处可去、
          // 外链变死链，与「keep page fully functional」的目标相悖
          if (window.top === window.self && window.__ryn && !window.__ryn_externalLinkCatcher && !window.__dshDesktopCompanionLinks) {
            window.__ryn_externalLinkCatcher = true
            window.__dshDesktopCompanionLinks = true
            var ryn = window.__ryn
            var isExternal = function (a, origin) {
              var href = a.getAttribute('href')
              if (!href) return false
              var u
              try { u = new URL(href, origin) } catch (e) { return false }
              if (u.protocol !== 'http:' && u.protocol !== 'https:') return false
              if (a.target === '_blank') return true
              return u.origin !== origin
            }
            var onClick = function (e) {
              var a = e.target && e.target.closest ? e.target.closest('a[href]') : null
              if (!a) return
              if (!isExternal(a, window.location.origin)) return
              e.preventDefault()
              ryn.invoke('app.openExternal', { url: a.href }).catch(function () {})
            }
            document.addEventListener('click', onClick, true)
          }
        } catch (e) { /* no __ryn bridge (plain browser tab): keep page fully functional */ }

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
            // save/discard 两态（主=反色填充，次=描边幽灵）；开关为 28×16 克隆。
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
            '.ddc-sw{position:relative;width:28px;height:16px;flex:none;border-radius:3px;' +
            'border:1px solid var(--dsw-alias-border-l2);background:transparent;cursor:pointer;' +
            'padding:0;transition:background .15s,border-color .15s}' +
            '.ddc-sw:hover:not(:disabled){border-color:var(--dsw-alias-label-dimmed)}' +
            '.ddc-sw[aria-checked="true"]{border-color:transparent;' +
            'background:#22c55e;background:var(--dsw-alias-state-success-primary,#22c55e)}' +
            '.ddc-sw[disabled]{opacity:.4;cursor:default}' +
            '.ddc-sw i{position:absolute;top:50%;left:1px;width:14px;height:14px;margin-top:-7px;' +
            'border-radius:2px;background:var(--dsw-alias-bg-base,#fff);' +
            'box-shadow:0 1px 3px rgba(0,0,0,.35);transform:translateX(0);transition:transform .15s}' +
            '.ddc-sw[aria-checked="true"] i{transform:translateX(11px)}'
          if (!document.getElementById(style.id)) document.head.append(style)

          function UpdateButton(props) {
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
            var wide = !(props && props.wide === false)
            var installing = !!state && state.status === 'installing'
            if (!state || (state.status !== 'ready' && !installing)) return null
            var cls = 'ddc-upd' + (wide ? '' : ' ddc-rail')
            return h('button', {
              className: cls,
              type: 'button',
              disabled: installing,
              title: state.version ? '\u66f4\u65b0 ' + state.version : '\u66f4\u65b0',
              'aria-label': state.version ? '\u5b89\u88c5\u5e76\u91cd\u542f ' + state.version : '\u5b89\u88c5\u5e76\u91cd\u542f',
              onClick: function () {
                window.__ryn.invoke('desktop.update.install', {}).catch(function () {})
              },
            }, h('span', { className: 'ddc-ic' },
              installing
                ? h('span', { className: 'ddc-spin' })
                : h('svg', { width: 14, height: 14, viewBox: '0 0 14 14', fill: 'none', 'aria-hidden': true },
                    h('path', { d: 'M7 11V3M3.5 7.63128L7 11L10.5 7.63128', stroke: 'currentColor' })),
              ), h('span', { className: 'ddc-lb' },
                installing ? '\u5b89\u88c5\u4e2d\u2026' : ('\u66f4\u65b0 ' + (state.version || ''))))
          }

          var STR = {
            label: '\u684c\u9762\u8bbe\u7f6e',
            cur: '\u5f53\u524d\u7248\u672c',
            idle: '\u5c1a\u672a\u68c0\u67e5\u66f4\u65b0',
            checking: '\u6b63\u5728\u68c0\u67e5\u66f4\u65b0\u2026',
            dl: '\u6b63\u5728\u4e0b\u8f7d',
            readySuffix: '\u5c31\u7eea\uff0c\u53ef\u5b89\u88c5',
            installBtn: '\u7acb\u5373\u5b89\u88c5\u5e76\u91cd\u542f',
            installing: '\u6b63\u5728\u5b89\u88c5\uff0c\u5e94\u7528\u5373\u5c06\u91cd\u542f\u2026',
            uptodate: '\u5df2\u662f\u6700\u65b0\u7248\u672c',
            errPrefix: '\u68c0\u67e5\u5931\u8d25\uff1a',
            unknown: '\u672a\u77e5\u539f\u56e0',
            checkBtn: '\u68c0\u67e5\u66f4\u65b0',
            unavail: '\u684c\u9762\u81ea\u66f4\u65b0\u5728\u5f53\u524d\u8fd0\u884c\u65f6\u4e0d\u53ef\u7528\uff08\u5f00\u53d1\u8fd0\u884c\u65f6\u53ef\u8bbe DSH_DESKTOP_UPDATE_FORCE=1 \u5f00\u542f\uff09',
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
            autostartDesc: '登录后自动启动 DeepSeek Harness 桌面端',
            closeTitle: '关闭时最小化到托盘',
            closeDesc: '勾选后点击关闭按钮会隐藏到系统托盘，取消则直接退出应用。',
            closeUnavailable: '当前运行环境无系统托盘，开关不可用。',
          }

          var statusText = function (s) {
            switch (s && s.status) {
              case 'checking': return STR.checking
              case 'downloading': return STR.dl + (s.version ? ' ' + s.version : '') + '\u2026'
              case 'ready': return (s.version ? s.version + ' ' : '') + STR.readySuffix
              case 'installing': return STR.installing
              case 'uptodate': return STR.uptodate
              default: return STR.idle
            }
          }

          // getState 兜底：宿主无自更新栈（dev 门禁）时命令路由不存在，invoke 应以失败
          // 告终；再叠加 4s 超时——任何「既不成功也不失败」的异常路径都收敛到不可用提示，
          // 设置页绝不留白。reason 仅进控制台，页面文案统一走 STR.unavail。
          var queryState = function () {
            return new Promise(function (resolve, reject) {
              var settled = false
              var once = function (fn) {
                return function (v) { if (!settled) { settled = true; fn(v) } }
              }
              window.__ryn.invoke('desktop.update.getState', {}).then(once(resolve), once(reject))
              setTimeout(once(reject), 4000)
            })
          }

          // opencode Switch 同款开关（28×16 轨道 + 14×14 滑块）
          function Switch2(props) {
            return h('button', {
              className: 'ddc-sw',
              type: 'button',
              role: 'switch',
              'aria-checked': props.checked ? 'true' : 'false',
              disabled: !!props.disabled,
              onClick: function () {
                if (!props.disabled && props.onChange) props.onChange(!props.checked)
              },
            }, h('i'))
          }

          // 设置页区块：undefined=查询中不渲染，null=宿主无自更新栈（页内提示），对象=正常状态帧
          function UpdateSection() {
            var pair = reactMod.useState(undefined)
            var state = pair[0]
            var setState = pair[1]
            reactMod.useEffect(function () {
              var onEvt = function (e) { if (e.detail) setState(e.detail) }
              document.addEventListener('dsh-desktop-update', onEvt)
              queryState().then(function (s) {
                setState(parseFrame(s) || null)
              }).catch(function (e3) {
                console.warn(TAG, 'update section unavailable:', e3 && e3.message)
                setState(null)
              })
              return function () { document.removeEventListener('dsh-desktop-update', onEvt) }
            }, [])
            if (state === undefined) return null
            if (state === null) {
              return h('div', { className: 'ddc-group' },
                h('div', { className: 'ddc-gtitle' }, '更新'),
                h('div', { className: 'ddc-desc' }, STR.unavail))
            }
            var busy = state.status === 'checking' || state.status === 'downloading' || state.status === 'installing'
            // 按钮标签随状态机切换（opencode updater-action 同款）：ready 即安装入口，不再单设主按钮
            var actionLabel =
              state.status === 'checking' ? STR.actChecking :
              state.status === 'downloading' ? STR.actDownloading :
              state.status === 'ready' ? STR.actInstall :
              state.status === 'installing' ? STR.actInstalling : STR.actCheck
            var statusLine = state.status === 'error'
              ? h('span', { className: 'ddc-err' }, STR.errPrefix + (state.message || STR.unknown))
              : statusText(state)
            return h('div', { className: 'ddc-group' },
              h('div', { className: 'ddc-gtitle' }, '更新'),
              h('div', { className: 'ddc-list' },
                h('div', { className: 'ddc-row2' },
                  h('div', { className: 'ddc-copy' },
                    h('div', { className: 'ddc-title' }, STR.cur),
                    h('div', { className: 'ddc-desc' }, state.current || '—', ' · ', statusLine)),
                  h('div', { className: 'ddc-ctl' })),
                h('div', { className: 'ddc-row2' },
                  h('div', { className: 'ddc-copy' },
                    h('div', { className: 'ddc-title' }, STR.checkTitle),
                    h('div', { className: 'ddc-desc' }, STR.checkDesc)),
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
          var DIAG = {
            btn: '\u5bfc\u51fa\u8bca\u65ad\u4fe1\u606f',
            hint: '\u4ec5\u5305\u542b\u65e5\u5fd7\u4e0e\u8fd0\u884c\u72b6\u6001\uff0c\u4e0d\u542b\u4f1a\u8bdd\u4e0e\u51ed\u636e',
            savedPrefix: '\u5df2\u4fdd\u5b58\u81f3\uff1a',
            fail: '\u5bfc\u51fa\u5931\u8d25',
          }
          function DiagnosticsSection() {
            var pair = reactMod.useState(null)
            var result = pair[0]
            var setResult = pair[1]
            return h('div', { className: 'ddc-group' },
              h('div', { className: 'ddc-gtitle' }, '诊断'),
              h('div', { className: 'ddc-list' },
                h('div', { className: 'ddc-row2' },
                  h('div', { className: 'ddc-copy' },
                    h('div', { className: 'ddc-title' }, STR.diagTitle),
                    result === null
                      ? h('div', { className: 'ddc-desc' }, DIAG.hint)
                      : typeof result === 'string'
                        ? h('div', { className: 'ddc-desc' }, DIAG.savedPrefix + result)
                        : h('div', { className: 'ddc-desc ddc-err' }, DIAG.fail + '：' + result.error)),
                  h('div', { className: 'ddc-ctl' },
                    h('button', {
                      className: 'ovn-btn ovn-btn--ghost',
                      type: 'button',
                      disabled: !!result,
                      onClick: function () {
                        window.__ryn.invoke('desktop.diagnostics.export', {}).then(function (res) {
                          var parsed = parseFrame(res)
                          if (parsed && parsed.path) setResult(parsed.path)
                          else setResult({ error: (parsed && parsed.error) || DIAG.fail })
                        }, function () {
                          setResult({ error: DIAG.fail })
                        })
                      },
                    }, STR.diagBtn)))))
          }

          // 「桌面」区块：开机自启 + 关闭时最小化到托盘（均为 opencode 发行说明同款开关行）
          var DESK = {
            autostart: '\u5f00\u673a\u81ea\u542f',
          }
          function DesktopSection() {
            var asp = reactMod.useState(null)
            var enabled = asp[0]
            var setEnabled = asp[1]
            reactMod.useEffect(function () {
              window.__ryn.invoke('desktop.autostart.getState', {}).then(function (res) {
                var p = parseFrame(res)
                if (p && typeof p.enabled === 'boolean') setEnabled(p.enabled)
              }, function () { setEnabled(false) })
            }, [])
            var toggleAutostart = function (next) {
              setEnabled(null)
              window.__ryn.invoke('desktop.autostart.set', { enabled: next }).then(function (res) {
                var p = parseFrame(res)
                setEnabled(p && typeof p.enabled === 'boolean' ? p.enabled : next)
              }, function () { setEnabled(!next) })
            }
            return h('div', { className: 'ddc-group' },
              h('div', { className: 'ddc-gtitle' }, '桌面'),
              h('div', { className: 'ddc-list' },
                h('div', { className: 'ddc-row2' },
                  h('div', { className: 'ddc-copy' },
                    h('div', { className: 'ddc-title' }, DESK.autostart),
                    h('div', { className: 'ddc-desc' }, STR.autostartDesc)),
                  h('div', { className: 'ddc-ctl' },
                    Switch2({ checked: enabled, disabled: enabled === null, onChange: toggleAutostart }))),
                CloseToTrayRow()))
          }

          // 关闭时最小化到托盘：宿主持久化于 <DSH_HOME>/desktop-preferences.json（默认开启，
          // 与历史行为一致）。available=false 表示无系统托盘——隐藏无从谈起，开关禁用。
          function CloseToTrayRow() {
            var sp = reactMod.useState(null)
            var st = sp[0]
            var setSt = sp[1]
            reactMod.useEffect(function () {
              window.__ryn.invoke('desktop.closeToTray.getState', {}).then(function (res) {
                var p = parseFrame(res)
                if (p && typeof p.enabled === 'boolean') setSt({ enabled: p.enabled, available: !!p.available })
                else setSt({ enabled: true, available: false })
              }, function () { setSt({ enabled: true, available: false }) })
            }, [])
            var toggle = function (next) {
              if (!st || !st.available) return
              setSt({ enabled: next, available: true })
              window.__ryn.invoke('desktop.closeToTray.set', { enabled: next }).then(function (res) {
                var p = parseFrame(res)
                if (p && typeof p.enabled === 'boolean') setSt({ enabled: p.enabled, available: !!p.available })
                else setSt({ enabled: !next, available: true })
              }, function () { setSt({ enabled: !next, available: true }) })
            }
            var unavailable = !!st && !st.available
            var desc = unavailable
              ? h('span', null, STR.closeDesc, h('br'), STR.closeUnavailable)
              : STR.closeDesc
            return h('div', { className: 'ddc-row2' },
              h('div', { className: 'ddc-copy' },
                h('div', { className: 'ddc-title' }, STR.closeTitle),
                h('div', { className: 'ddc-desc' }, desc)),
              h('div', { className: 'ddc-ctl' },
                Switch2({
                  checked: !!st && st.enabled,
                  disabled: !st || unavailable,
                  onChange: toggle,
                })))
          }

          ctx.slots.inject('sidebar.footer.action', function () {
            return ctx.slots.register({
              name: 'sidebar.footer.action',
              id: 'dsh-desktop-companion-update',
              label: function () { return '\u684c\u9762\u8bbe\u7f6e' },
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
              label: function () { return STR.label },
            }, function (props) {
              return h('div', { className: 'ddc-page' },
                h(UpdateSection, props),
                h(DiagnosticsSection, props),
                h(DesktopSection, props))
            })
          })
          return true
        }

        try {
          setupUpdateUI()
        } catch (e) {
          console.warn(TAG, 'update UI setup error', e)
        }
      }

      // inject 声明本插件要访问的宿主服务：不声明时访问 ctx.slots 会被
      // cordis 以 "cannot get property without inject" 拒绝（dshmarket 同款）。
      return { apply: apply, inject: ['slots'] }
    },
  })
})()
