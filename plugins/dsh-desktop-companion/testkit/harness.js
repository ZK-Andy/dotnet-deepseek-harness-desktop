/**
 * Test harness for the companion client half.
 *
 * `client.js` is a classic script that only registers a factory with
 * `window.__ModuleLoader__`; the module table it receives through `require`
 * resolves package names (never relative paths). So the test loads it the way
 * the web shell does — in a sandbox with the browser globals it touches — and
 * drives the materialized `apply(ctx)` against fakes:
 *
 *   - a DOM stub covering exactly what the plugin uses (createElement /
 *     append / getElementById / addEventListener / classList / textContent),
 *   - a controllable clock for setTimeout (retries, timeouts, toast lifetime),
 *   - a tiny Cordis context (slots / locale / effect),
 *   - a React stand-in whose hooks are real enough for the plugin's
 *     useState/useEffect pairs, rendering components eagerly.
 *
 * No npm dependencies: the plugin ships a single self-contained bundle, and
 * adding a bundler or jsdom for it would outweigh what these tests buy.
 */
import { readFileSync } from 'node:fs'
import { dirname, join } from 'node:path'
import { fileURLToPath } from 'node:url'
import vm from 'node:vm'

const HERE = dirname(fileURLToPath(import.meta.url))
const CLIENT_JS = join(HERE, '..', 'client', 'client.js')

/** Controllable clock: timers fire only when the test advances time. */
export function createClock() {
  let now = 0
  let seq = 0
  const timers = new Map()
  const schedule = (fn, ms) => {
    const id = ++seq
    timers.set(id, { at: now + Math.max(0, Number(ms) || 0), fn })
    return id
  }
  const cancel = (id) => timers.delete(id)
  const tick = (ms) => {
    const target = now + ms
    for (;;) {
      const due = [...timers.entries()]
        .filter(([, t]) => t.at <= target)
        .sort((a, b) => a[1].at - b[1].at || a[0] - b[0])
      if (due.length === 0) break
      const [id, timer] = due[0]
      timers.delete(id)
      now = timer.at
      timer.fn()
    }
    now = target
  }
  return { schedule, cancel, tick, get now() { return now }, get pending() { return timers.size } }
}

/** Element stub with the subsets of DOM the plugin actually touches. */
function createElement(document, tag) {
  const el = {
    tagName: String(tag || 'div').toUpperCase(),
    children: [],
    attributes: {},
    style: {},
    className: '',
    textContent: '',
    id: '',
    parentNode: null,
    _classes: new Set(),
    _listeners: new Map(),
    _observers: [],
    classList: {
      add: (...names) => names.forEach((n) => el._classes.add(n)),
      remove: (...names) => names.forEach((n) => el._classes.delete(n)),
      contains: (name) => el._classes.has(name),
    },
    setAttribute(name, value) {
      el.attributes[name] = String(value)
      if (name === 'id') el.id = String(value)
      if (name === 'lang') el._lang = String(value)
      for (const observer of el._observers) observer._notify(name)
    },
    getAttribute(name) {
      return Object.hasOwn(el.attributes, name) ? el.attributes[name] : null
    },
    append(...nodes) {
      nodes.forEach((n) => el.appendChild(n))
    },
    appendChild(node) {
      el.children.push(node)
      node.parentNode = el
      if (node.id) document._byId.set(node.id, node)
      return node
    },
    removeChild(node) {
      el.children = el.children.filter((n) => n !== node)
      node.parentNode = null
      return node
    },
    addEventListener(type, fn) {
      if (!el._listeners.has(type)) el._listeners.set(type, [])
      el._listeners.get(type).push(fn)
    },
    removeEventListener(type, fn) {
      const list = el._listeners.get(type) || []
      el._listeners.set(type, list.filter((f) => f !== fn))
    },
    dispatchEvent(event) {
      for (const fn of el._listeners.get(event.type) || []) fn(event)
      return true
    },
    click() {
      el.dispatchEvent({ type: 'click', target: el })
    },
  }
  return el
}

