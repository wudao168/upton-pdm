type Side = 'top' | 'right' | 'bottom' | 'left'
export type ContainerBorderBox = {
  element: HTMLElement
  rect: Pick<DOMRectReadOnly, 'top' | 'right' | 'bottom' | 'left'>
  borders: Record<Side, number>
}

const tolerance = .5
const sides: Side[] = ['top', 'right', 'bottom', 'left']
const attribute = 'data-pdm-shared-border'
const candidates = 'section, article, aside, main, form, fieldset, .pdm-panel, .pdm-project-sidebar, .pdm-table-scroll, .el-tabs__content, div[class*="-panel"], div[class*="__panel"], div[class*="-card"], div[class*="__card"], div[class*="-toolbar"], div[class*="__toolbar"], div[class*="-table-wrap"], div[class*="__table-wrap"]'

function covered(start: number, end: number, ranges: number[][]) {
  let cursor = start
  for (const [from, to] of ranges.sort((a, b) => a[0]! - b[0]!)) {
    if (to! < cursor) continue
    if (from! > cursor + tolerance) return false
    cursor = Math.max(cursor, to!)
    if (cursor >= end - tolerance) return true
  }
  return false
}

/** Only suppress a complete shared edge; keep every border box and its content size intact. */
export function sharedContainerBorders(boxes: ContainerBorderBox[]) {
  const result = new Map<HTMLElement, string>()
  const scopes = new Map(boxes.map(box => [box.element, box.element.closest('.el-overlay, .el-popper, .pdm-custom-drawer')]))
  for (const box of boxes) {
    const hidden = new Set<Side>()
    for (const side of sides) {
      if (!box.borders[side]) continue
      const vertical = side === 'left' || side === 'right'
      const start = vertical ? box.rect.top : box.rect.left
      const end = vertical ? box.rect.bottom : box.rect.right
      const shared: number[][] = []
      for (const other of boxes) {
        if (other === box || scopes.get(other.element) !== scopes.get(box.element)) continue
        if (other.element.contains(box.element) && other.borders[side]) {
          const inset = side === 'left' || side === 'top' ? other.borders[side] : -other.borders[side]
          if (Math.abs(box.rect[side] - other.rect[side] - inset) <= tolerance) {
            shared.push(vertical ? [other.rect.top, other.rect.bottom] : [other.rect.left, other.rect.right])
          }
        } else if (side === 'left' && other.borders.right && Math.abs(box.rect.left - other.rect.right) <= tolerance) {
          shared.push([other.rect.top, other.rect.bottom])
        } else if (side === 'top' && other.borders.bottom && Math.abs(box.rect.top - other.rect.bottom) <= tolerance) {
          shared.push([other.rect.left, other.rect.right])
        }
      }
      if (covered(start, end, shared)) hidden.add(side)
    }
    if (hidden.size) result.set(box.element, [...hidden].join(' '))
  }
  return result
}

export function installContainerBorders(owner: Document = document) {
  const view = owner.defaultView
  if (!view || owner.documentElement.hasAttribute('data-pdm-shared-borders')) return () => {}
  owner.documentElement.setAttribute('data-pdm-shared-borders', '')
  let frame = 0
  let marked = new Set<HTMLElement>()
  function refresh() {
    frame = 0
    const boxes: ContainerBorderBox[] = []
    for (const element of owner.querySelectorAll<HTMLElement>(candidates)) {
      const rect = element.getBoundingClientRect()
      if (!rect.width || !rect.height || rect.right < 0 || rect.bottom < 0 || rect.left > view!.innerWidth || rect.top > view!.innerHeight) continue
      const style = view!.getComputedStyle(element)
      const previous = element.getAttribute(attribute)?.split(' ') ?? []
      const borders = Object.fromEntries(sides.map(side => {
        const color = style.getPropertyValue(`border-${side}-color`)
        const transparent = color === 'transparent' || /rgba\([^)]*,\s*0\s*\)$/.test(color)
        return [side, transparent && !previous.includes(side) ? 0 : parseFloat(style.getPropertyValue(`border-${side}-width`)) || 0]
      })) as Record<Side, number>
      if (sides.some(side => borders[side] > 0)) boxes.push({ element, rect, borders })
    }
    const next = sharedContainerBorders(boxes)
    for (const element of marked) if (!next.has(element)) element.removeAttribute(attribute)
    for (const [element, value] of next) if (element.getAttribute(attribute) !== value) element.setAttribute(attribute, value)
    marked = new Set(next.keys())
  }
  function schedule() { if (!frame) frame = view!.requestAnimationFrame(refresh) }
  const mutations = new MutationObserver(schedule)
  mutations.observe(owner.body, { childList: true, subtree: true, attributes: true, attributeFilter: ['class', 'style', 'hidden'] })
  view.addEventListener('resize', schedule)
  view.addEventListener('scroll', schedule, true)
  owner.addEventListener('load', schedule, true)
  owner.addEventListener('transitionend', schedule, true)
  owner.addEventListener('animationend', schedule, true)
  schedule()
  return () => {
    mutations.disconnect()
    if (frame) view.cancelAnimationFrame(frame)
    view.removeEventListener('resize', schedule)
    view.removeEventListener('scroll', schedule, true)
    owner.removeEventListener('load', schedule, true)
    owner.removeEventListener('transitionend', schedule, true)
    owner.removeEventListener('animationend', schedule, true)
    for (const element of marked) element.removeAttribute(attribute)
    owner.documentElement.removeAttribute('data-pdm-shared-borders')
  }
}
