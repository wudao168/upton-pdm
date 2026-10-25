import { flushPromises, mount } from '@vue/test-utils'
import ElementPlus, { ElMessageBox } from 'element-plus'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import EngineeringKitActivityButton from '../src/components/EngineeringKitActivityButton.vue'
import type { EngineeringKit } from '../src/types'

const api = vi.hoisted(() => ({ setEngineeringKitActive: vi.fn() }))
vi.mock('../src/api', () => api)
const kit = { id: 'kit', code: 'UKIT-000001', name: '套件', rowVersion: 7, isActive: true } as EngineeringKit
const render = (value = kit, canManage = true) => mount(EngineeringKitActivityButton, { props: { kit: value, token: 'token', canManage }, global: { plugins: [ElementPlus] } })
describe('套件启停维护', () => {
  beforeEach(() => { api.setEngineeringKitActive.mockReset(); vi.spyOn(ElMessageBox, 'confirm').mockResolvedValue('confirm' as never) })
  afterEach(() => vi.restoreAllMocks())
  it('停用时提交当前数据版本，保留发布和引用信息并回传保存结果', async () => {
    const saved = { ...kit, isActive: false, rowVersion: 8 }
    api.setEngineeringKitActive.mockResolvedValue(saved)
    const wrapper = render(); await wrapper.get('button').trigger('click'); await flushPromises()
    expect(ElMessageBox.confirm).toHaveBeenCalledWith(expect.stringContaining('已有BOM引用、套件版本及附件均保留'), expect.any(String), expect.any(Object))
    expect(api.setEngineeringKitActive).toHaveBeenCalledExactlyOnceWith('kit', false, 7, 'token')
    expect(wrapper.emitted('changed')).toEqual([[saved]])
    await wrapper.setProps({ kit: saved }); expect(wrapper.text()).toBe('恢复启用'); wrapper.unmount()
  })
  it('恢复启用和取消操作使用相同入口，取消不提交', async () => {
    const wrapper = render({ ...kit, isActive: false })
    vi.mocked(ElMessageBox.confirm).mockRejectedValueOnce('cancel')
    await wrapper.get('button').trigger('click'); await flushPromises(); expect(api.setEngineeringKitActive).not.toHaveBeenCalled()
    api.setEngineeringKitActive.mockResolvedValue({ ...kit, rowVersion: 8 })
    await wrapper.get('button').trigger('click'); await flushPromises()
    expect(api.setEngineeringKitActive).toHaveBeenCalledExactlyOnceWith('kit', true, 7, 'token'); wrapper.unmount()
  })
  it('无管理权限不显示操作，保存失败不伪造状态变化', async () => {
    const wrapper = render(kit, false); expect(wrapper.find('button').exists()).toBe(false)
    await wrapper.setProps({ canManage: true }); api.setEngineeringKitActive.mockRejectedValue(new Error('套件已被其他用户修改，请刷新后重试。'))
    await wrapper.get('button').trigger('click'); await flushPromises(); expect(wrapper.emitted('changed')).toBeUndefined()
    expect(wrapper.text()).toBe('停用'); wrapper.unmount()
  })
})
