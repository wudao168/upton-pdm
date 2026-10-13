import type { App } from 'vue'

const loadedUi = () => Array.from(document.querySelectorAll<HTMLScriptElement>('script[type="module"][src]'))
  .map(script => script.src.split('/').pop()?.split('?')[0]).join(',').slice(0, 160)

export function recordClientDiagnostic(kind: 'runtime' | 'error', operation: string, value: unknown, requestId = '') {
  // The native host persists, redacts and uploads. Ordinary browsers do not collect local telemetry.
  if (!window.chrome?.webview) return
  try {
    const message = value instanceof Error ? value.stack || value.message : String(value)
    window.chrome.webview.postMessage({ type: 'client-diagnostic', payload: { kind, operation, message: message.slice(0, 32768), requestId, uiVersion: loadedUi() } })
  } catch { /* Logging cannot interfere with the application. */ }
}

export function installClientDiagnostics(app: App) {
  if (!window.chrome?.webview) return
  const previous = app.config.errorHandler
  app.config.errorHandler = (error, instance, info) => {
    recordClientDiagnostic('error', `网页组件/${info}`, error)
    previous?.(error, instance, info)
  }
  window.addEventListener('error', event => recordClientDiagnostic('error', '网页异常', event.error || event.message))
  window.addEventListener('unhandledrejection', event => recordClientDiagnostic('error', '网页后台任务异常', event.reason))
  recordClientDiagnostic('runtime', '界面加载', `已加载${location.pathname.includes('review-overlay') ? '审核浮层' : '主界面'}`)
}

export function diagnosticRequestId(): string {
  // getRandomValues also works on LAN HTTP; randomUUID is restricted to secure contexts.
  const bytes = new Uint8Array(16)
  globalThis.crypto.getRandomValues(bytes)
  return Array.from(bytes, byte => byte.toString(16).padStart(2, '0')).join('')
}
