import { PdmApiError, readUserTablePreferences, saveUserTablePreference } from './api'
import { recordClientDiagnostic } from './clientDiagnostics'
import { ref } from 'vue'

interface PreferenceSession {
  username: string
  company: string
  token: string
  cacheKey: string
  values: Record<string, string>
  writes: Map<string, Promise<void>>
  changes: number
  refreshing?: Promise<void>
}
let session: PreferenceSession | undefined
export const tablePreferenceVersion = ref(0)

function localRead(key: string): string | null {
  try { return window.localStorage.getItem(key) } catch { return null }
}
function persistCache(context: PreferenceSession) {
  try { window.localStorage.setItem(context.cacheKey, JSON.stringify(context.values)) } catch { /* 服务端保存仍可继续。 */ }
}
function ownedKey(key: string, username: string, company: string): boolean {
  if (key === `upton-pdm:procurement-columns:${username}` || key === `upton-pdm:production-drawing-columns:${username}`
    || key.startsWith(`upton-pdm:bom-columns:${username}:`)) return true
  if (!key.startsWith('upton-pdm-column-widths:v1:')) return false
  try {
    const scope = JSON.parse(key.slice('upton-pdm-column-widths:v1:'.length))[0] as string[]
    return scope[0]?.toLowerCase() === username.toLowerCase() && scope[1] === company
  } catch { return false }
}

export async function initializeTablePreferences(username: string, company: string, token: string) {
  if (session?.username === username && session.company === company) { session.token = token; return }
  const cacheKey = `upton-pdm:table-preferences:v1:${JSON.stringify([username, company])}`
  let cached: Record<string, string> = {}
  try { cached = JSON.parse(localRead(cacheKey) ?? '{}') ?? {} } catch { /* 使用服务端设置。 */ }
  const context: PreferenceSession = { username, company, token, cacheKey, values: {}, writes: new Map(), changes: 0 }
  for (const [key, value] of Object.entries(cached)) if (typeof value === 'string' && ownedKey(key, username, company)) context.values[key] = value
  // 迁移旧版同一账号的列设置，不读取登录信息或其他账号的设置。
  try {
    for (let index = 0; index < window.localStorage.length; index++) {
      const key = window.localStorage.key(index)
      if (key && ownedKey(key, username, company) && !(key in context.values)) {
        const value = localRead(key)
        if (value !== null) context.values[key] = value
      }
    }
  } catch { /* 本地存储不可用时仍读取服务端。 */ }
  session = context
  try {
    const result = await readUserTablePreferences(token, company)
    if (session !== context) return
    const remote = result.preferences ?? {}
    const migrating = Object.entries(context.values).filter(([key]) => !(key in remote))
    context.values = { ...context.values, ...remote }
    persistCache(context)
    for (const [key, value] of migrating) {
      void writeTablePreference(key, value).catch(() => recordClientDiagnostic('runtime', '列设置迁移', '本机列设置尚未同步，可打开列设置后重新保存。'))
    }
  } catch {
    recordClientDiagnostic('runtime', '列设置读取', '账号列设置暂未同步，使用本机缓存；保存时将重新同步。')
  }
  if (session === context) tablePreferenceVersion.value++
}

export async function refreshTablePreferences(): Promise<void> {
  const context = session
  if (!context) return
  if (context.writes.size) return
  if (context.refreshing) return context.refreshing
  const changes = context.changes
  const pending = (async () => {
    try {
      const result = await readUserTablePreferences(context.token, context.company)
      if (session !== context || context.changes !== changes) return
      context.values = { ...context.values, ...result.preferences }
      persistCache(context)
      tablePreferenceVersion.value++
    } catch { /* 保留本机设置；保存时明确报告同步结果。 */ }
  })()
  context.refreshing = pending
  try { await pending }
  finally { if (context.refreshing === pending) context.refreshing = undefined }
}

export function clearTablePreferencesSession() { session = undefined }

export function readTablePreference(key: string): string | null {
  return session ? session.values[key] ?? null : localRead(key)
}

export async function writeTablePreference(key: string, value: string): Promise<boolean> {
  const context = session
  if (!context) { window.localStorage.setItem(key, value); return false }
  if (!ownedKey(key, context.username, context.company)) throw new Error('账号已切换，请重新打开当前账号的列设置。')
  context.values[key] = value
  context.changes++
  persistCache(context)
  tablePreferenceVersion.value++
  const previous = context.writes.get(key) ?? Promise.resolve()
  const pending = previous.catch(() => {}).then(async () => {
    if (session !== context) throw new Error('账号已切换，请在当前账号重新保存列设置。')
    await saveUserTablePreference(key, value, context.token, context.company)
  })
  context.writes.set(key, pending)
  try {
    await pending
    if (session !== context) throw new Error('账号已切换，请在当前账号重新核对列设置。')
    return true
  }
  catch (error) {
    if (session !== context) throw new Error('账号已切换，请在当前账号重新核对列设置。')
    // 兼容界面先更新、API尚未更新的客户端，不能误报已经同步账号。
    if (error instanceof PdmApiError && (error.status === 404 || error.status === 405)) return false
    throw error
  }
  finally { if (context.writes.get(key) === pending) context.writes.delete(key) }
}
