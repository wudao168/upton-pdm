import { defineComponent, ref } from 'vue'
import { flushPromises, mount } from '@vue/test-utils'
import { afterEach, beforeEach, expect, it, vi } from 'vitest'
import { useProjectActivityFeed } from '../src/composables/useProjectActivityFeed'
import type { ProjectActivity } from '../src/projectActivity'
const { read, notify, close } = vi.hoisted(() => ({ read: vi.fn(), notify: vi.fn(), close: vi.fn() }))
vi.mock('../src/api', () => ({ listProjectActivities: read }))
vi.mock('element-plus', () => ({ ElNotification: notify }))
let wrapper: ReturnType<typeof mount> | undefined
const now = Date.parse('2026-11-07T02:00:00Z')
function item(id: string): ProjectActivity { return { id, projectId: 'project', projectCode: 'P700017', releasePackageId: null, title: '项目计划变更已生效', content: '日期调整', createdAt: new Date(now).toISOString(), expiresAt: new Date(now + 86400000).toISOString(), important: true, kind: 'Plan', target: 'project-plan' } }
beforeEach(() => { vi.useFakeTimers(); vi.setSystemTime(now); read.mockReset(); notify.mockReset().mockReturnValue({ close }); close.mockReset(); sessionStorage.clear() })
afterEach(() => { wrapper?.unmount(); vi.useRealTimers(); vi.restoreAllMocks() })
it('polls and notifies only new important events once, keeping the initial visit quiet', async () => {
  read.mockResolvedValue([item('old')])
  let feed!: ReturnType<typeof useProjectActivityFeed>
  const open = vi.fn()
  wrapper = mount(defineComponent({ setup() { feed = useProjectActivityFeed(() => 'token', () => 'company:user', open); return () => null } }))
  await flushPromises()
  expect(feed.items.value).toHaveLength(1); expect(notify).not.toHaveBeenCalled()
  read.mockResolvedValue([item('new')])
  await vi.advanceTimersByTimeAsync(30000); await flushPromises()
  expect(notify).toHaveBeenCalledTimes(1)
  notify.mock.calls[0]![0].onClick()
  expect(open).toHaveBeenCalledWith(item('new'))
  await vi.advanceTimersByTimeAsync(30000); await flushPromises()
  expect(notify).toHaveBeenCalledTimes(1)
})
it('discards an in-flight response from the old company and clears on logout', async () => {
  const token = ref('token'), scope = ref('A')
  let resolveOld!: (items: ProjectActivity[]) => void
  read.mockReturnValueOnce(new Promise(resolve => { resolveOld = resolve })).mockResolvedValue([item('B')])
  let feed!: ReturnType<typeof useProjectActivityFeed>
  wrapper = mount(defineComponent({ setup() { feed = useProjectActivityFeed(() => token.value, () => scope.value, vi.fn()); return () => null } }))
  scope.value = 'B'; await flushPromises()
  expect(feed.items.value[0]?.id).toBe('B')
  resolveOld([item('A')]); await flushPromises()
  expect(feed.items.value[0]?.id).toBe('B')
  token.value = ''; await flushPromises()
  expect(feed.items.value).toEqual([])
})
it('clears stale feed on failure and expires entries locally', async () => {
  read.mockResolvedValue([{ ...item('old'), expiresAt: new Date(now + 10000).toISOString() }])
  let feed!: ReturnType<typeof useProjectActivityFeed>
  wrapper = mount(defineComponent({ setup() { feed = useProjectActivityFeed(() => 'token', () => 'company:user', vi.fn()); return () => null } }))
  await flushPromises()
  read.mockRejectedValue(new Error('network'))
  vi.spyOn(console, 'warn').mockImplementation(() => {})
  await vi.advanceTimersByTimeAsync(30000); await flushPromises()
  expect(feed.items.value).toEqual([])
})
