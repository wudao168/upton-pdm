import { mount } from '@vue/test-utils'
import { describe, expect, it } from 'vitest'
import ReleaseCenter from '../src/components/ReleaseCenter.vue'
import type { CreateReleasePackageInput, ReleasePackageSummary } from '../src/types'

describe('ReleaseCenter', () => {
  it('creates a scoped release and delegates assignees to the configured workflow', async () => {
    const wrapper = mount(ReleaseCenter, {
      props: {
        releasePackage: null,
        username: 'admin',
        pending: false,
        progress: 0,
        error: '',
        canManage: true,
        canDecide: true,
        changeReasonTypes: ['设计变更', '客户需求'],
      },
    })

    expect(wrapper.text()).not.toContain('仅输出选中的标准件')
    expect(wrapper.text()).not.toContain('发布包编号')
    expect(wrapper.text()).not.toContain('生效起始序列号')
    expect(wrapper.text()).not.toContain('生效截止序列号')
    expect(wrapper.find('input[aria-label="变更单号由系统自动生成"]').exists()).toBe(false)
    await wrapper.get('select').setValue('StandardSupplement')
    const changeNumber = wrapper.get('input[aria-label="变更单号由系统自动生成"]')
    expect(changeNumber.element.closest('.pdm-release-type-row')).toBe(wrapper.get('.pdm-release-type-row').element)
    expect(changeNumber.attributes()).toHaveProperty('readonly')
    expect((changeNumber.element as HTMLInputElement).value).toBe('创建草稿后自动生成')
    await wrapper.get('input[aria-label="变更原因 设计变更"]').setValue(true)
    await wrapper.get('input[aria-label="变更原因 客户需求"]').setValue(true)
    await wrapper.get('form').trigger('submit')

    const created = wrapper.emitted('create')?.[0]?.[0] as CreateReleasePackageInput
    expect(created.changeReason).toBe('设计变更；客户需求')
    expect(created.scope).toBe('StandardSupplement')
    expect(created.selectedBomItemIds).toEqual([])
    expect('number' in created).toBe(false)
    expect('changeNumber' in created).toBe(false)
    expect('effectiveSerialFrom' in created).toBe(false)
    expect('effectiveSerialTo' in created).toBe(false)
    expect(wrapper.text()).not.toContain('审批人由系统管理中的版本化模板自动解析')
  })

  it('limits release types per BOM and submits the selected frozen package', async () => {
    const releasePackage: ReleasePackageSummary = {
      id: 'release-electrical-1', number: 'RP-E-001', state: '草稿', steps: [], scope: 'ElectricalFormal',
      workflowVersion: 2, selectedBomItemIds: [], createsManufacturingBaseline: false, locksDocuments: false,
      standardBomSnapshot: [], nonStandardBomSnapshot: [],
      electricalBomSnapshot: [{ id: 'electrical-1', kind: 'Electrical', sequence: 1, drawingNumber: 'EL-001', name: '电气元件', quantity: 2, unit: '个', revision: 'W2', complete: true }],
    }
    const wrapper = mount(ReleaseCenter, {
      props: {
        releasePackage,
        allowedScopes: ['ElectricalFormal', 'ElectricalSupplement'],
        previousVersionItems: [{ id: 'electrical-old', kind: 'Electrical', sequence: 1, drawingNumber: 'EL-001', name: '电气元件', quantity: 1, unit: '个', revision: 'W1', complete: true }],
        username: 'hardware-engineer', pending: false, progress: 0, error: '', canManage: true, canDecide: true,
      },
    })

    expect(wrapper.text()).toContain('审批固化快照')
    expect(wrapper.text()).not.toContain('生效序列号')
    expect(wrapper.text()).toContain('EL-001')
    expect(wrapper.text()).toContain('修改 1')
    await wrapper.findAll('button').find(button => button.text() === '提交审批')!.trigger('click')
    expect(wrapper.emitted('submit')).toEqual([['release-electrical-1']])
  })

  it('shows only the release scopes owned by the current BOM page', () => {
    const wrapper = mount(ReleaseCenter, {
      props: {
        releasePackage: null, allowedScopes: ['NonStandardWithDrawing'], username: 'engineer',
        pending: false, progress: 0, error: '', canManage: true, canDecide: true,
      },
    })

    expect(wrapper.findAll('option').map(option => option.text())).toEqual(['非标件BOM + 图纸'])
  })

  it('uses the preferred scope and limits creation to supplements after formal release', async () => {
    const wrapper = mount(ReleaseCenter, {
      props: {
        releasePackage: null,
        allowedScopes: ['StandardLongLead', 'StandardFormal', 'StandardSupplement'],
        preferredScope: 'StandardFormal',
        username: 'engineer', pending: false, progress: 0, error: '', canManage: true, canDecide: true,
      },
    })

    expect((wrapper.get('.pdm-release-type-row select').element as HTMLSelectElement).value).toBe('StandardFormal')
    await wrapper.setProps({ allowedScopes: ['StandardSupplement'], preferredScope: 'StandardSupplement' })
    expect(wrapper.findAll('.pdm-release-type-row option').map(option => option.text())).toEqual(['标准件 · 增补/变更'])
    expect((wrapper.get('.pdm-release-type-row select').element as HTMLSelectElement).value).toBe('StandardSupplement')
  })

  it('places draft action beside release type and renders long-lead items as a selectable list', async () => {
    const wrapper = mount(ReleaseCenter, {
      props: {
        releasePackage: null,
        allowedScopes: ['StandardLongLead'],
        standardItems: [
          { id: 'item-1', kind: 'Standard', sequence: 1, drawingNumber: 'STD-001', name: '长交期件', specification: 'M12', brand: 'SMC', remark: '提前采购', quantity: 1, unit: '个', revision: 'W1', complete: true },
        ],
        username: 'engineer', pending: false, progress: 0, error: '', canManage: true, canDecide: true,
      },
    })

    expect(wrapper.get('.pdm-release-type-row > button').text()).toBe('创建草稿')
    expect(wrapper.text()).toContain('备注')
    expect(wrapper.get('textarea').attributes()).not.toHaveProperty('required')
    expect(wrapper.findAll('.long-lead-picker th').map(cell => cell.text())).toEqual(['选择', '序号', '物料编码', '名称', '型号', '品牌', '数量', '备注'])
    expect(wrapper.findAll('.long-lead-picker tbody tr').at(0)!.findAll('td').slice(1).map(cell => cell.text())).toEqual(['1', 'STD-001', '长交期件', 'M12', 'SMC', '1', '提前采购'])
    expect(wrapper.get('.pdm-release-type-row > button').attributes()).toHaveProperty('disabled')

    await wrapper.get('input[aria-label="选择长交期物料 STD-001"]').setValue(true)
    expect(wrapper.get('.pdm-release-type-row > button').attributes()).not.toHaveProperty('disabled')
  })

  it('lists every formal item and marks items already released for long lead', () => {
    const wrapper = mount(ReleaseCenter, {
      props: {
        releasePackage: null,
        allowedScopes: ['StandardFormal'],
        preferredScope: 'StandardFormal',
        standardItems: [
          { id: 'item-1', kind: 'Standard', sequence: 1, drawingNumber: 'STD-001', name: '提前采购件', specification: 'M12', brand: 'SMC', quantity: 1, unit: '个', revision: 'W1', complete: true },
          { id: 'item-2', kind: 'Standard', sequence: 2, drawingNumber: 'STD-002', name: '普通件', specification: 'M8', brand: 'FESTO', quantity: 2, unit: '个', revision: 'W1', complete: true },
        ],
        longLeadPublishedItemIds: ['item-1'],
        username: 'engineer', pending: false, progress: 0, error: '', canManage: true, canDecide: true,
      },
    })

    expect(wrapper.text()).toContain('正式发布内容（共 2 项）')
    expect(wrapper.findAll('.release-item-picker tbody tr').at(0)!.text()).toContain('已提前发布')
    expect(wrapper.findAll('.release-item-picker tbody tr').at(1)!.text()).not.toContain('已提前发布')
  })

  it('shows only added, modified and deleted items for a supplement', () => {
    const wrapper = mount(ReleaseCenter, {
      props: {
        releasePackage: null,
        allowedScopes: ['StandardSupplement'],
        preferredScope: 'StandardSupplement',
        standardItems: [
          { id: 'item-current-1', kind: 'Standard', sequence: 1, drawingNumber: 'STD-001', name: '修改件', specification: 'M12', brand: 'SMC', quantity: 2, unit: '个', revision: 'W2', complete: true },
          { id: 'item-current-3', kind: 'Standard', sequence: 3, drawingNumber: 'STD-003', name: '新增件', specification: 'M6', brand: 'SMC', quantity: 1, unit: '个', revision: 'W1', complete: true },
        ],
        previousVersionItems: [
          { id: 'item-old-1', kind: 'Standard', sequence: 1, drawingNumber: 'STD-001', name: '修改件', specification: 'M12', brand: 'SMC', quantity: 1, unit: '个', revision: 'W1', complete: true },
          { id: 'item-old-2', kind: 'Standard', sequence: 2, drawingNumber: 'STD-002', name: '删除件', specification: 'M8', brand: 'SMC', quantity: 1, unit: '个', revision: 'W1', complete: true },
        ],
        username: 'engineer', pending: false, progress: 0, error: '', canManage: true, canDecide: true,
      },
    })

    expect(wrapper.text()).toContain('增补/变更内容（共 3 项）')
    expect(wrapper.findAll('.release-change-tag').map(tag => tag.text()).sort()).toEqual(['修改', '删除', '新增'])
    expect(wrapper.text()).toContain('STD-001')
    expect(wrapper.text()).toContain('STD-002')
    expect(wrapper.text()).toContain('STD-003')
  })
})
