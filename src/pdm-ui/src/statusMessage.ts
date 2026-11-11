import { ElMessage as toastMessage } from 'element-plus'
import { readonly, shallowRef } from 'vue'
import { userErrorMessage } from './userErrorMessage'

type StatusType = 'success' | 'warning' | 'error' | 'info'
const currentStatus = shallowRef<{ id: number; type: StatusType; message: string }>()
export const globalStatus = readonly(currentStatus)
let hostCount = 0
let sequence = 0
let dialogStatus: HTMLElement | undefined
let dialogObserver: MutationObserver | undefined

const labels: Record<StatusType, string> = { success: '成功', warning: '提醒', error: '失败', info: '提示' }

function clearDialogStatus() {
  dialogObserver?.disconnect()
  dialogObserver = undefined
  dialogStatus?.remove()
  dialogStatus = undefined
}

function dialogIsOpen(dialog: HTMLElement) {
  if (!dialog.isConnected || dialog.closest('[data-pdm-genie-snapshot], [data-pdm-genie-layer]')) return false
  for (let node: HTMLElement | null = dialog; node; node = node.parentElement) {
    if (node.hidden || node.style.display === 'none' || node.getAttribute('aria-hidden') === 'true'
      || node.classList.contains('dialog-fade-leave-active')
      || node.classList.contains('el-drawer-fade-leave-active')) return false
  }
  return true
}

function activeDialog() {
  if (typeof document === 'undefined') return undefined
  const dialogs = [...document.querySelectorAll<HTMLElement>('.el-overlay .el-dialog, .el-overlay .el-drawer, .pdm-dialog-backdrop [role="dialog"]')]
  return dialogs.reverse().find(dialogIsOpen)
}

function showInDialog(dialog: HTMLElement, type: StatusType, message: string) {
  clearDialogStatus()
  const status = document.createElement('div')
  status.className = `pdm-dialog-status is-${type}`
  status.setAttribute('role', type === 'error' ? 'alert' : 'status')
  status.setAttribute('aria-live', type === 'error' ? 'assertive' : 'polite')
  status.setAttribute('aria-atomic', 'true')

  const label = document.createElement('strong')
  label.textContent = labels[type]
  const content = document.createElement('span')
  content.textContent = message
  const close = document.createElement('button')
  close.type = 'button'
  close.setAttribute('aria-label', '关闭提醒')
  close.textContent = '×'
  status.append(label, content, close)

  const body = [...dialog.children].find(child => child.classList.contains('el-dialog__body') || child.classList.contains('el-drawer__body'))
  const header = [...dialog.children].find(child => child.matches('header, .el-dialog__header, .el-drawer__header'))
  const inlineHost = dialog.matches('.pdm-message-drawer')
    ? dialog.querySelector<HTMLElement>('[data-pdm-dialog-status-host]') : null
  if (inlineHost) {
    status.classList.add('is-inline')
    inlineHost.append(status)
  } else dialog.insertBefore(status, body ?? header?.nextSibling ?? dialog.firstChild)
  dialogStatus = status
  dialogObserver = new MutationObserver(() => {
    if (!dialogIsOpen(dialog)) clearDialogStatus()
  })
  for (let node: HTMLElement | null = dialog; node; node = node.parentElement) {
    dialogObserver.observe(node, { attributes: true, attributeFilter: ['style', 'class', 'aria-hidden', 'hidden'], childList: true })
  }
  close.addEventListener('click', clearDialogStatus, { once: true })
  return { close: () => { if (dialogStatus === status) clearDialogStatus(); else status.remove() } }
}

export function clearGlobalStatus() {
  currentStatus.value = undefined
  clearDialogStatus()
}

export function registerStatusHost() {
  hostCount += 1
  return () => {
    hostCount -= 1
    if (!hostCount) currentStatus.value = undefined
  }
}

function show(type: StatusType, message: string, pageScope = false) {
  if (type === 'error' || type === 'warning') {
    const translated = userErrorMessage(message)
    if (translated !== message) console.error('操作异常原文：', message)
    message = translated
  }
  const dialog = pageScope ? undefined : activeDialog()
  if (dialog) {
    currentStatus.value = undefined
    return showInDialog(dialog, type, message)
  }
  clearDialogStatus()
  // Login and standalone review windows have no application header.
  if (!hostCount) return toastMessage[type](message)
  const id = ++sequence
  currentStatus.value = { id, type, message }
  return { close: () => { if (currentStatus.value?.id === id) currentStatus.value = undefined } }
}

export const ElMessage = {
  success: (message: string, options?: { scope: 'page' }) => show('success', message, options?.scope === 'page'),
  warning: (message: string) => show('warning', message),
  error: (message: string) => show('error', message),
  info: (message: string) => show('info', message),
}
