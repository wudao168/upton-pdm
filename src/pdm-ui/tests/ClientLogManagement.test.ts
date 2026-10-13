import { mount, flushPromises } from '@vue/test-utils'
import { beforeEach, expect, it, vi } from 'vitest'
import ClientLogManagement from '../src/components/ClientLogManagement.vue'
const api = vi.hoisted(() => ({ readClientLogs: vi.fn(), downloadClientLogs: vi.fn() }))
vi.mock('../src/api', () => api)
beforeEach(() => {
  vi.clearAllMocks()
  api.readClientLogs.mockResolvedValue({ items: [], total: 1, pageSize: 50, runtimeDays: 30, errorDays: 90 })
  api.downloadClientLogs.mockResolvedValue(undefined)
})
it('uses filters for downloads and complete diagnostics explicitly includes both classes', async () => {
  const wrapper = mount(ClientLogManagement, { props: { token: 'login' } })
  await flushPromises()
  await wrapper.find('input').setValue('设计电脑')
  await wrapper.findAll('select')[1]!.setValue('error')
  await wrapper.find('form').trigger('submit'); await flushPromises()
  expect(api.readClientLogs.mock.lastCall![0].get('kind')).toBe('error')
  await wrapper.findAll('button').find(b => b.text() === '下载完整诊断包')!.trigger('click'); await flushPromises()
  expect(api.downloadClientLogs.mock.lastCall![0].get('kind')).toBeNull()
  expect(api.downloadClientLogs.mock.lastCall![0].get('device')).toBe('设计电脑')
  expect(wrapper.text()).toContain('诊断包已下载')
  wrapper.unmount()
})
it('does not show a raw query exception', async () => {
  api.readClientLogs.mockRejectedValue(new Error('database password=secret'))
  const wrapper = mount(ClientLogManagement, { props: { token: 'login' } })
  await flushPromises()
  expect(wrapper.get('[role="alert"]').text()).toContain('日志查询失败')
  expect(wrapper.text()).not.toContain('secret')
  wrapper.unmount()
})
