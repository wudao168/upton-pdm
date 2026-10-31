import { computed, onBeforeUnmount, ref, watch } from 'vue'
import type { ProjectSummary } from './types'
import { useProjectCenterAccess } from './projectCenterAccess'

type Context = { project: ProjectSummary; currentUsername?: string; token?: string; activeTab: string }
type Row = Record<string, any>
type Snapshot = { fingerprint: string; tokens?: string[]; revisions?: Record<string, string>; updatedAt?: string; actor?: string }
type Update = Snapshot & { unread: boolean }
export const projectUpdateTabs = ['information', 'files', 'project-plan', 'opl', 'budget', 'validation-plan', 'documents', 'bom', 'release']
const pick = (row: Row | null | undefined, fields: string[]) => Object.fromEntries(fields.map(key => [key, row?.[key] ?? null]))
function canonical(value: any): any {
  if (Array.isArray(value)) return value.map(canonical)
  if (value && typeof value === 'object') return Object.fromEntries(Object.keys(value).sort().map(key => [key, canonical(value[key])]))
  return value
}
export function fingerprint(value: unknown) {
  const text = JSON.stringify(canonical(value))
  let hash = 2166136261
  for (let i = 0; i < text.length; i++) hash = Math.imul(hash ^ text.charCodeAt(i), 16777619)
  return `${text.length}:${hash >>> 0}`
}
const sorted = (rows: any[] = []) => rows.map(canonical).sort((a, b) => JSON.stringify(a).localeCompare(JSON.stringify(b)))
// 只比较业务字段，权限、刷新时间及并发版本号不代表内容更新。
function business(value: any): any {
  if (Array.isArray(value)) return sorted(value.map(business))
  if (!value || typeof value !== 'object') return value
  return Object.fromEntries(Object.entries(value).filter(([key]) => !/^(can[A-Z]|rowVersion$|createdAt$|createdBy$|updatedAt$|updatedBy$|lastRefresh|lastSuccessfulRefresh|notification$)/.test(key)).map(([key, item]) => [key, business(item)]))
}
function metadata(value: unknown): Pick<Snapshot, 'updatedAt' | 'actor'> {
  let result: Pick<Snapshot, 'updatedAt' | 'actor'> = {}
  function visit(item: unknown) {
    if (!item || typeof item !== 'object') return
    const row = item as Row
    const time = [row.updatedAt, row.createdAt, row.publishedAt, row.uploadedAt].filter((v): v is string => typeof v === 'string' && !Number.isNaN(Date.parse(v))).sort().at(-1)
    if (time && (!result.updatedAt || time > result.updatedAt)) result = { updatedAt: time, actor: row.updatedBy ?? row.uploadedBy ?? row.createdBy }
    Object.values(row).forEach(visit)
  }
  visit(value)
  return result
}
export function projectTabSnapshot(tab: string, data: any[], project: ProjectSummary): Snapshot {
  const first = data[0]
  let content: unknown = null, tokens: string[] | undefined, revisions: Record<string, string> | undefined
  if (tab === 'information') content = [
    business(pick(first, ['name', 'owner', 'projectAlias', 'organizationId', 'organizationName', 'projectTypeCode', 'equipmentTypeCode', 'customerCode', 'customerName', 'customerProjectSequence', 'deviceModel', 'signedDate', 'isSpecialInitiation', 'deliveryDate', 'quantity', 'bomItemCategoryCode', 'serialNumbers', 'responsibleUsers', 'executionUnitId', 'executionUnitName', 'primaryProjectManager', 'collaborativeProjectManagers', 'designLead', 'designLeads', 'designers', 'phaseOwners'])),
    business(data[1]?.information), business(data[2]?.nameplate),
  ]
  if (tab === 'files') tokens = (first ?? []).filter((file: Row) => !file.deletedAt).map((file: Row) => `${file.id}:${file.currentVersion?.id ?? file.currentVersion?.versionNumber ?? file.createdAt}`)
  if (tab === 'project-plan') content = business(pick(first, ['id', 'templateId', 'plannedStart', 'plannedFinish', 'manualStage', 'manualStageReason', 'baselineVersion', 'tasks', 'stageSchedules', 'approvalStatus', 'approvalAssignee', 'submittedBy', 'submittedAt', 'approvedBy', 'approvedAt', 'approvalComment', 'changeRequest']))
  if (tab === 'opl') {
    const issues: Row[] = (first?.rows ?? []).map((row: Row) => row.issue).filter((issue: Row) => !project.parentProjectId || issue.relatedProjectId === project.id)
    tokens = issues.flatMap(issue => [
      `issue:${issue.id}`,
      ...['description', 'solution'].filter(key => issue[key]?.trim()).map(key => `${issue.id}:${key}:${fingerprint(issue[key])}`),
      ...(issue.attachments ?? []).map((file: Row) => `${issue.id}:attachment:${file.id}`),
    ])
  }
  if (tab === 'budget') content = business(data)
  if (tab === 'validation-plan') content = business(data)
  if (tab === 'documents') revisions = Object.fromEntries((first ?? []).filter((row: Row) => !row.deletedAt).map((row: Row) => [row.id, row.storedVersionCount === 0 ? '' : String(row.revision ?? '')]))
  if (tab === 'bom') content = data.map(rows => sorted((rows ?? []).map((row: Row) => pick(row, ['kind', 'sequence', 'drawingNumber', 'name', 'quantity', 'unit', 'material', 'specification', 'remark', 'brand', 'surfaceTreatment', 'heatTreatment', 'weight', 'revision', 'materialCode', 'materialId', 'isWearPart', 'impactStage', 'sourceDocumentId', 'sourceConfiguration', 'isManuallyExcluded', 'isReleaseExcluded', 'isPendingRemoval', 'isPendingClassification']))))
  if (tab === 'release') tokens = (first ?? []).flatMap((row: Row) => [`package:${row.id}`, ...(row.publishedAt ? [`published:${row.id}:${row.publishedAt}`] : [])])
  if (tokens) tokens = [...new Set(tokens)].sort()
  return { fingerprint: fingerprint(tokens ?? revisions ?? content), tokens, revisions, ...metadata(data) }
}
export function hasProjectTabUpdate(previous: Snapshot, next: Snapshot) {
  if (next.tokens) return next.tokens.some(token => !previous.tokens?.includes(token))
  if (next.revisions) return Object.entries(next.revisions).some(([id, revision]) => Boolean(revision && previous.revisions?.[id] && previous.revisions[id] !== revision))
  return previous.fingerprint !== next.fingerprint
}
export function useProjectTabUpdates(context: Context) {
  const projectAccess = useProjectCenterAccess()
  const latest = ref<Record<string, Update>>({})
  let generation = 0, running = false, serial = 0
  const controllers = new Set<AbortController>(), readOrder: Record<string, number> = {}
  const acknowledgements = new Map<string, Promise<void>>()
  const apiBase = (import.meta.env.VITE_PDM_API_BASE ?? '').replace(/\/$/, '')
  const storageKey = () => `upton-pdm-tab-seen:${JSON.stringify([context.currentUsername, context.project.id])}`
  function persist() { try { localStorage.setItem(storageKey(), JSON.stringify({ schemaVersion: 2, latest: latest.value })) } catch { /* 存储不可用时保留本次会话状态。 */ } }
  const pending = computed(() => Object.fromEntries(Object.entries(latest.value).filter(([tab, value]) => projectUpdateTabs.includes(tab) && value.unread)))
  function routes(project: ProjectSummary): Record<string, string[]> {
    if (!project.canReadContent) return {}
    const base = `/api/projects/${project.id}`
    return {
      information: [base, `${base}/contact-information`, `${base}/nameplate`],
      files: [`${base}/files?includeDeleted=true`], 'project-plan': [`${base}/plan`], opl: [`${base}/opl`],
      budget: [`${base}/budget`, `${base}/budget/assessments`], 'validation-plan': [`${base}/validation-plan`],
      documents: [`${base}/documents`], bom: ['Standard', 'NonStandard', 'Electrical', 'Unclassified'].map(kind => `${base}/boms/${kind}`),
      release: [`${base}/release-packages`],
    }
  }
  async function refreshTab(tab: string, acknowledge = false) {
    const urls = routes(context.project)[tab]
    if (!projectAccess.canViewTab(tab) || !urls || !context.token || !context.currentUsername) return
    const current = generation, project = context.project, order = ++serial
    const controller = new AbortController(), timeout = setTimeout(() => controller.abort(), 20000)
    controllers.add(controller)
    const headers = new Headers({ Authorization: `Bearer ${context.token}` })
    const companyId = localStorage.getItem('pdm_active_organization')
    if (companyId) headers.set('X-Company-Id', companyId)
    async function read(url: string) {
      const response = await fetch(`${apiBase}${url}`, { headers, signal: controller.signal })
      if (!response.ok) throw new Error('更新状态暂不可用')
      if (response.status === 204) return null
      const text = await response.text()
      return text.trim() ? JSON.parse(text) : null
    }
    try {
      const data = await Promise.all(urls.map(read))
      if (tab === 'validation-plan' && data[0]?.id) data.push(await read(`/api/validation-plans/${data[0].id}/execution-records`))
      if (current !== generation) return
      const next = projectTabSnapshot(tab, data, project), previous = latest.value[tab]
      if (order < (readOrder[tab] ?? 0)) {
        if (acknowledge && context.activeTab === tab && previous?.fingerprint === next.fingerprint) { previous.unread = false; persist() }
        return
      }
      readOrder[tab] = order
      const changed = Boolean(previous && hasProjectTabUpdate(previous, next))
      const viewed = acknowledge && context.activeTab === tab
      latest.value[tab] = {
        ...next,
        // 保留已知上传和图档编号，删除后恢复不会被误判成新上传或升版。
        tokens: next.tokens ? [...new Set([...(previous?.tokens ?? []), ...next.tokens])] : undefined,
        revisions: next.revisions ? { ...previous?.revisions, ...next.revisions } : undefined,
        unread: !viewed && Boolean(previous?.unread || changed),
        ...(!changed && previous?.unread ? { updatedAt: previous.updatedAt, actor: previous.actor } : {}),
      }
      persist()
    } catch { /* 读取失败不制造红点，也不清除尚未查看的更新。 */ }
    finally { clearTimeout(timeout); controllers.delete(controller) }
  }
  function markSeen(tab: string) {
    if (acknowledgements.has(tab)) return acknowledgements.get(tab)!
    const promise = refreshTab(tab, true).finally(() => { if (acknowledgements.get(tab) === promise) acknowledgements.delete(tab) })
    acknowledgements.set(tab, promise)
    return promise
  }
  async function refresh() {
    if (running || document.visibilityState === 'hidden') return
    const current = generation
    running = true
    try {
      const tabs = Object.keys(routes(context.project))
      for (let start = 0; start < tabs.length && current === generation; start += 3) await Promise.all(tabs.slice(start, start + 3).map(tab => refreshTab(tab)))
    } finally { if (current === generation) running = false }
  }
  watch(() => [context.project.id, context.token, context.currentUsername], () => {
    generation++; controllers.forEach(controller => controller.abort()); running = false; latest.value = {}; acknowledgements.clear()
    for (const tab of Object.keys(readOrder)) delete readOrder[tab]
    try {
      const stored = JSON.parse(localStorage.getItem(storageKey()) || '{}')
      if (stored.schemaVersion === 2) latest.value = stored.latest ?? {}
    } catch { /* 首次使用建立基线，旧版待办红点不沿用。 */ }
    void refresh()
  }, { immediate: true })
  function viewed(event: Event) {
    const detail = (event as CustomEvent<{ projectId: string; tab: string }>).detail
    if (detail?.projectId === context.project.id && detail.tab === context.activeTab) void markSeen(detail.tab)
  }
  function focus() { void refresh() }
  window.addEventListener('pdm-tab-content-viewed', viewed)
  window.addEventListener('focus', focus)
  const timer = setInterval(focus, 60000)
  onBeforeUnmount(() => { generation++; controllers.forEach(controller => controller.abort()); clearInterval(timer); window.removeEventListener('pdm-tab-content-viewed', viewed); window.removeEventListener('focus', focus) })
  return { pending, markSeen }
}
