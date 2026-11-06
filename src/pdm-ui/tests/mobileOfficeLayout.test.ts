import { flushPromises, mount } from '@vue/test-utils'
import ElementPlus from 'element-plus'
import { afterEach, beforeEach, expect, it, vi } from 'vitest'
import { defaultMobileOfficeLayout, mobileOfficeInitialPage, mobileOfficeNavigation, type MobileOfficeLayoutView } from '../src/mobileOfficeLayout'
import MobileOfficeHome from '../src/components/MobileOfficeHome.vue'
import MobileOfficeNavigation from '../src/components/MobileOfficeNavigation.vue'
import MobileOfficeLayoutEditor from '../src/components/MobileOfficeLayoutEditor.vue'
const api = vi.hoisted(() => ({ readMobileOfficeLayout: vi.fn(), saveMobileOfficeLayoutDraft: vi.fn(), publishMobileOfficeLayout: vi.fn(), rollbackMobileOfficeLayout: vi.fn() }))
vi.mock('../src/api', () => api)
let wrapper: ReturnType<typeof mount>
const initial = (): MobileOfficeLayoutView => ({ companyId: 'company', rowVersion: 0, draft: defaultMobileOfficeLayout(), publishedVersion: 0, history: [{ version: 0, publishedAt: null, publishedBy: '系统', action: 'default', sourceVersion: null, layout: defaultMobileOfficeLayout() }] })
beforeEach(() => { vi.clearAllMocks(); api.readMobileOfficeLayout.mockResolvedValue(initial()) })
afterEach(() => wrapper?.unmount())
async function click(text: string) { await wrapper.findAll('button').find(item => item.text() === text)!.trigger('click'); await flushPromises() }
async function editor() { wrapper = mount(MobileOfficeLayoutEditor, { props: { token: 'admin-token' }, global: { plugins: [ElementPlus] } }); await flushPromises() }

it('filters project navigation and chooses a permitted default without changing configured order', () => {
  const layout = defaultMobileOfficeLayout(); layout.navigation.reverse(); layout.defaultPage = 'projects'
  expect(mobileOfficeNavigation(layout, false).map(item => item.key)).toEqual(['mine', 'work-hours', 'tasks', 'home'])
  expect(mobileOfficeInitialPage(layout, false)).toBe('home')
  expect(mobileOfficeInitialPage(layout, true)).toBe('projects')
})
it('renders selected home blocks in configured order and hides project data without permission', () => {
  const layout = defaultMobileOfficeLayout(); layout.blocks.reverse(); layout.blocks.find(item => item.key === 'metrics')!.enabled = false
  wrapper = mount(MobileOfficeHome, { props: { layout, displayName: '张工', today: '2026-11-05', tasks: [], projects: [], unread: 0, todayHours: 4.5, canViewProjects: false } })
  expect(wrapper.findAll('[data-block]').map(item => item.attributes('data-block'))).toEqual(['approvals', 'shortcuts', 'welcome'])
  expect(wrapper.text()).not.toContain('我的项目')
})
it('renders renamed navigation with the actual visible column count and emits navigation', async () => {
  const layout = defaultMobileOfficeLayout(); layout.navigation[1]!.label = '审批中心'; layout.navigation[3]!.enabled = false
  wrapper = mount(MobileOfficeNavigation, { props: { layout, page: 'home', canViewProjects: false } })
  expect(wrapper.findAll('button')).toHaveLength(3)
  expect(wrapper.attributes('style')).toContain('repeat(3, minmax(0, 1fr))')
  await wrapper.findAll('button')[1]!.trigger('click')
  expect(wrapper.emitted('navigate')).toEqual([['tasks']])
  expect(wrapper.get('[aria-current="page"]').text()).toBe('首页')
})
it('previews edits immediately, blocks publishing unsaved changes, saves then publishes the draft', async () => {
  await editor()
  await wrapper.get('input[aria-label="模块标题"]').setValue('工程工作台')
  expect(wrapper.get('[aria-label="手机页面预览"]').text()).toContain('工程工作台')
  expect(wrapper.findAll('button').find(item => item.text() === '发布草稿')!.attributes('disabled')).toBeDefined()
  api.saveMobileOfficeLayoutDraft.mockImplementation(async layout => ({ ...initial(), rowVersion: 1, draft: structuredClone(layout) }))
  await click('保存草稿')
  expect(api.saveMobileOfficeLayoutDraft).toHaveBeenCalledWith(expect.objectContaining({ blocks: expect.arrayContaining([expect.objectContaining({ title: '工程工作台' })]) }), 0, 'admin-token')
  api.publishMobileOfficeLayout.mockResolvedValue({ ...initial(), draft: api.saveMobileOfficeLayoutDraft.mock.calls[0]![0], rowVersion: 2, publishedVersion: 1 })
  await click('发布草稿'); await click('确认发布')
  expect(api.publishMobileOfficeLayout).toHaveBeenCalledWith(1, 'admin-token')
  expect(wrapper.text()).toContain('已发布 V1')
})
it('preserves unsaved inputs on conflict and asks before replacing them', async () => {
  await editor(); await wrapper.get('input[aria-label="模块标题"]').setValue('保留输入')
  api.saveMobileOfficeLayoutDraft.mockRejectedValue(new Error('移动端页面已被其他管理员修改，请保留当前修改并重新读取后核对。'))
  await click('保存草稿')
  expect((wrapper.get('input[aria-label="模块标题"]').element as HTMLInputElement).value).toBe('保留输入')
  await click('重新读取'); expect(api.readMobileOfficeLayout).toHaveBeenCalledTimes(1)
  await click('取消操作'); expect(wrapper.text()).toContain('保留输入')
})
it('reorders modules using keyboard-accessible buttons and does not edit the saved snapshot', async () => {
  await editor(); await wrapper.get('[aria-label="上移审批列表"]').trigger('click')
  expect(wrapper.findAll('.layout-phone [data-block]').map(item => item.attributes('data-block'))).toEqual(['welcome', 'metrics', 'approvals', 'shortcuts', 'projects'])
  expect(initial().draft.blocks.map(item => item.key)).toEqual(['welcome', 'metrics', 'shortcuts', 'approvals', 'projects'])
})
it('previews a historical version and confirms rollback without discarding the current draft', async () => {
  const data = initial(); data.rowVersion = 2; data.publishedVersion = 1
  data.history.unshift({ version: 1, publishedAt: '2026-11-05T08:00:00Z', publishedBy: 'admin', action: 'publish', sourceVersion: null, layout: { ...defaultMobileOfficeLayout(), theme: 'blue' } })
  api.readMobileOfficeLayout.mockResolvedValue(data); await editor()
  await wrapper.get('input[aria-label="模块标题"]').setValue('未保存草稿')
  await wrapper.get('[aria-label="预览版本V0"]').trigger('click')
  expect(wrapper.get('.layout-phone h1').text()).toBe('移动办公')
  api.rollbackMobileOfficeLayout.mockResolvedValue({ ...data, rowVersion: 3, publishedVersion: 2 })
  await wrapper.get('[aria-label="回滚到V0"]').trigger('click'); await click('确认回滚')
  expect(api.rollbackMobileOfficeLayout).toHaveBeenCalledWith(0, 2, 'admin-token')
  expect((wrapper.get('input[aria-label="模块标题"]').element as HTMLInputElement).value).toBe('未保存草稿')
  expect(wrapper.text()).toContain('草稿保持不变')
})
