import { reactive } from 'vue'
import { postDesktopMessage } from './api'

// 客户端设计树缩略图：复用客户端“本地轻量预览”的本地只读缓存缩略图（浏览器端没有本地缓存，保持原图标）。
const thumbnails = reactive(new Map<string, string>())
const requested = new Set<string>()
let listening = false

function ensureListener() {
  if (listening || typeof window === 'undefined') return
  listening = true
  window.addEventListener('pdm-tree-thumbnail-status', event => {
    const detail = (event as CustomEvent<{ documentId?: string; dataUrl?: string }>).detail
    if (!detail?.documentId || !detail.dataUrl) return
    thumbnails.set(detail.documentId, detail.dataUrl)
  })
}

export function requestDocumentThumbnail(documentId?: string | null) {
  if (!documentId || requested.has(documentId)) return
  requested.add(documentId)
  ensureListener()
  postDesktopMessage('tree-thumbnail-request', { documentId })
}

export function documentThumbnail(documentId?: string | null) {
  return documentId ? thumbnails.get(documentId) : undefined
}
