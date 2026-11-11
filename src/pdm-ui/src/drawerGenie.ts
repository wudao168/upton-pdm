import { genieBandTransform } from './drawerGenieMath'

const panelSelector = '.el-overlay-dialog > .el-dialog:not(.is-fullscreen), .el-drawer.rtl, .pdm-dialog-backdrop > .pdm-custom-drawer'
const enterSelector = '.dialog-fade-enter-active, .el-drawer-fade-enter-active, .pdm-drawer-enter-active'
const leaveSelector = '.dialog-fade-leave-active, .el-drawer-fade-leave-active, .pdm-drawer-leave-active'
type Motion = { phase: 'enter' | 'leave'; from: number; to: number; started: number; duration: number; cleanup: () => void }

export function installDrawerGenie() {
  const motions = new Map<HTMLElement, Motion>()
  const phases = new WeakMap<HTMLElement, string>()
  const reduced = window.matchMedia('(prefers-reduced-motion: reduce)')
  let scheduled = 0, stopped = false

  function animate(panel: HTMLElement, phase: 'enter' | 'leave') {
    const previous = motions.get(panel)
    const elapsed = previous ? Math.min(1, (performance.now() - previous.started) / previous.duration) : 0
    const from = previous ? previous.from + (previous.to - previous.from) * elapsed : phase === 'enter' ? 1 : 0
    previous?.cleanup()
    if (reduced.matches || typeof panel.animate !== 'function') return
    const rect = panel.getBoundingClientRect()
    if (!rect.width || !rect.height) { phases.delete(panel); return }
    const to = phase === 'enter' ? 0 : 1, duration = phase === 'enter' ? 560 : 480
    const originalNodes = [panel, ...panel.querySelectorAll<HTMLElement>('*')]
    const count = originalNodes.length > 2000 ? 16 : Math.min(32, Math.max(20, Math.ceil(rect.height / 32)))
    const layer = document.createElement('div')
    layer.className = 'pdm-genie-layer'
    layer.dataset.pdmGenieLayer = phase
    layer.setAttribute('aria-hidden', 'true')
    layer.inert = true
    const parentClasses = new Set<string>()
    let ancestor = panel.parentElement, zIndex = 2000
    while (ancestor && ancestor !== document.body) {
      ancestor.classList.forEach(value => { if (!/-(enter|leave)-(active|from|to)$/.test(value)) parentClasses.add(value) })
      const z = Number.parseInt(getComputedStyle(ancestor).zIndex)
      if (Number.isFinite(z)) zIndex = Math.max(zIndex, z)
      ancestor = ancestor.parentElement
    }
    const style = getComputedStyle(panel)
    for (let i = 0; i < style.length; i++) { const key = style[i]!; if (key.startsWith('--')) layer.style.setProperty(key, style.getPropertyValue(key)) }
    Object.assign(layer.style, { left: `${rect.left}px`, top: `${rect.top}px`, width: `${rect.width}px`, height: `${rect.height}px`, zIndex: String(zIndex + 1) })
    const scrolls = originalNodes.map((node, index) => ({ index, top: node.scrollTop, left: node.scrollLeft })).filter(value => value.top || value.left)
    const canvases = originalNodes.map((node, index) => ({ node, index })).filter(value => value.node instanceof HTMLCanvasElement)
    const animations: Animation[] = []
    let timer = 0, finished = false
    const blockAnimatingClick = (event: Event) => {
      // 用户在展开动画中点击真实按钮时，先结束动画，再执行操作，避免取消按钮吞点击。
      if (phase === 'enter') { cleanup(); return }
      event.preventDefault(); event.stopImmediatePropagation()
    }
    function cleanup() {
      if (finished) return
      finished = true
      clearTimeout(timer)
      animations.forEach(animation => animation.cancel())
      layer.remove()
      panel.removeAttribute('data-pdm-genie-source')
      panel.removeEventListener('click', blockAnimatingClick, true)
      motions.delete(panel)
    }
    try {
      for (let index = 0; index < count; index++) {
        const top = rect.height * index / count, height = Math.min(rect.height - top, rect.height / count + .6)
        const band = document.createElement('div')
        band.className = 'pdm-genie-band'
        Object.assign(band.style, { width: `${rect.width}px`, height: `${height}px` })
        const shell = document.createElement('div')
        shell.className = [...parentClasses].join(' ')
        shell.dataset.pdmGenieShell = ''
        Object.assign(shell.style, { width: `${rect.width}px`, height: `${rect.height}px`, top: `${-top}px` })
        for (const [key, value] of Object.entries({ position: 'absolute', left: '0', right: 'auto', bottom: 'auto', display: 'block', padding: '0', margin: '0', border: '0', overflow: 'visible', background: 'transparent', transform: 'none', animation: 'none', transition: 'none', opacity: '1' })) shell.style.setProperty(key, value, 'important')
        const snapshot = panel.cloneNode(true) as HTMLElement
        snapshot.removeAttribute('data-pdm-genie-source')
        snapshot.dataset.pdmGenieSnapshot = ''
        for (const [key, value] of Object.entries({ position: 'relative', inset: 'auto', top: '0', left: '0', right: 'auto', bottom: 'auto', margin: '0', width: `${rect.width}px`, height: `${rect.height}px`, 'min-height': '0', 'max-height': 'none', 'max-width': 'none', transform: 'none', animation: 'none', transition: 'none', opacity: '1', visibility: 'visible', 'box-shadow': 'none' })) snapshot.style.setProperty(key, value, 'important')
        const clones = [snapshot, ...snapshot.querySelectorAll<HTMLElement>('*')]
        clones.forEach(node => { node.removeAttribute('id'); node.removeAttribute('autofocus'); node.removeAttribute('name'); node.removeAttribute('form') })
        shell.append(snapshot); band.append(shell); layer.append(band)
        // 每条带使用同一连续曲面的上下边界，内容随梯形投影弯曲，不裁切文字模拟变形。
        const frames = Array.from({ length: 33 }, (_, frame) => {
          const p = from + (to - from) * frame / 32
          return { transform: genieBandTransform(rect.width, rect.height, top, height, p) }
        })
        animations.push(band.animate(frames, { duration, easing: 'linear', fill: 'both' }))
        band.dataset.pdmGenieBand = String(index)
        for (const scroll of scrolls) { const clone = clones[scroll.index]; if (clone) { clone.dataset.pdmSnapshotScrollTop = String(scroll.top); clone.dataset.pdmSnapshotScrollLeft = String(scroll.left) } }
        for (const item of canvases) {
          const clone = clones[item.index] as HTMLCanvasElement, original = item.node as HTMLCanvasElement
          if (clone) { clone.width = original.width; clone.height = original.height; try { clone.getContext('2d')?.drawImage(original, 0, 0) } catch { /* WebGL 已清空的帧保留静态背景，不中断关闭。 */ } }
        }
      }
      animations.push(layer.animate(Array.from({ length: 33 }, (_, frame) => ({ opacity: Math.min(1, (1 - (from + (to - from) * frame / 32)) * 8) })), { duration, easing: 'linear', fill: 'both' }))
      document.body.append(layer)
      layer.querySelectorAll<HTMLElement>('[data-pdm-snapshot-scroll-top]').forEach(node => { node.scrollTop = Number(node.dataset.pdmSnapshotScrollTop); node.scrollLeft = Number(node.dataset.pdmSnapshotScrollLeft) })
      panel.dataset.pdmGenieSource = ''
      panel.addEventListener('click', blockAnimatingClick, true)
      motions.set(panel, { phase, from, to, duration, started: performance.now(), cleanup })
      // 所有条带从同一个时间点开始，避免大表单克隆耗时造成上下条带错拍。
      animations.forEach(animation => { animation.currentTime = 0 })
      timer = window.setTimeout(cleanup, 5000)
      void animations[0]!.finished.then(cleanup, () => {})
    } catch (cause) { console.warn('[抽屉动效] 分段动画初始化失败', cause); cleanup() }
  }
  function scan() {
    scheduled = 0
    if (stopped) return
    document.querySelectorAll<HTMLElement>(panelSelector).forEach(panel => {
      if (panel.closest('[data-pdm-genie-layer]')) return
      const phase = panel.closest(leaveSelector) ? 'leave' : panel.closest(enterSelector) ? 'enter' : ''
      if (!phase) { phases.delete(panel); return }
      if (phases.get(panel) === phase) return
      phases.set(panel, phase)
      animate(panel, phase)
    })
  }
  const observer = new MutationObserver(records => {
    if (!records.some(record => !(record.target instanceof Element && record.target.closest('[data-pdm-genie-layer]')))) return
    if (!scheduled) scheduled = requestAnimationFrame(scan)
  })
  observer.observe(document.body, { childList: true, subtree: true, attributes: true, attributeFilter: ['class'] })
  function cancelAll() { for (const motion of [...motions.values()]) motion.cleanup() }
  window.addEventListener('resize', cancelAll)
  reduced.addEventListener('change', cancelAll)
  return () => { stopped = true; observer.disconnect(); cancelAnimationFrame(scheduled); cancelAll(); window.removeEventListener('resize', cancelAll); reduced.removeEventListener('change', cancelAll) }
}