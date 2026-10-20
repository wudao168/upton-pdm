import type { App } from 'vue'

const dateTypes = new Set(['date', 'datetime-local', 'month', 'week', 'time'])

export function installDatePickers(app: App, owner: Document = document) {
  function openPicker(event: MouseEvent) {
    const input = event.target
    if (!(input instanceof HTMLInputElement) || !dateTypes.has(input.type)
      || event.defaultPrevented || event.button !== 0 || event.detail === 0
      || input.readOnly || input.matches(':disabled, [aria-disabled="true"]')
      || typeof input.showPicker !== 'function') return
    try {
      input.focus({ preventScroll: true })
      input.showPicker()
      // 避免原生选择器图标再执行一次默认操作，导致刚打开的面板被关闭。
      event.preventDefault()
    } catch {
      // 浏览器不允许打开时保留原有交互，不拦截键盘或原生选择器图标。
    }
  }
  owner.addEventListener('click', openPicker, true)
  app.onUnmount(() => owner.removeEventListener('click', openPicker, true))
}
