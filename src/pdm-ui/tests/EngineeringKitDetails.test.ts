import { flushPromises, mount } from '@vue/test-utils'
import ElementPlus from 'element-plus'
import { describe, expect, it } from 'vitest'
import EngineeringKitDetails from '../src/components/EngineeringKitDetails.vue'
import type { EngineeringKit } from '../src/types'

const kit: EngineeringKit = {
  id: 'kit', code: 'UKIT-000001', name: '支架组件', model: 'R722.S2.12-00', brand: 'UPTON', rowVersion: 2,
  currentReleasedRevisionId: 'released', createdBy: 'admin', createdAt: '', updatedBy: 'admin', updatedAt: '',
  revisions: [
    { id: 'released', kitId: 'kit', versionNumber: 1, state: 'Released', createdBy: 'admin', createdAt: '', components: [
      { id: 'part', revisionId: 'released', materialId: 'material', materialCode: '01021000185', materialName: 'T型支座', specification: 'GCL08-D12-H10', brand: 'YHDA', remark: '已发布明细', quantity: 1, unit: '001', isOptional: false, sortOrder: 1 },
    ] },
    { id: 'draft', kitId: 'kit', versionNumber: 2, state: 'Draft', createdBy: 'admin', createdAt: '', components: [] },
  ],
}

describe('套件只读明细', () => {
  it('工程师仅查看已发布版本及完整明细字段', async () => {
    const wrapper = mount(EngineeringKitDetails, { props: { kit, canManage: false }, global: { plugins: [ElementPlus], stubs: { ElDialog: { template: '<div><slot /><slot name="footer" /></div>' } } } })
    await flushPromises()
    expect(wrapper.text()).toContain('01021000185')
    expect(wrapper.text()).toContain('GCL08-D12-H10')
    expect(wrapper.text()).toContain('YHDA')
    expect(wrapper.text()).toContain('已发布明细')
    expect(wrapper.text()).toContain('个')
    const select = wrapper.getComponent({ name: 'ElSelect' })
    expect(select.props('modelValue')).toBe('released')
    expect(select.findAllComponents({ name: 'ElOption' }).map(option => option.props('value'))).toEqual(['released'])
    expect(wrapper.findAll('button').some(button => /保存|发布|删除/.test(button.text()))).toBe(false)
    wrapper.unmount()
  })

  it('标准化可切换草稿和历史已发布版本，不修改明细', async () => {
    const wrapper = mount(EngineeringKitDetails, { props: { kit, canManage: true }, global: { plugins: [ElementPlus], stubs: { ElDialog: { template: '<div><slot /><slot name="footer" /></div>' } } } })
    await flushPromises()
    const select = wrapper.getComponent({ name: 'ElSelect' })
    expect(select.props('modelValue')).toBe('draft')
    select.vm.$emit('update:modelValue', 'released')
    await flushPromises()
    expect(wrapper.text()).toContain('GCL08-D12-H10')
    wrapper.unmount()
  })
})
