import { watch, type App, type ComponentPublicInstance, type Ref } from 'vue'
import type { TableInstance } from 'element-plus'
import { readTablePreference, tablePreferenceVersion, writeTablePreference } from './tablePreferences'
import { ElMessage } from './statusMessage'

const minimumWidth = 32

type HeaderPosition = { start: number; span: number }
function htmlHeaders(table: HTMLTableElement) {
  const positions = new Map<HTMLTableCellElement, HeaderPosition>()
  const occupied: number[] = []
  let count = 0
  for (const [rowIndex, row] of Array.from(table.tHead?.rows ?? []).entries()) {
    let column = 0
    for (const cell of Array.from(row.cells)) {
      while ((occupied[column] ?? 0) > rowIndex) column++
      positions.set(cell, { start: column, span: cell.colSpan })
      for (let offset = 0; offset < cell.colSpan; offset++) occupied[column + offset] = rowIndex + cell.rowSpan
      column += cell.colSpan
    }
    count = Math.max(count, column)
  }
  return { positions, count }
}

export function resizeToFit(widths: number[], index: number, delta: number) {
  const result = [...widths]
  const peers = index < widths.length - 1 ? widths.map((_, i) => i).slice(index + 1) : [index - 1]
  if (!peers.length || peers[0]! < 0) return result
  const minimum = Math.min(minimumWidth, ...widths)
  const peerTotal = peers.reduce((sum, i) => sum + widths[i]!, 0)
  const capacity = peerTotal - minimum * peers.length
  result[index] = Math.min(widths[index]! + capacity, Math.max(minimum, widths[index]! + delta))
  const remaining = peerTotal - (result[index]! - widths[index]!) - minimum * peers.length
  peers.forEach(i => { result[i] = minimum + (capacity ? (widths[i]! - minimum) / capacity : 1 / peers.length) * remaining })
  return result
}

