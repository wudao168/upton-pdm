import { computed, onBeforeUnmount, ref, watch } from 'vue'
import type { ProjectSummary } from './types'

type Context = { project: ProjectSummary; currentUsername?: string; token?: string; activeTab: string }
type Update = { sourceVersion?: string; needsHandling?: boolean; fingerprint: string; updatedAt?: string; actor?: string }
export function procurementContent(value: { items?: unknown[] }) { return value.items ?? [] }
export function fingerprint(value: unknown) {
  const text = JSON.stringify(value)
  let hash = 2166136261
  for (let i = 0; i < text.length; i++) hash = Math.imul(hash ^ text.charCodeAt(i), 16777619)
  return `${text.length}:${hash >>> 0}`
}
function metadata(value: unknown): Omit<Update, 'fingerprint'> {
  let result: Omit<Update, 'fingerprint'> = {}
  function visit(item: unknown) {
    if (!item || typeof item !== 'object') return
    const row = item as Record<string, unknown>
    const time = [row.updatedAt, row.occurredAt, row.createdAt, row.publishedAt].filter((v): v is string => typeof v === 'string' && !Number.isNaN(Date.parse(v))).sort().at(-1)
    if (time && (!result.updatedAt || time > result.updatedAt)) result = { updatedAt: time, actor: typeof row.updatedBy === 'string' ? row.updatedBy : typeof row.actor === 'string' ? row.actor : typeof row.createdBy === 'string' ? row.createdBy : undefined }
    Object.values(row).forEach(visit)
  }
  visit(value)
  return result
}
type Row = Record<string, any>
export function bomNeedsHandling(current: Row[][], versions: Row[], packages: Row[]) {
  const fields = ['drawingNumber', 'name', 'quantity', 'unit', 'material', 'specification', 'remark', 'brand', 'surfaceTreatment', 'heatTreatment', 'weight', 'revision', 'materialCode', 'materialId', 'isWearPart', 'impactStage']
  const normalize = (items: Row[]) => items.filter(item => !item.isManuallyExcluded && !item.isReleaseExcluded).map(item => JSON.stringify(fields.map(key => item[key] ?? null))).sort()
  return current.some((items, index) => {
    if (index === 3) return items.some(item => !item.isManuallyExcluded && !item.isReleaseExcluded)
    if (items.some(item => item.isPendingRemoval || item.isPendingClassification)) return true
    const versionKey = ['standardBomVersionId', 'nonStandardBomVersionId', 'electricalBomVersionId'][index]!
    const published = packages.filter(pkg => pkg.publishedAt && pkg[versionKey]).sort((a,b) => String(b.publishedAt).localeCompare(String(a.publishedAt)))[0]
    const baseline = published && versions.find(version => version.id === published[versionKey] && (version.state === 'Released' || version.state === 2))
    return JSON.stringify(normalize(items)) !== JSON.stringify(normalize(baseline?.items ?? []))
  })
}
export function needsHandling(tab: string, data: any[], username?: string) {
  if (tab === 'opl') return (data[0]?.rows ?? []).some((row: Row) => !['已完成', '已作废'].includes(row.issue.status))
  if (tab === 'project-plan') return (data[0]?.tasks ?? []).some((task: Row) => task.assignee === username && task.status !== 'Completed')
  if (tab === 'release') return (data[0] ?? []).some((pkg: Row) => !pkg.publishedAt && ['Draft', 'ProcessReview', 'Approval', 'Publishing', 'PublishFailed', 0, 1, 2, 4, 6].includes(pkg.state))
  if (tab === 'validation-plan') return (data[0]?.approvalTasks ?? []).some((task: Row) => task.assignee === username && !task.decidedAt)
  return false
}
export function useProjectTabUpdates(context: Context) {
  const latest = ref<Record<string, Update>>({}), seen = ref<Record<string, string>>({})
  let controller: AbortController | undefined, generation = 0, running = false

  const apiBase = (import.meta.env.VITE_PDM_API_BASE ?? '').replace(/\/$/, '')
  const storageKey = () => `upton-pdm-tab-seen:${JSON.stringify([context.currentUsername, context.project.id])}`
  function persist() { try { localStorage.setItem(storageKey(), JSON.stringify({ seen: seen.value, latest: latest.value })) } catch { /* 存储不可用时保留本次会话的已查看状态。 */ } }
  function markSeen(tab: string) { const value = latest.value[tab]; if (value) { seen.value[tab] = value.fingerprint; persist() } }
  const pending = computed(() => Object.fromEntries(Object.entries(latest.value).filter(([tab, update]) => update.needsHandling || seen.value[tab] !== update.fingerprint)))
  async function refresh() {
    if (running || !context.token || !context.currentUsername || document.visibilityState === 'hidden') return
    const current = generation, project = context.project, token = context.token
    running = true
    controller = new AbortController()
    const currentController = controller, signal = controller.signal, timeout = setTimeout(() => currentController.abort(), 20000)
    const base = `/api/projects/${project.id}`
    const routes: Record<string, string[]> = {
      information: [base], records: [`${base}/audit?take=200`],
      ...(project.canReadContent ? {
        files: [`${base}/folders`, `${base}/files?includeDeleted=true`],
        'project-plan': [`${base}/plan`], opl: [`${base}/opl`], budget: [`${base}/budget`],
        'validation-plan': [`${base}/validation-plan`],
        documents: [`${base}/documents`, `${base}/folder-documents`, `${base}/document-relations`],
        bom: ['Standard', 'NonStandard', 'Electrical', 'Unclassified'].map(kind => `${base}/boms/${kind}`),
        release: [`${base}/release-packages`], procurement: [`${base}/procurement-tracking`],
      } : {}),
    }
    async function read(url: string) {
      const headers = new Headers({ Authorization: `Bearer ${token}` })
      const companyId = localStorage.getItem('pdm_active_organization')
      if (companyId) headers.set('X-Company-Id', companyId)
      const response = await fetch(`${apiBase}${url}`, { headers, signal })
      if (!response.ok) throw new Error('更新状态暂不可用')
      return response.json()
    }
    try {
      const entries = Object.entries(routes)
      // 限制并发，避免打开项目时集中占用服务端连接。
      for (let start = 0; start < entries.length; start += 3) {
        await Promise.all(entries.slice(start, start + 3).map(async ([tab, urls]) => {
          try {
            const data = await Promise.all(urls.map(read))
            if (tab === 'validation-plan' && data[0]?.id) data.push(await read(`/api/validation-plans/${data[0].id}/execution-records`))
            if (tab === 'opl' && project.parentProjectId && data[0]?.rows) data[0] = { ...data[0], rows: data[0].rows.filter((row: { issue: { relatedProjectId?: string } }) => row.issue.relatedProjectId === project.id) }
            let work = needsHandling(tab, data, context.currentUsername)
            if (tab === 'bom') {
              const [versions, packages] = await Promise.all([read(`${base}/bom-versions`), read(`${base}/release-packages`)])
              work = bomNeedsHandling(data, versions ?? [], packages ?? [])
            }
            if (current !== generation) return
            const businessData = tab === 'procurement' ? procurementContent(data[0] ?? {}) : data
            const migration = tab === 'procurement' && latest.value[tab]?.sourceVersion !== 'items-v1'
            const update = { fingerprint: fingerprint(businessData), sourceVersion: tab === 'procurement' ? 'items-v1' : undefined, needsHandling: work, ...metadata(businessData) }
            const wasHandling = latest.value[tab]?.needsHandling
            latest.value[tab] = update

            if (migration || (tab === 'procurement' && (data[0]?.items?.length ?? 0) === 0) || !seen.value[tab] || (wasHandling && !work) || (tab === 'bom' && !work)) seen.value[tab] = update.fingerprint
            persist()
          } catch { /* 检测失败不覆盖上次结果，也不制造更新提醒。 */ }
        }))
      }
      if (current === generation) {
        const overview = ['information', 'project-plan', 'opl', 'bom', 'release', 'procurement', 'validation-plan'].map(tab => latest.value[tab]).filter(Boolean)
        if (overview.length) {
          const wasHandling = latest.value.overview?.needsHandling
          const work = overview.some(update => update?.needsHandling)
          latest.value.overview = { fingerprint: fingerprint(overview), needsHandling: work, ...metadata(overview) }

          if (!seen.value.overview || (wasHandling && !work)) seen.value.overview = latest.value.overview.fingerprint
          persist()
        }
      }
    } finally { clearTimeout(timeout); if (current === generation) running = false }
  }
  watch(() => [context.project.id, context.token, context.currentUsername], () => {
    generation++; controller?.abort(); running = false; latest.value = {}
    try { const stored = JSON.parse(localStorage.getItem(storageKey()) || '{}'); seen.value = stored.seen ?? stored; latest.value = stored.latest ?? {} } catch { seen.value = {} }
    void refresh()
  }, { immediate: true })
  function viewed(event: Event) {
    const detail = (event as CustomEvent<{ projectId: string; tab: string }>).detail
    if (detail?.projectId === context.project.id) markSeen(detail.tab)
  }
  window.addEventListener('pdm-tab-content-viewed', viewed)
  const timer = setInterval(() => { void refresh() }, 60000)
  function focus() { void refresh() }
  window.addEventListener('focus', focus)
  onBeforeUnmount(() => { generation++; controller?.abort(); clearInterval(timer); window.removeEventListener('pdm-tab-content-viewed', viewed); window.removeEventListener('focus', focus) })
  return { pending, markSeen }
}