import { flushPromises, mount } from '@vue/test-utils'
import { afterEach, beforeEach, expect, it, vi } from 'vitest'
import ReleaseExecutionPanel from '../src/components/ReleaseExecutionPanel.vue'
import type { ReleaseExecutionView } from '../src/releaseDelivery'
import { acknowledgeReleaseDepartment, readReleaseExecution, retryReleaseNotification } from '../src/api'
vi.mock('../src/api', () => ({ acknowledgeReleaseDepartment: vi.fn(), readReleaseExecution: vi.fn(), retryReleaseNotification: vi.fn() }))
const record = (): ReleaseExecutionView => ({ id: 'release', state: 'Published', ready: false, acknowledgementIds: ['receipt'], selectedBomItemIds: ['bom'], confirmedRemovedBomItemIds: [],
  versionBindings: [{ documentId: 'part', versionId: 'work', fileName: '需修改的零件.SLDPRT', revision: 'W2', sha256: 'sha', publish: true },
    { documentId: 'ref', versionId: 'old', fileName: '未修改的引用.SLDPRT', revision: 'A', sha256: 'sha2', publish: false }],
  progress: { u9: { NonStandard: { state: 'Pending', message: '等待本发布版本回查' } }, previewRequired: true, previewState: 'Pending', notificationRules: [{ id: 'rule', projectId: null, bomKind: 'All', event: 'Changed', departmentIds: ['department'], includeChildren: false, channels: ['InApp'], dingTalkGroupId: null, requireExecution: true }],
    departments: [{ id: 'receipt', departmentId: 'department', departmentName: '管理员指定部门', event: 'Changed', requireExecution: true, recipients: [{ username: 'user' }] }],
    deliveries: [{ id: 'message', event: 'Changed', channel: 'Email', destination: 'user', departmentIds: ['department'], state: 'Unknown', attempts: 1, message: '发送结果未确认' }] } })
beforeEach(() => { vi.clearAllMocks(); vi.useFakeTimers({ toFake: ['setInterval', 'clearInterval'] }); vi.mocked(readReleaseExecution).mockResolvedValue(record()); vi.mocked(acknowledgeReleaseDepartment).mockResolvedValue({}); vi.mocked(retryReleaseNotification).mockResolvedValue({}) })
afterEach(() => vi.useRealTimers())

it('separates publication from U9, drawing and notification completion and shows the pinned scope', async () => {
  const wrapper = mount(ReleaseExecutionPanel, { props: { packageId: 'release', token: 'fixture', canManage: true } }); await flushPromises()
  expect(wrapper.text()).toContain('等待同步回查'); expect(wrapper.text()).toContain('发送结果待核对')
  expect(wrapper.text()).toContain('未齐套不代表生产资料已全部可用')
  expect(wrapper.text()).toContain('保留引用，不随本次升版')
  expect(wrapper.text()).not.toContain('本发布版本的U9同步和所需生产图纸已齐套')
  const retry = wrapper.findAll('button').find(button => button.text() === '重试此通知')!
  expect(retry.attributes()).toHaveProperty('disabled')
  await wrapper.get('input[type="checkbox"]').setValue(true); await retry.trigger('click'); await flushPromises()
  expect(retryReleaseNotification).toHaveBeenCalledWith('release', 'message', true, 'fixture')
  wrapper.unmount()
})

it('requires an execution note and only exposes acknowledgements allowed for the current department member', async () => {
  const wrapper = mount(ReleaseExecutionPanel, { props: { packageId: 'release', token: 'fixture', canManage: false } }); await flushPromises()
  const execute = wrapper.findAll('button').find(button => button.text() === '确认执行')!
  expect(execute.attributes()).toHaveProperty('disabled')
  await wrapper.get('textarea').setValue('旧图纸已收回，按本次新版本执行')
  await execute.trigger('click'); await flushPromises()
  expect(acknowledgeReleaseDepartment).toHaveBeenCalledWith('release', 'receipt', true, '旧图纸已收回，按本次新版本执行', 'fixture')
  wrapper.unmount()
  vi.mocked(readReleaseExecution).mockResolvedValue({ ...record(), acknowledgementIds: [] })
  const other = mount(ReleaseExecutionPanel, { props: { packageId: 'release', token: 'fixture', canManage: true } }); await flushPromises()
  expect(other.text()).toContain('由该部门成员确认'); expect(other.find('textarea').exists()).toBe(false)
  other.unmount()
})

it('stops background refresh when the release panel is closed', async () => {
  const wrapper = mount(ReleaseExecutionPanel, { props: { packageId: 'release', token: 'fixture', canManage: false } }); await flushPromises()
  wrapper.unmount(); const count = vi.mocked(readReleaseExecution).mock.calls.length
  await vi.advanceTimersByTimeAsync(45000)
  expect(readReleaseExecution).toHaveBeenCalledTimes(count)
})
