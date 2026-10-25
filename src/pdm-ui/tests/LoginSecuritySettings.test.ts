import { mount, flushPromises } from '@vue/test-utils'
import { beforeEach, expect, it, vi } from 'vitest'
import LoginSecuritySettings from '../src/components/LoginSecuritySettings.vue'
import type { LoginSecurityDirectory } from '../src/api'
const api = vi.hoisted(() => ({ getLoginSecurity: vi.fn(), saveLoginSecurityPolicy: vi.fn(), approveLoginDevice: vi.fn(), revokeLoginSession: vi.fn(), PdmApiError: class extends Error {} }))
vi.mock('../src/api', () => api)
let directory: LoginSecurityDirectory
beforeEach(() => {
  vi.clearAllMocks()
  directory = { policy: { singleSessionEnabled: false, deviceBindingEnabled: false, version: 'initial' }, canEditPolicy: true, users: [{ username: 'engineer', devices: [{ id: 'one', machine: '设计电脑', mac: '001122334455', approved: false, requestedAt: '2026-10-22T00:00:00Z', lastSeenAt: '2026-10-22T00:00:00Z' }], sessions: [{ id: 'session', type: 'solidworks', machine: '设计电脑', version: 'initial', revoked: false, createdAt: '2026-10-22T00:00:00Z', lastSeenAt: '2026-10-22T00:00:00Z' }] }] }
  api.getLoginSecurity.mockImplementation(async () => structuredClone(directory))
  api.saveLoginSecurityPolicy.mockImplementation(async input => ({ ...input, version: 'enabled' }))
  vi.spyOn(window, 'confirm').mockReturnValue(true)
})
it('默认关闭的两个独立开关保存到服务器，并明确网页不限制电脑', async () => {
  const wrapper = mount(LoginSecuritySettings, { props: { token: 'admin-token' } })
  await flushPromises()
  const switches = wrapper.findAll('input[role=switch]')
  expect(switches).toHaveLength(2)
  expect(switches.every(item => !(item.element as HTMLInputElement).checked)).toBe(true)
  await switches[0]!.setValue(true)
  await wrapper.get('form').trigger('submit'); await flushPromises()
  expect(api.saveLoginSecurityPolicy).toHaveBeenCalledWith({ singleSessionEnabled: true, deviceBindingEnabled: false }, 'admin-token')
  expect(wrapper.get('[role=status]').text()).toContain('所有旧登录需要重新登录')
  expect(wrapper.text()).toContain('网页端不限制电脑')
  wrapper.unmount()
})
it('管理员可以确认电脑绑定和强制下线，并刷新服务器记录', async () => {
  const wrapper = mount(LoginSecuritySettings, { props: { token: 'admin-token' } })
  await flushPromises()
  await wrapper.findAll('button').find(item => item.text() === '确认绑定')!.trigger('click'); await flushPromises()
  expect(api.approveLoginDevice).toHaveBeenCalledWith('engineer', 'one', true, 'admin-token')
  await wrapper.findAll('button').find(item => item.text() === '强制下线')!.trigger('click'); await flushPromises()
  expect(api.revokeLoginSession).toHaveBeenCalledWith('engineer', 'session', 'admin-token')
  expect(wrapper.text()).toContain('自动登录凭证同时失效')
  wrapper.unmount()
})
it('公司管理员不能修改全系统开关，加载失败不显示虚假的默认关闭状态', async () => {
  directory.canEditPolicy = false
  const wrapper = mount(LoginSecuritySettings, { props: { token: 'company-admin' } })
  await flushPromises()
  expect(wrapper.findAll('input[role=switch]').every(item => item.attributes('disabled') !== undefined)).toBe(true)
  wrapper.unmount()
  api.getLoginSecurity.mockRejectedValue(new Error('database password=secret'))
  const failure = mount(LoginSecuritySettings, { props: { token: 'admin' } })
  await flushPromises()
  expect(failure.find('form').exists()).toBe(false)
  expect(failure.get('[role=alert]').text()).toContain('操作未完成')
  expect(failure.text()).not.toContain('secret')
  failure.unmount()
})
