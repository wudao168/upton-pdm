import { ElMessage } from './statusMessage'

/** Clipboard API on HTTPS, selected-text fallback for intranet HTTP and embedded clients. */
export async function copyText(value: string | null | undefined, label: string) {
  if (!value) return
  let copied = false
  try {
    if (navigator.clipboard?.writeText) {
      try { await navigator.clipboard.writeText(value); copied = true } catch { /* Continue with the HTTP-compatible fallback. */ }
    }
    if (!copied) {
      const previous = document.activeElement as HTMLElement | null
      const field = document.createElement('textarea')
      field.value = value
      field.readOnly = true
      field.style.cssText = 'position:fixed;left:0;top:0;opacity:0;pointer-events:none'
      field.setAttribute('aria-hidden', 'true')
      document.body.append(field)
      try { field.focus({ preventScroll: true }); field.select(); copied = Boolean(document.execCommand?.('copy')) }
      finally { field.remove(); previous?.focus({ preventScroll: true }) }
    }
  } catch { copied = false }
  if (copied) ElMessage.success(`已复制${label}`)
  else ElMessage.warning('自动复制未成功，请选中内容后按 Ctrl+C 复制。')
}
