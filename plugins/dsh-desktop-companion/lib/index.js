/**
 * dsh-desktop-companion host half.
 *
 * Features live mostly in the client half (self-update UI / settings section,
 * see client/client.js); external-link interception lives in the host
 * navigation layer (PageBridge/ShellBridge). This host half exists so the
 * profile layer stack has a real entry and a place to mount privileged routes
 * later (self-update: release check / download state surfaced to the shell).
 */

/** Stable Cordis plugin name. */
export const name = 'dsh-desktop-companion'

/**
 * Register the plugin against the host context.
 * @param {object} ctx - Host cordis context. No services acquired yet.
 * @param {Record<string, unknown>} [config] - Optional profile override from the loader.
 */
export function apply(ctx, config) {
  // Intentionally inert: nothing here needs host services yet.
}
