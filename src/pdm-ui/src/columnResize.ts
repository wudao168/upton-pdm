import { watch, type App, type ComponentPublicInstance, type Ref } from 'vue'
import type { TableInstance } from 'element-plus'
import { readTablePreference, tablePreferenceVersion, writeTablePreference } from './tablePreferences'
import { ElMessage } from './statusMessage'
import { isInactiveDom } from './activeDom'

const minimumWidth = 32
const hasFixedWidth = (column: { className?: string }) => column.className?.split(/\s+/).includes('pdm-column-fixed-width') === true

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

export function resizeColumn(widths: number[], index: number, delta: number) {
  const result = [...widths]
  const peers = index < widths.length - 1 ? widths.map((_, i) => i).slice(index + 1) : [index - 1]
  if (peers[0] === undefined || peers[0] < 0) return result
  if (delta < 0) {
    const change = Math.max(delta, Math.min(minimumWidth, widths[index]!) - widths[index]!)
    result[index]! += change
    result[peers[0]]! -= change
  } else {
    let remaining = delta
    for (const peer of peers) {
      const change = Math.min(remaining, widths[peer]! - Math.min(minimumWidth, widths[peer]!))
      result[index]! += change
      result[peer]! -= change
      remaining -= change
      if (remaining <= 0) break
    }
  }
  return result
}

export function fitColumnWidths(widths: number[], space: number) {
  const total = widths.reduce((sum, width) => sum + width, 0)
  return total > 0 && space > 0 ? widths.map(width => width * space / total) : widths
}

/** 宽字段目录保留最小列宽；剩余空间按比例分配，空间不足时由表格内部滚动。 */
export function readableColumnWidths(widths: number[], minimums: number[], space: number, fixed: boolean[] = []) {
  const result = minimums.map((minimum, index) => fixed[index] ? minimum : Math.max(minimum, widths[index] ?? minimum))
  const extra = Math.max(0, space - result.reduce((sum, width) => sum + width, 0))
  const flexible = result.reduce((sum, width, index) => sum + (fixed[index] ? 0 : width), 0)
  return result.map((width, index) => fixed[index] || !flexible ? width : width + extra * width / flexible)
}