/** Document stub: id registry, document-level listeners, lang observers. */
export function createDocument() {
  const doc = { _byId: new Map(), _listeners: new Map(), _observers: [] }
  doc.createElement = (tag) => createElement(doc, tag)
  doc.head = createElement(doc, 'head')
  doc.body = createElement(doc, 'body')
  doc.documentElement = createElement(doc, 'html')
  doc.getElementById = (id) => doc._byId.get(id) || null
  doc.addEventListener = (type, fn) => {
    if (!doc._listeners.has(type)) doc._listeners.set(type, [])
    doc._listeners.get(type).push(fn)
  }
  doc.removeEventListener = (type, fn) => {
    doc._listeners.set(type, (doc._listeners.get(type) || []).filter((f) => f !== fn))
  }
  doc.dispatchEvent = (event) => {
    for (const fn of doc._listeners.get(event.type) || []) fn(event)
    return true
  }
  class MutationObserver {
    constructor(callback) {
      this._callback = callback
    }

    observe(target, options) {
      this._target = target
      this._options = options
      target._observers.push(this)
    }

    _notify(name) {
      if (!this._options || !this._options.attributeFilter) return
      if (this._options.attributeFilter.includes(name)) this._callback([{ type: 'attributes', attributeName: name }])
    }

    disconnect() {
      if (this._target) this._target._observers = this._target._observers.filter((o) => o !== this)
    }
  }
  doc.MutationObserver = MutationObserver
  return doc
}

/**
 * React stand-in: createElement renders function components eagerly, hooks keep
 * per-component state across explicit re-renders of the same component.
 */
export function createReact() {
  const stores = new Map()
  let current = null
  let index = 0

  function bind(component) {
    const previous = { current, index }
    current = component
    index = 0
    const slots = stores.get(component) || []
    stores.set(component, slots)
    return { previous, slots }
  }

  function useState(initial) {
    const slots = stores.get(current)
    const slot = slots[index] || (slots[index] = { kind: 'state', value: undefined })
    index += 1
    if (slot.kind !== 'state') throw new Error('hook order changed: useState saw a different hook')
    if (slot.value === undefined) slot.value = typeof initial === 'function' ? initial() : initial
    const set = (value) => {
      slot.value = typeof value === 'function' ? value(slot.value) : value
    }
    return [slot.value, set]
  }

  function useEffect(fn, deps) {
    const slots = stores.get(current)
    const slot = slots[index] || (slots[index] = { kind: 'effect', deps: undefined })
    index += 1
    if (slot.kind !== 'effect') throw new Error('hook order changed: useEffect saw a different hook')
    const changed =
      slot.deps === undefined ||
      !deps ||
      slot.deps.length !== deps.length ||
      deps.some((d, i) => !Object.is(d, slot.deps[i]))
    if (!changed) return
    slot.deps = deps ? [...deps] : undefined
    const cleanup = fn()
    if (typeof cleanup === 'function') slot.cleanup = cleanup
  }

  function createElement(type, props, ...children) {
    const merged = { ...(props || {}) }
    if (children.length > 0) merged.children = children.length === 1 ? children[0] : children
    if (typeof type === 'function') {
      const { previous, slots } = bind(type)
      const out = type(merged)
      stores.set(type, slots)
      current = previous.current
      index = previous.index
      return out
    }
    return { type, props: merged }
  }

  return { createElement, useState, useEffect }
}

/** Cordis context stub: slot registration, locale dictionaries, effects. */
export function createContext(document) {
  const injections = []
  const registrations = []
  const effects = []
  const dictionaries = new Map()
  const language = () => (document.documentElement.getAttribute('lang') || 'zh').toLowerCase()

  const ctx = {
    slots: {
      inject(name, factory) {
        injections.push({ name, factory })
        return { dispose() {} }
      },
      register(description, render) {
        const registration = { description, render }
        registrations.push(registration)
        return registration
      },
    },
    locale: {
      register(namespace, dicts) {
        dictionaries.set(namespace, dicts)
        return () => dictionaries.delete(namespace)
      },
      bind(namespace) {
        return (key) => {
          const dicts = dictionaries.get(namespace) || {}
          const dict = language().startsWith('en') ? dicts.en : dicts.zh
          return (dict && Object.hasOwn(dict, key) ? dict[key] : key)
        }
      },
    },
    effect(fn, label) {
      // Cordis runs an effect when its fiber starts; the plugin relies on that
      // for dictionary registration and the locale bridge, so run it here.
      const cleanup = fn()
      effects.push({ fn, label, cleanup })
      return () => {}
    },
  }
  return { ctx, injections, registrations, effects, dictionaries }
}

/** `window.__shell` stub: records invokes, resolves per-command responders. */
export function createShellBridge() {
  const invokes = []
  const handlers = new Map()
  const responders = new Map()
  const listeners = new Map()
  const bridge = {
    on(name, fn) {
      if (!listeners.has(name)) listeners.set(name, [])
      listeners.get(name).push(fn)
    },
    invoke(command, args) {
      invokes.push({ command, args })
      const responder = responders.get(command)
      if (responder) {
        try {
          return Promise.resolve(responder(args))
        } catch (error) {
          return Promise.reject(error)
        }
      }
      return Promise.resolve({})
    },
  }
  return {
    bridge,
    invokes,
    handlers,
    onCommand(command, responder) {
      responders.set(command, responder)
    },
    emit(name, data) {
      for (const fn of listeners.get(name) || []) fn(data)
    },
    invokesOf(command) {
      return invokes.filter((i) => i.command === command)
    },
  }
}

