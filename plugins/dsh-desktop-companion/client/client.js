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

        var parseState = function (raw) {
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
            '.ddc-set{max-width:520px;display:flex;flex-direction:column;gap:12px;padding:4px 0}' +
            '.ddc-set .ddc-cur,.ddc-set .ddc-status,.ddc-set .ddc-hint{font-size:13px;line-height:1.5}' +
            '.ddc-set .ddc-cur{opacity:.72}' +
            '.ddc-set .ddc-err{color:#ef4444;color:var(--dsw-alias-state-danger-primary,#ef4444)}' +
            '.ddc-btn{height:30px;padding:0 14px;border-radius:8px;border:1px solid rgba(127,127,127,.35);' +
            'background:transparent;color:inherit;cursor:pointer;font-size:13px;' +
            'transition:background .15s ease,border-color .15s ease}' +
            '.ddc-btn:hover:not(:disabled){background:rgba(127,127,127,.12);border-color:rgba(127,127,127,.55)}' +
            '.ddc-btn:disabled{opacity:.55;cursor:default}' +
            '.ddc-btn-primary{background:#22c55e;background:var(--dsw-alias-state-success-primary,#22c55e);' +
            'border-color:transparent;color:#e6fff2;color:var(--dsw-alias-state-success-tertiary,#e6fff2)}' +
            '.ddc-btn-primary:hover:not(:disabled){filter:brightness(1.05)}'
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
                var parsed = parseState(s)
                if (parsed && parsed.status === 'ready') setState(parsed)
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

          // 设置页区块：undefined=查询中不渲染，null=宿主无自更新栈（页内提示），对象=正常状态帧
          function UpdateSection() {
            var pair = reactMod.useState(undefined)
            var state = pair[0]
            var setState = pair[1]
            reactMod.useEffect(function () {
              var onEvt = function (e) { if (e.detail) setState(e.detail) }
              document.addEventListener('dsh-desktop-update', onEvt)
              queryState().then(function (s) {
                setState(parseState(s) || null)
              }).catch(function (e3) {
                console.warn(TAG, 'update section unavailable:', e3 && e3.message)
                setState(null)
              })
              return function () { document.removeEventListener('dsh-desktop-update', onEvt) }
            }, [])
            if (state === undefined) return null
            if (state === null) {
              return h('div', { className: 'ddc-set' }, h('div', { className: 'ddc-hint' }, STR.unavail))
            }
            var busy = state.status === 'checking' || state.status === 'downloading' || state.status === 'installing'
            return h('div', { className: 'ddc-set' },
              h('div', { className: 'ddc-cur' }, STR.cur + (state.current ? ' ' + state.current : '')),
              h('div', { className: 'ddc-status' },
                state.status === 'error'
                  ? h('span', { className: 'ddc-err' }, STR.errPrefix + (state.message || STR.unknown))
                  : statusText(state)),
              h('div', { className: 'ddc-row' },
                h('button', {
                  className: 'ddc-btn',
                  type: 'button',
                  disabled: busy,
                  onClick: function () {
                    window.__ryn.invoke('desktop.update.check', {}).catch(function () {})
                  },
                }, STR.checkBtn),
                state.status === 'ready'
                  ? h('button', {
                      className: 'ddc-btn ddc-btn-primary',
                      type: 'button',
                      onClick: function () {
                        window.__ryn.invoke('desktop.update.install', {}).catch(function () {})
                      },
                    }, STR.installBtn)
                  : null))
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
            return h('div', { className: 'ddc-set' },
              h('div', { className: 'ddc-hint' }, DIAG.hint),
              h('div', { className: 'ddc-row' },
                h('button', {
                  className: 'ddc-btn',
                  type: 'button',
                  disabled: !!result,
                  onClick: function () {
                    window.__ryn.invoke('desktop.diagnostics.export', {}).then(function (res) {
                      var parsed = null
                      try { parsed = JSON.parse(res) } catch (e2) {}
                      if (parsed && parsed.path) setResult(parsed.path)
                      else setResult({ error: (parsed && parsed.error) || DIAG.fail })
                    }, function () {
                      setResult({ error: DIAG.fail })
                    })
                  },
                }, DIAG.btn)),
              result === null
                ? null
                : typeof result === 'string'
                  ? h('div', { className: 'ddc-hint' }, DIAG.savedPrefix + result)
                  : h('div', { className: 'ddc-err' }, DIAG.fail + '：' + result.error))
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
          // 设置页「桌面设置」：手动检查入口（遗留项①后半，见 ADR companion-update-settings-section）；
          // order 50 排在市场（40）之后；无自更新栈时由 UpdateSection 自行降级为不可用提示
          ctx.slots.inject('settings.section', function () {
            return ctx.slots.register({
              name: 'settings.section',
              id: 'dsh-desktop-companion-update',
              order: 50,
              label: function () { return STR.label },
            }, function (props) {
              return h(UpdateSection, props)
            })
          })
          // 「诊断」区块：一键导出诊断 zip（ADR shell-observability-diagnostics）
          ctx.slots.inject('settings.section', function () {
            return ctx.slots.register({
              name: 'settings.section',
              id: 'dsh-desktop-companion-diagnostics',
              order: 51,
              label: function () { return '\u8bca\u65ad' },
            }, function (props) {
              return h(DiagnosticsSection, props)
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