/** Delegated events also cover tables inside lazily mounted dialogs. */
export function installColumnResize(app: App, owner: Document = document) {
  const elementTables = new WeakMap<HTMLElement, TableInstance>()
  const observers = new WeakMap<ComponentPublicInstance, ResizeObserver>()
  const fitters = new WeakMap<ComponentPublicInstance, () => void>()
  const readableMinimums = new WeakMap<object, number>()
  const manualWidths = new WeakMap<HTMLElement, { key: string | null; schema: string; widths: number[] }>()
  let drag: { root: HTMLElement; key: string | null; startX: number; widths: number[]; current?: number[]; index: number; apply: (widths: number[]) => void } | null = null
  let disposed = false, restoreQueued = false
  const label = (text: string) => text.replace(/\d{4}[-/]\d{1,2}[-/]\d{1,2}|\d{1,2}[-/]\d{1,2}/g, '').replace(/\s+/g, ' ').trim()
  function preferenceKey(root: HTMLElement, columns: string[]) {
    try {
      const scope = JSON.parse(owner.querySelector('[data-pdm-preference-scope]')?.getAttribute('data-pdm-preference-scope') ?? '[]') as string[]
      if (!scope[0] || !columns.length) return null
      const path = []
      for (let node: HTMLElement | null = root; node && node !== owner.body; node = node.parentElement) {
        const classes = [...node.classList].filter(value => !/^(el-|is-|has-|theme-)|^pdm-(fit-columns|columns-resized|resized-grid-list|column-resize-edge|column-scroll)$/.test(value)).sort()
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
    const value = widths.map(width => gantt ? `${width}px` : `minmax(0, ${width}fr)`).join(' ')
    if (root.style.getPropertyValue(property) !== value) root.style.setProperty(property, value)
    if (gantt) root.style.setProperty('--pdm-resized-gantt-width', `${widths.reduce((sum, width) => sum + width, 0)}px`)
    root.classList.add(gantt ? 'pdm-columns-resized' : 'pdm-resized-grid-list')
  }
  function applyHtml(table: HTMLTableElement, structure: ReturnType<typeof htmlHeaders>, values: number[]) {
    let group = table.querySelector(':scope > colgroup')
    if (!group) { group = owner.createElement('colgroup'); table.prepend(group) }
    if (group.children.length !== structure.count) group.replaceChildren(...values.map(() => owner.createElement('col')))
    const total = values.reduce((sum, value) => sum + value, 0)
    const width = (start: number, span: number) => `${values.slice(start, start + span).reduce((sum, value) => sum + value, 0) / total * 100}%`
    Array.from(group.children).forEach((column, index) => { (column as HTMLElement).style.width = width(index, 1) })
    for (const [cell, position] of structure.positions) cell.style.width = width(position.start, position.span)
    table.style.setProperty('width', '100%', 'important')
    table.style.setProperty('min-width', '0', 'important')
    table.style.setProperty('max-width', '100%', 'important')
    table.classList.add('pdm-fit-columns', 'pdm-columns-resized'); table.style.tableLayout = 'fixed'
    table.parentElement?.classList.add('pdm-column-scroll')
  }
  function restoreTables() {
    for (const root of owner.querySelectorAll<HTMLElement>('table, .pdm-gantt-table, .pdm-project-browser-list')) {
      if (isInactiveDom(root)) continue
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
    const fit = () => {
      if (isInactiveDom(root)) return
      if (drag?.root === root) return
      const space = root.clientWidth - (table.layout.scrollY.value ? table.layout.gutterWidth : 0)
      const columns = table.store.states.columns.value
      const current = columns.map(column => column.realWidth ?? Number(column.width ?? column.minWidth ?? 80))
      const schema = JSON.stringify(elementSchema(table))
      const key = preferenceKey(root, elementSchema(table))
      const manual = manualWidths.get(root)
      const saved = savedWidths(key, columns.length)
        ?? (manual?.schema === schema && manual.key === key ? manual.widths : null)
      if (saved) {
        root.classList.add('pdm-fit-columns')
        const fitted = fitColumnWidths(saved, space)
        if (fitted.some((width, index) => Math.abs(width - current[index]!) > 0.1)) fitElement(table, fitted)
        return
      }
      if (root.dataset.pdmColumnLayout === 'scroll') {
        const minimums = columns.map(column => {
          if (!readableMinimums.has(column)) readableMinimums.set(column, Number(column.width ?? column.minWidth ?? 80))
          return readableMinimums.get(column)!
        })
        const fitted = fitColumnWidths(readableColumnWidths(minimums, minimums, space, columns.map(hasFixedWidth)), space)
        if (fitted.some((width, index) => Math.abs(width - current[index]!) > 1)) fitElement(table, fitted)
        return
      }
      const widths = current
      // 固定宽度列与其余列在此统一分配，避免页面再次改写列宽而触发布局循环。
      const fixed = columns.map(hasFixedWidth)
      const fixedSpace = current.reduce((sum, width, index) => sum + (fixed[index] ? width : 0), 0)
      const total = widths.reduce((sum, width, index) => sum + (fixed[index] ? 0 : width), 0)
      if (space <= fixedSpace || total <= 0) return
      const fitted = widths.map((width, index) => fixed[index] ? current[index]! : width * (space - fixedSpace) / total)
      if (Math.abs(current.reduce((sum, width) => sum + width, 0) - space) > 1
        || fitted.some((width, index) => Math.abs(width - current[index]!) > 1)) {
        root.classList.add('pdm-fit-columns')
        fitElement(table, fitted)
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
    const instance = this.$ as typeof this.$ & { columnConfig: Ref<{ resizable: boolean; className?: string }> }
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
      widths = columns.map(column => column.realWidth ?? Number(column.width ?? column.minWidth ?? 80))
      apply = values => {
        manualWidths.set(elementRoot, { key: preferenceKey(root, schema), schema: JSON.stringify(schema), widths: values })
        elementRoot.classList.add('pdm-fit-columns'); fitElement(table, values)
      }
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
      widths = fitColumnWidths(widths, availableWidth(table.parentElement!))
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
      drag.current = resizeColumn(drag.widths, drag.index, event.clientX - drag.startX)
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
