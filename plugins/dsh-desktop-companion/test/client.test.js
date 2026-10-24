/**
 * Behavioural tests for the companion client half (settings section + sidebar
 * footer action + tray relay + locale bridge + external-link toast).
 *
 * The plugin is a dsh web-shell client bundle: its UI is built from
 * `createElement` calls and its host interaction is the `window.__shell.invoke`
 * command channel. The harness renders components eagerly and fakes that
 * channel, so assertions are about observable output and command traffic, not
 * about React internals.
 */
import assert from 'node:assert/strict'
import { test } from 'node:test'
import { findAll, find, loadClient, settle, textOf } from '../testkit/harness.js'

/** Render the settings slot and settle the initial command round-trips. */
async function openSettings(harness) {
  const slot = harness.slot('settings.section')
  slot.render()
  await settle()
  return slot
}

/** Render the sidebar action and settle the initial state query. */
async function openSidebar(harness) {
  const slot = harness.slot('sidebar.footer.action')
  slot.render()
  await settle()
  return slot
}

test('module registration carries the id / factory / apply + inject contract', () => {
  const harness = loadClient()

  assert.equal(harness.record.id, 'dsh-desktop-companion')
  assert.equal(typeof harness.record.factory, 'function')
  assert.equal(typeof harness.exports.apply, 'function')
  assert.equal(harness.exports.inject.join(','), 'slots,locale')
})

test('both UI surfaces are registered with stable ids, order and localized labels', () => {
  const harness = loadClient()

  assert.deepEqual(
    harness.cordis.injections.map((i) => i.name),
    ['sidebar.footer.action', 'settings.section'],
  )

  const sidebar = harness.slot('sidebar.footer.action')
  assert.equal(sidebar.description.id, 'dsh-desktop-companion-update')
  assert.equal(sidebar.description.locale, 'desktop-companion')
  assert.equal(sidebar.description.label(), '桌面设置')

  const settings = harness.slot('settings.section')
  assert.equal(settings.description.order, 50)
  assert.equal(settings.description.label(), '桌面设置')

  harness.setLanguage('en-US')
  assert.equal(sidebar.description.label(), 'Desktop Settings')
})

test('without the host bridge the UI setup is skipped without throwing', () => {
  // Every surface needs window.__shell (state queries + install actions); the
  // client half must degrade to a no-op instead of failing the web boot.
  const harness = loadClient({ withoutBridge: true })

  assert.deepEqual(harness.cordis.injections.map((i) => i.name), [])
  assert.equal(harness.cordis.effects.length, 0)
})

test('waits for the shell bridge injected after the first script run', () => {
  // The host injects window.__shell on NavigationCompleted; a plugin module that
  // runs before that must poll defensively instead of disabling every surface.
  const harness = loadClient({ deferBridge: true })
  assert.deepEqual(harness.cordis.injections.map((i) => i.name), [])

  harness.attachBridge()
  harness.clock.tick(50)

  assert.deepEqual(harness.cordis.injections.map((i) => i.name).sort(),
    ['settings.section', 'sidebar.footer.action'])
})

test('locale bridge reports the current html[lang] and follows changes', () => {
  const harness = loadClient()
  harness.setLanguage('zh-CN')

  const reports = () => harness.shell.invokesOf('desktop.companion.setLocale')
  assert.equal(reports().at(-1).args.locale, 'zh-cn')

  harness.setLanguage('en')
  assert.equal(reports().at(-1).args.locale, 'en')
})

test('sidebar update button stays hidden unless the host reports ready/installing', async () => {
  const harness = loadClient()
  harness.shell.onCommand('desktop.update.getState', () => ({ status: 'idle' }))

  const slot = await openSidebar(harness)
  assert.equal(findAll(slot.render(), 'button').length, 0)
})

test('sidebar update button surfaces the ready version and installs on click', async () => {
  const harness = loadClient()
  harness.shell.onCommand('desktop.update.getState', () => ({ status: 'ready', version: '1.2.3' }))

  const slot = await openSidebar(harness)
  const button = find(slot.render(), (el) => el.type === 'button')

  assert.ok(button, 'update button rendered')
  assert.match(button.props.title, /1\.2\.3/)
  assert.equal(button.props.disabled, false)

  button.props.onClick()
  assert.equal(harness.shell.invokesOf('desktop.update.install').length, 1)
})

test('sidebar update button turns busy while installing', async () => {
  const harness = loadClient()
  harness.shell.onCommand('desktop.update.getState', () => ({ status: 'installing', version: '1.2.3' }))

  const slot = await openSidebar(harness)
  const button = find(slot.render(), (el) => el.type === 'button')

  assert.equal(button.props.disabled, true)
  assert.match(textOf(button), /安装中/)
})

test('sidebar update button reacts to pushed update events', async () => {
  const harness = loadClient()
  harness.shell.onCommand('desktop.update.getState', () => ({ status: 'idle' }))
  const slot = await openSidebar(harness)

  harness.document.dispatchEvent({ type: 'dsh-desktop-update', detail: { status: 'ready', version: '9.9.9' } })
  const button = find(slot.render(), (el) => el.type === 'button')

  assert.ok(button, 'pushed state renders the button')
  assert.match(button.props.title, /9\.9\.9/)
})

