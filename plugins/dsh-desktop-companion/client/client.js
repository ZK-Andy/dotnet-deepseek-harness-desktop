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
            '@keyframes ddc-rot{to{transform:rotate(360deg)}}'
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

          ctx.slots.inject('sidebar.footer.action', function () {
            return ctx.slots.register({
              name: 'sidebar.footer.action',
              id: 'dsh-desktop-companion-update',
              label: function () { return '\u684c\u9762\u66f4\u65b0' },
            }, function (props) {
              return h(UpdateButton, props)
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
