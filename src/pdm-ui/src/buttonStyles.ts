import type { App } from 'vue'
import './styles/buttons.css'
import './styles/refresh-buttons.css'
import './styles/table-sorting.css'

// 导航、树节点、日历格和图片入口属于业务内容，不作为操作按钮装饰。
export const buttonNavigationSelector = 'nav, .pdm-sidebar, .pdm-tabs, [role="tablist"], .el-tree, [role="tree"], .el-pagination, .el-picker-panel, .el-dropdown-menu, .pdm-tree-context-menu'
export const buttonContentSelector = [
  '[role="tab"]', '[role="treeitem"]', '[role="option"]', '[role="menuitem"]', '[role="checkbox"]', '[role="switch"]',
  '.pdm-global-status', '.pdm-company-switcher', '.pdm-project-code-link', '.pdm-project-switcher__display', '.pdm-project-switcher__search-icon',
  '.pdm-release-package-row', '.pdm-titlebar__sidebar-toggle',
  '.wr-next', '.pdm-plan-summary-action', '.bom-overview__link',
  '[aria-label^="选择项目号"]', '.pdm-project-switcher__option',
  '.pdm-tree-row', '.pdm-tree-toggle', '.pdm-bom-cell-edit', '.pdm-gantt-cell-edit', '.pdm-gantt-resize-handle',
  '.pdm-bom-issue-button', '.pdm-bom-files-link',
  '.el-table .caret-wrapper',
  '.bom-tree-node__select', '.bom-tree-node__toggle', '.bom-tree-node__category',
  '.pdm-project-classification__division', '.pdm-project-classification__unassigned',
  '.production-workbench__project', '.production-workbench__package', '.production-drawings__project', '.production-suppliers__category-filter',
  '.pdm-bom-release-history > button',
  '.production-workbench__all-categories', '.pdm-overview-timeline button', '.wh-department-options button',
  '.production-scan-settings__node', '.el-tag__close',
  '.wr-project', '.wh-day', '.wh-bar-column', '.pc-day', '.all-category', '.all-categories', '.standard-library__all', '.material-category-all', '.material-category-nav__toggle',
  '.cover-button', '.cover-preview-button', '.material-image-add', '.material-image-card__preview',
  '.el-dialog__headerbtn', '.el-drawer__close-btn', '.el-message-box__headerbtn',
].join(', ')

const compactLabels: Record<string, string> = {
  新增问题: '新增',
  添加自定义: '自定义',
  添加自定义项: '自定义',
  保存批注: '保存',
  清空筛选: '清空',
  保存可用工时: '保存',
  新建供应商: '新建',
  新增常备件: '新增',
  新增物料: '新增',
  保存基线设置: '保存设置',
  新增根分类: '新增分类',
  保存并继续: '保存',
  创建主项目: '创建',
}
const generatedTitles = new WeakMap<HTMLButtonElement, string>()

function updateButton(button: HTMLButtonElement) {
  const text = button.textContent?.replace(/\s+/g, ' ').trim() ?? ''
  if (button.closest(buttonNavigationSelector) || button.matches(buttonContentSelector) || button.closest('.pdm-overview-alerts') || button.querySelector('img, canvas')) {
    delete button.dataset.pdmButtonKind
    delete button.dataset.pdmButtonLabel
    delete button.dataset.pdmButtonIconOnly
    delete button.dataset.pdmButtonRefresh
    return
  }
  const label = text || button.getAttribute('aria-label') || button.title
  const action = label.replace(/^(?:批量|一键)/, '')
  if (/^(?:(?:立即|全量|正在|手动))*刷新/.test(action)) button.dataset.pdmButtonRefresh = 'true'
  else delete button.dataset.pdmButtonRefresh
  let kind = 'outline'
  if (/^(?:确认|永久|彻底)?(?:删除|移除|清空)/.test(action)) kind = 'delete'
  else if (/^(?:新增|新建|添加|增加|创建|加入)/.test(action)) kind = 'add'
  else if (/^(?:确认|确定|保存|提交|同意|通过|批准)/.test(action)) kind = 'confirm'
  else if (button.disabled && /^(?:正在|处理中|请稍候)|中[.…]*$/.test(label)) kind = button.dataset.pdmButtonKind ?? kind
  if (kind === 'outline' && button.matches('.pdm-text-action, .is-link, .el-button--text, .pdm-link-button, .pdm-project-assignment-button')) {
    delete button.dataset.pdmButtonKind
    delete button.dataset.pdmButtonLabel
    delete button.dataset.pdmButtonIconOnly
    return
  }
  button.dataset.pdmButtonKind = kind
  if (kind === 'outline') {
    delete button.dataset.pdmButtonLabel
    delete button.dataset.pdmButtonIconOnly
  } else {
    const compact = compactLabels[label] ?? (/^保存BOM(?:（\d+）)?$/.test(label) ? '保存' : label)
    button.dataset.pdmButtonLabel = compact
    button.dataset.pdmButtonIconOnly = String(!text || /^[+＋×✕✓✔]$/.test(text))
    if (compact !== label && (!button.title || button.title === generatedTitles.get(button))) {
      const title = button.getAttribute('aria-label') || label
      generatedTitles.set(button, title)
      if (button.title !== title) button.title = title
    } else if (compact === label && button.title === generatedTitles.get(button)) {
      generatedTitles.delete(button)
      button.removeAttribute('title')
    }
  }
}

/** 仅附加表现属性，保留 Vue 管理的子节点、事件、可访问名称及原有尺寸。 */
export function installButtonStyles(app: App) {
  const update = (node: Node) => {
    if (!(node instanceof Element)) return
    if (node instanceof HTMLButtonElement) updateButton(node)
    node.querySelectorAll<HTMLButtonElement>('button').forEach(updateButton)
  }
  const observer = new MutationObserver(records => {
    for (const record of records) {
      if (record.type === 'attributes') update(record.target)
      else {
        if (record.type === 'childList') record.addedNodes.forEach(update)
        const element = record.target instanceof Element ? record.target : record.target.parentElement
        const button = element?.closest('button')
        if (button) updateButton(button)
      }
    }
  })
  observer.observe(document.body, { subtree: true, childList: true, characterData: true, attributes: true, attributeFilter: ['class', 'role', 'aria-label', 'title', 'disabled'] })
  document.querySelectorAll<HTMLButtonElement>('button').forEach(updateButton)
  app.onUnmount(() => observer.disconnect())
}
