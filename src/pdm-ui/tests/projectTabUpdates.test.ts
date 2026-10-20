import { afterEach, expect, it, vi } from 'vitest'
import { defineComponent, reactive } from 'vue'
import { flushPromises, mount } from '@vue/test-utils'
import { useProjectTabUpdates, projectTabSnapshot, hasProjectTabUpdate, projectUpdateTabs } from '../src/projectTabUpdates'
import type { ProjectSummary } from '../src/types'

const project = { id: 'p1', canReadContent: true } as ProjectSummary
const snapshot = (tab: string, ...data: any[]) => projectTabSnapshot(tab, data, project)
const changed = (tab: string, before: any[], after: any[]) => hasProjectTabUpdate(snapshot(tab, ...before), snapshot(tab, ...after))
let wrapper: ReturnType<typeof mount>
let state: ReturnType<typeof useProjectTabUpdates>
const context = reactive({ project, currentUsername: 'user1', token: 'token', activeTab: 'information' })
let issues: any[] = [{ issue: { id: '1', status: '待处理', description: '原问题' } }], fail = false
function setup(clear = true) {
  if (clear) localStorage.clear()
  vi.useFakeTimers()
  Object.defineProperty(document, 'visibilityState', { configurable: true, value: 'visible' })
  vi.stubGlobal('fetch', vi.fn(async (url: string) => {
    if (url.endsWith('/opl')) {
      if (fail) throw Error('offline')
      return new Response(JSON.stringify({ rows: structuredClone(issues) }))
    }
    return new Response('null')
  }))
  wrapper = mount(defineComponent({ setup() { state = useProjectTabUpdates(context); return () => null } }))
}
async function refresh() { await vi.advanceTimersByTimeAsync(60000); await flushPromises() }
function viewed(tab = 'opl') { window.dispatchEvent(new CustomEvent('pdm-tab-content-viewed', { detail: { projectId: context.project.id, tab } })) }
afterEach(() => { wrapper?.unmount(); vi.useRealTimers(); vi.unstubAllGlobals(); issues = [{ issue: { id: '1', status: '待处理', description: '原问题' } }]; fail = false; context.currentUsername = 'user1'; context.activeTab = 'information'; context.project = project })

