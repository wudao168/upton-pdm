import { ElMessage as toastMessage } from 'element-plus'
import { readonly, shallowRef } from 'vue'

type StatusType = 'success' | 'warning' | 'error' | 'info'
const currentStatus = shallowRef<{ id: number; type: StatusType; message: string }>()
export const globalStatus = readonly(currentStatus)
let hostCount = 0
let sequence = 0
let dialogStatus: HTMLElement | undefined

const labels: Record<StatusType, string> = { success: '成功', warning: '提醒', error: '失败', info: '提示' }

function clearDialogStatus() {
  dialogStatus?.remove()
  dialogStatus = undefined
}

function activeDialog() {
  if (typeof document === 'undefined') return undefined
  const dialogs = [...document.querySelectorAll<HTMLElement>('.el-overlay .el-dialog, .el-overlay .el-drawer, .pdm-dialog-backdrop [role="dialog"]')]
  return dialogs.reverse().find(dialog => {
    const overlay = dialog.closest<HTMLElement>('.el-overlay, .pdm-dialog-backdrop')
    return overlay
      && overlay.style.display !== 'none'
      && overlay.getAttribute('aria-hidden') !== 'true'
      && !overlay.classList.contains('dialog-fade-leave-active')
      && !overlay.classList.contains('el-drawer-fade-leave-active')
  })
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
  dialog.insertBefore(status, body ?? header?.nextSibling ?? dialog.firstChild)
  dialogStatus = status
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

function show(type: StatusType, message: string) {
  const dialog = activeDialog()
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
  success: (message: string) => show('success', message),
  warning: (message: string) => show('warning', message),
  error: (message: string) => show('error', message),
  info: (message: string) => show('info', message),
}
