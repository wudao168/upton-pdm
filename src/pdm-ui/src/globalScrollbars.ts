import type { App } from 'vue'
import { ClickScrollPlugin, OverlayScrollbars, type OverlayScrollbars as ScrollbarInstance } from 'overlayscrollbars'
import 'overlayscrollbars/overlayscrollbars.css'
import './styles/scrollbars.css'

const candidates = 'div, section, main, aside, nav, ol, ul, pre, article, form, header, footer, fieldset, menu, dl, figure'
const ignored = '.el-scrollbar, [class^="os-"]'

/** Keep the original viewport and its children: sticky headers, scroll refs and Vue ownership stay intact. */
export function installGlobalScrollbars(app: App, owner: Document = document) {
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
    observer.disconnect()
    cancelAnimationFrame(frame)
    window.removeEventListener('resize', rescan)
    owner.removeEventListener('load', rescan, true)
    for (const instance of instances.values()) instance.destroy()
    instances.clear()
    pending.clear()
  })
}