/** Delegated events also cover tables inside lazily mounted dialogs. */
export function installColumnResize(app: App, owner: Document = document) {
  const elementTables = new WeakMap<HTMLElement, TableInstance>()
  const fittedTables = new WeakSet<HTMLElement>()
  const observers = new WeakMap<ComponentPublicInstance, ResizeObserver>()
  const fitters = new WeakMap<ComponentPublicInstance, () => void>()
  let drag: { root: HTMLElement; key: string | null; startX: number; widths: number[]; current?: number[]; index: number; apply: (widths: number[]) => void } | null = null
  let disposed = false, restoreQueued = false
  const label = (text: string) => text.replace(/\d{4}[-/]\d{1,2}[-/]\d{1,2}|\d{1,2}[-/]\d{1,2}/g, '').replace(/\s+/g, ' ').trim()
  function preferenceKey(root: HTMLElement, columns: string[]) {
    try {
      const scope = JSON.parse(owner.querySelector('[data-pdm-preference-scope]')?.getAttribute('data-pdm-preference-scope') ?? '[]') as string[]
      if (!scope[0] || !columns.length) return null
      const path = []
      for (let node: HTMLElement | null = root; node && node !== owner.body; node = node.parentElement) {
        const classes = [...node.classList].filter(value => !/^(el-|is-|has-|theme-)|^pdm-(fit-columns|columns-resized|resized-grid-list|column-resize-edge)$/.test(value)).sort()
        path.push([node.tagName, classes, node.getAttribute('data-navigation-page'), label(node.getAttribute('aria-label') ?? '')])
        if (node.hasAttribute('data-pdm-preference-scope')) break
      }
      return 'upton-pdm-column-widths:v1:' + JSON.stringify([scope, path, columns])
    } catch { return null }
  }
  function savedWidths(key: string | null, count: number): number[] | null {
    try {
      const values: unknown = key ? JSON.parse(readTablePreference(key) ?? 'null') : null
      return Array.isArray(values) && values.length === count && values.every(value => typeof value === 'number' && Number.isFinite(value) && value > 0 && value < 1_000_000) ? values : null
    } catch { return null }
  }
  function saveWidths(key: string | null, widths: number[]) {
    if (key) void writeTablePreference(key, JSON.stringify(widths)).catch(() => ElMessage.warning('列宽同步失败，当前调整已保留在本机，请稍后重试。'))
  }
  function elementSchema(table: TableInstance) {
    return table.store.states.columns.value.map(column => JSON.stringify([column.property, column.type, label(column.label ?? '')]))
  }
  function htmlSchema(structure: ReturnType<typeof htmlHeaders>) {
    return [...structure.positions].map(([cell, position]) => JSON.stringify([label(cell.textContent ?? ''), position.start, position.span, cell.rowSpan]))
  }
  function applyGrid(root: HTMLElement, widths: number[], gantt: boolean) {
    const property = gantt ? '--pdm-resized-gantt-columns' : '--pdm-resized-list-columns'
    const value = widths.map(width => `${width}${gantt ? 'px' : 'fr'}`).join(' ')
    if (root.style.getPropertyValue(property) !== value) root.style.setProperty(property, value)
    if (gantt) root.style.setProperty('--pdm-resized-gantt-width', `${widths.reduce((sum, width) => sum + width, 0)}px`)
    root.classList.add(gantt ? 'pdm-columns-resized' : 'pdm-resized-grid-list')
  }
  function applyHtml(table: HTMLTableElement, structure: ReturnType<typeof htmlHeaders>, values: number[]) {
    let group = table.querySelector(':scope > colgroup')
    if (!group) { group = owner.createElement('colgroup'); table.prepend(group) }
    if (group.children.length !== structure.count) group.replaceChildren(...values.map(() => owner.createElement('col')))
    const total = values.reduce((sum, width) => sum + width, 0)
    Array.from(group.children).forEach((column, index) => { (column as HTMLElement).style.width = `${100 * values[index]! / total}%` })
    for (const [cell, position] of structure.positions) cell.style.width = `${100 * values.slice(position.start, position.start + position.span).reduce((sum, width) => sum + width, 0) / total}%`
    table.style.width = '100%'; table.style.minWidth = '0'; table.style.maxWidth = '100%'
    table.classList.add('pdm-fit-columns'); table.style.tableLayout = 'fixed'
  }
  function restoreTables() {
    for (const root of owner.querySelectorAll<HTMLElement>('table, .pdm-gantt-table, .pdm-project-browser-list')) {
      if (drag?.root === root || root.closest('.el-table')) continue
      if (root instanceof HTMLTableElement) {
        const structure = htmlHeaders(root)
        const values = savedWidths(preferenceKey(root, htmlSchema(structure)), structure.count)
        if (values) applyHtml(root, structure, values)
      } else {
        const gantt = root.classList.contains('pdm-gantt-table')
        const head = root.querySelector(gantt ? '.pdm-gantt-info-head' : '.pdm-project-browser-table-head')
        if (!head) continue
        const columns = [...head.children].map(cell => label(cell.textContent ?? ''))
        const values = savedWidths(preferenceKey(root, columns), columns.length)
        if (values) applyGrid(root, values, gantt)
      }
    }
  }
  function scheduleRestore() {
    if (restoreQueued || disposed) return
    restoreQueued = true
    queueMicrotask(() => { restoreQueued = false; if (!disposed) restoreTables() })
  }
  function fitElement(table: TableInstance, widths: number[]) {
    table.store.states.columns.value.forEach((column, index) => { column.width = column.realWidth = widths[index]! })
    table.doLayout()
  }
  function availableWidth(root: HTMLElement) {
    const style = getComputedStyle(root)
    return root.clientWidth - parseFloat(style.paddingLeft || '0') - parseFloat(style.paddingRight || '0')
  }
  function enableElementTable(this: ComponentPublicInstance) {
    if (this.$options.name !== 'ElTable') return
    const root = this.$el as HTMLElement
    const table = this.$.exposed as TableInstance
    elementTables.set(root, table)
    fittedTables.add(root)
    const fit = () => {
      if (drag?.root === root) return
      const space = root.clientWidth - (table.layout.scrollY.value ? table.layout.gutterWidth : 0)
      const current = table.store.states.columns.value.map(column => column.realWidth ?? Number(column.width ?? column.minWidth ?? 80))
      const widths = savedWidths(preferenceKey(root, elementSchema(table)), current.length) ?? current
      const total = widths.reduce((sum, width) => sum + width, 0)
      if (space > 0 && total > 0 && (Math.abs(current.reduce((sum, width) => sum + width, 0) - space) > 1
        || widths.some((width, index) => Math.abs(width * space / total - current[index]!) > 1))) {
        root.classList.add('pdm-fit-columns')
        fitElement(table, widths.map(width => width * space / total))
      }
    }
    fitters.set(this, fit)
    const observer = new ResizeObserver(fit)
    observer.observe(root)
    observers.set(this, observer)
  }
  // Element Plus forces selection/index columns to be non-resizable internally.
  function enableElementColumns(this: ComponentPublicInstance) {
    if (this.$options.name !== 'ElTableColumn') return
    const instance = this.$ as typeof this.$ & { columnConfig: Ref<{ resizable: boolean }> }
    instance.columnConfig.value.resizable = true
  }
  app.mixin({ mounted() { enableElementColumns.call(this); enableElementTable.call(this); scheduleRestore() }, updated() { enableElementColumns.call(this); fitters.get(this)?.(); scheduleRestore() }, unmounted() { observers.get(this)?.disconnect(); fitters.delete(this) } })
  restoreTables()
  const mutationObserver = new MutationObserver(scheduleRestore)
  mutationObserver.observe(owner.documentElement, { childList: true, subtree: true, attributes: true, attributeFilter: ['data-pdm-preference-scope'] })
  let hovered: HTMLElement | null = null
  let suppressClick = false

  function target(event: PointerEvent) {
    if (!(event.target instanceof Element)) return null
    const header = event.target.closest<HTMLElement>('thead th, .pdm-gantt-info-head > span, .pdm-project-browser-table-head > span')
    if (!header) return null
    const rect = header.getBoundingClientRect()
    return rect.width > 0 && rect.right - event.clientX >= 0 && rect.right - event.clientX <= 7 ? header : null
  }

  function clearHover() {
    hovered?.classList.remove('pdm-column-resize-edge')
    hovered = null
  }

  function begin(event: PointerEvent) {
    if (event.button !== 0) return
    const header = target(event)
    if (!header) return
    let widths: number[]
    let index: number
    let apply: (widths: number[]) => void
    let root: HTMLElement, schema: string[]
    const elementRoot = header.closest<HTMLElement>('.el-table')
    const browserList = header.closest<HTMLElement>('.pdm-project-browser-list')
    const gantt = header.closest<HTMLElement>('.pdm-gantt-table')
    if (elementRoot) {
      const table = elementTables.get(elementRoot)
      if (!table) return
      const columns = table.store.states.columns.value
      root = elementRoot; schema = elementSchema(table)
      index = columns.findIndex(column => header.classList.contains(column.id))
      if (index < 0) return
      const space = elementRoot.clientWidth - (table.layout.scrollY.value ? table.layout.gutterWidth : 0)
      const total = columns.reduce((sum, column) => sum + (column.realWidth ?? Number(column.width ?? column.minWidth ?? 80)), 0)
      widths = columns.map(column => (column.realWidth ?? Number(column.width ?? column.minWidth ?? 80)) * space / total)
      apply = values => { fittedTables.add(elementRoot); elementRoot.classList.add('pdm-fit-columns'); fitElement(table, values) }
    } else if (browserList) {
      const head = header.parentElement!
      root = browserList; schema = [...head.children].map(cell => label(cell.textContent ?? ''))
      widths = Array.from(head.children).map(cell => cell.getBoundingClientRect().width)
      index = Array.from(head.children).indexOf(header)
      apply = values => applyGrid(browserList, values, false)
    } else if (gantt) {
      const head = header.parentElement!
      root = gantt; schema = [...head.children].map(cell => label(cell.textContent ?? ''))
      widths = Array.from(head.children).map(cell => cell.getBoundingClientRect().width)
      index = Array.from(head.children).indexOf(header)
      apply = values => applyGrid(gantt, values, true)
    } else {
      const table = header.closest('table')!
      if (!table.tHead) return
      const structure = htmlHeaders(table)
      const { positions, count } = structure
      root = table; schema = htmlSchema(structure)
      widths = Array.from({ length: count }, () => 0)
      for (const [cell, position] of positions) {
        if (position.span === 1) widths[position.start] = cell.getBoundingClientRect().width
      }
      if (widths.some(width => width <= 0)) return
      const total = widths.reduce((sum, width) => sum + width, 0)
      const space = availableWidth(table.parentElement!)
      widths = widths.map(width => width * space / total)
      const position = positions.get(header as HTMLTableCellElement)!
      index = position.start + position.span - 1
      apply = values => applyHtml(table, structure, values)
    }
    event.preventDefault()
    event.stopImmediatePropagation()
    suppressClick = true
    drag = { root, key: preferenceKey(root, schema), startX: event.clientX, widths, index, apply }
    owner.documentElement.classList.add('pdm-column-resizing')
  }

  function move(event: PointerEvent) {
    if (drag) {
      event.preventDefault()
      drag.current = resizeToFit(drag.widths, drag.index, event.clientX - drag.startX)
      drag.apply(drag.current)
      return
    }
    const header = target(event)
    if (header === hovered) return
    clearHover()
    hovered = header
    hovered?.classList.add('pdm-column-resize-edge')
  }

  function end(event?: Event) {
    if (drag?.current && (event?.type === 'pointerup' || event?.type === 'blur')) saveWidths(drag.key, drag.current)
    drag = null
    owner.documentElement.classList.remove('pdm-column-resizing')
    clearHover()
    // The click generated by releasing a drag must not sort or activate the header.
    setTimeout(() => { suppressClick = false }, 0)
  }
  function click(event: MouseEvent) {
    if (suppressClick) { event.preventDefault(); event.stopImmediatePropagation() }
  }
  owner.addEventListener('pointerdown', begin, true)
  owner.addEventListener('pointermove', move, true)
  owner.addEventListener('pointerup', end, true)
  owner.addEventListener('pointercancel', end, true)
  owner.addEventListener('click', click, true)
  owner.defaultView?.addEventListener('blur', end)
  const stopPreferences = watch(tablePreferenceVersion, () => {
    scheduleRestore()
    for (const root of owner.querySelectorAll<HTMLElement>('.el-table')) {
      const table = elementTables.get(root)
      if (table?.$?.proxy) fitters.get(table.$.proxy)?.()
    }
  })
  app.onUnmount(() => {
    stopPreferences()
    disposed = true
    mutationObserver.disconnect()
    end()
    owner.removeEventListener('pointerdown', begin, true)
    owner.removeEventListener('pointermove', move, true)
    owner.removeEventListener('pointerup', end, true)
    owner.removeEventListener('pointercancel', end, true)
    owner.removeEventListener('click', click, true)
    owner.defaultView?.removeEventListener('blur', end)
  })
}
