import { flushPromises, mount } from '@vue/test-utils'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import ArchiveWarningSettings from '../src/components/ArchiveWarningSettings.vue'
import { getArchiveWarningPolicy, getArchiveWarningCatalog, saveArchiveWarningPolicy } from '../src/api'
vi.mock('../src/api', () => ({ getArchiveWarningPolicy: vi.fn(), getArchiveWarningCatalog: vi.fn(), saveArchiveWarningPolicy: vi.fn() }))
beforeEach(() => {
  vi.mocked(getArchiveWarningPolicy).mockReset().mockResolvedValue({ revision: 3, blockAll: false, blockingCodes: [] })
  vi.mocked(getArchiveWarningCatalog).mockResolvedValue([
    { code: 'CyclicReference', name: '确认的循环引用', source: 'PDM官方', supported: true, alwaysBlocks: true },
    { code: 'MissingConfiguration', name: '引用配置不存在', source: 'UPLM扩展', supported: true, alwaysBlocks: false },
    { code: 'NotRebuilt', name: '文件未重建', source: 'PDM官方', supported: false, alwaysBlocks: false },
  ])
  vi.mocked(saveArchiveWarningPolicy).mockReset().mockResolvedValue({ revision: 4, blockAll: false, blockingCodes: ['MissingConfiguration'] })
})
describe('存档警告管理员策略', () => {
  it('只给已支持的可选警告提供开关，提交版本供并发保护', async () => {
    const wrapper = mount(ArchiveWarningSettings, { props: { token: 'test', canEdit: true } })
    await flushPromises()
    expect(wrapper.findAll('select')).toHaveLength(1)
    expect(wrapper.text()).toContain('始终阻止')
    expect(wrapper.text()).toContain('尚未接入')
    expect(wrapper.findAll('tbody tr')).toHaveLength(2)
    await wrapper.get('select').setValue('block')
    await wrapper.findAll('button').find(button => button.text() === '保存策略')!.trigger('click')
    await flushPromises()
    expect(saveArchiveWarningPolicy).toHaveBeenCalledWith({ revision: 3, blockAll: false, blockingCodes: ['MissingConfiguration'] }, 'test')
    expect(wrapper.text()).toContain('策略版本 4')
  })
  it('普通用户不能保存或改变策略', async () => {
    const wrapper = mount(ArchiveWarningSettings, { props: { token: 'test', canEdit: false } })
    await flushPromises()
    expect(wrapper.get('select').attributes('disabled')).toBeDefined()
    expect(wrapper.findAll('button').find(button => button.text() === '保存策略')!.attributes('disabled')).toBeDefined()
  })
  it('严格模式显示有效阻止状态但保留单项配置', async () => {
    const wrapper = mount(ArchiveWarningSettings, { props: { token: 'test', canEdit: true } })
    await flushPromises(); await wrapper.get('input').setValue(true)
    expect(wrapper.get('select').attributes('disabled')).toBeDefined()
    expect((wrapper.get('select').element as HTMLSelectElement).value).toBe('block')
    await wrapper.get('input').setValue(false)
    expect((wrapper.get('select').element as HTMLSelectElement).value).toBe('warn')
  })
  it('无法加载时禁止保存，不显示假成功', async () => {
    vi.mocked(getArchiveWarningPolicy).mockRejectedValue(new Error('offline'))
    const wrapper = mount(ArchiveWarningSettings, { props: { token: 'test', canEdit: true } })
    await flushPromises(); expect(wrapper.get('[role="alert"]').text()).toContain('无法读取')
    expect(saveArchiveWarningPolicy).not.toHaveBeenCalled()
  })
})