it('only monitors the nine requested tabs, never overview, procurement or records', () => {
  expect(projectUpdateTabs).toEqual(['information', 'files', 'project-plan', 'opl', 'budget', 'validation-plan', 'documents', 'bom', 'release'])
})
it('information detects business edits but ignores permissions and derived counts', () => {
  expect(changed('information', [{ name: '项目', canReadContent: true, documentCount: 1 }], [{ name: '项目', canReadContent: false, documentCount: 2 }])).toBe(false)
  expect(changed('information', [{ name: '原名' }], [{ name: '新名' }])).toBe(true)
  expect(changed('information', [{}, { information: { customerPhone: '1' } }], [{}, { information: { customerPhone: '2' } }])).toBe(true)
  expect(changed('information', [{}, {}, { nameplate: { weight: '1' } }], [{}, {}, { nameplate: { weight: '2' } }])).toBe(true)
})
it('files only notify for a new upload or uploaded version', () => {
  const file = { id: 'f1', fileName: '文件', currentVersion: { id: 'v1' } }
  expect(changed('files', [[file]], [[{ ...file, fileName: '新名称', folderId: '新目录', updatedAt: '2026-10-19' }]])).toBe(false)
  expect(changed('files', [[file]], [[]])).toBe(false)
  expect(changed('files', [[file]], [[{ ...file, currentVersion: { id: 'v2' } }]])).toBe(true)
  expect(changed('files', [[file]], [[file, { id: 'f2', currentVersion: { id: 'v1' } }]])).toBe(true)
})
it('plan creation, changes and completion notify, forecast and metadata do not', () => {
  const plan = { id: 'plan1', rowVersion: 1, forecastFinish: '2026-10-19', tasks: [{ id: 't1', status: 'InProgress', plannedFinish: '2026-10-19' }] }
  expect(changed('project-plan', [null], [plan])).toBe(true)
  expect(changed('project-plan', [plan], [{ ...plan, rowVersion: 2, forecastFinish: '2026-10-20' }])).toBe(false)
  expect(changed('project-plan', [plan], [{ ...plan, tasks: [{ ...plan.tasks[0], status: 'Completed' }] }])).toBe(true)
  expect(changed('project-plan', [plan], [{ ...plan, tasks: [{ ...plan.tasks[0], plannedFinish: '2026-10-20' }] }])).toBe(true)
})
it('OPL only notifies for added issues, text or attachments', () => {
  const issue = { id: 'i1', description: '原内容', status: '待处理', attachments: [{ id: 'a1' }] }
  const row = (value: any) => [{ rows: [{ issue: value }] }]
  expect(changed('opl', row(issue), row({ ...issue, status: '已完成', owner: '新负责人', rowVersion: 8 }))).toBe(false)
  expect(changed('opl', row(issue), row({ ...issue, solution: '补充解决方案' }))).toBe(true)
  expect(changed('opl', row(issue), row({ ...issue, attachments: [...issue.attachments, { id: 'a2' }] }))).toBe(true)
  expect(changed('opl', row(issue), [{ rows: [{ issue }, { issue: { id: 'i2' } }] }])).toBe(true)
  expect(changed('opl', row(issue), [{ rows: [] }])).toBe(false)
})
it.each(['budget', 'validation-plan'])('%s detects content but not row versions, permissions or sorting', tab => {
  const before = { rows: [{ id: '1', amount: 1 }, { id: '2', amount: 2 }], rowVersion: 1, canEdit: true }
  expect(changed(tab, [before], [{ ...before, rows: [...before.rows].reverse(), rowVersion: 2, canEdit: false, updatedAt: '2026-10-19' }])).toBe(false)
  expect(changed(tab, [before], [{ ...before, rows: [{ id: '1', amount: 3 }, before.rows[1]] }])).toBe(true)
})
it('drawing notifications require a revision change on an existing drawing', () => {
  const doc = { id: 'd1', revision: 'W1', storedVersionCount: 1 }
  expect(changed('documents', [[]], [[doc]])).toBe(false)
  expect(changed('documents', [[doc]], [[{ ...doc, checkedOutBy: 'user', rowVersion: 8, storedVersionCount: 2 }]])).toBe(false)
  expect(changed('documents', [[doc]], [[{ ...doc, revision: 'W2' }]])).toBe(true)
})
it('BOM changes notify, transient row IDs, order and checkout metadata do not', () => {
  const rows = [{ id: '1', drawingNumber: 'A', quantity: 1 }, { id: '2', drawingNumber: 'B', quantity: 2 }]
  expect(changed('bom', [rows], [[{ ...rows[1], id: 'new', checkedOutBy: 'user' }, rows[0]]])).toBe(false)
  expect(changed('bom', [rows], [[{ ...rows[0], quantity: 2 }, rows[1]]])).toBe(true)
  expect(changed('bom', [rows], [[rows[0]]])).toBe(true)
})
it('release notifies new packages and publication but ignores approval/preview status', () => {
  const pkg = { id: 'r1', state: 'Draft' }
  expect(changed('release', [[pkg]], [[{ ...pkg, state: 'Approval', previewStatus: 'Queued' }]])).toBe(false)
  expect(changed('release', [[pkg]], [[pkg, { id: 'r2' }]])).toBe(true)
  expect(changed('release', [[pkg]], [[{ ...pkg, publishedAt: '2026-10-19T10:00:00Z' }]])).toBe(true)
})
it('baselines unfinished work, keeps real updates until viewed, and re-notifies', async () => {
  setup(); await flushPromises(); expect(Object.keys(state.pending.value)).toHaveLength(0)
  issues.push({ issue: { id: '2', status: '待处理', createdBy: 'engineer', createdAt: '2026-10-19T01:00:00Z' } }); await refresh()
  expect(state.pending.value.opl?.actor).toBe('engineer')
  context.activeTab = 'opl'; await flushPromises(); expect(state.pending.value.opl).toBeDefined()
  viewed(); await flushPromises(); expect(state.pending.value.opl).toBeUndefined()
  await refresh(); expect(state.pending.value.opl).toBeUndefined()
  issues.push({ issue: { id: '3', status: '待处理' } }); await refresh(); expect(state.pending.value.opl).toBeDefined()
})
it('persists unread updates and isolates users and projects', async () => {
  setup(); await flushPromises(); issues.push({ issue: { id: '2' } }); await refresh(); wrapper.unmount()
  setup(false); await flushPromises(); expect(state.pending.value.opl).toBeDefined()
  context.currentUsername = 'user2'; await flushPromises(); expect(state.pending.value.opl).toBeUndefined()
  context.currentUsername = 'user1'; await flushPromises(); expect(state.pending.value.opl).toBeDefined()
  context.project = { ...project, id: 'p2' }; await flushPromises(); expect(state.pending.value.opl).toBeUndefined()
})
it('failure neither invents nor clears unread updates, wrong tab views cannot acknowledge', async () => {
  setup(); await flushPromises(); fail = true; await refresh(); expect(state.pending.value.opl).toBeUndefined()
  fail = false; issues.push({ issue: { id: '2' } }); await refresh(); expect(state.pending.value.opl).toBeDefined()
  viewed(); await flushPromises(); expect(state.pending.value.opl).toBeDefined()
  context.activeTab = 'opl'; fail = true; viewed(); await flushPromises(); expect(state.pending.value.opl).toBeDefined()
  fail = false; viewed(); await flushPromises(); expect(state.pending.value.opl).toBeUndefined()
})
it('migrates old permanent handling dots to a clean baseline', async () => {
  localStorage.clear(); localStorage.setItem('upton-pdm-tab-seen:["user1","p1"]', JSON.stringify({ latest: { overview: { needsHandling: true }, opl: { needsHandling: true } } }))
  setup(false); await flushPromises(); expect(Object.keys(state.pending.value)).toHaveLength(0)
})
it('does not re-notify when a known deleted file is restored', async () => {
  let files = [{ id: 'f1', currentVersion: { id: 'v1' } }]
  setup(); vi.mocked(fetch).mockImplementation(async (url: any) => new Response(JSON.stringify(url.includes('/files?') ? files : null)))
  await flushPromises(); await refresh(); context.activeTab = 'files'; await state.markSeen('files'); files = []; await refresh(); files = [{ id: 'f1', currentVersion: { id: 'v1' } }]; await refresh()
  expect(state.pending.value.files).toBeUndefined()
})
it('baselines HTTP 204 plans and detects first creation', async () => {
  let plan: any = null
  setup(); vi.mocked(fetch).mockImplementation(async (url: any) => url.endsWith('/plan') && !plan ? new Response(null, { status: 204 }) : new Response(JSON.stringify(url.endsWith('/plan') ? plan : null)))
  await flushPromises(); await refresh(); expect(state.pending.value['project-plan']).toBeUndefined()
  plan = { id: 'plan1', tasks: [] }; await refresh(); expect(state.pending.value['project-plan']).toBeDefined()
})
it('an older background read cannot re-light a successfully viewed update', async () => {
  setup(); await flushPromises(); issues.push({ issue: { id: '2' } }); await refresh(); context.activeTab = 'opl'
  const original = vi.mocked(fetch).getMockImplementation()!
  let finish: (() => void) | undefined
  vi.mocked(fetch).mockImplementationOnce(original).mockImplementation(original)
  vi.mocked(fetch).mockImplementation(async (url: any, options?: any) => {
    if (url.endsWith('/opl') && !finish) {
      const rows = structuredClone(issues)
      return new Promise<Response>(resolve => { finish = () => resolve(new Response(JSON.stringify({ rows }))) })
    }
    return original(url, options)
  })
  window.dispatchEvent(new Event('focus')); await flushPromises()
  await state.markSeen('opl'); expect(state.pending.value.opl).toBeUndefined()
  finish?.(); await flushPromises(); expect(state.pending.value.opl).toBeUndefined()
})

it('baselines empty HTTP 200 responses as absent content', async () => {
  setup(); vi.mocked(fetch).mockImplementation(async () => new Response(''))
  await flushPromises(); await refresh();
  const saved = JSON.parse(localStorage.getItem('upton-pdm-tab-seen:["user1","p1"]')!)
  expect(Object.keys(saved.latest)).toHaveLength(9)
  expect(Object.keys(state.pending.value)).toHaveLength(0)
})
it.each(projectUpdateTabs)('clears the %s update after a successful view', async tab => {
  localStorage.clear()
  const latest = Object.fromEntries(projectUpdateTabs.map(key => [key, { ...snapshot(key, null), unread: true }]))
  localStorage.setItem('upton-pdm-tab-seen:["user1","p1"]', JSON.stringify({ schemaVersion: 2, latest }))
  setup(false); await flushPromises(); context.activeTab = tab
  await state.markSeen(tab)
  expect(state.pending.value[tab]).toBeUndefined()
  expect(Object.keys(state.pending.value)).toHaveLength(8)
})