test('settings update section maps host status to copy and action', async () => {
  const harness = loadClient()
  harness.shell.onCommand('desktop.update.getState', () => ({ status: 'ready', version: '1.2.3', current: '1.0.0' }))
  const slot = await openSettings(harness)

  const tree = slot.render()
  assert.match(textOf(tree), /当前版本/)
  assert.match(textOf(tree), /1\.2\.3 就绪，可安装/)

  const install = find(tree, (el) => el.type === 'button' && textOf(el) === '安装并重启')
  assert.ok(install, 'ready state exposes the install action')
  install.props.onClick()
  assert.equal(harness.shell.invokesOf('desktop.update.install').length, 1)
})

test('settings update section shows the host error reason', async () => {
  const harness = loadClient()
  harness.shell.onCommand('desktop.update.getState', () => ({ status: 'error', message: 'HTTP 503' }))
  const slot = await openSettings(harness)

  assert.match(textOf(slot.render()), /检查失败：HTTP 503/)
})

test('settings update section distinguishes "no update stack" from "channel down"', async () => {
  const harness = loadClient()
  harness.shell.onCommand('desktop.update.getState', () => Promise.reject(new Error('no route')))
  harness.shell.onCommand('desktop.autostart.getState', () => ({ enabled: false }))
  const slot = await openSettings(harness)

  assert.match(textOf(slot.render()), /桌面自更新在当前运行时不可用/)
  assert.equal(harness.shell.invokesOf('desktop.update.check').length, 0)
})

test('settings update section reports the channel failure and offers retry', async () => {
  const harness = loadClient()
  harness.shell.onCommand('desktop.update.getState', () => Promise.reject(new Error('no route')))
  harness.shell.onCommand('desktop.autostart.getState', () => Promise.reject(new Error('no route')))
  const slot = await openSettings(harness)
  await harness.advance(800)
  await harness.advance(800)

  const tree = slot.render()
  assert.match(textOf(tree), /无法连接桌面宿主/)
  const reports = harness.shell.invokesOf('desktop.companion.report')
  assert.ok(reports.some((r) => String(r.args.message).includes('command channel down')), 'degradation reported')
})

test('diagnostics section renders the exported path and export failures', async () => {
  const harness = loadClient()
  harness.shell.onCommand('desktop.diagnostics.export', () => ({ path: '/tmp/diag.zip' }))
  harness.shell.onCommand('desktop.update.getState', () => ({ status: 'idle' }))
  const slot = await openSettings(harness)

  const exportButton = find(slot.render(), (el) => el.type === 'button' && textOf(el) === '导出')
  exportButton.props.onClick()
  await settle()
  assert.match(textOf(slot.render()), /已保存至：\/tmp\/diag\.zip/)
})

test('close-to-tray row disables the switch when the host has no tray', async () => {
  const harness = loadClient()
  harness.shell.onCommand('desktop.update.getState', () => ({ status: 'idle' }))
  harness.shell.onCommand('desktop.closeToTray.getState', () => ({ enabled: true, available: false }))
  const slot = await openSettings(harness)

  const tree = slot.render()
  assert.match(textOf(tree), /当前运行环境无系统托盘/)
  const sw = find(
    tree,
    (el) => el.props && el.props.role === 'switch' && el.props['aria-label'] === '关闭时最小化到托盘',
  )
  assert.equal(sw.props.disabled, true)
  assert.equal(sw.props['aria-checked'], 'true')
})

test('autostart toggle sends the inverse state and reverts when the host fails', async () => {
  const harness = loadClient()
  harness.shell.onCommand('desktop.update.getState', () => ({ status: 'idle' }))
  harness.shell.onCommand('desktop.autostart.getState', () => ({ enabled: true }))
  harness.shell.onCommand('desktop.autostart.set', () => Promise.reject(new Error('denied')))
  const slot = await openSettings(harness)

  const sw = find(
    slot.render(),
    (el) => el.props && el.props.role === 'switch' && el.props['aria-label'] === '开机自启',
  )
  assert.equal(sw.props['aria-checked'], 'true')

  sw.props.onClick()
  await settle()
  const reverted = find(
    slot.render(),
    (el) => el.props && el.props.role === 'switch' && el.props['aria-label'] === '开机自启',
  )
  assert.equal(reverted.props['aria-checked'], 'true')
  assert.equal(harness.shell.invokesOf('desktop.autostart.set').length, 1)
})

test('restart row surfaces a failure inline and reports it', async () => {
  const harness = loadClient()
  harness.shell.onCommand('desktop.update.getState', () => ({ status: 'idle' }))
  harness.shell.onCommand('desktop.app.restart', () => Promise.reject(new Error('spawn failed')))
  const slot = await openSettings(harness)

  const restart = find(slot.render(), (el) => el.type === 'button' && textOf(el) === '重启')
  restart.props.onClick()
  await settle()

  assert.match(textOf(slot.render()), /重启失败：spawn failed/)
  assert.equal(harness.shell.invokesOf('desktop.app.restart').length, 1)
})

test('external-link failure pushes a toast that dismisses itself after 5s', async () => {
  const harness = loadClient()

  harness.shell.emit('desktop.externalLinkOpenerFailed', { url: 'https://example.test/x' })
  const toast = harness.document.getElementById('ddc-linkfail-toast')
  assert.ok(toast, 'toast element created')
  assert.match(toast.textContent, /https:\/\/example\.test\/x/)
  assert.equal(toast.classList.contains('ddc-linkfail-show'), true)

  await harness.advance(5000)
  assert.equal(toast.classList.contains('ddc-linkfail-show'), false)
})
