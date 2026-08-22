/**
 * dsh-desktop-companion client half.
 *
 * Loaded by the dsh web shell's module system (window.__ModuleLoader__) once
 * per app boot — before React mounts, and again after every full document
 * load, so a plain capture-phase listener registered here survives all SPA
 * re-renders without any host-side re-injection machinery.
 *
 * The web kernel adopts each boot-manifest module as a Cordis loader entry and
 * applies its exports: the factory must return an object carrying apply(ctx)
 * (the dsh-client-* convention). An exports object without apply fails the
 * entry — and one failed entry aborts the whole web boot.
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
      /**
       * Register the client half against the client cordis context.
       * @param {object} ctx - Client cordis context. No services acquired.
       */
      function apply(ctx) {
        // External-link takeover (the feature proper).
        try {
          // Transitional coexistence: released shells (≤ the version that still
          // ships the injected catcher) may register their own capture listener
          // guarded by window.__ryn_externalLinkCatcher. Whichever of the two
          // claims first wins and sets BOTH flags, so the latecomer's own guard
          // makes it bail — exactly one handler per document either way.
          // Remove this flag dance once no released shell injects the catcher.
          if (window.top === window.self && !window.__ryn_externalLinkCatcher && !window.__dshDesktopCompanionLinks) {
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
      }

      return { apply: apply }
    },
  })
})()
