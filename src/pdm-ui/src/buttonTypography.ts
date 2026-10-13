import type { App } from 'vue'

/** 按实际规格统一普通按钮字体，包含后续挂载的弹窗。 */
export function installButtonTypography(app: App) {
  const marker = 'pdm-button-font-80x30'
  const observed = new WeakSet<HTMLButtonElement>()
  const matched = new WeakMap<HTMLButtonElement, boolean>()
  function update(button: HTMLButtonElement) {
    if (!button.isConnected) return
    button.classList.remove(marker)
    const match = button.getAttribute('role') !== 'tab' && button.offsetWidth === 80 && button.offsetHeight === 30
    matched.set(button, match)
    button.classList.toggle(marker, match)
  }
  const resize = new ResizeObserver(entries => entries.forEach(entry => update(entry.target as HTMLButtonElement)))
  function buttons(node: Node): HTMLButtonElement[] {
    if (!(node instanceof Element)) return []
    return [...(node instanceof HTMLButtonElement ? [node] : []), ...node.querySelectorAll<HTMLButtonElement>('button')]
  }
  function register(button: HTMLButtonElement) {
    if (!observed.has(button)) { resize.observe(button); observed.add(button) }
    update(button)
  }
  const strip = (value: string) => value.split(/\s+/).filter(token => token && token !== marker).join(' ')
  const mutations = new MutationObserver(records => {
    for (const record of records) {
      if (record.type === 'attributes' && record.target instanceof HTMLButtonElement) {
        const button = record.target
        if (record.attributeName === 'style' || strip(record.oldValue ?? '') !== strip(button.className)
          || matched.get(button) !== button.classList.contains(marker)) update(button)
      } else if (record.type === 'childList') {
        record.addedNodes.forEach(node => buttons(node).forEach(register))
        const parent = record.target instanceof Element ? record.target.closest('button') : null
        if (parent) register(parent)
        record.removedNodes.forEach(node => buttons(node).forEach(button => {
          if (!button.isConnected) { resize.unobserve(button); observed.delete(button); matched.delete(button) }
        }))
      }
    }
  })
  mutations.observe(document.body, { subtree: true, childList: true, attributes: true, attributeOldValue: true, attributeFilter: ['class', 'style'] })
  document.querySelectorAll<HTMLButtonElement>('button').forEach(register)
  app.onUnmount(() => { mutations.disconnect(); resize.disconnect() })
}