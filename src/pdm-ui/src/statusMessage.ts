import { ElMessage as toastMessage } from 'element-plus'
import { readonly, shallowRef } from 'vue'

type StatusType = 'success' | 'warning' | 'error' | 'info'
const currentStatus = shallowRef<{ id: number; type: StatusType; message: string }>()
export const globalStatus = readonly(currentStatus)
let hostCount = 0
let sequence = 0

export function registerStatusHost() {
  hostCount += 1
  return () => {
    hostCount -= 1
    if (!hostCount) currentStatus.value = undefined
  }
}

function show(type: StatusType, message: string) {
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
