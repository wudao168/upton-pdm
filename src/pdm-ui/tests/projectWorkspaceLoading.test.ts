import { afterEach, beforeEach, expect, it, vi } from 'vitest'
import { listProjects, loadProjectWorkspace } from '../src/api'

const project = { id: 'project', code: 'P1', name: '按需加载', owner: 'admin', vaultLocation: '', releaseLocation: '', serialNumbers: [], designers: [], responsibleUsers: [], collaborativeProjectManagers: [], quantity: 1, canReadContent: true }
beforeEach(() => { localStorage.clear(); vi.stubGlobal('fetch', vi.fn(async () => new Response(JSON.stringify(project), { status: 200, headers: { 'Content-Type': 'application/json' } }))) })
afterEach(() => vi.unstubAllGlobals())

it('信息页只加载项目元数据，不提前读取图档、BOM或发布资料', async () => {
  const value = await loadProjectWorkspace('project', 'token', false, [], ['information'])
  expect(value.project.code).toBe('P1')
  expect(fetch).toHaveBeenCalledTimes(1)
  expect(String(vi.mocked(fetch).mock.calls[0]![0])).toMatch(/\/api\/projects\/project$/)
  expect(value.standardBom).toEqual([])
})

it('已有项目元数据时只补读所需模块', async () => {
  const initial = await loadProjectWorkspace('project', 'token', false, [], ['information'])
  vi.mocked(fetch).mockClear().mockResolvedValue(new Response('[]', { status: 200 }))
  await loadProjectWorkspace('project', 'token', false, [], ['files'], initial.project)
  expect(fetch).toHaveBeenCalledTimes(1)
  expect(String(vi.mocked(fetch).mock.calls[0]![0])).toMatch(/\/folders$/)
})

it('相同账号公司下的并行读取合并请求，返回对象相互独立', async () => {
  let finish!: (response: Response) => void
  vi.mocked(fetch).mockImplementation(() => new Promise(resolve => { finish = resolve }))
  const first = listProjects('dedupe-token')
  const second = listProjects('dedupe-token')
  expect(fetch).toHaveBeenCalledTimes(1)
  finish(new Response('[]', { status: 200 }))
  const [left, right] = await Promise.all([first, second])
  expect(left).not.toBe(right)
})

it('不同公司不共享在途请求', async () => {
  vi.mocked(fetch).mockImplementation(async () => new Response('[]', { status: 200 }))
  localStorage.setItem('pdm_active_organization', 'company-a')
  const first = listProjects('company-token')
  localStorage.setItem('pdm_active_organization', 'company-b')
  const second = listProjects('company-token')
  await Promise.all([first, second])
  expect(fetch).toHaveBeenCalledTimes(2)
})
