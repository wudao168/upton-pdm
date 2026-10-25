import type { App } from 'vue'
import { ClickScrollPlugin, OverlayScrollbars, type OverlayScrollbars as ScrollbarInstance } from 'overlayscrollbars'
import 'overlayscrollbars/overlayscrollbars.css'
import './styles/scrollbars.css'

const candidates = 'div, section, main, aside, nav, ol, ul, pre, article, form, header, footer, fieldset, menu, dl, figure'
const ignored = '.el-scrollbar, [class^="os-"]'

// Firefox has no scrollbar hover pseudo-element; reveal native bars only in their gutter.
function installNativeScrollbarHover(owner: Document) {
  const view = owner.defaultView
  if (!view || view.CSS?.supports?.('selector(::-webkit-scrollbar)')) return () => {}
  let hovered: HTMLElement | undefined, dragging = false
  const clear = () => { hovered?.removeAttribute('data-pdm-native-scrollbar-hover'); hovered = undefined }
  function update(event: PointerEvent) {
    if (dragging) return
    const next = event.composedPath().find(node => {
      if (!(node instanceof HTMLElement) || node.matches('[data-overlayscrollbars-viewport], .el-scrollbar__wrap')) return false
      const root = node === owner.scrollingElement, style = getComputedStyle(node)
      if (style.scrollbarWidth === 'none') return false
      const x = node.scrollWidth > node.clientWidth && (root || /^(auto|scroll)$/.test(style.overflowX))
      const y = node.scrollHeight > node.clientHeight && (root || /^(auto|scroll)$/.test(style.overflowY))
      if (!x && !y) return false
      const rect = root ? { left: 0, top: 0, right: view!.innerWidth, bottom: view!.innerHeight } : node.getBoundingClientRect()
      const vertical = Math.max(12, root ? view!.innerWidth - node.clientWidth : node.offsetWidth - node.clientWidth)
      const horizontal = Math.max(12, root ? view!.innerHeight - node.clientHeight : node.offsetHeight - node.clientHeight)
      if (event.clientX < rect.left || event.clientX > rect.right || event.clientY < rect.top || event.clientY > rect.bottom) return false
      return y && (style.direction === 'rtl' ? event.clientX < rect.left + vertical : event.clientX > rect.right - vertical)
        || x && event.clientY > rect.bottom - horizontal
    }) as HTMLElement | undefined
    if (next === hovered) return
    clear(); hovered = next; hovered?.setAttribute('data-pdm-native-scrollbar-hover', '')
  }
  const down = (event: PointerEvent) => { update(event); dragging = event.button === 0 && !!hovered }
  const up = (event: PointerEvent) => { dragging = false; update(event) }
  const cancel = () => { dragging = false; clear() }
  const leave = (event: PointerEvent) => { if (!dragging && (event.target === owner || event.target === owner.documentElement)) clear() }
  owner.addEventListener('pointermove', update, true)
  owner.addEventListener('pointerdown', down, true)
  owner.addEventListener('pointerup', up, true)
  owner.addEventListener('pointercancel', cancel, true)
  owner.addEventListener('pointerleave', leave, true)
  view.addEventListener('blur', cancel)
  return () => {
    owner.removeEventListener('pointermove', update, true)
    owner.removeEventListener('pointerdown', down, true)
    owner.removeEventListener('pointerup', up, true)
    owner.removeEventListener('pointercancel', cancel, true)
    owner.removeEventListener('pointerleave', leave, true)
    view.removeEventListener('blur', cancel)
    cancel()
  }
}

/** Keep the original viewport and its children: sticky headers, scroll refs and Vue ownership stay intact. */
export function installGlobalScrollbars(app: App, owner: Document = document) {
  const disposeNativeHover = installNativeScrollbarHover(owner)
  OverlayScrollbars.plugin(ClickScrollPlugin)
  const instances = new Map<HTMLElement, ScrollbarInstance>()
  const pending = new Set<Element>()
  let frame = 0

  function scan(root: Element) {
    const elements = [root, ...root.querySelectorAll(candidates)]
    for (const element of elements) {
      if (!(element instanceof HTMLElement) || !element.matches(candidates)
        || element.closest(ignored) || instances.has(element)) continue
      const style = getComputedStyle(element)
      const x = /^(auto|scroll)$/.test(style.overflowX)
      const y = /^(auto|scroll)$/.test(style.overflowY)
      if (!x && !y) continue
      const instance = OverlayScrollbars({
        target: element,
        elements: { viewport: element },
      }, {
        overflow: { x: x ? 'scroll' : 'hidden', y: y ? 'scroll' : 'hidden' },
        scrollbars: { theme: 'os-theme-pdm', autoHide: 'never', visibility: 'auto', clickScroll: true },
      })
      instances.set(element, instance)
    }
  }

  function flush() {
    frame = 0
    for (const [element, instance] of instances) {
      if (!element.isConnected) {
        instance.destroy()
        instances.delete(element)
      }
    }
    for (const root of pending) if (root.isConnected) scan(root)
    pending.clear()
  }

  function schedule(root: Element) {
    if (root.closest(ignored)) return
    for (const queued of pending) {
      if (queued.contains(root)) return
      if (root.contains(queued)) pending.delete(queued)
    }
    pending.add(root)
    if (!frame) frame = requestAnimationFrame(flush)
  }

  const observer = new MutationObserver(records => {
    for (const record of records) {
      if (record.type === 'attributes') {
        const element = record.target as Element
        if (!instances.has(element as HTMLElement)) schedule(element)
      } else {
        for (const node of record.addedNodes) if (node instanceof Element) schedule(node)
        // A removed subtree can contain several registered scroll containers.
        if (record.removedNodes.length && !frame) frame = requestAnimationFrame(flush)
      }
    }
  })
  observer.observe(owner.body, { childList: true, subtree: true, attributes: true, attributeFilter: ['class', 'style'] })
  const rescan = () => schedule(owner.body)
  window.addEventListener('resize', rescan)
  owner.addEventListener('load', rescan, true)
  schedule(owner.body)
  app.onUnmount(() => {
    disposeNativeHover()
    observer.disconnect()
    cancelAnimationFrame(frame)
    window.removeEventListener('resize', rescan)
    owner.removeEventListener('load', rescan, true)
    for (const instance of instances.values()) instance.destroy()
    instances.clear()
    pending.clear()
  })
}
