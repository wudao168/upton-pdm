import type { App, ComponentPublicInstance, Ref } from 'vue'
import type { TableInstance } from 'element-plus'

const minimumWidth = 32

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
      const space = root.clientWidth - (table.layout.scrollY.value ? table.layout.gutterWidth : 0)
      const widths = table.store.states.columns.value.map(column => column.realWidth ?? Number(column.width ?? column.minWidth ?? 80))
      const total = widths.reduce((sum, width) => sum + width, 0)
      if (space > 0 && total > 0 && Math.abs(total - space) > 1) {
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
  app.mixin({ mounted() { enableElementColumns.call(this); enableElementTable.call(this) }, updated() { enableElementColumns.call(this); fitters.get(this)?.() }, unmounted() { observers.get(this)?.disconnect(); fitters.delete(this) } })
  let drag: { startX: number; widths: number[]; index: number; apply: (widths: number[]) => void } | null = null
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
    const elementRoot = header.closest<HTMLElement>('.el-table')
    const browserList = header.closest<HTMLElement>('.pdm-project-browser-list')
    const gantt = header.closest<HTMLElement>('.pdm-gantt-table')
    if (elementRoot) {
      const table = elementTables.get(elementRoot)
      if (!table) return
      const columns = table.store.states.columns.value
      index = columns.findIndex(column => header.classList.contains(column.id))
      if (index < 0) return
      const space = elementRoot.clientWidth - (table.layout.scrollY.value ? table.layout.gutterWidth : 0)
      const total = columns.reduce((sum, column) => sum + (column.realWidth ?? Number(column.width ?? column.minWidth ?? 80)), 0)
      widths = columns.map(column => (column.realWidth ?? Number(column.width ?? column.minWidth ?? 80)) * space / total)
      apply = values => { fittedTables.add(elementRoot); elementRoot.classList.add('pdm-fit-columns'); fitElement(table, values) }
    } else if (browserList) {
      const head = header.parentElement!
      widths = Array.from(head.children).map(cell => cell.getBoundingClientRect().width)
      index = Array.from(head.children).indexOf(header)
      apply = values => {
        browserList.style.setProperty('--pdm-resized-list-columns', values.map(width => `${width}fr`).join(' '))
        browserList.classList.add('pdm-resized-grid-list')
      }
    } else if (gantt) {
      const head = header.parentElement!
      widths = Array.from(head.children).map(cell => cell.getBoundingClientRect().width)
      index = Array.from(head.children).indexOf(header)
      apply = values => {
        gantt.style.setProperty('--pdm-resized-gantt-columns', values.map(value => `${value}px`).join(' '))
        gantt.style.setProperty('--pdm-resized-gantt-width', `${values.reduce((sum, value) => sum + value, 0)}px`)
        gantt.classList.add('pdm-columns-resized')
      }
    } else {
      const table = header.closest('table')!
      if (!table.tHead) return
      const positions = new Map<HTMLTableCellElement, { start: number; span: number }>()
      const occupied: number[] = []
      let count = 0
      for (const [rowIndex, row] of Array.from(table.tHead.rows).entries()) {
        let column = 0
        for (const cell of Array.from(row.cells)) {
          while ((occupied[column] ?? 0) > rowIndex) column++
          positions.set(cell, { start: column, span: cell.colSpan })
          for (let offset = 0; offset < cell.colSpan; offset++) occupied[column + offset] = rowIndex + cell.rowSpan
          column += cell.colSpan
        }
        count = Math.max(count, column)
      }
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
      let group = table.querySelector('colgroup')
      if (!group) { group = owner.createElement('colgroup'); table.prepend(group) }
      if (group.children.length !== count) group.replaceChildren(...widths.map(() => owner.createElement('col')))
      const columns = Array.from(group.children) as HTMLElement[]
      apply = values => {
        const total = values.reduce((sum, width) => sum + width, 0)
        values.forEach((width, column) => { columns[column]!.style.width = `${100 * width / total}%` })
        for (const [cell, position] of positions) cell.style.width = `${100 * values.slice(position.start, position.start + position.span).reduce((sum, width) => sum + width, 0) / total}%`
        table.style.width = '100%'
        table.style.minWidth = '0'
        table.style.maxWidth = '100%'
        table.classList.add('pdm-fit-columns')
        table.style.tableLayout = 'fixed'
      }
    }
    event.preventDefault()
    event.stopImmediatePropagation()
    suppressClick = true
    drag = { startX: event.clientX, widths, index, apply }
    owner.documentElement.classList.add('pdm-column-resizing')
  }

  function move(event: PointerEvent) {
    if (drag) {
      event.preventDefault()
      drag.apply(resizeToFit(drag.widths, drag.index, event.clientX - drag.startX))
      return
    }
    const header = target(event)
    if (header === hovered) return
    clearHover()
    hovered = header
    hovered?.classList.add('pdm-column-resize-edge')
  }

  function end() {
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
  app.onUnmount(() => {
    end()
    owner.removeEventListener('pointerdown', begin, true)
    owner.removeEventListener('pointermove', move, true)
    owner.removeEventListener('pointerup', end, true)
    owner.removeEventListener('pointercancel', end, true)
    owner.removeEventListener('click', click, true)
    owner.defaultView?.removeEventListener('blur', end)
  })
}