/** Microtask settle: lets promise chains queued by the plugin run to completion. */
export const settle = () => new Promise((resolve) => setImmediate(resolve))

/**
 * Load client.js in a sandbox, materialize its module and run apply(ctx).
 * @param {object} [options] `withoutBridge` drops the `window.__shell` bridge;
 *   `deferBridge` installs it only when the test calls `attachBridge()` (simulates the
 *   shell injecting the bridge after the page's first script run);
 *   `primitives` supplies the ui-primitives module (default: empty export).
 */
export function loadClient(options = {}) {
  const clock = createClock()
  const document = createDocument()
  const shell = createShellBridge()
  const react = createReact()
  const registrations = []
  const windowObject = { __ModuleLoader__: { load: (record) => registrations.push(record) } }
  if (!options.withoutBridge && !options.deferBridge) windowObject.__shell = shell.bridge

  const sandbox = {
    window: windowObject,
    document,
    MutationObserver: document.MutationObserver,
    console: options.verbose ? console : { warn() {}, error() {}, log() {}, info() {} },
    setTimeout: clock.schedule,
    clearTimeout: clock.cancel,
  }
  const context = vm.createContext(sandbox)
  vm.runInContext(readFileSync(CLIENT_JS, 'utf8'), context, { filename: 'client.js' })

  if (registrations.length !== 1) throw new Error(`expected exactly one module registration, got ${registrations.length}`)
  const record = registrations[0]
  const required = []
  const require = (specifier) => {
    required.push(specifier)
    if (specifier === 'react') return react
    if (specifier === '@deepseek-ai/dsh-client-ui-primitives') return options.primitives || {}
    throw new Error(`unexpected require: ${specifier}`)
  }
  const exports = record.factory(require)
  const cordis = createContext(document)
  exports.apply(cordis.ctx)

  const render = (component, props = {}) => react.createElement(component, props)

  return {
    record,
    exports,
    require,
    required,
    document,
    window: windowObject,
    clock,
    react,
    shell,
    attachBridge() {
      windowObject.__shell = shell.bridge
    },
    cordis,
    render,
    /**
     * Materialize a slot declared by the plugin: returns the registration and a
     * render function (calling it again re-renders with the same hook state, the
     * way React re-renders after setState).
     */
    slot(slotName) {
      const injection = cordis.injections.find((i) => i.name === slotName)
      if (!injection) throw new Error(`slot not injected: ${slotName}`)
      const registered = injection.factory()
      const t = cordis.ctx.locale.bind(registered.description.locale)
      return {
        description: registered.description,
        render: (props = {}) => registered.render({ t, ...props }),
      }
    },
    /** Localized text for the companion namespace (`t` bound at call time). */
    t(key) {
      return cordis.ctx.locale.bind('desktop-companion')(key)
    },
    /** Switch the surface language the way dsh does: `html[lang]` attribute. */
    setLanguage(lang) {
      document.documentElement.setAttribute('lang', lang)
    },
    /** Advance the fake clock and settle pending promise chains. */
    async advance(milliseconds) {
      clock.tick(milliseconds)
      await settle()
    },
  }
}

/** Depth-first walk over a rendered element tree (components are already expanded). */
export function walk(node, visit) {
  if (!node) return
  if (Array.isArray(node)) {
    node.forEach((child) => walk(child, visit))
    return
  }
  if (typeof node !== 'object') return
  visit(node)
  const children = node.props && node.props.children
  if (children === undefined) return
  if (Array.isArray(children)) children.forEach((child) => walk(child, visit))
  else walk(children, visit)
}

/** Find every element whose tag matches (lowercase). */
export function findAll(node, tag) {
  const found = []
  walk(node, (el) => {
    if (el.type === tag) found.push(el)
  })
  return found
}

/** Text content of a rendered subtree (element children + string children). */
export function textOf(node) {
  if (node === null || node === undefined) return ''
  if (typeof node === 'string' || typeof node === 'number') return String(node)
  if (Array.isArray(node)) return node.map(textOf).join('')
  const children = node.props && node.props.children
  return children === undefined ? '' : textOf(children)
}

/** First element matching a predicate. */
export function find(node, predicate) {
  let hit = null
  walk(node, (el) => {
    if (!hit && predicate(el)) hit = el
  })
  return hit
}
