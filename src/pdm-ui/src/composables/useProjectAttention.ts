import { onBeforeUnmount, ref, watch } from 'vue'
import { listReleasePackages, readProjectWorkbench } from '../api'
import type { WorkbenchData, WorkbenchProject } from '../projectWorkbench'
import type { ReleasePackageSummary } from '../types'
import { chinaToday } from '../projectCalendar'
import { projectAttentionDetails } from '../projectAttention'

const families = new Map<string, { until: number; pending: Promise<WorkbenchData> }>()
const releaseCache = new Map<string, { until: number; pending: Promise<ReleasePackageSummary[]> }>()
let active = 0
const queue: Array<() => void> = []
export function invalidateProjectAttention(token: string) {
  for (const key of families.keys()) if (key.startsWith(`${token}:`)) families.delete(key)
  for (const key of releaseCache.keys()) if (key.startsWith(`${token}:`)) releaseCache.delete(key)
}
async function limited<T>(read: () => Promise<T>) {
  if (active >= 2) await new Promise<void>(resolve => queue.push(resolve))
  else active++
  try { return await read() } finally { const next = queue.shift(); if (next) next(); else active-- }
}
export function readProjectAttentionFamily(id: string, token: string) {
  const key = `${token}:${id}`, cached = families.get(key)
  if (cached && cached.until > Date.now()) return cached.pending
  const pending = limited(() => readProjectWorkbench('', token, 'company', { projectId: id }))
  families.set(key, { until: Infinity, pending })
  pending.then(() => { const entry = families.get(key); if (entry?.pending === pending) entry.until = Date.now() + 60000 }, () => {})
  pending.catch(() => { if (families.get(key)?.pending === pending) families.delete(key) })
  return pending
}
function readReleases(id: string, token: string) {
  const key = `${token}:${id}`, cached = releaseCache.get(key)
  if (cached && cached.until > Date.now()) return cached.pending
  const pending = limited(() => listReleasePackages(id, token))
  releaseCache.set(key, { until: Infinity, pending })
  pending.then(() => { const entry = releaseCache.get(key); if (entry?.pending === pending) entry.until = Date.now() + 60000 }, () => {})
  pending.catch(() => { if (releaseCache.get(key)?.pending === pending) releaseCache.delete(key) })
  return pending
}

export function useProjectAttention(token: () => string | undefined, ids: () => string[], enabled: () => boolean = () => true) {
  const entries = ref<Record<string, WorkbenchProject>>({}), releases = ref<Record<string, ReleasePackageSummary[]>>({})
  const loading = ref(false), error = ref(''), today = ref(chinaToday())
  let generation = 0
  async function refresh() {
    const currentToken = token(), projectIds = [...new Set(ids())]
    if (!currentToken || !enabled() || !projectIds.length) return
    const run = ++generation
    loading.value = true; error.value = ''; today.value = chinaToday()
    await Promise.all(projectIds.map(async id => {
      try {
        const family = await readProjectAttentionFamily(id, currentToken)
        if (run !== generation) return
        for (const item of family.projects) entries.value[item.project.id] = item
        await Promise.all(family.projects.filter(item => item.project.hasReleaseAttention).map(async item => {
          const packages = await readReleases(item.project.id, currentToken)
          if (run === generation) releases.value[item.project.id] = packages
        }))
      } catch { if (run === generation) error.value = '部分项目状态暂时无法读取，请刷新后重试。' }
    }))
    if (run === generation) loading.value = false
  }
  watch([token, () => ids().join('|'), enabled], () => { generation++; entries.value = {}; releases.value = {}; void refresh() }, { immediate: true })
  const timer = window.setInterval(() => { void refresh() }, 60000)
  onBeforeUnmount(() => { generation++; window.clearInterval(timer) })
  function details(id: string, fallback?: WorkbenchProject, aggregate = false) {
    const selected = entries.value[id] ?? fallback
    const family = aggregate ? Object.values(entries.value).filter(item => item.project.id === id || item.project.rootProjectId === id || item.project.parentProjectId === id) : []
    const items = family.length ? family : selected ? [selected] : []
    return items.flatMap(item => projectAttentionDetails(item, today.value, releases.value[item.project.id]).map(detail => ({ ...detail, reason: aggregate && item.project.id !== id ? `${item.project.code}：${detail.reason}` : detail.reason })))
  }
  const reasons = (id: string, fallback?: WorkbenchProject, aggregate = false) => details(id, fallback, aggregate).map(detail => detail.reason)
  return { entries, releases, loading, error, today, reasons, details, refresh }
}
