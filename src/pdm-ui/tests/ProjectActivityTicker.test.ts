import { mount } from '@vue/test-utils'
import { afterEach, expect, it, vi } from 'vitest'
import ProjectActivityTicker from '../src/components/ProjectActivityTicker.vue'
import { currentActivities, type ProjectActivity } from '../src/projectActivity'
const now = Date.parse('2026-11-07T02:00:00Z')
function item(id: string, extra: Partial<ProjectActivity> = {}): ProjectActivity {
  return { id, projectId: id, projectCode: 'P700017-11', releasePackageId: null, title: 'BOM已发布' + id, content: '请查看发布明细',
    createdAt: new Date(now - 1000).toISOString(), expiresAt: new Date(now + 86400000).toISOString(), important: false, target: 'overview', kind: 'Release', ...extra }
}
afterEach(() => vi.useRealTimers())
it('rotates every six seconds, pauses on hover/focus and opens the displayed item', async () => {
  vi.useFakeTimers()
  const wrapper = mount(ProjectActivityTicker, { props: { items: [item('1'), item('2')] } })
  await vi.advanceTimersByTimeAsync(6000)
  expect(wrapper.text()).toContain('BOM已发布2')
  await wrapper.trigger('mouseenter')
  await vi.advanceTimersByTimeAsync(12000)
  expect(wrapper.text()).toContain('2/2')
  await wrapper.get('.pdm-project-activity__main').trigger('click')
  expect(wrapper.emitted('open')?.[0]?.[0]).toMatchObject({ id: '2' })
  await wrapper.trigger('mouseleave')
  await wrapper.trigger('focusin')
  await vi.advanceTimersByTimeAsync(6000)
  expect(wrapper.text()).toContain('2/2')
  await wrapper.trigger('focusout')
  await vi.advanceTimersByTimeAsync(6000)
  expect(wrapper.text()).toContain('1/2')
  wrapper.unmount()
})
it('supports explicit pause and correct selection after feed replacement', async () => {
  vi.useFakeTimers()
  const wrapper = mount(ProjectActivityTicker, { props: { items: [item('1'), item('2')] } })
  await wrapper.get('[aria-label="暂停轮播"]').trigger('click')
  await vi.advanceTimersByTimeAsync(12000)
  expect(wrapper.text()).toContain('1/2')
  await wrapper.setProps({ items: [item('3')] })
  expect(wrapper.text()).toContain('BOM已发布3')
  expect(wrapper.find('.pdm-project-activity__toggle').exists()).toBe(false)
  wrapper.unmount()
})
it('expires items, merges status updates and puts important events first', () => {
  const data = currentActivities([
    item('1'), item('2', { projectId: 'same', kind: 'Status', important: true, createdAt: new Date(now - 3000).toISOString() }),
    item('3', { projectId: 'same', kind: 'Status', important: true }), item('4', { expiresAt: new Date(now).toISOString() }),
    item('5', { createdAt: new Date(now + 1000).toISOString() }),
  ], now)
  expect(data.map(x => x.id)).toEqual(['3', '1'])
})
