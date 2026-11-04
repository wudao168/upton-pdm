const trackSelector = '.pdm-tabs, [role="tablist"]'
const itemSelector = ':scope > button, :scope > .el-tabs__item, :scope > .el-radio-button'
const selectedSelector = '.is-active, .active, .selected, [aria-selected="true"], [aria-pressed="true"], [aria-current="page"]'

/** Measure the existing tab controls without replacing their events or keyboard behavior. */
export function installCapsuleTabs() {
  const tracks = new Map<HTMLElement, ResizeObserver>()
  const pending = new Set<HTMLElement>()
  let frame = 0
  const flush = () => {
    frame = 0
    if (document.hidden) return
    for (const track of pending) {
      if (!track.isConnected) { tracks.get(track)?.disconnect(); tracks.delete(track); continue }
      const items = Array.from(track.querySelectorAll<HTMLElement>(itemSelector))
      const selected = items.find(item => item.matches(selectedSelector))
      if (!selected || !selected.getClientRects().length) {
        if (track.classList.contains('pdm-capsule-ready')) track.classList.remove('pdm-capsule-ready')
        continue
      }
      const target = selected.querySelector<HTMLElement>('.el-radio-button__inner') ?? selected
      const bounds = track.getBoundingClientRect(), rect = target.getBoundingClientRect()
      const values = { x: rect.left - bounds.left + track.scrollLeft - track.clientLeft, y: rect.top - bounds.top + track.scrollTop - track.clientTop, w: rect.width, h: rect.height }
      for (const [key, value] of Object.entries(values)) track.style.setProperty(`--pdm-thumb-${key}`, `${value}px`)
      if (!track.classList.contains('pdm-capsule-ready')) track.classList.add('pdm-capsule-ready')
      const observer = tracks.get(track)!
      observer.observe(track)
      items.forEach(item => observer.observe(item))
    }
    pending.clear()
  }
  const schedule = (track: HTMLElement) => {
    pending.add(track)
    if (!frame && !document.hidden) frame = requestAnimationFrame(flush)
  }
  const discover = (node: Node) => {
    if (!(node instanceof Element)) return
    const found = Array.from(node.querySelectorAll<HTMLElement>(trackSelector))
    if (node.matches(trackSelector)) found.push(node as HTMLElement)
    for (const track of found) {
      if (!tracks.has(track)) {
        const observer = new ResizeObserver(() => schedule(track))
        tracks.set(track, observer)
        observer.observe(track)
        schedule(track)
      }
    }
  }
  const mutations = new MutationObserver(records => {
    for (const record of records) {
      if (record.type === 'childList') record.addedNodes.forEach(discover)
      const node = record.target instanceof Element ? record.target : record.target.parentElement
      const track = node?.closest<HTMLElement>(trackSelector)
      if (track && tracks.has(track)) schedule(track)
      if (record.type === 'attributes' && node?.matches(trackSelector)) discover(node)
    }
    for (const [track, observer] of tracks) if (!track.isConnected) { observer.disconnect(); tracks.delete(track); pending.delete(track) }
  })
  mutations.observe(document.body, { subtree: true, childList: true, characterData: true, attributes: true, attributeFilter: ['class', 'aria-selected', 'aria-pressed', 'aria-current', 'role', 'hidden'] })
  const resume = () => { if (!document.hidden) tracks.forEach((_, track) => schedule(track)) }
  document.addEventListener('visibilitychange', resume)
  discover(document.body)
  return () => {
    mutations.disconnect()
    tracks.forEach((observer, track) => { observer.disconnect(); track.classList.remove('pdm-capsule-ready') })
    cancelAnimationFrame(frame)
    document.removeEventListener('visibilitychange', resume)
  }
}
