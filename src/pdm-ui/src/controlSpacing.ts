import './styles/control-spacing.css'

const control = 'button, input:not([type="hidden"]), select, textarea, [role="tab"], .el-input, .el-select, .el-date-editor, .el-cascader, .el-input-number, .el-dropdown, .el-checkbox, .el-radio-group, .el-switch'
const wrapper = 'label, fieldset, .el-form-item, .pdm-inline-search, .pdm-titlebar__left, .pdm-titlebar__actions, .pdm-project-filters, .pdm-bom-selection-actions, .pdm-bom-filters, .pdm-bom-version-picker'
const excluded = '.pdm-sidebar, .pdm-tree, .el-tree, [role="tree"], .wh-department-options, .pdm-project-family, .wr-project-list, .pdm-overview-timeline, .pdm-overview-alerts, .production-scan-settings__chain, .pc-grid, .wh-day-grid, .el-input, .el-select, .el-date-editor, .el-cascader, .el-input-number, .el-picker-panel, .el-time-panel, .el-dropdown-menu'

/** 只调整控件所在行的间距，不改变表格、树和内容卡片的排布。 */
export function installControlSpacing() {
  const pending = new Set<Element>()
  let frame = 0
  const isControl = (element: Element) => element.matches(control)
    || (element.matches(wrapper) && !!element.querySelector(control))
  const flush = () => {
    frame = 0
    const updates = [...pending].filter(element => element.isConnected).map(element => {
      const children = [...element.children]
      const controls = children.filter(isControl)
      let layout = ''
      if (controls.length > 1 && !element.matches('button, input, select, textarea') && !element.closest(excluded)) {
        const display = getComputedStyle(element).display
        layout = /flex|grid/.test(display) ? 'flow'
          : controls.every(child => getComputedStyle(child).display.startsWith('inline')) ? 'inline' : 'stack'
      }
      return { element, children, controls, layout }
    })
    pending.clear()
    for (const { element, children, controls, layout } of updates) {
      if (layout) {
        if (element.getAttribute('data-pdm-control-layout') !== layout) element.setAttribute('data-pdm-control-layout', layout)
      } else element.removeAttribute('data-pdm-control-layout')
      for (const child of children) {
        if (layout && controls.includes(child)) {
          if (!child.hasAttribute('data-pdm-control-item')) child.setAttribute('data-pdm-control-item', '')
        } else child.removeAttribute('data-pdm-control-item')
      }
    }
  }
  const collect = (node: Node) => {
    if (!(node instanceof Element)) return
    pending.add(node)
    if (node.parentElement) pending.add(node.parentElement)
    node.querySelectorAll(`${control}, ${wrapper}`).forEach(element => {
      if (element.parentElement) pending.add(element.parentElement)
    })
    if (!frame) frame = requestAnimationFrame(flush)
  }
  const observer = new MutationObserver(records => {
    for (const record of records) {
      collect(record.target)
      if (record.type === 'childList') record.addedNodes.forEach(collect)
    }
  })
  const resized = () => collect(document.body)
  observer.observe(document.body, { childList: true, subtree: true, attributes: true, attributeFilter: ['class', 'style', 'role'] })
  window.addEventListener('resize', resized)
  collect(document.body)
  return () => { observer.disconnect(); window.removeEventListener('resize', resized); cancelAnimationFrame(frame); pending.clear() }
}
