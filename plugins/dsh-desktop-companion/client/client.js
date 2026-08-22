/**
 * dsh-desktop-companion client half.
 *
 * Loaded by the dsh web shell's module system (window.__ModuleLoader__) once
 * per app boot — before React mounts, and again after every full document
 * load, so a plain capture-phase listener registered here survives all SPA
 * re-renders without any host-side re-injection machinery.
 *
 * External-link takeover mirrors the retired host-side injected catcher
 * exactly: top frame only, http(s) links that are target=_blank or
 * cross-origin are preventDefault'ed and handed to the desktop shell via
 * window.__ryn.invoke('app.openExternal', { url }). Same-origin navigations
 * and every non-http(s) scheme pass through untouched.
 */
;(function () {
  if (typeof window === 'undefined' || !window.__ModuleLoader__) return
  window.__ModuleLoader__.load({
    id: 'dsh-desktop-companion',
    factory: function () {
      // ---- SPIKE: temporary visibility marker (delete after real-desktop acceptance) ----
      try {
        if (!document.getElementById('dsh-desktop-companion-spike')) {
          var marker = document.createElement('div')
          marker.id = 'dsh-desktop-companion-spike'
          marker.textContent = 'dsh-desktop-companion · spike'
          marker.setAttribute('style', [
            'position:fixed', 'right:12px', 'bottom:12px', 'z-index:2147483647',
            'padding:4px 10px', 'border-radius:9999px',
            'background:rgba(15,15,19,.85)', 'color:#e6e6ea',
            'font:12px/1.6 system-ui,sans-serif',
            'pointer-events:none', 'opacity:.75',
          ].join(';'))
          document.body.append(marker)
        }
      } catch (e) { /* headless/undommented host: marker is cosmetic only */ }
      // ---- SPIKE end ----

      // External-link takeover (the feature proper).
      try {
        if (window.top === window.self && !window.__dshDesktopCompanionLinks) {
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

      return {}
    },
  })
})()
