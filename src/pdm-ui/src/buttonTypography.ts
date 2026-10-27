import type { App } from 'vue'
import { buttonContentSelector, buttonNavigationSelector } from './buttonStyles'
import './styles/button-sizing.css'
import { installControlSpacing } from './controlSpacing'

const inlineContext = 'table, .el-table, .pdm-tree, .project-information__staff-actions, .project-contacts__header-actions, .pc-details, .pdm-bom-reconciliation-actions, .material-image-card, .batch-material-image-cards'
const blockButton = '.pdm-login-submit, .production-scan-page__login button, .production-scan-page__actions > button, .production-scan-page .production-operation form > button'
const iconButton = '.el-dialog__headerbtn, .el-drawer__close-btn, .el-message-box__headerbtn, .pdm-icon-button, .pdm-icon-action, .pdm-tree-toggle, .material-category-nav__toggle'

function updateButton(button: HTMLButtonElement) {
  if (button.classList.contains('pdm-button-font-80x30')) button.classList.remove('pdm-button-font-80x30')
  const text = button.textContent?.replace(/\s+/g, ' ').trim() ?? ''
  const icon = button.matches(iconButton) || !text || /^[+＋×✕✓✔‹›←→↑↓▴▾▸▹◂◃−－]$/.test(text)
  const inline = !!button.closest(inlineContext) || button.matches('.is-link, .el-button--text, .pdm-text-action, .pdm-link-button')
  const excluded = !!button.closest(buttonNavigationSelector) || !!button.closest('.pdm-overview-alerts')
    || !!button.querySelector('img, canvas') || (button.matches(buttonContentSelector) && !button.matches(iconButton))
  if (excluded) {
    delete button.dataset.pdmButtonSize
    delete button.dataset.pdmButtonFit
    return
  }
  const size = button.matches(blockButton) ? 'block' : icon ? (inline ? 'inline-icon' : 'icon') : inline ? 'inline' : 'default'
  if (button.dataset.pdmButtonSize !== size) button.dataset.pdmButtonSize = size
  const compact = !!button.dataset.pdmButtonLabel && button.dataset.pdmButtonLabel !== text
  if (compact) {
    if (button.dataset.pdmButtonFit !== 'compact') button.dataset.pdmButtonFit = 'compact'
  } else delete button.dataset.pdmButtonFit
}

/** 按用途统一尺寸和字体；延迟挂载的弹窗共用同一套规则。 */
export function installButtonTypography(app: App) {
  const disposeSpacing = installControlSpacing()
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
  observer.observe(document.body, { subtree: true, childList: true, characterData: true, attributes: true,
    attributeFilter: ['class', 'role', 'style', 'aria-label', 'title', 'data-pdm-button-label'] })
  update(document.body)
  app.onUnmount(() => { observer.disconnect(); disposeSpacing() })
}
