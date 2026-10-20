import { flushPromises, mount } from '@vue/test-utils'
import { afterEach, describe, expect, it, vi } from 'vitest'
import ProductionScanSettings from '../src/components/ProductionScanSettings.vue'
import * as api from '../src/api'
afterEach(() => vi.restoreAllMocks())
describe('生产扫码设置', () => {
  it('无固定账号或角色绑定，可配置工位和角色岗位并保存版本', async () => {
    vi.spyOn(api, 'readProductionScanSettings').mockResolvedValue({ companyId: 'company', workstations: [], bindings: [], rowVersion: 0 })
    vi.spyOn(api, 'readProductionScanOptions').mockResolvedValue({ users: [{ username: 'machine', displayName: '机加员' }], roles: [{ role: 'custom-role', name: '自定义机加角色' }] })
    const save = vi.spyOn(api, 'saveProductionScanSettings').mockImplementation(async input => ({ companyId: 'company', workstations: input.workstations, bindings: input.bindings, rowVersion: 1 }))
    const wrapper = mount(ProductionScanSettings, { props: { token: 'token' } }); await flushPromises()
    expect(wrapper.findAll('.production-scan-settings__binding')).toHaveLength(0)
    await wrapper.findAll('button').find(button => button.text() === '新增工位')!.trigger('click')
    await wrapper.get('[aria-label="第1个工位名称"]').setValue('CNC01')
    await wrapper.get('[aria-label="第1个工位资源组"]').setValue('CNC组')
    await wrapper.findAll('button').find(button => button.text() === '新增绑定')!.trigger('click')
    await wrapper.get('[aria-label="第1条绑定对象类型"]').setValue('Role')
    await wrapper.get('[aria-label="第1条绑定账号或角色"]').setValue('custom-role')
    await wrapper.get('[aria-label="第1条绑定岗位"]').setValue('Machining')
    await wrapper.get('.production-scan-settings__stations input[type=checkbox]').setValue(true)
    await wrapper.get('form').trigger('submit'); await flushPromises()
    expect(save).toHaveBeenCalledWith(expect.objectContaining({ expectedRowVersion: 0, workstations: [expect.objectContaining({ name: 'CNC01', resourceGroup: 'CNC组' })], bindings: [expect.objectContaining({ subjectType: 'Role', subject: 'custom-role', post: 'Machining' })] }), 'token')
    expect(wrapper.text()).toContain('配置已保存')
    wrapper.unmount()
  })
})
