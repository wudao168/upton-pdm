import { flushPromises, mount } from '@vue/test-utils'
import { describe, expect, it, vi } from 'vitest'
import ReleaseCenter from '../src/components/ReleaseCenter.vue'
import * as api from '../src/api'
import type { BomItem, CreateReleasePackageInput, ReleasePackageSummary } from '../src/types'

describe('ReleaseCenter', () => {
  const frozenItem = (id: string): BomItem => ({ id, kind: 'Standard', sequence: 1, drawingNumber: id,
    name: `物料${id}`, specification: 'M5', brand: '国优', quantity: 1, unit: '个', revision: 'W1', complete: true })
  const frozenPackage = (items: BomItem[], scope: ReleasePackageSummary['scope'] = 'StandardSupplement'): ReleasePackageSummary => ({
    id: 'supplement-draft', number: 'RP-SUP', state: '草稿', scope, workflowVersion: 1, selectedBomItemIds: [],
    createsManufacturingBaseline: false, locksDocuments: false, steps: [],
    standardBomSnapshot: scope === 'StandardSupplement' ? items : [], nonStandardBomSnapshot: [],
    electricalBomSnapshot: scope === 'ElectricalSupplement' ? items : [],
  })
  const frozenProps = { username: 'reviewer', pending: false, progress: 0, error: '', canManage: true, canDecide: false }

  it.each(['StandardSupplement', 'ElectricalSupplement'] as const)('defaults %s to two additions and preserves the full snapshot toggle', async scope => {
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
    expect(wrapper.findAll('.pdm-release-frozen-table th')).toHaveLength(11)
    await click('查看完整BOM')
    expect(rows()).toHaveLength(30)
    expect(wrapper.findAll('.pdm-release-frozen-table th')).toHaveLength(9)
    await click('按结构')
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
    expect(deleted.findAll('td')[6]!.text()).toBe('0')
    expect(deleted.find('button').exists()).toBe(false)
    await wrapper.setProps({ releasePackage: frozenPackage(previous) })
    expect(wrapper.get('.pdm-empty-info').text()).toContain('本次没有增补或变更内容')
    expect(wrapper.get('.pdm-empty-info').attributes('colspan')).toBe('11')
    wrapper.unmount()
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
      expect(wrapper.findAll('.pdm-release-frozen-table th').map(cell => cell.text())).toEqual(['序号', '物料编码', '物料名称', '型号', '品牌', '备注', 'BOM总量', '已提前发布', '本次新增下发', '版本', '批注'])
      expect(wrapper.findAll('.pdm-release-frozen-table tbody td').slice(6, 9).map(cell => cell.text())).toEqual(['4', '4', '0'])
      expect(wrapper.get('.pdm-item-comment-action').classes()).toContain('has-comments')
      expect(wrapper.get('.pdm-item-comment-action').text()).toBe('批注（1）')
      await wrapper.setProps({ releasePackage: { ...formal, wholeSetMultiplier: 3 } })
      expect(wrapper.findAll('.pdm-release-frozen-table tbody td').slice(6, 9).map(cell => cell.text())).toEqual(['12', '4', '8'])
      await wrapper.setProps({ releasePackages: [history[0]!] })
      expect(wrapper.findAll('.pdm-release-frozen-table tbody td').slice(6, 9).map(cell => cell.text())).toEqual(['12', '1', '11'])
      await wrapper.findAll('.pdm-view-switch button').find(button => button.text() === '按结构')!.trigger('click')
      expect(wrapper.findAll('.pdm-release-frozen-table th')).toHaveLength(9)
      expect(wrapper.findAll('.pdm-release-frozen-table tbody td')[6]!.text()).toBe('12')
    } finally { wrapper.unmount(); comments.mockRestore() }
  })

  it('renders rejected and skipped approval steps with their real decision details', () => {
    const releasePackage: ReleasePackageSummary = {
      id: 'release-rejected', number: 'RP-REJECTED-001', state: '已驳回', scope: 'StandardFormal',
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
        username: 'admin',
        pending: false,
        progress: 0,
        error: '',
        canManage: true,
        canDecide: true,
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
    await wrapper.get('input[aria-label="变更原因 物料问题 / 交期不满足"]').setValue(true)
    await wrapper.get('input[aria-label="变更原因 客户原因 / 客户需求变更"]').setValue(true)
    await wrapper.get('form').trigger('submit')

    const created = wrapper.emitted('create')?.[0]?.[0] as CreateReleasePackageInput
    expect(created.changeReason).toBe('物料问题 / 交期不满足；客户原因 / 客户需求变更')
    expect(created.scope).toBe('StandardSupplement')
    expect(created.selectedBomItemIds).toEqual([])
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
    expect(wrapper.findAll('.pdm-release-frozen-table th').map(cell => cell.text())).toEqual(['序号', '物料编码', '物料名称', '型号', '品牌', '备注', '数量', '版本', '批注'])
    expect(wrapper.findAll('.pdm-release-frozen-table tbody tr').at(0)!.findAll('td').map(cell => cell.text())).toEqual(['1', 'EL-001', '电气元件', 'M18', 'SMC', '安装备注', '2', 'W2', '批注'])
    expect(wrapper.findAll('.pdm-release-frozen-table tbody tr').at(0)!.findAll('td.is-release-centered')).toHaveLength(6)
    expect(wrapper.findAll('.pdm-release-frozen-table col')).toHaveLength(9)
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

  it('shows only the release scopes owned by the current BOM page', () => {
    const wrapper = mount(ReleaseCenter, {
      props: {
        releasePackage: null, allowedScopes: ['NonStandardWithDrawing'], username: 'engineer',
        pending: false, progress: 0, error: '', canManage: true, canDecide: true,
      },
    })

    expect(wrapper.findAll('option').map(option => option.text())).toEqual(['非标件BOM + 图纸'])
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
    await wrapper.setProps({ allowedScopes: ['StandardSupplement'], preferredScope: 'StandardSupplement' })
    expect(wrapper.findAll('.pdm-release-type-row option').map(option => option.text())).toEqual(['标准件 · 增补/变更'])
    expect((wrapper.get('.pdm-release-type-row select').element as HTMLSelectElement).value).toBe('StandardSupplement')
  })

  it('keeps one fixed header and detail table while switching standard release types', async () => {
    const wrapper = mount(ReleaseCenter, {
      props: {
        releasePackage: null,
        allowedScopes: ['StandardLongLead', 'StandardFormal', 'StandardSupplement'],
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
    expect(wrapper.findAll('.release-detail-picker col')).toHaveLength(10)
    expect(wrapper.findAll('.release-detail-picker tbody tr').at(0)!.findAll('td.is-release-centered')).toHaveLength(6)

    await wrapper.get('.pdm-release-type-row select').setValue('StandardFormal')
    expect(wrapper.get('.pdm-release-create-header').element).toBe(header)
    expect(wrapper.get('.pdm-release-parameter-slot').element).toBe(parameterSlot)
    expect(wrapper.get('.release-detail-picker').element).toBe(detail)
    expect(wrapper.get('.release-detail-picker table').element).toBe(table)
    expect(wrapper.findAll('.release-detail-picker th').map(cell => cell.text())).toEqual(columnHeaders)
    expect(wrapper.get('.release-detail-picker legend').text()).toContain('正式发布内容')
    expect(wrapper.findAll('.release-detail-picker tbody tr').at(0)!.findAll('td.is-release-centered')).toHaveLength(6)

    await wrapper.get('.pdm-release-type-row select').setValue('StandardSupplement')
    expect(wrapper.get('.pdm-release-create-header').element).toBe(header)
    expect(wrapper.get('.pdm-release-parameter-slot').element).toBe(parameterSlot)
    expect(wrapper.get('.release-detail-picker').element).toBe(detail)
    expect(wrapper.get('.release-detail-picker table').element).toBe(table)
    expect(wrapper.findAll('.release-detail-picker th').map(cell => cell.text())).toEqual([...columnHeaders, '变更明细（原值 → 新值）'])
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
    expect(wrapper.findAll('.release-detail-picker th').map(cell => cell.text())).toEqual(['标记', '序号', '物料编码', '名称', '型号', '品牌', '可发布数量', '发布总数量', '备注', '发布状态'])
    expect(wrapper.findAll('.release-detail-picker tbody tr').at(0)!.findAll('td').slice(1).map(cell => cell.text())).toEqual(['1', 'STD-001', '长交期件', 'M12', 'SMC', '1', '—', '提前采购', '剩余可发布'])
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
    expect(rows.at(0)!.findAll('td').slice(1).map(cell => cell.text())).toEqual(['1', 'STD-001', '部分已发布件', '—', '—', '3', '—', '—', '剩余可发布'])
    expect(rows.at(1)!.findAll('td').slice(1).map(cell => cell.text())).toEqual(['2', 'STD-002', '待发布件', 'M8', 'FESTO', '7', '—', '—', '剩余可发布'])

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
    await wrapper.get('textarea').setValue('调整数量')
    await wrapper.get('.pdm-release-draft-actions .pdm-primary-action').trigger('submit')

    expect(wrapper.emitted('updateDraft')).toEqual([['draft-long-lead', {
      changeReason: '调整数量', selectedBomItemIds: ['item-1'], selectedBomItemQuantities: { 'item-1': 1 }, wholeSetMultiplier: 1,
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
    expect(rows.at(0)!.findAll('td').map(cell => cell.text())).toEqual(['', '1', 'STD-001', '提前采购件', 'M12', 'SMC', '4', '4', '—', '已提前发布 1/4'])
    expect(rows.at(1)!.text()).toContain('本次发布')
    expect(wrapper.findAll('input[aria-label^="本次发布物料"]').every(input => (input.element as HTMLInputElement).checked)).toBe(true)

    await wrapper.get('input[aria-label="本次发布物料 STD-001"]').setValue(false)
    expect(wrapper.get('.release-detail-picker legend').text()).toContain('已选 1 / 共 2 项')
    expect(rows.at(0)!.findAll('td').at(7)!.text()).toBe('—')
    expect(rows.at(0)!.text()).toContain('本次不发布')
    await wrapper.get('.pdm-release-draft-actions .pdm-primary-action').trigger('submit')
    expect(wrapper.emitted('create')?.at(0)?.at(0)).toMatchObject({
      scope: 'StandardFormal',
      selectedBomItemIds: ['item-3'],
    })

    await wrapper.get('input[aria-label="本次发布物料 STD-002"]').setValue(false)
    expect(wrapper.get('.pdm-release-draft-actions .pdm-primary-action').attributes()).toHaveProperty('disabled')
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

    expect(wrapper.text()).toContain('增补/变更内容（共 3 项 · 整套倍率 ×1）')
    expect(wrapper.findAll('.release-change-tag').map(tag => tag.text()).sort()).toEqual(['修改', '删除', '新增'])
    expect(wrapper.text()).toContain('STD-001')
    expect(wrapper.text()).toContain('STD-002')
    expect(wrapper.text()).toContain('STD-003')
    expect(wrapper.text()).toContain('数量：1 → 2')
    expect(wrapper.text()).toContain('版本：W1 → W2')
  })

  it('matches repeated material instances one to one and shows only real field changes', async () => {
    const previous: BomItem[] = [2, 2, 2, 2, 1, 1, 1, 1].map((quantity, index) => ({
      id: `repeated-${index}`, kind: 'Standard', sequence: index + 1, drawingNumber: '01020000088',
      name: '螺钉', specification: 'M5x18', brand: '国优', quantity, unit: '个', revision: 'W2', complete: true,
    }))
    const wrapper = mount(ReleaseCenter, { props: {
      releasePackage: null, allowedScopes: ['StandardSupplement'], preferredScope: 'StandardSupplement',
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

  it('defaults the approval snapshot to summary and switches to structured instances with per-material comments', async () => {
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

    expect(wrapper.get('button[aria-pressed="true"]').text()).toBe('按汇总')
    expect(wrapper.findAll('.pdm-release-frozen-table tbody tr')).toHaveLength(2)
    expect(wrapper.findAll('.pdm-release-frozen-table tbody tr').at(0)!.findAll('td').at(6)!.text()).toBe('4')
    expect(wrapper.get('.release-list-pagination').text()).toContain('共 2 条 · 50 条/页')

    await wrapper.findAll('.pdm-view-switch button').find(button => button.text() === '按结构')!.trigger('click')
    expect(wrapper.get('button[aria-pressed="true"]').text()).toBe('按结构')
    expect(wrapper.findAll('.pdm-release-frozen-table tbody tr')).toHaveLength(3)
    expect(wrapper.text()).toContain('根装配/子装配/支架-2')

    await wrapper.get('button[aria-label="查看或添加物料批注 STD-001"]').trigger('click')
    expect(wrapper.get('.pdm-item-comment-dialog').text()).toContain('STD-001 · 支架')
    expect(wrapper.get('textarea[aria-label="物料审批批注"]').attributes('placeholder')).toContain('不修改BOM内容')
  })
})
