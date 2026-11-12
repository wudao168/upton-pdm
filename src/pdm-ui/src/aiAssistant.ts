import { apiBase, authenticatedHeaders, requestJson } from './api'
export { productionRequestId as aiRequestId } from './productionTracking'

export interface AiModel { id: string; name: string; enabled: boolean; tools: boolean; contextTokens: number; outputTokens: number }
export interface AiProvider { id: string; name: string; baseUrl: string; network: 'intranet' | 'cloud'; adapter: 'compatible' | 'deepseek'; enabled: boolean; models: AiModel[]; hasKey?: boolean; apiKey?: string; clearKey?: boolean }
export interface AiSettings { revision: number; providers: AiProvider[]; defaultModel: string | null }
export interface AiChoice { key: string; provider: string; name: string; tools: boolean; network: string }
export interface AiBootstrap { canChat: boolean; canManage: boolean; canBusiness: boolean; defaultModel: string | null; models: AiChoice[] }
export interface AiSource { name: string; projectId: string | null; kind: string; permissions: string[] }
export interface AiMessage { id: string; role: string; text: string; status: string; createdAt: string; sources?: AiSource[]; inputTokens?: number; outputTokens?: number }
export interface AiConversation { id: string; title: string; model: string; business: boolean; revision: number; updatedAt: string; busyUntil?: string; messages: AiMessage[] }
export interface AiEvent { text?: string; message?: string; clear?: boolean; omittedHistory?: boolean; business?: boolean; status?: string; sources?: AiSource[]; inputTokens?: number; outputTokens?: number }
export const aiJson = <T>(path: string, token: string, method = 'GET', data?: unknown) => requestJson<T>(`/api/ai/${path}`, { method, ...(data === undefined ? {} : { body: JSON.stringify(data) }) }, token)
export const aiError = (error: unknown) => error instanceof Error && /[\u3400-\u9fff]/.test(error.message) ? error.message : 'AI服务暂时不可用，请检查连接后重试。'

export async function streamAi(id: string, data: unknown, token: string, signal: AbortSignal, receive: (event: string, data: AiEvent) => void) {
  const headers = authenticatedHeaders(token); headers.set('Content-Type', 'application/json'); headers.set('Accept', 'text/event-stream')
  const response = await fetch(`${apiBase}/api/ai/conversations/${id}/messages`, { method: 'POST', headers, body: JSON.stringify(data), signal, cache: 'no-store' })
  if (!response.ok) {
    const problem = await response.json().catch(() => ({}))
    if (response.status === 401) window.dispatchEvent(new CustomEvent('pdm-session-expired', { detail: { token, message: '登录已过期，请重新登录。' } }))
    throw new Error(aiError(new Error(problem.detail || problem.message || problem.title || '请求失败，请重新登录或检查权限。')))
  }
  if (!response.body) throw new Error('浏览器未收到流式回复，请刷新后重试。')
  const reader = response.body.getReader(), decoder = new TextDecoder(); let pending = '', complete = false
  try {
    for (;;) {
      const { done, value } = await reader.read()
      pending += decoder.decode(value, { stream: !done }).replace(/\r/g, '')
      let end: number
      while ((end = pending.indexOf('\n\n')) >= 0) {
        const frame = pending.slice(0, end); pending = pending.slice(end + 2)
        const lines = frame.split('\n'), event = lines.find(line => line.startsWith('event:'))?.slice(6).trim() ?? 'message'
        const json = lines.filter(line => line.startsWith('data:')).map(line => line.slice(5).trim()).join('\n')
        if (json) receive(event, JSON.parse(json) as AiEvent)
        if (event === 'done') complete = true
      }
      if (done) break
    }
    if (!complete) throw new Error('连接中断，回答可能未完成，请刷新会话核对后重试。')
  } finally { await reader.cancel().catch(() => {}); reader.releaseLock() }
}
