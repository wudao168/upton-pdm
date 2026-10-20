import { flushPromises, mount } from '@vue/test-utils'
import { beforeEach, expect, it, vi } from 'vitest'
import ReleaseNotificationSettings from '../src/components/ReleaseNotificationSettings.vue'
import type { ReleaseNotificationSettings as Settings } from '../src/releaseDelivery'
import { readReleaseNotificationSettings, saveReleaseNotificationSettings } from '../src/api'

vi.mock('../src/api', () => ({ readReleaseNotificationSettings: vi.fn(), saveReleaseNotificationSettings: vi.fn() }))
const initial = (): Settings => ({ enabled: false, publicUrl: 'http://192.168.2.8:5173', rowVersion: 3, rules: [],
  mail: { host: '', port: 587, from: '', username: '', password: '' }, hasMailPassword: true,
  departments: [{ id: 'dept-a', name: '自定义组织甲' }, { id: 'dept-b', name: '自定义组织乙' }],
  projects: [{ id: 'project-a', code: 'P700021', name: '隔离项目' }], groups: [{ id: 'group-a', name: '已登记接收群' }] })
beforeEach(() => { vi.clearAllMocks(); vi.mocked(readReleaseNotificationSettings).mockResolvedValue(initial()); vi.mocked(saveReleaseNotificationSettings).mockImplementation(async input => ({ ...input, rowVersion: 4, mail: { ...input.mail, password: '' } })) })

it('lets the administrator select actual organization departments instead of predefined recipients', async () => {
  const wrapper = mount(ReleaseNotificationSettings, { props: { token: 'fixture' } }); await flushPromises()
  expect(wrapper.text()).toContain('尚未配置接收部门')
  await wrapper.findAll('button').find(button => button.text() === '新增通知规则')!.trigger('click')
  await wrapper.get('select[aria-label="规则1接收部门"]').setValue(['dept-b'])
  await wrapper.get('select[aria-label="规则1通知时机"]').setValue('Changed')
  await wrapper.get('select[aria-label="规则1项目范围"]').setValue('project-a')
  await wrapper.get('input[aria-label="启用发布通知"]').setValue(true)
  await wrapper.get('form').trigger('submit'); await flushPromises()
  const input = vi.mocked(saveReleaseNotificationSettings).mock.calls[0]![0]
  expect(input.rules[0]).toMatchObject({ departmentIds: ['dept-b'], event: 'Changed', projectId: 'project-a', channels: ['InApp'] })
  expect(input.rules[0]!.id).toMatch(/^[0-9a-f-]{36}$/i)
  expect(input.rowVersion).toBe(3)
  expect(wrapper.get('[role="status"]').text()).toContain('配置已保存')
  wrapper.unmount()
})

it('keeps unsaved department choices on a concurrency rejection and never displays stored passwords', async () => {
  vi.mocked(saveReleaseNotificationSettings).mockRejectedValue(new Error('通知配置已被修改，请刷新后重试。'))
  const wrapper = mount(ReleaseNotificationSettings, { props: { token: 'fixture' } }); await flushPromises()
  await wrapper.findAll('button').find(button => button.text() === '新增通知规则')!.trigger('click')
  await wrapper.get('select[aria-label="规则1接收部门"]').setValue(['dept-a'])
  await wrapper.get('input[value="Email"]').setValue(true)
  expect((wrapper.get('input[aria-label="邮件授权码"]').element as HTMLInputElement).value).toBe('')
  expect(wrapper.get('input[aria-label="邮件授权码"]').attributes('placeholder')).toContain('已设置')
  await wrapper.get('form').trigger('submit'); await flushPromises()
  expect(wrapper.get('[role="alert"]').text()).toContain('已被修改')
  expect((wrapper.get('select[aria-label="规则1接收部门"]').element as HTMLSelectElement).selectedOptions[0]!.value).toBe('dept-a')
  wrapper.unmount()
})

it('requires choosing an existing DingTalk group and keeps in-app notification enabled', async () => {
  const wrapper = mount(ReleaseNotificationSettings, { props: { token: 'fixture' } }); await flushPromises()
  await wrapper.findAll('button').find(button => button.text() === '新增通知规则')!.trigger('click')
  await wrapper.get('input[value="DingTalk"]').setValue(true)
  expect(wrapper.get('select[aria-label="规则1钉钉群"]').text()).toContain('已登记接收群')
  await wrapper.get('select[aria-label="规则1钉钉群"]').setValue('group-a')
  await wrapper.get('form').trigger('submit'); await flushPromises()
  expect(vi.mocked(saveReleaseNotificationSettings).mock.calls[0]![0].rules[0]).toMatchObject({ dingTalkGroupId: 'group-a', channels: ['InApp', 'DingTalk'] })
  wrapper.unmount()
})
