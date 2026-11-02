import { flushPromises, mount } from '@vue/test-utils'
import { afterEach, describe, expect, it, vi } from 'vitest'
import QuickCommand from '../src/components/QuickCommand.vue'
import { listMaterials } from '../src/api'

vi.mock('../src/api', () => ({ listMaterials: vi.fn() }))
const stubs = { Teleport: true, ElDialog: { props: ['modelValue'], template: '<div v-if="modelValue"><slot /></div>' }, ObjectInspector: true }
const props = { token: 'session', companyId: 'company', canSearchMaterials: false, items: [{ id: 'project', label: 'P001 测试项目', kind: '项目' }] }
afterEach(() => { vi.useRealTimers(); vi.clearAllMocks() })

describe('快速查找', () => {
  it('快捷键打开并选择权限内对象，无料品权限时不查询料品', async () => {
    vi.useFakeTimers()
    const wrapper = mount(QuickCommand, { props, attachTo: document.body, global: { stubs } })
    window.dispatchEvent(new KeyboardEvent('keydown', { key: 'k', ctrlKey: true }))
    await flushPromises()
    await wrapper.get('input').setValue('P001')
    await vi.advanceTimersByTimeAsync(350)
    expect(listMaterials).not.toHaveBeenCalled()
    await wrapper.get('input').trigger('keydown', { key: 'ArrowDown' })
    expect(document.activeElement).toBe(wrapper.get('.pdm-command-results button').element)
    await wrapper.get('.pdm-command-results button').trigger('keydown', { key: 'ArrowUp' })
    expect(document.activeElement).toBe(wrapper.get('input').element)
    await wrapper.get('input').trigger('keydown', { key: 'Enter' })
    expect(wrapper.emitted('choose')).toEqual([[{ id: 'project', label: 'P001 测试项目', kind: '项目' }]])
    wrapper.unmount()
  })

  it('切换公司取消请求且忽略旧公司返回的结果', async () => {
    vi.useFakeTimers()
    let resolve!: (value: Awaited<ReturnType<typeof listMaterials>>) => void
    vi.mocked(listMaterials).mockReturnValue(new Promise(done => { resolve = done }))
    const wrapper = mount(QuickCommand, { props: { ...props, canSearchMaterials: true }, global: { stubs } })
    await wrapper.get('.pdm-command-trigger').trigger('click')
    await wrapper.get('input').setValue('轴承')
    await vi.advanceTimersByTimeAsync(350)
    const signal = vi.mocked(listMaterials).mock.calls[0]![5]!
    await wrapper.setProps({ companyId: 'other' })
    expect(signal.aborted).toBe(true)
    resolve([{ id: 'old', materialCode: 'OLD', name: '旧公司物料' } as Awaited<ReturnType<typeof listMaterials>>[number]])
    await flushPromises()
    await wrapper.get('.pdm-command-trigger').trigger('click')
    expect(wrapper.text()).not.toContain('旧公司物料')
    wrapper.unmount()
  })
})
