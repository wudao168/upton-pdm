import { mount, flushPromises } from '@vue/test-utils'
import ElementPlus from 'element-plus'
import { afterEach, beforeEach, expect, it, vi } from 'vitest'
import DingTalkSettings from '../src/components/DingTalkSettings.vue'
import ProjectDingTalkBinding from '../src/components/ProjectDingTalkBinding.vue'
const api = vi.hoisted(() => ({ readDingTalkSettings: vi.fn(), saveDingTalkSettings: vi.fn(), publishDingTalkEntry: vi.fn(), readDingTalkBinding: vi.fn(), saveDingTalkBinding: vi.fn() }))
vi.mock('../src/api', () => api)
let wrapper: ReturnType<typeof mount>
const group = { id: 'group-1', name: '项目共享群', projectCount: 2, registered: true, entryUrl: 'http://192.168.2.8:5173/?dingtalkOpl=1&groupId=group-1' }
const settings = { enabled: true, corpId: 'corp', appKey: 'app', publicUrl: 'http://192.168.2.8:5173', companyId: 'company', rowVersion: 4, hasAppSecret: true, entryUrl: 'http://192.168.2.8:5173/?dingtalkOpl=1', users: [], groups: [group] }
const binding = { rootProjectId: 'root', canManage: true, rowVersion: 4, groupId: 'group-1', groups: [group], entryUrl: settings.entryUrl, enabled: true }
beforeEach(() => { vi.clearAllMocks(); api.readDingTalkSettings.mockResolvedValue(structuredClone(settings)); api.readDingTalkBinding.mockResolvedValue(structuredClone(binding)) })
afterEach(() => { wrapper?.unmount(); document.body.innerHTML = '' })
const click = async (text: string) => { const button = wrapper.findAll('button').find(button => button.text() === text)!; await button.trigger('click'); await flushPromises() }
it('edits, previews, saves and restores the group notification template', async () => {
  const template = '问题描述：{问题描述}\n负责人：{负责人}\n变更内容：{变更内容}'
  api.readDingTalkSettings.mockResolvedValue({ ...settings, notificationTemplate: template, defaultNotificationTemplate: template, notificationFields: ['问题描述','负责人','变更内容'] })
  api.saveDingTalkSettings.mockImplementation(async input => ({ ...settings, ...input, rowVersion: 5 }))
  wrapper = mount(DingTalkSettings, { props: { token: 'token' }, global: { plugins: [ElementPlus] } }); await flushPromises()
  expect(wrapper.get('.ding-message-preview').text()).toBe('问题描述：测试\n负责人：白红梅')
  await wrapper.get('textarea[aria-label="群通知消息内容"]').setValue('内容：{问题描述}')
  expect(wrapper.get('.ding-message-preview').text()).toBe('内容：测试')
  await click('保存配置')
  expect(api.saveDingTalkSettings).toHaveBeenCalledWith(expect.objectContaining({ notificationTemplate: '内容：{问题描述}' }), 'token')
  await click('恢复默认模板')
  expect(wrapper.get<HTMLTextAreaElement>('textarea').element.value).toBe(template)
})
it('keeps saved application secrets blank and sends a versioned update', async () => {
  api.saveDingTalkSettings.mockResolvedValue({ ...settings, rowVersion: 5 })
  wrapper = mount(DingTalkSettings, { props: { token: 'token' }, global: { plugins: [ElementPlus] } }); await flushPromises()
  expect(wrapper.find<HTMLInputElement>('input[type="password"]').element.value).toBe('')
  expect(wrapper.text()).toContain('统一应用首页地址')
  expect(wrapper.text()).toContain('通知中显示负责人名称')
  expect(wrapper.text()).not.toContain('qyapi_addresslist_search')
  expect(wrapper.text()).not.toContain('qyapi_chat_read')
  expect(wrapper.text()).not.toContain('酷应用')
  await click('保存配置')
  expect(api.saveDingTalkSettings).toHaveBeenCalledWith(expect.objectContaining({ appSecret: '', expectedRowVersion: 4 }), 'token')
  expect(wrapper.text()).toContain('配置已保存')
})
it('reuses the same group without exposing or resubmitting its credentials', async () => {
  api.saveDingTalkBinding.mockResolvedValue({ ...binding, rowVersion: 5 })
  wrapper = mount(ProjectDingTalkBinding, { props: { projectId: 'project-2', token: 'token' }, global: { plugins: [ElementPlus] } }); await flushPromises()
  await click('保存群绑定')
  expect(api.saveDingTalkBinding).toHaveBeenCalledWith('project-2', { groupId: 'group-1', expectedRowVersion: 4 }, 'token')
  expect(wrapper.text()).toContain('项目群绑定已保存')
})
it('hides mutation actions when the actor cannot manage the project', async () => {
  api.readDingTalkBinding.mockResolvedValue({ ...binding, canManage: false })
  wrapper = mount(ProjectDingTalkBinding, { props: { projectId: 'root', token: 'token' }, global: { plugins: [ElementPlus] } }); await flushPromises()
  expect(wrapper.findAll('button').some(button => button.text() === '保存群绑定')).toBe(false)
  expect(wrapper.text()).toContain('仅项目经理或管理员可修改绑定')
  expect(wrapper.findAll('button').some(button => button.text() === '重发提交入口')).toBe(false)
})

it('resends entry with the same enterprise robot', async () => {
  api.publishDingTalkEntry.mockResolvedValue({ sent: true, message: '入口已发送' })
  wrapper = mount(ProjectDingTalkBinding, { props: { projectId: 'root', token: 'token' }, global: { plugins: [ElementPlus] } }); await flushPromises()
  await click('重发提交入口')
  expect(api.publishDingTalkEntry).toHaveBeenCalledWith('root', 'token')
  expect(wrapper.text()).toContain('入口已发送')
})
