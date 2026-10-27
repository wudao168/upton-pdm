import { flushPromises, mount } from '@vue/test-utils'
import { describe, expect, it, vi } from 'vitest'
import ReleaseCenter from '../src/components/ReleaseCenter.vue'
import * as api from '../src/api'
import type { BomItem, CreateReleasePackageInput, ReleasePackageSummary } from '../src/types'

describe('ReleaseCenter', () => {
  it('发布记录显示整包日期和汇总物料的逐项交付要求，旧记录显示未设置', async () => {
    const item: BomItem = { id: 'delivery-a', kind: 'Standard', sequence: 1, drawingNumber: 'STD-DATE', name: '接头', quantity: 1, unit: '个', revision: 'A', complete: true }
    const release = { ...publishedFormal('StandardFormal'), drawingRequiredOn: '2026-11-02', standardBomSnapshot: [item, { ...item, id: 'delivery-b' }],
      bomItemDeliveryOverrides: { 'delivery-b': { priority: 'Urgent' as const, requiredOn: '2026-10-30' } } }
    const wrapper = mount(ReleaseCenter, { props: { ...frozenProps, releasePackage: release } })
    expect(wrapper.get('.pdm-release-summary').text()).toContain('整包需求日期2026-11-02')
    expect(wrapper.get('.pdm-release-delivery').text()).toBe('普通 · 2026-11-02；紧急 · 2026-10-30')
    await wrapper.setProps({ releasePackage: { ...release, standardBomSnapshot: [{ ...item, quantity: 2, summaryItemIds: ['delivery-a', 'delivery-b'] }] } })
    expect(wrapper.get('.pdm-release-delivery').text()).toBe('普通 · 2026-11-02；紧急 · 2026-10-30')
    await wrapper.setProps({ releasePackage: { ...release, drawingRequiredOn: null, bomItemDeliveryOverrides: {} } })
    expect(wrapper.get('.pdm-release-delivery').text()).toBe('普通 · 未设置')
    wrapper.unmount()
  })
  it('requires an explicit deletion choice and never expands an empty selection to the whole BOM', async () => {
    const removed: BomItem = { id: 'removed', kind: 'Standard', sequence: 1, drawingNumber: 'REMOVE-1', name: '待删除项', specification: 'M5', brand: '国优', quantity: 1, unit: '个', revision: 'A', complete: true }
    const wrapper = mount(ReleaseCenter, { props: { releasePackage: null, username: 'admin', pending: false, progress: 0, error: '', canManage: true, canDecide: true,
      allowedScopes: ['StandardSupplement'], preferredScope: 'StandardSupplement', releaseItems: [], previousVersionItems: [removed], releasePackages: [publishedFormal('StandardFormal')] } })
    await wrapper.get('input[aria-label="变更原因 设计问题 / 设计变更"]').setValue(true)
    expect(wrapper.get('.pdm-release-draft-actions .pdm-primary-action').attributes()).toHaveProperty('disabled')
    const selection = wrapper.get('input[aria-label="本次纳入 REMOVE-1"]')
    expect((selection.element as HTMLInputElement).checked).toBe(false)
    await selection.setValue(true)
    await wrapper.get('form').trigger('submit')
    expect(wrapper.emitted('create')?.[0]?.[0]).toMatchObject({ selectedBomItemIds: [], confirmedRemovedBomItemIds: ['removed'] })
    wrapper.unmount()
  })
  it('does not reselect twenty formally promoted items when only one item is new', async () => {
    const previous: BomItem[] = Array.from({ length: 20 }, (_, i) => ({ id: `old-${i}`, kind: 'NonStandard', sequence: i + 1,
      drawingNumber: `CODE-${i}`, name: `已发布${i}`, specification: `MODEL-${i}`, quantity: 1, unit: '个', revision: 'W1', complete: true }))
    const added: BomItem = { ...previous[0]!, id: 'new', drawingNumber: '', name: '圆柱', specification: 'TSST.01-02' }
    const current = [...previous.map(item => ({ ...item, revision: 'A' })), added]
    const wrapper = mount(ReleaseCenter, { props: { ...frozenProps, releasePackage: null, releaseItems: current, previousVersionItems: previous,
      changeReasonTypes: ['物料问题 / 交期不满足', '物料问题 / custom-stock'], changeReasonLabels: { '物料问题 / 交期不满足': '库存交期不足', '物料问题 / custom-stock': '临时库存替代' }, allowedScopes: ['NonStandardSupplement'], preferredScope: 'NonStandardSupplement', releasePackages: [publishedFormal('NonStandardWithDrawing')],
      drawingReviewCandidates: current.map(item => ({ candidateId: item.id!, bomItemId: item.id, drawingDocumentId: `drawing-${item.id}`,
        drawingNumber: item.specification!, name: item.name, modelRevision: item.revision, bomKinds: ['NonStandard' as const], state: 'ApprovedCurrent' as const, selectable: false, hasUnreleasedDrawingChanges: false })) } })
    expect(wrapper.findAll('.release-change-tag').map(tag => tag.text())).toEqual(['新增'])
    expect(wrapper.findAll('.release-detail-picker tbody input[type="checkbox"]')).toHaveLength(1)
    expect(wrapper.get('input[aria-label="变更原因 物料问题 / 交期不满足"]').element.closest('label')?.textContent).toContain('库存交期不足')
    await wrapper.get('input[aria-label="变更原因 物料问题 / 交期不满足"]').setValue(true)
    await wrapper.get('input[aria-label="变更原因 物料问题 / custom-stock"]').setValue(true)
    expect(wrapper.get('.release-change-reason-summary').text()).toContain('临时库存替代')
    expect(wrapper.get('.release-change-reason-summary').text()).not.toContain('custom-stock')
    await wrapper.get('form').trigger('submit')
    expect(wrapper.emitted('create')?.[0]?.[0]).toMatchObject({ selectedBomItemIds: ['new'] })
    await wrapper.setProps({ releasePackage: { ...publishedFormal('NonStandardWithDrawing'), scope: 'NonStandardSupplement', nonStandardBomSnapshot: current } })
    const rows = wrapper.findAll('.pdm-release-frozen-table tbody tr')
    expect(rows).toHaveLength(1)
    expect(rows[0]!.text()).toContain('圆柱')
    expect(wrapper.text()).not.toContain('版本：W1 → A')
    wrapper.unmount()
  })
  it('草稿汇总同一来源的空料号垫块，保留不同零件', () => {
    const spacer: BomItem = { id: 'spacer-1', sourceDocumentId: 'spacer-model', sourceConfiguration: '默认', kind: 'NonStandard',
      sequence: 1, drawingNumber: '', name: '垫块', specification: 'P700022.10.00-05', quantity: 1, unit: '个', revision: 'W1', complete: true }
    const wrapper = mount(ReleaseCenter, { props: { ...frozenProps, previousVersionItems: [], releasePackage: {
      ...publishedFormal('NonStandardWithDrawing'), scope: 'NonStandardSupplement', nonStandardBomSnapshot: [spacer,
        { ...spacer, id: 'spacer-2' }, { ...spacer, id: 'other', sourceDocumentId: 'other-model', name: '背板' }] } } })
    const rows = wrapper.findAll('.pdm-release-frozen-table tbody tr')
    expect(rows).toHaveLength(2)
    expect(rows[0]!.findAll('td')[2]!.text()).toBe('垫块')
    expect(rows[0]!.findAll('td')[7]!.text()).toBe('2')
    wrapper.unmount()
  })
  it('空料号独立选择，默认及全选仅纳入审批完成的三项', async () => {
    const items: BomItem[] = Array.from({ length: 8 }, (_, i) => ({ id: `part-${i}`, kind: 'NonStandard', sequence: i + 1,
      drawingNumber: '', name: `零件${i}`, specification: `MODEL-${i}`, quantity: 1, unit: '个', revision: 'W1', complete: true }))
    const wrapper = mount(ReleaseCenter, { props: { ...frozenProps, releasePackage: null, releaseItems: items,
      allowedScopes: ['NonStandardSupplement'], preferredScope: 'NonStandardSupplement', releasePackages: [publishedFormal('NonStandardWithDrawing')],
      drawingReviewCandidates: items.map((item, i) => ({ candidateId: item.id!, bomItemId: item.id, drawingNumber: '', name: item.name,
        modelRevision: 'W1', drawingDocumentId: i < 3 ? 'drawing-' + i : undefined, bomKinds: ['NonStandard' as const], state: i < 3 ? 'ApprovedCurrent' as const : 'Ready' as const, selectable: false })) } })
    const boxes = wrapper.findAll('.release-detail-picker tbody input[type="checkbox"]')
    expect(boxes.map(box => (box.element as HTMLInputElement).checked)).toEqual([true, true, true, false, false, false, false, false])
    await boxes[0]!.setValue(false)
    expect((boxes[1]!.element as HTMLInputElement).checked).toBe(true)
    await wrapper.get('[aria-label="全选可发布变更"]').trigger('change')
    expect(boxes.every(box => !(box.element as HTMLInputElement).checked)).toBe(true)
    await wrapper.get('[aria-label="全选可发布变更"]').setValue(true)
    expect(boxes.filter(box => (box.element as HTMLInputElement).checked)).toHaveLength(3)
    wrapper.unmount()
  })
  it('多个独立装配体统一按项目汇总创建发布草稿', async () => {
    const rootA = '11111111-1111-1111-1111-111111111111'
    const rootB = '22222222-2222-2222-2222-222222222222'
    const item: BomItem = { id: 'a', kind: 'Standard', sequence: 1, drawingNumber: 'STD-A', name: 'A树物料', quantity: 1, unit: '001', revision: 'W1', complete: true, sourceInstancePath: `roots/${rootA.replaceAll('-', '')}/child` }
    const wrapper = mount(ReleaseCenter, { props: {
      referenceRoot: { id: 'forest', name: '项目装配结构', kind: 'Assembly', children: [
        { id: rootA, documentId: rootA, name: '装配A', kind: 'Assembly', children: [] },
        { id: rootB, documentId: rootB, name: '装配B', kind: 'Assembly', children: [] },
      ] } as any,
      releasePackage: null, releaseItems: [item, { ...item, id: 'b', drawingNumber: 'STD-B', name: 'B树物料', sourceInstancePath: `roots/${rootB.replaceAll('-', '')}/child` }],
      allowedScopes: ['StandardFormal'], preferredScope: 'StandardFormal', username: 'engineer', pending: false, progress: 0, error: '', canManage: true, canDecide: false,
    } })
    expect(wrapper.find('select[aria-label="发布装配体范围"]').exists()).toBe(false)
    await wrapper.get('input[aria-label="整包需求日期"]').setValue('2026-10-20')
    expect(wrapper.text()).toContain('A树物料')
    expect(wrapper.text()).toContain('B树物料')
    await wrapper.get('form').trigger('submit')
    const input = wrapper.emitted('create')?.[0]?.[0] as CreateReleasePackageInput
    expect(input.rootDocumentId).toBeUndefined()
    expect(input.selectedBomItemIds).toEqual(['a', 'b'])
    wrapper.unmount()
  })
  it('电气件BOM为空时不回退标准件且禁止创建草稿，有电气物料时仅发布电气项', async () => {
    const standard: BomItem = { id: 'standard', kind: 'Standard', sequence: 1, drawingNumber: 'STD-001', name: '标准接头', quantity: 1, unit: '001', revision: 'W1', complete: true }
    const electrical: BomItem = { ...standard, id: 'electrical', kind: 'Electrical', drawingNumber: 'ELEC-001', name: '电气模块' }
    const wrapper = mount(ReleaseCenter, { props: { releasePackage: null, standardItems: [standard], releaseItems: [], allowedScopes: ['ElectricalFormal'], preferredScope: 'ElectricalFormal', username: 'engineer', pending: false, progress: 0, error: '', canManage: true, canDecide: false } })
    await wrapper.get('input[aria-label="整包需求日期"]').setValue('2026-10-20')
    expect(wrapper.text()).not.toContain('标准接头')
    expect(wrapper.text()).toContain('当前没有可发布的物料')
    expect(wrapper.get('.pdm-release-draft-actions .pdm-primary-action').attributes('disabled')).toBeDefined()
    await wrapper.get('form').trigger('submit')
    expect(wrapper.emitted('create')).toBeUndefined()
    await wrapper.setProps({ releaseItems: [standard, electrical] })
    await flushPromises()
    expect(wrapper.text()).not.toContain('标准接头')
    expect(wrapper.text()).toContain('电气模块')
    await wrapper.get('form').trigger('submit')
    const input = wrapper.emitted('create')?.[0]?.[0] as CreateReleasePackageInput
    expect(input.scope).toBe('ElectricalFormal')
    expect(input.selectedBomItemIds).toEqual(['electrical'])
    wrapper.unmount()
  })
  it.each([true, false])('restores a compact draft selection only when all instance IDs are selected: %s', async complete => {
    const item: BomItem = { id: 'first', summaryItemIds: ['first', 'second'], kind: 'Standard', sequence: 1,
      drawingNumber: '01020003283', name: '接头', quantity: 2, unit: '001', revision: 'W1', complete: true }
    const wrapper = mount(ReleaseCenter, { props: {
      releasePackage: { id: 'draft', number: 'RP-DRAFT', state: '草稿', scope: 'StandardFormal', workflowVersion: 1,
        selectedBomItemIds: complete ? ['first', 'second'] : ['first'], createsManufacturingBaseline: true, locksDocuments: false,
        standardBomSnapshot: [item], nonStandardBomSnapshot: [], electricalBomSnapshot: [], steps: [] },
      releaseItems: [item], allowedScopes: ['StandardFormal'], username: 'engineer',
      pending: false, progress: 0, error: '', canManage: true, canDecide: true,
    } })
    await wrapper.findAll('button').find(button => button.text() === '编辑草稿')!.trigger('click')
    expect((wrapper.get('input[aria-label^="本次发布物料"]').element as HTMLInputElement).checked).toBe(complete)
    wrapper.unmount()
  })

  it.each(['StandardFormal', 'NonStandardWithDrawing', 'ElectricalFormal'] as const)('expands compact instances when creating %s and keeps deferred groups excluded', async scope => {
    const ids = Array.from({ length: 8 }, (_, index) => `instance-${index}`)
    const kind = scope === 'StandardFormal' ? 'Standard' : scope === 'ElectricalFormal' ? 'Electrical' : 'NonStandard'
    const item: BomItem = { id: ids[0], summaryItemIds: ids, kind, sequence: 1, drawingNumber: '01020003283',
      name: '直通卡套接头', quantity: 8, unit: '001', revision: 'W1', complete: true }
    const wrapper = mount(ReleaseCenter, { props: {
      releasePackage: null, allowedScopes: [scope], preferredScope: scope, releaseItems: [item,
        { ...item, id: 'deferred', summaryItemIds: ['deferred', 'deferred-2'], drawingNumber: 'OTHER', quantity: 2 }],
      username: 'engineer', pending: false, progress: 0, error: '', canManage: true, canDecide: true,
      drawingReviewCandidates: [{ candidateId: 'review', bomItemId: ids[0], drawingNumber: '01020003283', name: item.name,
        bomKinds: ['NonStandard'], modelRevision: 'W1', drawingRevision: 'W1', state: 'ApprovedCurrent', selectable: false }],
    } })
    await wrapper.get('input[aria-label="整包需求日期"]').setValue('2026-10-18')
    await wrapper.findAll('input[aria-label^="本次发布物料"]')[1]!.setValue(false)
    expect(wrapper.get('.pdm-release-draft-actions .pdm-primary-action').attributes('disabled')).toBeUndefined()
    await wrapper.get('form').trigger('submit')
    const created = wrapper.emitted<CreateReleasePackageInput[]>('create')![0]![0]!
    expect(created.selectedBomItemIds).toEqual(ids)
    expect(created.selectedBomItemQuantities).toBeUndefined()
    wrapper.unmount()
  })

  const frozenItem = (id: string): BomItem => ({ id, kind: 'Standard', sequence: 1, drawingNumber: id,
    name: `物料${id}`, specification: 'M5', brand: '国优', quantity: 1, unit: '个', revision: 'W1', complete: true })
  const frozenPackage = (items: BomItem[], scope: ReleasePackageSummary['scope'] = 'StandardSupplement'): ReleasePackageSummary => ({
    id: 'supplement-draft', number: 'RP-SUP', state: '草稿', scope, workflowVersion: 1, selectedBomItemIds: [],
    createsManufacturingBaseline: false, locksDocuments: false, steps: [],
    standardBomSnapshot: scope === 'StandardSupplement' ? items : [], nonStandardBomSnapshot: scope === 'NonStandardSupplement' ? items : [],
    electricalBomSnapshot: scope === 'ElectricalSupplement' ? items : [],
  })
  const frozenProps = { username: 'reviewer', pending: false, progress: 0, error: '', canManage: true, canDecide: false }
  it('冻结增补只展示本次选中图纸，不把其余二十张当前工作图的变化混入发布记录', async () => {
    const items: BomItem[] = Array.from({ length: 21 }, (_, index) => ({ ...frozenItem(`part-${index}`),
      kind: 'NonStandard', sourceDocumentId: `model-${index}`, specification: `MODEL-${index}` }))
    const release = { ...frozenPackage(items, 'NonStandardSupplement'), state: '发布失败', selectedBomItemIds: ['part-4'] }
    const candidates = items.map((item, index) => ({ candidateId: item.id!, bomItemId: item.id, modelDocumentId: item.sourceDocumentId,
      drawingDocumentId: `drawing-${index}`, drawingNumber: item.specification!, name: item.name, modelRevision: 'A-W1',
      bomKinds: ['NonStandard' as const], state: index === 4 ? 'ApprovedCurrent' as const : 'Ready' as const,
      selectable: false, hasUnreleasedDrawingChanges: true }))
    const wrapper = mount(ReleaseCenter, { props: { ...frozenProps, releasePackage: release, previousVersionItems: items, drawingReviewCandidates: candidates } })
    const rows = () => wrapper.findAll('.pdm-release-frozen-table tbody tr')
    expect(rows()).toHaveLength(1)
    expect(rows()[0]!.text()).toContain('MODEL-4')
    await wrapper.setProps({ drawingReviewCandidates: [] })
    expect(rows()).toHaveLength(1)
    expect(rows()[0]!.text()).toContain('MODEL-4')
    await wrapper.findAll('button').find(button => button.text() === '查看完整BOM')!.trigger('click')
    expect(rows()).toHaveLength(21)
    wrapper.unmount()
  })
  it('发布失败可退回草稿，已发布和发布处理中不能使用该入口', async () => {
    const release = { ...frozenPackage([frozenItem('failed')]), state: '发布失败' }
    const wrapper = mount(ReleaseCenter, { props: { ...frozenProps, releasePackage: release } })
    const back = wrapper.findAll('button').find(button => button.text() === '退回草稿')
    expect(back).toBeDefined()
    await back!.trigger('click')
    expect(wrapper.emitted('withdraw')?.[0]).toEqual([release.id])
    for (const state of ['已发布', '发布中']) {
      await wrapper.setProps({ releasePackage: { ...release, state } })
      expect(wrapper.findAll('button').some(button => button.text() === '退回草稿')).toBe(false)
    }
    wrapper.unmount()
  })
  // 增补/变更仅在对应BOM完成首次正式发布后可选，需要该已发布正式单作为前置。
  const publishedFormal = (scope: ReleasePackageSummary['scope']): ReleasePackageSummary => ({
    id: `published-${scope}`, number: `RP-${scope}`, state: '已发布', scope, workflowVersion: 1, selectedBomItemIds: [],
    createsManufacturingBaseline: false, locksDocuments: false, publishedAt: '2026-09-01T00:00:00Z',
    standardBomSnapshot: [], nonStandardBomSnapshot: [], electricalBomSnapshot: [], steps: [],
  })

  it.each(['StandardLongLead', 'StandardFormal', 'StandardSupplement', 'ElectricalLongLead', 'ElectricalFormal', 'ElectricalSupplement', 'NonStandardLongLead', 'NonStandardWithDrawing', 'NonStandardSupplement'] as const)('defaults approval history and package information to collapsed for %s', async scope => {
    const release = { ...publishedFormal(scope), steps: [{ id: 'approved', stage: '工程师自检', assignee: 'engineer', status: 'approved' as const, detail: '已同意' }] }
    const wrapper = mount(ReleaseCenter, { props: { ...frozenProps, releasePackage: release } })
    const approval = wrapper.get('details[aria-label="审批流程"]')
    const information = wrapper.get('details[aria-label="发布包信息"]')
    expect(approval.attributes('open')).toBeUndefined()
    expect(information.attributes('open')).toBeUndefined()
    expect(approval.text()).toContain('工程师自检')
    expect(information.text()).toContain(release.number)
    // A newly selected package starts closed even after opening the previous record.
    approval.element.setAttribute('open', '')
    information.element.setAttribute('open', '')
    await wrapper.setProps({ releasePackage: { ...release, id: 'next-package' } })
    expect(wrapper.get('details[aria-label="审批流程"]').attributes('open')).toBeUndefined()
    expect(wrapper.get('details[aria-label="发布包信息"]').attributes('open')).toBeUndefined()
    wrapper.unmount()
  })

  it.each(['StandardSupplement', 'ElectricalSupplement', 'NonStandardSupplement'] as const)('shows the OPL selector only for %s and restores its draft selections', async scope => {
    const item = { ...frozenItem('opl-item'), kind: scope === 'StandardSupplement' ? 'Standard' as const : scope === 'ElectricalSupplement' ? 'Electrical' as const : 'NonStandard' as const }
    const formalScope = scope === 'StandardSupplement' ? 'StandardFormal' : scope === 'ElectricalSupplement' ? 'ElectricalFormal' : 'NonStandardWithDrawing'
    const draft = { ...publishedFormal(formalScope), id: 'opl-draft', scope, state: '草稿', changeReason: '设计问题 / 设计变更',
      relatedOplIssueIds: ['opl-a', 'opl-b'], selectedBomItemIds: [item.id!], standardBomSnapshot: [item], nonStandardBomSnapshot: [item], electricalBomSnapshot: [item] }
    const wrapper = mount(ReleaseCenter, { props: { ...frozenProps, releasePackage: draft, releasePackages: [publishedFormal(formalScope)],
      allowedScopes: [scope], preferredScope: scope, releaseItems: [item], drawingReviewCandidates: [{ candidateId: 'review', bomItemId: item.id,
        drawingDocumentId: 'drawing', drawingNumber: item.drawingNumber, name: item.name, modelRevision: 'W1', bomKinds: [item.kind], state: 'ApprovedCurrent', selectable: false }] },
      global: { stubs: { ReleaseOplPicker: { name: 'ReleaseOplPicker', props: ['modelValue', 'readonly'], template: '<div class="opl-picker-stub" />' } } } })
    expect(wrapper.findComponent({ name: 'ReleaseOplPicker' }).props('modelValue')).toEqual(['opl-a', 'opl-b'])
    await wrapper.findAll('button').find(button => button.text() === '编辑草稿')!.trigger('click')
    await flushPromises()
    const picker = wrapper.findComponent({ name: 'ReleaseOplPicker' })
    expect(picker.props('modelValue')).toEqual(['opl-a', 'opl-b'])
    picker.vm.$emit('update:modelValue', ['opl-b'])
    await wrapper.get('input[aria-label="整包需求日期"]').setValue('2026-10-30')
    await wrapper.get('form').trigger('submit')
    expect(wrapper.emitted('updateDraft')?.[0]?.[1]).toMatchObject({ relatedOplIssueIds: ['opl-b'] })
    wrapper.unmount()
  })

  it.each(['StandardSupplement', 'ElectricalSupplement', 'NonStandardSupplement'] as const)('defaults %s to two additions and preserves the full snapshot toggle', async scope => {
    const previous = Array.from({ length: 28 }, (_, index) => frozenItem(`old-${index}`))
    const items = [...previous, frozenItem('new-1'), frozenItem('new-2')]
    const releasePackage = frozenPackage(items, scope)
    const snapshot = JSON.stringify(releasePackage)
    const wrapper = mount(ReleaseCenter, { props: { ...frozenProps, releasePackage, previousVersionItems: previous } })
    const rows = () => wrapper.findAll('.pdm-release-frozen-table tbody tr')
    const click = async (text: string) => wrapper.findAll('button').find(button => button.text() === text)!.trigger('click')
    expect(rows()).toHaveLength(2)
    expect(rows().map(row => row.findAll('td')[1]!.text())).toEqual(['new-1', 'new-2'])
    expect(wrapper.get('.pdm-release-diff-summary').text()).toContain('新增 2')
    expect(wrapper.findAll('.pdm-release-frozen-table th')).toHaveLength(12)
    await click('查看完整BOM')
    expect(rows()).toHaveLength(30)
    expect(wrapper.findAll('.pdm-release-frozen-table th')).toHaveLength(10)
    expect(wrapper.find('[aria-label="审批快照显示方式"]').exists()).toBe(false)
    expect(rows()).toHaveLength(30)
    await click('仅看本次变更')
    expect(rows()).toHaveLength(2)
    await click('查看完整BOM')
    await wrapper.setProps({ releasePackage: { ...releasePackage, id: 'another-draft' } })
    expect(rows()).toHaveLength(2)
    expect(JSON.stringify(releasePackage)).toBe(snapshot)
    expect(wrapper.emitted('submit')).toBeUndefined()
    wrapper.unmount()
  })

  it('shows original and new values, explicitly marks deletions and has an unchanged empty state', async () => {
    const previous = [frozenItem('same'), frozenItem('modified'), frozenItem('deleted')]
    const releasePackage = frozenPackage([previous[0]!, { ...previous[1]!, quantity: 3, brand: '新品牌' }])
    const wrapper = mount(ReleaseCenter, { props: { ...frozenProps, releasePackage, previousVersionItems: previous } })
    expect(wrapper.findAll('.release-change-tag').map(tag => tag.text())).toEqual(['修改', '删除'])
    expect(wrapper.get('.pdm-frozen-change-details').text()).toContain('数量：1 → 3')
    expect(wrapper.get('.pdm-frozen-change-details').text()).toContain('品牌：国优 → 新品牌')
    const deleted = wrapper.findAll('.pdm-release-frozen-table tbody tr')[1]!
    expect(deleted.text()).toContain('当前发布范围已移除此项')
    expect(deleted.findAll('td')[7]!.text()).toBe('0')
    expect(deleted.find('button').exists()).toBe(false)
    await wrapper.setProps({ releasePackage: frozenPackage(previous) })
    expect(wrapper.get('.pdm-empty-info').text()).toContain('本次没有增补或变更内容')
    expect(wrapper.get('.pdm-empty-info').attributes('colspan')).toBe('12')
    wrapper.unmount()
  })

  it('does not treat wear-part or impact annotations as release changes', () => {
    const previous = [{ ...frozenItem('auxiliary'), isWearPart: false, impactStage: 'Assembly' as const }]
    const annotated = [{ ...previous[0]!, isWearPart: true, impactStage: 'Commissioning' as const }]
    const wrapper = mount(ReleaseCenter, { props: { ...frozenProps, releasePackage: frozenPackage(annotated), previousVersionItems: previous } })

    expect(wrapper.get('.pdm-empty-info').text()).toContain('本次没有增补或变更内容')
    expect(wrapper.find('.release-change-tag').exists()).toBe(false)
  })

  it('paginates changes independently and resets pagination when switching the full BOM', async () => {
    const previous = [frozenItem('old')]
    const additions = Array.from({ length: 51 }, (_, index) => frozenItem(`new-${index}`))
    const wrapper = mount(ReleaseCenter, { props: { ...frozenProps, releasePackage: frozenPackage([...previous, ...additions]), previousVersionItems: previous } })
    await wrapper.get('button[aria-label="固化快照下一页"]').trigger('click')
    expect(wrapper.findAll('.pdm-release-frozen-table tbody tr')).toHaveLength(1)
    await wrapper.findAll('button').find(button => button.text() === '查看完整BOM')!.trigger('click')
    expect(wrapper.findAll('.pdm-release-frozen-table tbody tr')).toHaveLength(50)
    expect(wrapper.get('button[aria-label="固化快照上一页"]').attributes('disabled')).toBeDefined()
    wrapper.unmount()
  })

  it('shows total, previously released and new issue quantities and highlights existing comments', async () => {
    const item = { id: 'item-1', kind: 'Standard' as const, sequence: 1, drawingNumber: '1001', name: '平垫', quantity: 4, unit: '个', revision: 'W2', complete: true }
    const formal: ReleasePackageSummary = { id: 'formal', number: 'RP-FORMAL', state: '审批中', scope: 'StandardFormal', workflowVersion: 1,
      selectedBomItemIds: [], createsManufacturingBaseline: false, locksDocuments: false, steps: [],
      standardBomSnapshot: [item], nonStandardBomSnapshot: [], electricalBomSnapshot: [] }
    const history: ReleasePackageSummary[] = [1, 3].map((quantity, index) => ({ ...formal, id: `long-${index}`, scope: 'StandardLongLead',
      state: '已发布', publishedAt: `2026-09-0${index + 1}T00:00:00Z`, standardBomSnapshot: [{ ...item, quantity, unit: index ? '个' : '001' }] }))
    const comments = vi.spyOn(api, 'listReleaseItemComments').mockResolvedValue([{ id: 'comment-1', releasePackageId: formal.id,
      bomItemId: item.id, materialKey: 'material:1001|个', materialCode: '1001', materialName: '平垫', comment: '请核对', createdBy: 'reviewer', createdAt: '2026-09-07T00:00:00Z' }])
    const wrapper = mount(ReleaseCenter, { props: { releasePackage: formal, releasePackages: history, token: 'test-token',
      username: 'reviewer', pending: false, progress: 0, error: '', canManage: false, canDecide: false } })
    try {
      await flushPromises()
      expect(wrapper.findAll('.pdm-release-frozen-table th').map(cell => cell.text())).toEqual(['序号', '物料编码', '物料名称', '型号', '版本', '品牌', '备注', 'BOM总量', '前期已发布', '本次新增下发', '批注', '紧急程度 / 需求日期'])
      expect(wrapper.findAll('.pdm-release-frozen-table tbody td').slice(7, 10).map(cell => cell.text())).toEqual(['4', '4', '0'])
      expect(wrapper.get('.pdm-item-comment-action').classes()).toContain('has-comments')
      expect(wrapper.get('.pdm-item-comment-action').text()).toBe('批注（1）')
      await wrapper.setProps({ releasePackage: { ...formal, wholeSetMultiplier: 3 } })
      expect(wrapper.findAll('.pdm-release-frozen-table tbody td').slice(7, 10).map(cell => cell.text())).toEqual(['12', '4', '8'])
      await wrapper.setProps({ releasePackages: [history[0]!] })
      expect(wrapper.findAll('.pdm-release-frozen-table tbody td').slice(7, 10).map(cell => cell.text())).toEqual(['12', '1', '11'])
      expect(wrapper.findAll('.pdm-release-frozen-table th')).toHaveLength(12)
      expect(wrapper.findAll('.pdm-release-frozen-table tbody td').slice(7, 10).map(cell => cell.text())).toEqual(['12', '1', '11'])
    } finally { wrapper.unmount(); comments.mockRestore() }
  })

  it('renders rejected and skipped approval steps with their real decision details', () => {
    const releasePackage: ReleasePackageSummary = {
      id: 'release-rejected', number: 'RP-REJECTED-001', state: '已退回', scope: 'StandardFormal',
      workflowVersion: 1, selectedBomItemIds: [], createsManufacturingBaseline: false, locksDocuments: false,
      standardBomSnapshot: [], nonStandardBomSnapshot: [], electricalBomSnapshot: [],
      steps: [
        { id: 'task-1', stage: '机械工程师自检', assignee: 'engineer', decisionBy: 'engineer', status: 'approved', detail: '09/03 14:36', comment: '提交人自检' },
        { id: 'task-2', stage: '主设审核', assignee: 'designer', decisionBy: 'designer', status: 'rejected', detail: '09/03 16:11', comment: '结构需修改' },
        { id: 'task-3', stage: '机械主管批准', assignee: 'supervisor', status: 'skipped', detail: '本轮未到达' },
      ],
    }
    const wrapper = mount(ReleaseCenter, {
      props: {
        releasePackage, username: 'engineer', pending: false, progress: 0, error: '', canManage: false, canDecide: false,
      },
    })

    expect(wrapper.get('.pdm-approval-chain .is-rejected').text()).toContain('结构需修改')
    expect(wrapper.get('.pdm-approval-chain .is-skipped').text()).toContain('本轮未到达')
  })

  it('shows the completed approval state while publication continues in the background', () => {
    const releasePackage: ReleasePackageSummary = {
      id: 'release-publishing', number: 'RP-PUBLISHING-001', state: '发布中', scope: 'StandardFormal',
      workflowVersion: 1, selectedBomItemIds: [], createsManufacturingBaseline: false, locksDocuments: false,
      standardBomSnapshot: [], nonStandardBomSnapshot: [], electricalBomSnapshot: [], steps: [],
    }
    const wrapper = mount(ReleaseCenter, {
      props: { releasePackage, username: 'designer', pending: false, progress: 0, error: '', canManage: false, canDecide: false },
    })

    expect(wrapper.get('.pdm-release-completion-status').text()).toContain('审批已完成')
    expect(wrapper.get('.pdm-release-summary').text()).toContain('审批完成 · 后台发布中')
  })

  it('shows the completed approval state while publication waits for conversion and supports retry', async () => {
    const releasePackage: ReleasePackageSummary = {
      id: 'release-publishing', number: 'RP-PUBLISHING-001', state: '发布中', previewState: 'Pending', previewError: '转图连接超时', scope: 'StandardFormal',
      workflowVersion: 1, selectedBomItemIds: [], createsManufacturingBaseline: false, locksDocuments: false,
      standardBomSnapshot: [], nonStandardBomSnapshot: [], electricalBomSnapshot: [], steps: [],
    }
    const wrapper = mount(ReleaseCenter, {
      props: { releasePackage, username: 'designer', pending: false, progress: 0, error: '', canManage: false, canDecide: false },
    })

    expect(wrapper.get('.pdm-release-completion-status').text()).toContain('审批已完成')
    expect(wrapper.get('.pdm-release-summary').text()).toContain('审批完成 · 等待转图')
    expect(wrapper.get('.pdm-release-completion-status').text()).toContain('当前环节：等待图纸转换。最近一次转图错误：转图连接超时')
    const retry = wrapper.findAll('button').find(button => button.text() === '重试转图')!
    await retry.trigger('click')
    expect(wrapper.emitted('retryPreview')).toEqual([['release-publishing']])
  })

  it('requires a rejection reason and defaults blank approval comments to agreed', async () => {
    const releasePackage: ReleasePackageSummary = {
      id: 'release-current', number: 'RP-CURRENT-001', state: '审批中', scope: 'StandardFormal',
      workflowVersion: 1, selectedBomItemIds: [], createsManufacturingBaseline: false, locksDocuments: false,
      standardBomSnapshot: [], nonStandardBomSnapshot: [], electricalBomSnapshot: [],
      steps: [{ id: 'task-current', stage: '主设审核', assignee: 'designer', status: 'current', detail: '待处理' }],
    }
    const wrapper = mount(ReleaseCenter, {
      props: {
        releasePackage, username: 'designer', pending: false, progress: 0, error: '', canManage: false, canDecide: true,
      },
    })

    await wrapper.findAll('.pdm-decision-box button').find(button => button.text() === '退回')!.trigger('click')
    expect(wrapper.text()).toContain('请填写退回原因')
    expect(wrapper.emitted('decide')).toBeUndefined()
    await wrapper.findAll('.pdm-decision-box button').find(button => button.text() === '通过')!.trigger('click')
    expect(wrapper.emitted('decide')).toEqual([['task-current', 'Approved', '同意']])
  })

  it('creates a scoped release and delegates assignees to the configured workflow', async () => {
    const wrapper = mount(ReleaseCenter, {
      props: {
        releasePackage: null,
        releaseItems: [frozenItem('scoped-change')],
        username: 'admin',
        pending: false,
        progress: 0,
        error: '',
        canManage: true,
        canDecide: true,
        releasePackages: [publishedFormal('StandardFormal')],
      },
    })

    expect(wrapper.text()).not.toContain('仅输出选中的标准件')
    expect(wrapper.text()).not.toContain('发布包编号')
    expect(wrapper.text()).not.toContain('生效起始序列号')
    expect(wrapper.text()).not.toContain('生效截止序列号')
    await wrapper.get('select').setValue('StandardSupplement')
    const changeNumber = wrapper.get('input[aria-label="变更单号由系统自动生成"]')
    expect(changeNumber.element.closest('.pdm-release-type-row')).toBe(wrapper.get('.pdm-release-type-row').element)
    expect(changeNumber.attributes()).toHaveProperty('readonly')
    expect((changeNumber.element as HTMLInputElement).value).toBe('创建草稿后自动生成')
    await wrapper.get('input[aria-label="变更原因 物料问题 / 交期不满足"]').setValue(true)
    await wrapper.get('input[aria-label="变更原因 客户原因 / 客户需求变更"]').setValue(true)
    await wrapper.get('form').trigger('submit')

    const created = wrapper.emitted('create')?.[0]?.[0] as CreateReleasePackageInput
    expect(created.changeReason).toBe('物料问题 / 交期不满足；客户原因 / 客户需求变更')
    expect(created.scope).toBe('StandardSupplement')
    expect(created.selectedBomItemIds).toEqual(['scoped-change'])
    expect('number' in created).toBe(false)
    expect('changeNumber' in created).toBe(false)
    expect('effectiveSerialFrom' in created).toBe(false)
    expect('effectiveSerialTo' in created).toBe(false)
    expect(wrapper.text()).not.toContain('审批人由系统管理中的版本化模板自动解析')
  })

  it('requires other details and shows the snapshotted formal supplement quota', async () => {
    const formal: ReleasePackageSummary = {
      id: 'formal-1', number: 'RP-FORMAL-1', state: '已发布', scope: 'StandardFormal', workflowVersion: 1,
      selectedBomItemIds: [], createsManufacturingBaseline: true, locksDocuments: false, steps: [],
      standardBomSnapshot: [], nonStandardBomSnapshot: [], electricalBomSnapshot: [],
      publishedAt: '2026-09-01T00:00:00Z', formalSupplementPolicySnapshotted: true,
      formalSupplementMaximumCount: 2, formalSupplementValidDays: null,
    }
    const used: ReleasePackageSummary = {
      id: 'supplement-1', number: 'RP-SUP-1', state: '已发布', scope: 'StandardSupplement', workflowVersion: 1,
      selectedBomItemIds: [], createsManufacturingBaseline: true, locksDocuments: false, steps: [],
      standardBomSnapshot: [], nonStandardBomSnapshot: [], electricalBomSnapshot: [],
      changeReason: '正式补充', changeReasonSelections: [{ categoryCode: 'FormalSupplement', reasonCode: 'FormalSupplement', category: '正式补充', reason: '正式补充' }],
    }
    const wrapper = mount(ReleaseCenter, {
      props: {
        releasePackage: null, allowedScopes: ['StandardSupplement'], preferredScope: 'StandardSupplement',
        releaseItems: [frozenItem('reason-change')],
        releasePackages: [formal, used], username: 'engineer', pending: false, progress: 0, error: '', canManage: true, canDecide: true,
      },
    })

    expect(wrapper.text()).toContain('已发布 1/2 次')
    await wrapper.get('input[aria-label="变更原因 其他"]').setValue(true)
    expect(wrapper.get('.pdm-release-draft-actions .pdm-primary-action').attributes()).toHaveProperty('disabled')
    await wrapper.get('input[aria-label="其他具体原因"]').setValue('现场临时调整')
    expect(wrapper.get('.pdm-release-draft-actions .pdm-primary-action').attributes()).not.toHaveProperty('disabled')
    await wrapper.get('form').trigger('submit')
    expect((wrapper.emitted('create')?.[0]?.[0] as CreateReleasePackageInput).changeReason).toBe('其他 / 现场临时调整')
  })

  it('limits release types per BOM and submits the selected frozen package', async () => {
    const releasePackage: ReleasePackageSummary = {
      id: 'release-electrical-1', number: 'RP-E-001', state: '草稿', steps: [], scope: 'ElectricalFormal',
      workflowCode: 'electrical-release', workflowVersion: 2, selectedBomItemIds: [], createsManufacturingBaseline: false, locksDocuments: false,
      standardBomRevision: 'S-V01', electricalBomRevision: 'E-V02',
      standardBomSnapshot: [], nonStandardBomSnapshot: [],
      electricalBomSnapshot: [{ id: 'electrical-1', kind: 'Electrical', sequence: 1, drawingNumber: 'EL-001', name: '电气元件', specification: 'M18', brand: 'SMC', remark: '安装备注', quantity: 2, unit: '个', revision: 'W2', complete: true }],
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
    expect(wrapper.text()).toContain('电气发布审批 · 第2版')
    expect(wrapper.text()).not.toContain('electrical-release')
    expect(wrapper.text()).toContain('标准件：S-V01 · 非标件：未发布 · 电气件：E-V02')
    expect(wrapper.findAll('button').some(button => button.text() === '新建发布包')).toBe(false)
    expect(wrapper.text()).not.toContain('生效序列号')
    expect(wrapper.text()).toContain('EL-001')
    expect(wrapper.text()).toContain('修改 1')
    expect(wrapper.findAll('.pdm-release-frozen-table th').map(cell => cell.text())).toEqual(['序号', '物料编码', '物料名称', '型号', '版本', '品牌', '备注', 'BOM总量', '前期已发布', '本次新增下发', '批注', '紧急程度 / 需求日期'])
    expect(wrapper.findAll('.pdm-release-frozen-table tbody tr').at(0)!.findAll('td').map(cell => cell.text())).toEqual(['1', 'EL-001', '电气元件', 'M18', '未发布', 'SMC', '安装备注', '2', '0', '2', '批注', '普通 · 未设置'])
    expect(wrapper.findAll('.pdm-release-frozen-table tbody tr').at(0)!.findAll('td.is-release-centered')).toHaveLength(9)
    expect(wrapper.findAll('.pdm-release-frozen-table col')).toHaveLength(12)
    const topWorkflow = wrapper.get('.pdm-release-top-workflow')
    const summary = wrapper.get('.pdm-release-summary')
    expect(topWorkflow.find('.pdm-release-preparation').exists()).toBe(true)
    expect(topWorkflow.findAll('button').map(button => button.text())).toContain('提交审批')
    expect(topWorkflow.element.compareDocumentPosition(summary.element) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy()
    expect(summary.element.compareDocumentPosition(wrapper.get('.pdm-release-frozen-snapshot').element) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy()
    await wrapper.findAll('button').find(button => button.text() === '提交审批')!.trigger('click')
    expect(wrapper.emitted('submit')).toEqual([['release-electrical-1']])
  })

  it('shows long-lead batch revision without unrelated internal BOM hashes', () => {
    const releasePackage: ReleasePackageSummary = {
      id: 'release-long-lead', number: 'RP-LL-001', state: '已发布', steps: [], scope: 'StandardLongLead',
      workflowCode: 'mechanical-release', workflowVersion: 1, selectedBomItemIds: [], createsManufacturingBaseline: false, locksDocuments: false,
      standardBomRevision: 'LL-ED75AF81', electricalBomRevision: 'E-4F53CDA1',
      standardBomSnapshot: [], nonStandardBomSnapshot: [], electricalBomSnapshot: [],
    }
    const wrapper = mount(ReleaseCenter, {
      props: {
        releasePackage, username: 'engineer', pending: false, progress: 0, error: '',
        canManage: true, canDecide: false,
      },
    })

    expect(wrapper.text()).toContain('标准件：长交期批次 LL-ED75AF81 · 非标件：不适用 · 电气件：不适用')
    expect(wrapper.text()).not.toContain('E-4F53CDA1')
  })

  it('lets release managers retry U9C automation for a published long-lead package', async () => {
    const releasePackage: ReleasePackageSummary = {
      id: 'release-long-lead', number: 'RP-LL-001', state: '已发布', steps: [], scope: 'StandardLongLead',
      workflowCode: 'mechanical-release', workflowVersion: 1, selectedBomItemIds: [], createsManufacturingBaseline: false, locksDocuments: false,
      standardBomSnapshot: [], nonStandardBomSnapshot: [], electricalBomSnapshot: [],
    }
    const wrapper = mount(ReleaseCenter, {
      props: {
        releasePackage, username: 'engineer', pending: false, progress: 0, error: '',
        canManage: true, canDecide: false,
      },
    })

    const retry = wrapper.findAll('button').find(button => button.text() === '重试U9C')
    expect(retry).toBeDefined()
    await retry!.trigger('click')
    expect(wrapper.emitted('retryU9')).toEqual([['release-long-lead']])
  })

  it('shows non-standard long-lead, formal and supplement scopes with the same controls as standard parts', async () => {
    const item: BomItem = { id: 'ns-1', kind: 'NonStandard', sequence: 1, drawingNumber: 'NS-001',
      name: '非标长交期件', quantity: 2, unit: '个', revision: 'W1', complete: true }
    const wrapper = mount(ReleaseCenter, {
      props: {
        releasePackage: null, allowedScopes: ['NonStandardLongLead', 'NonStandardWithDrawing', 'NonStandardSupplement'],
        preferredScope: 'NonStandardLongLead', releaseItems: [item], username: 'engineer',
        drawingReviewCandidates: [{ candidateId: 'review-ns-1', bomItemId: 'ns-1', drawingNumber: 'NS-001', name: '非标长交期件', bomKinds: ['NonStandard'], modelRevision: 'W1', drawingRevision: 'W1', state: 'ApprovedCurrent', selectable: false }],
        pending: false, progress: 0, error: '', canManage: true, canDecide: true,
      },
    })

    expect(wrapper.findAll('.pdm-release-type-row > label:first-child option').map(option => option.text())).toEqual([
      '非标件 · 长交期BOM发布', '非标件BOM + 图纸 · 正式发布',
    ])
    expect(wrapper.get('.release-detail-picker legend').text()).toContain('选择长交期非标件')
    await wrapper.get('input[aria-label="选择长交期物料 NS-001"]').setValue(true)
    await wrapper.get('form').trigger('submit')
    const created = wrapper.emitted<CreateReleasePackageInput[]>('create')![0]![0]!
    expect(created.scope).toBe('NonStandardLongLead')
    expect(created.selectedBomItemIds).toEqual(['ns-1'])
    expect(created.selectedBomItemQuantities).toEqual({ 'ns-1': 2 })
  })

  it('lets a non-standard formal release defer an item whose drawing is not approved yet', async () => {
    const approved: BomItem = { id: 'ns-ok', kind: 'NonStandard', sequence: 1, drawingNumber: 'NS-200',
      name: '已批准件', quantity: 1, unit: '个', revision: 'W1', complete: true, sourceDocumentId: 'model-ok' }
    const pending: BomItem = { id: 'ns-pending', kind: 'NonStandard', sequence: 2, drawingNumber: 'NS-201',
      name: '待审图件', quantity: 1, unit: '个', revision: 'W1', complete: true, sourceDocumentId: 'model-pending' }
    const wrapper = mount(ReleaseCenter, {
      props: {
        releasePackage: null, allowedScopes: ['NonStandardWithDrawing'], preferredScope: 'NonStandardWithDrawing',
        releaseItems: [approved, pending], username: 'engineer', pending: false, progress: 0, error: '', canManage: true, canDecide: true,
        drawingReviewCandidates: [
          { candidateId: 'review-ok', bomItemId: 'ns-ok', modelDocumentId: 'model-ok', drawingNumber: 'NS-200', name: '已批准件', bomKinds: ['NonStandard'], modelRevision: 'W1', drawingRevision: 'W1', state: 'ApprovedCurrent', selectable: false },
          { candidateId: 'review-pending', bomItemId: 'ns-pending', modelDocumentId: 'model-pending', drawingNumber: 'NS-201', name: '待审图件', bomKinds: ['NonStandard'], modelRevision: 'W1', drawingRevision: 'W1', state: 'Ready', reason: '当前工程图尚未发起审核', selectable: true },
        ],
      },
    })

    expect(wrapper.get('.pdm-release-draft-actions .pdm-primary-action').attributes()).toHaveProperty('disabled')
    expect(wrapper.text()).toContain('当前发布范围包含未完成当前版本图纸审核的非标件')
    await wrapper.get('input[aria-label="整包需求日期"]').setValue('2026-10-01')
    await wrapper.get('input[aria-label="本次发布物料 NS-201"]').setValue(false)
    expect(wrapper.get('.pdm-release-draft-actions .pdm-primary-action').attributes('disabled')).toBeUndefined()
    await wrapper.get('.pdm-release-draft-actions .pdm-primary-action').trigger('submit')
    expect(wrapper.emitted('create')?.at(0)?.at(0)).toMatchObject({
      scope: 'NonStandardWithDrawing',
      selectedBomItemIds: ['ns-ok'],
    })
    wrapper.unmount()
  })

  it('allows approved unnumbered items after unselecting unreviewed unnumbered items', async () => {
    const approved: BomItem = { id: 'ns-ok', kind: 'NonStandard', sequence: 1, drawingNumber: '',
      name: '已批准件', quantity: 1, unit: '个', revision: 'W1', complete: true, sourceDocumentId: 'model-ok' }
    const pending: BomItem = { id: 'ns-pending', kind: 'NonStandard', sequence: 2, drawingNumber: '',
      name: '待审图件', quantity: 1, unit: '个', revision: 'W1', complete: true, sourceDocumentId: 'model-pending' }
    const wrapper = mount(ReleaseCenter, {
      props: {
        releasePackage: null, allowedScopes: ['NonStandardWithDrawing'], preferredScope: 'NonStandardWithDrawing',
        releaseItems: [approved, pending], username: 'engineer', pending: false, progress: 0, error: '', canManage: true, canDecide: true,
        drawingReviewCandidates: [
          { candidateId: 'review-ok', bomItemId: 'ns-ok', modelDocumentId: 'model-ok', drawingNumber: '', name: '已批准件', bomKinds: ['NonStandard'], modelRevision: 'W1', drawingRevision: 'W1', state: 'ApprovedCurrent', selectable: false },
          { candidateId: 'review-pending', bomItemId: 'ns-pending', modelDocumentId: 'model-pending', drawingNumber: '', name: '待审图件', bomKinds: ['NonStandard'], modelRevision: 'W1', drawingRevision: 'W1', state: 'Ready', reason: '当前工程图尚未发起审核', selectable: true },
        ],
      },
    })

    expect(wrapper.get('.pdm-release-draft-actions .pdm-primary-action').attributes()).toHaveProperty('disabled')
    expect(wrapper.text()).toContain('当前发布范围包含未完成当前版本图纸审核的非标件')
    await wrapper.get('input[aria-label="整包需求日期"]').setValue('2026-10-01')
    await wrapper.findAll('input[aria-label^="本次发布物料"]')[1]!.setValue(false)
    expect(wrapper.get('.pdm-release-draft-actions .pdm-primary-action').attributes('disabled')).toBeUndefined()
    await wrapper.get('.pdm-release-draft-actions .pdm-primary-action').trigger('submit')
    expect(wrapper.emitted('create')?.at(0)?.at(0)).toMatchObject({
      scope: 'NonStandardWithDrawing',
      selectedBomItemIds: ['ns-ok'],
    })
    wrapper.unmount()
  })

  it('defaults the formal drawing delivery date to the assembly task start date', async () => {
    const plan = { tasks: [{ stage: 'Assembly', plannedStart: '2026-11-02' }] }
    const readPlan = vi.spyOn(api, 'readProjectPlan').mockResolvedValue(plan as never)
    const wrapper = mount(ReleaseCenter, {
      props: {
        releasePackage: null, projectId: 'project-1', token: 'test-token', allowedScopes: ['NonStandardWithDrawing'], preferredScope: 'NonStandardWithDrawing',
        releaseItems: [frozenItem('ns-formal')], username: 'engineer', pending: false, progress: 0, error: '', canManage: true, canDecide: false,
      },
    })

    try {
      await flushPromises()
      expect((wrapper.get('input[aria-label="整包需求日期"]').element as HTMLInputElement).value).toBe('2026-11-02')
      expect(wrapper.get('.pdm-release-assembly-note').text()).toBe('计划装配节点')
      await wrapper.get('input[aria-label="整包需求日期"]').setValue('2026-11-03')
      expect(wrapper.find('.pdm-release-assembly-note').exists()).toBe(false)
    } finally {
      readPlan.mockRestore()
      wrapper.unmount()
    }
  })

  it('defaults long-lead delivery dates to the assembly task start date', async () => {
    const plan = { tasks: [{ stage: 'Assembly', plannedStart: '2026-11-02' }] }
    const readPlan = vi.spyOn(api, 'readProjectPlan').mockResolvedValue(plan as never)
    const wrapper = mount(ReleaseCenter, {
      props: {
        releasePackage: null, projectId: 'project-1', token: 'test-token', allowedScopes: ['StandardLongLead'], preferredScope: 'StandardLongLead',
        releaseItems: [frozenItem('standard-long-lead')], username: 'engineer', pending: false, progress: 0, error: '', canManage: true, canDecide: false,
      },
    })

    try {
      await flushPromises()
      expect((wrapper.get('input[aria-label="整包需求日期"]').element as HTMLInputElement).value).toBe('2026-11-02')
      expect(wrapper.get('.pdm-release-assembly-note').text()).toBe('计划装配节点')
    } finally {
      readPlan.mockRestore()
      wrapper.unmount()
    }
  })

  it('keeps the four formal drawing release controls in the dedicated release row', () => {
    const wrapper = mount(ReleaseCenter, {
      props: {
        releasePackage: null, allowedScopes: ['NonStandardWithDrawing'], preferredScope: 'NonStandardWithDrawing',
        releaseItems: [frozenItem('ns-formal')], username: 'engineer', pending: false, progress: 0, error: '', canManage: true, canDecide: false,
      },
    })

    expect(wrapper.get('.pdm-release-create-header').classes()).toContain('has-drawing-delivery')
    expect(wrapper.findAll('.pdm-release-type-row > label').map(label => label.text().replace(/\s+/g, '')))
      .toEqual(['发布类型非标件BOM+图纸·正式发布', '整套倍率', '整包紧急程度普通优先紧急', '整包需求日期'])
    expect(wrapper.get('.pdm-release-draft-actions').element.parentElement).toBe(wrapper.get('.pdm-release-type-row').element)
    wrapper.unmount()
  })

  it('defaults the drawing delivery date to ten days from today when no assembly start date is available', async () => {
    const today = new Date()
    today.setHours(0, 0, 0, 0)
    today.setDate(today.getDate() + 10)
    const expected = `${today.getFullYear()}-${String(today.getMonth() + 1).padStart(2, '0')}-${String(today.getDate()).padStart(2, '0')}`
    const readPlan = vi.spyOn(api, 'readProjectPlan').mockResolvedValue(null)
    const wrapper = mount(ReleaseCenter, {
      props: {
        releasePackage: null, projectId: 'project-1', token: 'test-token', allowedScopes: ['NonStandardWithDrawing'], preferredScope: 'NonStandardWithDrawing',
        releaseItems: [frozenItem('ns-formal')], username: 'engineer', pending: false, progress: 0, error: '', canManage: true, canDecide: false,
      },
    })

    try {
      await flushPromises()
      expect((wrapper.get('input[aria-label="整包需求日期"]').element as HTMLInputElement).value).toBe(expected)
    } finally {
      readPlan.mockRestore()
      wrapper.unmount()
    }
  })

  it('disables non-standard release selection until the current drawing version is approved', async () => {
    const item: BomItem = { id: 'ns-unreviewed', kind: 'NonStandard', sequence: 1, drawingNumber: '',
      name: '待审图非标件', quantity: 1, unit: '个', revision: 'W1', complete: true, sourceDocumentId: 'model-unreviewed' }
    const wrapper = mount(ReleaseCenter, {
      props: {
        releasePackage: null, allowedScopes: ['NonStandardLongLead'], preferredScope: 'NonStandardLongLead',
        releaseItems: [item], username: 'engineer', pending: false, progress: 0, error: '', canManage: true, canDecide: true,
        drawingReviewCandidates: [{ candidateId: 'review-unreviewed', bomItemId: 'ns-unreviewed', modelDocumentId: 'model-unreviewed', drawingNumber: '', name: '待审图非标件', bomKinds: ['NonStandard'], modelRevision: 'W1', drawingRevision: 'W1', state: 'Ready', reason: '当前工程图尚未发起审核', selectable: true }],
      },
    })

    const checkbox = wrapper.get('input[aria-label="选择长交期物料 "]')
    expect(checkbox.attributes()).toHaveProperty('disabled')
    expect(wrapper.text()).toContain('当前工程图尚未发起审核')
    expect(wrapper.text()).toContain('不能勾选发布')
    expect(wrapper.get('.pdm-release-draft-actions .pdm-primary-action').attributes()).toHaveProperty('disabled')
    await checkbox.setValue(true)
    expect(wrapper.emitted('create')).toBeUndefined()
  })

  it('does not override current review status with a historical approved package', async () => {
    const item: BomItem = { id: 'ns-approved', kind: 'NonStandard', sequence: 1, drawingNumber: 'NS-100',
      name: '已批准非标件', quantity: 1, unit: '个', revision: 'W1', complete: true, sourceDocumentId: 'model-approved' }
    const wrapper = mount(ReleaseCenter, {
      props: {
        releasePackage: null, allowedScopes: ['NonStandardLongLead'], preferredScope: 'NonStandardLongLead',
        releaseItems: [item], username: 'engineer', pending: false, progress: 0, error: '', canManage: true, canDecide: true,
        drawingReviewCandidates: [{ candidateId: 'review-approved', bomItemId: 'ns-approved', modelDocumentId: 'model-approved', drawingDocumentId: 'drawing-approved', drawingNumber: 'NS-100', name: '已批准非标件', bomKinds: ['NonStandard'], modelRevision: 'W1', drawingRevision: 'W1', state: 'InReview', reason: '已在其他审核单中', selectable: false }],
        drawingReviews: [{
          id: 'review-approved', projectId: 'project-1', number: 'DR-1', state: 'WritingProperties', createdBy: 'submitter', createdAt: '2026-09-19T00:00:00Z', markups: [],
          items: [{
            id: 'item-approved', packageId: 'review-approved', bomItemId: 'ns-approved', drawingNumber: 'NS-100', name: '已批准非标件',
            modelDocumentId: 'model-approved', modelVersionId: 'v1', modelRevision: 'W1', modelSha256: 'A'.repeat(64), modelCreatedBy: 'designer',
            drawingDocumentId: 'drawing-approved', drawingVersionId: 'v1', drawingRevision: 'W1', drawingSha256: 'B'.repeat(64), drawingCreatedBy: 'designer',
            modelState: 'NotRequired', drawingState: 'Approved', effectiveModelVersionId: 'v1', effectiveDrawingVersionId: 'v1',
          }],
        }],
      },
    })

    const checkbox = wrapper.get('input[aria-label="选择长交期物料 NS-100"]')
    expect(checkbox.attributes()).toHaveProperty('disabled')
    expect(wrapper.text()).toContain('图纸待审核')
    expect(wrapper.text()).not.toContain('图纸已批准')
    await wrapper.setProps({ drawingReviewCandidates: [{ ...wrapper.props('drawingReviewCandidates')![0]!, state: 'ApprovedCurrent' }] })
    expect(checkbox.attributes('disabled')).toBeUndefined()
    expect(wrapper.text()).toContain('图纸已批准')
  })

  it('lets the current assignee decide or transfer without showing the withdraw action', () => {
    const releasePackage: ReleasePackageSummary = {
      id: 'release-main-designer', number: 'RP-M-001', state: '审批中', scope: 'StandardFormal',
      workflowVersion: 1, selectedBomItemIds: [], createsManufacturingBaseline: false, locksDocuments: false,
      standardBomSnapshot: [], nonStandardBomSnapshot: [], electricalBomSnapshot: [],
      steps: [{ id: 'task-main-designer', stage: '主设审核', assignee: 'main-designer', status: 'current', detail: '待处理' }],
    }
    const wrapper = mount(ReleaseCenter, {
      props: {
        releasePackage, username: 'main-designer', pending: false, progress: 0, error: '',
        canManage: true, canDecide: false,
      },
    })

    expect(wrapper.findAll('.pdm-decision-box .pdm-manager-actions button').map(button => button.text())).toEqual(['退回', '转交', '通过'])
    expect(wrapper.findAll('button').some(button => button.text() === '撤回审批')).toBe(false)
  })

  it('lets a rejected package be withdrawn back to draft so the BOM can be re-bound', async () => {
    const releasePackage: ReleasePackageSummary = {
      id: 'release-rejected-withdraw', number: 'RP-R-002', state: '已退回', scope: 'NonStandardWithDrawing',
      workflowVersion: 1, selectedBomItemIds: [], createsManufacturingBaseline: false, locksDocuments: true,
      standardBomSnapshot: [], nonStandardBomSnapshot: [], electricalBomSnapshot: [],
      steps: [{ id: 'task-supervisor', stage: '机械主管批准', assignee: 'supervisor', status: 'rejected', detail: '已退回' }],
    }
    const wrapper = mount(ReleaseCenter, {
      props: {
        releasePackage, username: 'initiator', pending: false, progress: 0, error: '',
        canManage: true, canDecide: false,
      },
    })

    const withdraw = wrapper.get('.pdm-withdraw-decision')
    expect(withdraw.text()).toContain('退回后撤回：发布包恢复为草稿')
    await withdraw.findAll('button').find(button => button.text() === '撤回为草稿')!.trigger('click')
    expect(wrapper.emitted('withdraw')).toEqual([['release-rejected-withdraw']])
    wrapper.unmount()
  })

  it('places the withdraw action in the top workflow area used by approval decisions', () => {
    const releasePackage: ReleasePackageSummary = {
      id: 'release-withdraw', number: 'RP-W-001', state: '待批准', scope: 'StandardFormal',
      workflowVersion: 1, selectedBomItemIds: [], createsManufacturingBaseline: false, locksDocuments: false,
      standardBomSnapshot: [], nonStandardBomSnapshot: [], electricalBomSnapshot: [],
      steps: [{ id: 'task-supervisor', stage: '机械主管批准', assignee: 'supervisor', status: 'current', detail: '待处理' }],
    }
    const wrapper = mount(ReleaseCenter, {
      props: {
        releasePackage, username: 'initiator', pending: false, progress: 0, error: '',
        canManage: true, canDecide: false,
      },
    })

    expect(wrapper.findAll('.pdm-withdraw-decision .pdm-manager-actions button').map(button => button.text())).toEqual(['撤回审批'])
    expect(wrapper.get('.pdm-withdraw-decision').text()).toContain('撤回后当前BOM版本恢复为草稿。')
    expect(wrapper.get('.pdm-release-top-workflow').find('.pdm-withdraw-decision').exists()).toBe(true)
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
    await wrapper.setProps({ allowedScopes: ['StandardSupplement'], preferredScope: 'StandardSupplement', releasePackages: [publishedFormal('StandardFormal')] })
    expect(wrapper.findAll('.pdm-release-type-row > label:first-child option').map(option => option.text())).toEqual(['标准件 · 增补/变更'])
    expect((wrapper.get('.pdm-release-type-row select').element as HTMLSelectElement).value).toBe('StandardSupplement')
  })

  it('只允许在对应BOM首次正式发布后选择增补/变更，正式发布后不再提供长交期与正式发布', async () => {
    const wrapper = mount(ReleaseCenter, {
      props: {
        releasePackage: null,
        allowedScopes: ['StandardLongLead', 'StandardFormal', 'StandardSupplement'],
        preferredScope: 'StandardLongLead',
        username: 'engineer', pending: false, progress: 0, error: '', canManage: true, canDecide: true,
      },
    })

    // 尚未首次正式发布：只有长交期与正式发布。
    expect(wrapper.findAll('.pdm-release-type-row > label:first-child option').map(option => option.text()))
      .toEqual(['标准件 · 长交期BOM发布', '标准件 · 正式发布'])

    // 首次正式发布完成后：只剩增补/变更，长交期不再可选。
    await wrapper.setProps({
      releasePackages: [{
        id: 'release-formal', number: 'RP-S-001', state: '已发布', scope: 'StandardFormal', workflowVersion: 1,
        selectedBomItemIds: [], createsManufacturingBaseline: false, locksDocuments: false, publishedAt: '2026-09-01T00:00:00Z',
        standardBomSnapshot: [], nonStandardBomSnapshot: [], electricalBomSnapshot: [], steps: [],
      }],
    })
    expect(wrapper.findAll('.pdm-release-type-row > label:first-child option').map(option => option.text()))
      .toEqual(['标准件 · 增补/变更'])
  })

  it('keeps one fixed header and detail table while switching standard release types', async () => {
    const wrapper = mount(ReleaseCenter, {
      props: {
        releasePackage: null,
        allowedScopes: ['StandardLongLead', 'StandardFormal', 'StandardSupplement'],
        preferredScope: 'StandardLongLead',
        standardItems: [
          { id: 'item-current', kind: 'Standard', sequence: 1, drawingNumber: 'STD-001', name: '当前件', specification: 'M12', brand: 'SMC', quantity: 2, unit: '个', revision: 'W2', complete: true },
        ],
        previousVersionItems: [
          { id: 'item-previous', kind: 'Standard', sequence: 1, drawingNumber: 'STD-001', name: '当前件', specification: 'M12', brand: 'SMC', quantity: 1, unit: '个', revision: 'W1', complete: true },
        ],
        username: 'engineer', pending: false, progress: 0, error: '', canManage: true, canDecide: true,
      },
    })

    const header = wrapper.get('.pdm-release-create-header').element
    const parameterSlot = wrapper.get('.pdm-release-parameter-slot').element
    const detail = wrapper.get('.release-detail-picker').element
    const table = wrapper.get('.release-detail-picker table').element
    const columnHeaders = wrapper.findAll('.release-detail-picker th').map(cell => cell.text())
    expect(wrapper.findAll('.release-detail-picker col')).toHaveLength(12)
    expect(wrapper.findAll('.release-detail-picker tbody tr').at(0)!.findAll('td.is-release-centered')).toHaveLength(6)

    await wrapper.get('.pdm-release-type-row select').setValue('StandardFormal')
    expect(wrapper.get('.pdm-release-create-header').element).toBe(header)
    expect(wrapper.get('.pdm-release-parameter-slot').element).toBe(parameterSlot)
    expect(wrapper.get('.release-detail-picker').element).toBe(detail)
    expect(wrapper.get('.release-detail-picker table').element).toBe(table)
    expect(wrapper.findAll('.release-detail-picker th').map(cell => cell.text())).toEqual(columnHeaders)
    expect(wrapper.get('.release-detail-picker legend').text()).toContain('正式发布内容')
    expect(wrapper.findAll('.release-detail-picker tbody tr').at(0)!.findAll('td.is-release-centered')).toHaveLength(6)

    // 正式发布完成后增补/变更才可选，并继续保留逐项交付字段。
    await wrapper.setProps({ releasePackages: [publishedFormal('StandardFormal')] })
    await wrapper.get('.pdm-release-type-row select').setValue('StandardSupplement')
    expect(wrapper.get('.pdm-release-create-header').element).toBe(header)
    expect(wrapper.get('.pdm-release-parameter-slot').element).toBe(parameterSlot)
    expect(wrapper.get('.release-detail-picker').element).toBe(detail)
    expect(wrapper.get('.release-detail-picker table').element).toBe(table)
    expect(wrapper.findAll('.release-detail-picker th').map(cell => cell.text())).toEqual([...columnHeaders.slice(0, -2), '变更明细', ...columnHeaders.slice(-2)])
    expect(wrapper.get('.release-detail-picker legend').text()).toContain('增补/变更内容')
    expect(wrapper.findAll('.release-detail-picker tbody tr').at(0)!.findAll('td.is-release-centered')).toHaveLength(6)
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

    expect(wrapper.get('.pdm-release-draft-actions .pdm-primary-action').text()).toBe('创建草稿')
    expect(wrapper.text()).toContain('备注')
    expect(wrapper.get('textarea').attributes()).not.toHaveProperty('required')
    expect(wrapper.findAll('.release-detail-picker th').map(cell => cell.text())).toEqual(['标记', '序号', '物料编码', '名称', '型号', '品牌', '可发布', '总发布', '备注', '状态', '紧急', '需求时间'])
    expect(wrapper.findAll('.release-detail-picker tbody tr').at(0)!.findAll('td').slice(1).map(cell => cell.text())).toEqual(['1', 'STD-001', '长交期件', 'M12', 'SMC', '1', '—', '提前采购', '剩余可发布', '普通优先紧急', ''])
    expect(wrapper.get('.pdm-release-draft-actions .pdm-primary-action').attributes()).toHaveProperty('disabled')

    await wrapper.get('input[aria-label="选择长交期物料 STD-001"]').setValue(true)
    expect(wrapper.get('.pdm-release-draft-actions .pdm-primary-action').attributes()).not.toHaveProperty('disabled')
  })

  it('summarizes long-lead candidates, subtracts published quantities, and submits only the remainder', async () => {
    const wrapper = mount(ReleaseCenter, {
      props: {
        releasePackage: null,
        allowedScopes: ['StandardLongLead'],
        standardItems: [
          { id: 'current-1', kind: 'Standard', sequence: 1, drawingNumber: 'STD-001', name: '部分已发布件', quantity: 4, unit: '个', revision: 'W1', complete: true },
          { id: 'candidate-1', kind: 'Standard', sequence: 3, drawingNumber: 'STD-002', name: '待发布件', specification: 'M8', brand: 'FESTO', quantity: 3, unit: '个', revision: 'W1', complete: true },
          { id: 'candidate-2', kind: 'Standard', sequence: 4, drawingNumber: 'STD-002', name: '待发布件', specification: 'M8', brand: 'FESTO', quantity: 4, unit: '个', revision: 'W1', complete: true },
          { id: 'fully-published-1', kind: 'Standard', sequence: 5, drawingNumber: 'STD-003', name: '全部已发布件', quantity: 2, unit: '个', revision: 'W1', complete: true },
        ],
        longLeadPublishedItems: [
          { id: 'published-1', kind: 'Standard', sequence: 1, drawingNumber: 'STD-001', name: '部分已发布件', quantity: 1, unit: '个', revision: 'W1', complete: true },
          { id: 'published-2', kind: 'Standard', sequence: 2, drawingNumber: 'STD-003', name: '全部已发布件', quantity: 2, unit: '个', revision: 'W1', complete: true },
        ],
        username: 'engineer', pending: false, progress: 0, error: '', canManage: true, canDecide: true,
      },
    })

    expect(wrapper.get('.release-detail-pagination').text()).toContain('共 2 条 · 50 条/页')
    const rows = wrapper.findAll('.release-detail-picker tbody tr')
    expect(rows).toHaveLength(2)
    expect(wrapper.text()).not.toContain('STD-003')
    expect(rows.at(0)!.findAll('td').slice(1).map(cell => cell.text())).toEqual(['1', 'STD-001', '部分已发布件', '—', '—', '3', '—', '—', '剩余可发布', '普通优先紧急', ''])
    expect(rows.at(1)!.findAll('td').slice(1).map(cell => cell.text())).toEqual(['2', 'STD-002', '待发布件', 'M8', 'FESTO', '7', '—', '—', '剩余可发布', '普通优先紧急', ''])

    await wrapper.get('input[aria-label="选择长交期物料 STD-001"]').setValue(true)
    await wrapper.get('input[aria-label="选择长交期物料 STD-002"]').setValue(true)
    await wrapper.get('input[aria-label="本次发布数量 STD-001"]').setValue('2')
    await wrapper.get('input[aria-label="本次发布数量 STD-002"]').setValue('5')
    await wrapper.get('.pdm-release-draft-actions .pdm-primary-action').trigger('submit')
    expect(wrapper.emitted('create')?.at(0)?.at(0)).toMatchObject({
      scope: 'StandardLongLead',
      selectedBomItemIds: ['current-1', 'candidate-1', 'candidate-2'],
      selectedBomItemQuantities: { 'current-1': 2, 'candidate-1': 3, 'candidate-2': 2 },
    })
  })

  it('edits only a draft package and keeps the release scope immutable', async () => {
    const releasePackage: ReleasePackageSummary = {
      id: 'draft-long-lead', number: 'RP-LL-DRAFT', state: '草稿', steps: [], scope: 'StandardLongLead',
      workflowCode: 'mechanical-release', workflowVersion: 1, selectedBomItemIds: ['item-1'], createsManufacturingBaseline: false, locksDocuments: false,
      changeReason: '原备注', standardBomRevision: 'LL-OLD',
      standardBomSnapshot: [{ id: 'item-1', kind: 'Standard', sequence: 1, drawingNumber: 'STD-001', name: '长交期件', quantity: 2, unit: '个', revision: 'W1', complete: true }],
      nonStandardBomSnapshot: [], electricalBomSnapshot: [],
    }
    const wrapper = mount(ReleaseCenter, {
      props: {
        releasePackage, allowedScopes: ['StandardLongLead'],
        standardItems: [{ id: 'item-1', kind: 'Standard', sequence: 1, drawingNumber: 'STD-001', name: '长交期件', quantity: 5, unit: '个', revision: 'W1', complete: true }],
        username: 'engineer', pending: false, progress: 0, error: '', canManage: true, canDecide: false,
      },
    })

    await wrapper.get('button').trigger('click')
    expect(wrapper.get('select[aria-label="发布类型"]').attributes()).toHaveProperty('disabled')
    expect((wrapper.get('input[aria-label="本次发布数量 STD-001"]').element as HTMLInputElement).value).toBe('2')
    await wrapper.get('input[aria-label="本次发布数量 STD-001"]').setValue('1')
    await wrapper.get('input[aria-label="整包需求日期"]').setValue('2026-10-09')
    await wrapper.get('textarea').setValue('调整数量')
    await wrapper.get('.pdm-release-draft-actions .pdm-primary-action').trigger('submit')

    expect(wrapper.emitted('updateDraft')).toEqual([['draft-long-lead', {
      changeReason: '调整数量', selectedBomItemIds: ['item-1'], selectedBomItemQuantities: { 'item-1': 1 }, wholeSetMultiplier: 1,
      confirmedRemovedBomItemIds: [], relatedOplIssueIds: [],
      drawingPriority: 'Normal', drawingRequiredOn: '2026-10-09', drawingDeliveryOverrides: {}, bomItemDeliveryOverrides: {},
    }]])
  })

  it('confirms before deleting a draft package', async () => {
    const confirm = vi.spyOn(window, 'confirm').mockReturnValue(true)
    const wrapper = mount(ReleaseCenter, {
      props: {
        releasePackage: {
          id: 'draft-delete', number: 'RP-DRAFT-DELETE', state: '草稿', steps: [], scope: 'StandardFormal',
          workflowVersion: 1, selectedBomItemIds: [], createsManufacturingBaseline: false, locksDocuments: false,
          standardBomSnapshot: [], nonStandardBomSnapshot: [], electricalBomSnapshot: [],
        },
        username: 'engineer', pending: false, progress: 0, error: '', canManage: true, canDecide: false,
      },
    })

    await wrapper.findAll('button').find(button => button.text() === '删除草稿')!.trigger('click')
    expect(confirm).toHaveBeenCalledOnce()
    expect(wrapper.emitted('deleteDraft')).toEqual([['draft-delete']])
    confirm.mockRestore()
  })

  it('paginates long-lead items at 50 rows per page', async () => {
    const standardItems = Array.from({ length: 55 }, (_, index) => ({
      id: `item-${index + 1}`, kind: 'Standard' as const, sequence: index + 1,
      drawingNumber: `STD-${String(index + 1).padStart(3, '0')}`, name: `标准件${index + 1}`,
      quantity: 1, unit: '个', revision: 'W1', complete: true,
    }))
    const wrapper = mount(ReleaseCenter, {
      props: {
        releasePackage: null, allowedScopes: ['StandardLongLead'], standardItems,
        username: 'engineer', pending: false, progress: 0, error: '', canManage: true, canDecide: true,
      },
    })

    expect(wrapper.findAll('.release-detail-picker tbody tr')).toHaveLength(50)
    expect(wrapper.get('.release-detail-pagination').text()).toContain('共 55 条 · 50 条/页')
    expect(wrapper.findAll('.release-detail-picker tbody tr').at(49)!.findAll('td').at(1)!.text()).toBe('50')

    await wrapper.get('button[aria-label="发布明细下一页"]').trigger('click')
    expect(wrapper.findAll('.release-detail-picker tbody tr')).toHaveLength(5)
    expect(wrapper.findAll('.release-detail-picker tbody tr').at(0)!.findAll('td').at(1)!.text()).toBe('51')
  })

  it('defaults formal items to selected and can defer a whole summarized material group', async () => {
    const wrapper = mount(ReleaseCenter, {
      props: {
        releasePackage: null,
        allowedScopes: ['StandardFormal'],
        preferredScope: 'StandardFormal',
        standardItems: [
          { id: 'item-1', kind: 'Standard', sequence: 1, drawingNumber: 'STD-001', name: '提前采购件', specification: 'M12', brand: 'SMC', quantity: 1, unit: '个', revision: 'W1', complete: true },
          { id: 'item-2', kind: 'Standard', sequence: 2, drawingNumber: 'STD-001', name: '提前采购件', specification: 'M12', brand: 'SMC', quantity: 3, unit: '个', revision: 'W1', complete: true },
          { id: 'item-3', kind: 'Standard', sequence: 3, drawingNumber: 'STD-002', name: '普通件', specification: 'M8', brand: 'FESTO', quantity: 2, unit: '个', revision: 'W1', complete: true },
        ],
        longLeadPublishedItems: [
          { id: 'published-1', kind: 'Standard', sequence: 1, drawingNumber: 'STD-001', name: '提前采购件', specification: 'M12', brand: 'SMC', quantity: 1, unit: '个', revision: 'W1', complete: true },
        ],
        username: 'engineer', pending: false, progress: 0, error: '', canManage: true, canDecide: true,
      },
    })

    expect(wrapper.text()).toContain('正式发布内容（已选 2 / 共 2 项 · 默认全选 · 整套倍率 ×1）')
    const rows = wrapper.findAll('.release-detail-picker tbody tr')
    expect(rows).toHaveLength(2)
    expect(rows.at(0)!.findAll('td').map(cell => cell.text())).toEqual(['', '1', 'STD-001', '提前采购件', 'M12', 'SMC', '3', '3', '—', '已发布 1/4', '普通优先紧急', ''])
    expect(rows.at(1)!.text()).toContain('本次发布')
    expect(wrapper.findAll('input[aria-label^="本次发布物料"]').every(input => (input.element as HTMLInputElement).checked)).toBe(true)

    await wrapper.get('input[aria-label="本次发布物料 STD-001"]').setValue(false)
    expect(wrapper.get('.release-detail-picker legend').text()).toContain('已选 1 / 共 2 项')
    expect(rows.at(0)!.findAll('td').at(7)!.text()).toBe('—')
    expect(rows.at(0)!.text()).toContain('本次不发布')
    await wrapper.get('select[aria-label="STD-002紧急程度"]').setValue('Urgent')
    await wrapper.get('input[aria-label="STD-002需求时间"]').setValue('2026-11-03')
    await wrapper.get('.pdm-release-draft-actions .pdm-primary-action').trigger('submit')
    expect(wrapper.emitted('create')?.at(0)?.at(0)).toMatchObject({
      scope: 'StandardFormal',
      selectedBomItemIds: ['item-3'],
      bomItemDeliveryOverrides: { 'item-3': { priority: 'Urgent', requiredOn: '2026-11-03' } },
    })

    await wrapper.get('input[aria-label="本次发布物料 STD-002"]').setValue(false)
    expect(wrapper.get('.pdm-release-draft-actions .pdm-primary-action').attributes()).toHaveProperty('disabled')
  })

  it('locks parts already published in full so they are not published again', async () => {
    const wrapper = mount(ReleaseCenter, {
      props: {
        releasePackage: null,
        allowedScopes: ['StandardFormal'],
        preferredScope: 'StandardFormal',
        standardItems: [
          { id: 'item-1', kind: 'Standard', sequence: 1, drawingNumber: 'STD-001', name: '已提前发布件', specification: '12X14', brand: 'UPTON', quantity: 1, unit: '个', revision: 'W1', complete: true },
          { id: 'item-2', kind: 'Standard', sequence: 2, drawingNumber: 'STD-002', name: '普通件', specification: 'M8', brand: 'FESTO', quantity: 2, unit: '个', revision: 'W1', complete: true },
        ],
        longLeadPublishedItems: [
          { id: 'published-1', kind: 'Standard', sequence: 1, drawingNumber: 'STD-001', name: '已提前发布件', specification: '12X14', brand: 'UPTON', quantity: 1, unit: '个', revision: 'W1', complete: true },
        ],
        username: 'engineer', pending: false, progress: 0, error: '', canManage: true, canDecide: true,
      },
    })

    expect(wrapper.get('.release-detail-picker legend').text()).toContain('已选 2 / 共 2 项')
    expect(wrapper.get('.release-detail-picker legend').text()).toContain('1 项已发布不再重复下发')
    const rows = wrapper.findAll('.release-detail-picker tbody tr')
    expect(rows.at(0)!.findAll('td').map(cell => cell.text())).toEqual(['', '1', 'STD-001', '已提前发布件', '12X14', 'UPTON', '0', '0', '—', '已发布 1/1', '普通优先紧急', ''])

    const published = wrapper.get('input[aria-label="本次发布物料 STD-001"]')
    expect((published.element as HTMLInputElement).checked).toBe(true)
    expect((published.element as HTMLInputElement).disabled).toBe(true)
    await published.setValue(false)
    expect(wrapper.get('.release-detail-picker legend').text()).toContain('已选 2 / 共 2 项')

    await wrapper.get('.pdm-release-draft-actions .pdm-primary-action').trigger('submit')
    expect(wrapper.emitted('create')?.at(0)?.at(0)).toMatchObject({
      scope: 'StandardFormal',
      selectedBomItemIds: ['item-1', 'item-2'],
    })
  })

  it('uses an operator-selected whole-set multiplier without changing source quantities', async () => {
    const wrapper = mount(ReleaseCenter, {
      props: {
        releasePackage: null, allowedScopes: ['StandardFormal'], preferredScope: 'StandardFormal',
        standardItems: [{ id: 'item-1', kind: 'Standard', sequence: 1, drawingNumber: 'STD-001', name: '标准件', quantity: 2, unit: '001', revision: 'W1', complete: true }],
        username: 'engineer', pending: false, progress: 0, error: '', canManage: true, canDecide: true,
      },
    })

    await wrapper.get('input[aria-label="整套倍率"]').setValue(3)
    expect(wrapper.get('.release-detail-picker legend').text()).toContain('整套倍率 ×3')
    const totalQuantityCell = wrapper.findAll('.release-detail-picker tbody td').at(7)!
    expect(totalQuantityCell.text()).toBe('6')
    expect(totalQuantityCell.classes()).toContain('is-release-multiplied')
    await wrapper.get('.pdm-release-draft-actions .pdm-primary-action').trigger('submit')

    expect(wrapper.emitted('create')?.[0]?.[0]).toMatchObject({ wholeSetMultiplier: 3 })
    expect((wrapper.props('standardItems') ?? [])[0]?.quantity).toBe(2)
  })

  it('paginates every formal release list at 50 summarized rows per page', async () => {
    const releaseItems = Array.from({ length: 55 }, (_, index) => ({
      id: `formal-${index + 1}`, kind: 'Electrical' as const, sequence: index + 1,
      drawingNumber: `EL-${String(index + 1).padStart(3, '0')}`, name: `电气件${index + 1}`,
      quantity: 1, unit: '个', revision: 'W1', complete: true,
    }))
    const wrapper = mount(ReleaseCenter, {
      props: {
        releasePackage: null, allowedScopes: ['ElectricalFormal'], releaseItems,
        username: 'engineer', pending: false, progress: 0, error: '', canManage: true, canDecide: true,
      },
    })

    expect(wrapper.findAll('.release-detail-picker tbody tr')).toHaveLength(50)
    expect(wrapper.get('.release-detail-pagination').text()).toContain('共 55 条 · 50 条/页')
    expect(wrapper.findAll('.release-detail-picker tbody tr').at(49)!.findAll('td').at(1)!.text()).toBe('50')

    await wrapper.get('button[aria-label="发布明细下一页"]').trigger('click')
    expect(wrapper.findAll('.release-detail-picker tbody tr')).toHaveLength(5)
    expect(wrapper.findAll('.release-detail-picker tbody tr').at(0)!.findAll('td').at(1)!.text()).toBe('51')
  })

  it('shows only added, modified and deleted items for a supplement', () => {
    const wrapper = mount(ReleaseCenter, {
      props: {
        releasePackage: null,
        allowedScopes: ['StandardSupplement'],
        preferredScope: 'StandardSupplement',
        releasePackages: [publishedFormal('StandardFormal')],
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

    expect(wrapper.text()).toContain('增补/变更内容（共 3 项变更 · 新增/修改项已选 2 / 2 项')
    expect(wrapper.findAll('.release-change-tag').map(tag => tag.text()).sort()).toEqual(['修改', '删除', '新增'])
    expect(wrapper.text()).toContain('STD-001')
    expect(wrapper.text()).toContain('STD-002')
    expect(wrapper.text()).toContain('STD-003')
    expect(wrapper.text()).toContain('数量：1 → 2')
    expect(wrapper.text()).toContain('版本：W1 → W2')
  })

  it('lets electrical formal releases select the parts published this time', async () => {
    const wrapper = mount(ReleaseCenter, {
      props: {
        releasePackage: null, allowedScopes: ['ElectricalFormal'], releaseItems: [
          { id: 'el-1', kind: 'Electrical', sequence: 1, drawingNumber: 'EL-001', name: '电气一', quantity: 1, unit: '个', revision: 'W1', complete: true },
          { id: 'el-2', kind: 'Electrical', sequence: 2, drawingNumber: 'EL-002', name: '电气二', quantity: 2, unit: '个', revision: 'W1', complete: true },
        ],
        username: 'engineer', pending: false, progress: 0, error: '', canManage: true, canDecide: true,
      },
    })

    expect(wrapper.get('.release-detail-picker legend').text()).toContain('正式发布内容（已选 2 / 共 2 项')
    expect(wrapper.findAll('input[aria-label^="本次发布物料"]').every(input => (input.element as HTMLInputElement).checked)).toBe(true)
    await wrapper.get('input[aria-label="本次发布物料 EL-001"]').setValue(false)
    expect(wrapper.findAll('.release-detail-picker tbody tr').at(0)!.text()).toContain('本次不发布')
    await wrapper.get('.pdm-release-draft-actions .pdm-primary-action').trigger('submit')

    expect(wrapper.emitted('create')?.at(0)?.at(0)).toMatchObject({
      scope: 'ElectricalFormal',
      selectedBomItemIds: ['el-2'],
    })
    wrapper.unmount()
  })

  it('keeps the whole supplement scope by default and defers unchecked additions', async () => {
    const wrapper = mount(ReleaseCenter, {
      props: {
        releasePackage: null, allowedScopes: ['StandardSupplement'], preferredScope: 'StandardSupplement',
        releasePackages: [publishedFormal('StandardFormal')],
        standardItems: [
          { id: 'new-1', kind: 'Standard', sequence: 1, drawingNumber: 'NEW-001', name: '新增件', specification: 'M6', brand: 'SMC', quantity: 1, unit: '个', revision: 'W1', complete: true },
          { id: 'keep-1', kind: 'Standard', sequence: 2, drawingNumber: 'KEEP-001', name: '保持件', specification: 'M8', brand: 'SMC', quantity: 1, unit: '个', revision: 'W1', complete: true },
          { id: 'mod-1', kind: 'Standard', sequence: 3, drawingNumber: 'MOD-001', name: '修改件', specification: 'M10', brand: 'SMC', quantity: 2, unit: '个', revision: 'W2', complete: true },
        ],
        previousVersionItems: [
          { id: 'keep-1', kind: 'Standard', sequence: 1, drawingNumber: 'KEEP-001', name: '保持件', specification: 'M8', brand: 'SMC', quantity: 1, unit: '个', revision: 'W1', complete: true },
          { id: 'mod-1', kind: 'Standard', sequence: 2, drawingNumber: 'MOD-001', name: '修改件', specification: 'M10', brand: 'SMC', quantity: 1, unit: '个', revision: 'W1', complete: true },
        ],
        username: 'engineer', pending: false, progress: 0, error: '', canManage: true, canDecide: true,
      },
    })

    const rows = wrapper.findAll('.release-detail-picker tbody tr')
    expect(rows.map(row => row.findAll('.release-change-tag')[0]!.text())).toEqual(['新增', '修改'])
    expect(wrapper.get('.release-detail-picker legend').text()).toContain('未勾选的新增/修改项顺延到下一次变更')
    const modifierCheckbox = rows.at(1)!.get('input[type="checkbox"]').element as HTMLInputElement
    expect(modifierCheckbox.disabled).toBe(false)
    await wrapper.setProps({ standardItems: wrapper.props('standardItems')!.map(item => item.id === 'mod-1'
      ? { ...item, releaseConfirmation: { token: 'current', actor: 'admin', confirmedAt: '2026-10-12', reason: '' } } : item) })
    expect(wrapper.findAll('.release-change-tag').map(tag => tag.text())).toEqual(['新增'])
    expect(wrapper.find('.release-detail-picker').text()).not.toContain('MOD-001')
    await wrapper.setProps({ standardItems: wrapper.props('standardItems')!.map(item => ({ ...item, releaseConfirmation: undefined })) })
    await wrapper.get('input[aria-label="变更原因 物料问题 / 交期不满足"]').setValue(true)
    await wrapper.get('.pdm-release-draft-actions .pdm-primary-action').trigger('submit')
    expect(wrapper.emitted('create')?.at(0)?.at(0)).toMatchObject({ scope: 'StandardSupplement', selectedBomItemIds: ['new-1', 'mod-1'] })

    await wrapper.get('input[aria-label="本次纳入 NEW-001"]').setValue(false)
    expect(rows.at(0)!.text()).toContain('本次不发布')
    await wrapper.get('.pdm-release-draft-actions .pdm-primary-action').trigger('submit')
    expect(wrapper.emitted('create')?.at(1)?.at(0)).toMatchObject({
      scope: 'StandardSupplement',
      selectedBomItemIds: ['mod-1'],
    })
    await wrapper.get('input[aria-label="本次纳入 MOD-001"]').setValue(false)
    expect(wrapper.get('input[aria-label="本次纳入 MOD-001"]').element).toHaveProperty('checked', false)
    expect(wrapper.get('.pdm-release-draft-actions .pdm-primary-action').attributes('disabled')).toBeDefined()
    await wrapper.get('input[aria-label="本次纳入 NEW-001"]').setValue(true)
    await wrapper.get('.pdm-release-draft-actions .pdm-primary-action').trigger('submit')
    expect(wrapper.emitted('create')?.at(-1)?.at(0)).toMatchObject({ selectedBomItemIds: ['new-1'] })
    wrapper.unmount()
  })

  it('历史批准不制造修改或覆盖待审核，2D新存档在批准前后都纳入变更', async () => {
    const items: BomItem[] = Array.from({ length: 35 }, (_, index) => ({ id: `new-${index}`, summaryItemIds: [`new-${index}`], releaseTrackingId: `track-${index}`, kind: 'NonStandard', sequence: index + 1, drawingNumber: `NS-${index}`, name: `零件${index}`, specification: `NS-${index}`, quantity: 2, unit: '个', revision: 'W1', complete: true, sourceDocumentId: `model-${index}`, sourceConfiguration: '默认' }))
    const previous = items.map((item, index) => ({ ...item, id: `old-${index}`, summaryItemIds: [`old-${index}`], releaseTrackingId: `old-track-${index}` }))
    const candidates = items.map((item, index) => ({ candidateId: `candidate-${index}`, bomItemId: item.id, modelDocumentId: item.sourceDocumentId, drawingDocumentId: `drawing-${index}`, drawingNumber: item.drawingNumber, name: item.name, bomKinds: ['NonStandard'] as const, modelRevision: 'W1', drawingRevision: 'W1', state: 'ApprovedCurrent' as const, selectable: false }))
    const review = { id: 'review', projectId: 'project', number: 'DR-UPDATE', state: 'Approved' as const, createdBy: 'designer', createdAt: '2026-10-10T01:00:00Z', approvedAt: '2026-10-10T02:00:00Z', markups: [], items: [7, 9].map(index => ({ id: `review-${index}`, packageId: 'review', bomItemId: items[index]!.id!, drawingNumber: items[index]!.drawingNumber, name: items[index]!.name, modelDocumentId: `model-${index}`, modelVersionId: 'v2', modelRevision: 'W1', modelSha256: 'sha', modelCreatedBy: 'designer', drawingDocumentId: `drawing-${index}`, drawingVersionId: 'v2', drawingRevision: 'W1', drawingState: 'Approved' as const, modelState: 'NotRequired' as const, effectiveModelVersionId: 'v2', effectiveDrawingVersionId: 'v2' })) }
    const formal = { ...publishedFormal('NonStandardWithDrawing'), publishedAt: '2026-10-09T01:00:00Z' }
    const wrapper = mount(ReleaseCenter, { props: { ...frozenProps, releasePackage: null, releaseItems: items, previousVersionItems: previous, releasePackages: [formal], allowedScopes: ['NonStandardSupplement'], preferredScope: 'NonStandardSupplement', drawingReviewCandidates: candidates.map(candidate => ({ ...candidate, bomKinds: [...candidate.bomKinds] })), drawingReviews: [{ ...review, state: 'PendingSupervisorApproval' }] } })
    expect(wrapper.findAll('.release-change-tag')).toHaveLength(0)
    await wrapper.setProps({ drawingReviews: [review] })
    expect(wrapper.findAll('.release-change-tag')).toHaveLength(0)
    const updatedCandidates = candidates.map((candidate, index) => ({ ...candidate, bomKinds: [...candidate.bomKinds], hasUnreleasedDrawingChanges: [7, 9].includes(index), state: 'Ready' as const }))
    await wrapper.setProps({ drawingReviewCandidates: updatedCandidates })
    expect(wrapper.findAll('.release-change-tag').map(tag => tag.text())).toEqual(['修改', '修改'])
    expect(wrapper.findAll('.release-detail-picker tbody tr').every(row => row.text().includes('图纸待发起审核'))).toBe(true)
    expect(wrapper.findAll('.release-detail-picker tbody tr').some(row => row.text().includes('图纸已批准'))).toBe(false)
    await wrapper.setProps({ drawingReviewCandidates: updatedCandidates.map(candidate => ({ ...candidate, state: 'ApprovedCurrent' as const })) })
    expect(wrapper.findAll('.release-detail-picker tbody tr').every(row => row.text().includes('图纸已批准'))).toBe(true)
    expect(wrapper.text()).toContain('2D图纸较正式版有新存档')
    const checks = wrapper.findAll('.pdm-edit-table.is-supplement tbody input[type="checkbox"]')
    expect(checks).toHaveLength(2)
    expect(checks.every(check => (check.element as HTMLInputElement).checked)).toBe(true)
    await wrapper.setProps({ drawingReviewCandidates: updatedCandidates.map(candidate => ({ ...candidate, hasUnreleasedDrawingChanges: false, state: 'ApprovedCurrent' as const })), releasePackages: [formal, { ...formal, id: 'published-update', scope: 'NonStandardSupplement', publishedAt: '2026-10-10T03:00:00Z' }] })
    expect(wrapper.findAll('.release-change-tag')).toHaveLength(0)
    wrapper.unmount()
  })
  it('matches repeated material instances one to one and shows only real field changes', async () => {
    const previous: BomItem[] = [2, 2, 2, 2, 1, 1, 1, 1].map((quantity, index) => ({
      id: `repeated-${index}`, kind: 'Standard', sequence: index + 1, drawingNumber: '01020000088',
      name: '螺钉', specification: 'M5x18', brand: '国优', quantity, unit: '个', revision: 'W2', complete: true,
    }))
    const wrapper = mount(ReleaseCenter, { props: {
      releasePackage: null, allowedScopes: ['StandardSupplement'], preferredScope: 'StandardSupplement',
      releasePackages: [publishedFormal('StandardFormal')],
      standardItems: previous.map(item => ({ ...item, sequence: item.sequence + 2 })), previousVersionItems: previous,
      username: 'engineer', pending: false, progress: 0, error: '', canManage: true, canDecide: true,
    } })
    expect(wrapper.findAll('.release-change-tag')).toHaveLength(0)
    await wrapper.setProps({ standardItems: previous.map((item, index) => index === 1 ? { ...item, quantity: 3, brand: '新品牌' } : { ...item }) })
    expect(wrapper.findAll('.release-change-tag').map(tag => tag.text())).toEqual(['修改'])
    expect(wrapper.get('.release-change-details').text()).toContain('数量：2 → 3')
    expect(wrapper.get('.release-change-details').text()).toContain('品牌：国优 → 新品牌')
    await wrapper.setProps({ standardItems: previous.map(item => ({ ...item, id: `rebuilt-${item.id}` })).reverse() })
    expect(wrapper.findAll('.release-change-tag')).toHaveLength(0)
    await wrapper.setProps({ standardItems: previous.slice(1) })
    expect(wrapper.findAll('.release-change-tag').map(tag => tag.text())).toEqual(['删除'])
  })

  it('compares frozen instances against a summarized baseline without false additions or quantity changes', () => {
    const old: BomItem = { id: 'old', kind: 'Standard', sequence: 1, drawingNumber: 'OLD', name: '原物料', quantity: 4, unit: '个', revision: 'W1', complete: true }
    const instances = [0, 1, 2, 3].map(i => ({ ...old, id: `instance-${i}`, quantity: 1 }))
    const added = [0, 1].map(i => ({ ...old, id: `new-${i}`, drawingNumber: 'NEW', name: '新增物料', quantity: 1 }))
    const wrapper = mount(ReleaseCenter, { props: { ...frozenProps, previousVersionItems: [old],
      releasePackage: { ...publishedFormal('StandardFormal'), scope: 'StandardSupplement', standardBomSnapshot: [...instances, ...added] } } })
    const rows = wrapper.findAll('.pdm-release-frozen-table tbody tr')
    expect(rows).toHaveLength(1)
    expect(rows[0]!.text()).toContain('NEW')
    expect(rows[0]!.findAll('td')[7]!.text()).toBe('2')
    expect(rows[0]!.text()).toContain('新增')
    wrapper.unmount()
  })

  it('keeps approval snapshots summarized with per-material comments and no structure switch', async () => {
    const releasePackage: ReleasePackageSummary = {
      id: 'release-review-view', number: 'RP-REVIEW-001', state: '审批中', scope: 'StandardFormal',
      workflowVersion: 1, selectedBomItemIds: [], createsManufacturingBaseline: false, locksDocuments: false,
      steps: [{ id: 'task-review', stage: '工艺审核', assignee: 'reviewer', status: 'current', detail: '待处理' }],
      standardBomSnapshot: [
        { id: 'instance-1', kind: 'Standard', sequence: 1, drawingNumber: 'STD-001', name: '支架', specification: 'M8', brand: 'UPTON', quantity: 1, unit: '个', revision: 'W1', complete: true, sourceInstancePath: '根装配/支架-1' },
        { id: 'instance-2', kind: 'Standard', sequence: 2, drawingNumber: 'STD-001', name: '支架', specification: 'M8', brand: 'UPTON', quantity: 3, unit: '个', revision: 'W1', complete: true, sourceInstancePath: '根装配/子装配/支架-2' },
        { id: 'instance-3', kind: 'Standard', sequence: 3, drawingNumber: 'STD-002', name: '传感器', specification: 'M12', brand: 'SMC', quantity: 2, unit: '个', revision: 'W1', complete: true, sourceInstancePath: '根装配/传感器-1' },
      ],
      nonStandardBomSnapshot: [], electricalBomSnapshot: [],
    }
    const wrapper = mount(ReleaseCenter, {
      props: {
        releasePackage, username: 'reviewer', pending: false, progress: 0, error: '', canManage: false, canDecide: true,
      },
    })

    expect(wrapper.find('[aria-label="审批快照显示方式"]').exists()).toBe(false)
    expect(wrapper.findAll('button').some(button => button.text() === '按结构')).toBe(false)
    expect(wrapper.findAll('.pdm-release-frozen-table tbody tr')).toHaveLength(2)
    expect(wrapper.findAll('.pdm-release-frozen-table tbody tr').at(0)!.findAll('td').at(7)!.text()).toBe('4')
    expect(wrapper.get('.release-list-pagination').text()).toContain('共 2 条 · 50 条/页')

    expect(wrapper.text()).not.toContain('根装配/子装配/支架-2')

    await wrapper.get('button[aria-label="查看或添加物料批注 STD-001"]').trigger('click')
    expect(wrapper.get('.pdm-item-comment-dialog').text()).toContain('STD-001 · 支架')
    expect(wrapper.get('textarea[aria-label="物料审批批注"]').attributes('placeholder')).toContain('不修改BOM内容')
    wrapper.unmount()
  })
})
