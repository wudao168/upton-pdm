import { flushPromises, mount, type VueWrapper } from '@vue/test-utils'
import { ElDatePicker } from 'element-plus'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import ProductionWorkbench from '../src/components/ProductionWorkbench.vue'
import ProductionLineDrawingDialog from '../src/components/ProductionLineDrawingDialog.vue'
import * as api from '../src/api'
import { selfProductionState, externalProductionState, type ProductionReleaseRecord } from '../src/productionTracking'
import type { ProductionDrawingItem } from '../src/types'
import { ElMessage } from '../src/statusMessage'

vi.mock('../src/statusMessage', () => ({ ElMessage: { success: vi.fn(), warning: vi.fn(), error: vi.fn() } }))
vi.mock('../src/components/ProductionDrawingQr.vue', () => ({ default: { template: '<div />' } }))

function release(id = 'one'): ProductionReleaseRecord {
  return { rowVersion: 1, state: { releasePackageId: id, projectId: 'project', companyId: 'company', projectCode: 'P001',
    projectName: '排程测试', packageNumber: `RP-${id}`, scope: 'NonStandardWithDrawing', publishedAt: '2026-10-19T00:00:00Z',
    lines: ['外协板', '支架', '轴'].map((name, index) => ({ id: `line-${index}`, materialKey: `${index}`, name,
      drawingNumber: `D-00${index + 1}`, revision: 'W1', requiredQuantity: 10, unit: '001', priority: 'Normal', requiredOn: '2026-11-05' })),
    lots: [{ id: 'external', lineId: 'line-0', route: 'External', quantity: 10, paused: false, frozen: false, schedule: [] },
      { id: 'self-a', lineId: 'line-1', route: 'Self', quantity: 2, paused: false, frozen: false, schedule: [] },
      { id: 'self-b', lineId: 'line-2', route: 'Self', quantity: 3, paused: false, frozen: false, schedule: [{ resourceGroupId: '车床组', date: '2026-11-01', hours: 4 }] },
      { id: 'self-c', lineId: 'line-2', route: 'Self', quantity: 4, paused: false, frozen: false, promisedOn: '2026-11-03', schedule: [{ resourceGroupId: '磨床组', date: '2026-11-02', hours: 5 }] }],
    receivedAt: '2026-10-19T00:00:00Z', movements: ['self-a', 'self-b', 'self-c'].map((lotId, index) => ({ id: `received-${lotId}`, requestId: `received-${lotId}`, lotId, kind: 'ProductionReceived', quantity: index + 2, actor: 'admin', occurredAt: '2026-10-19T00:00:00Z' })) } }
}
let records: Record<string, ProductionReleaseRecord>
const previewAttention = new Set<string>()
function drawingForLine(index: number, overrides: Partial<ProductionDrawingItem> = {}): ProductionDrawingItem {
  return { projectId: 'project', projectCode: 'P001', projectName: '排程测试', releasePackageId: 'one', releasePackageNumber: 'RP-one',
    documentId: `drawing-${index}`, versionId: `version-${index}`, drawingNumber: `D-00${index + 1}`, name: '正式图纸', model: '', revision: 'W1',
    publishedAt: '', priority: 'Normal', isCurrent: true, legacyUnverified: false, pdfReady: true,
    bomItems: [{ id: `bom-${index}`, sequence: index + 1, drawingNumber: `MAT-${index}`, specification: `MODEL-${index}`, name: `物料${index}`, quantity: 2, unit: '001', revision: 'W1', complete: true }],
    bomQuantities: [{ materialKey: `${index}`, sourceBomItemIds: [`bom-${index}`], singleSetQuantity: 2, wholeSetMultiplier: 3, totalQuantity: 6, priorReleasedQuantity: 1, netIssueQuantity: 5 }], modelFiles: [{ documentId: `model-${index}`, versionId: `model-version-${index}`, drawingNumber: 'MODEL', name: '模型', revision: 'W1', stepReady: true }], ...overrides }
}
beforeEach(() => {
  previewAttention.clear()
  records = { one: release(), two: release('two') }
  records.two.state.lots = [records.two.state.lots[0]]
  vi.spyOn(api, 'listProductionTrackingProjects').mockImplementation(async (_token, selfOnly, externalOnly) => {
    const states = Object.values(records).map(({ state }) => externalOnly ? externalProductionState(state) : selfOnly ? selfProductionState(state) : state).filter(state => !!state)
    return states.length ? [{ id: 'project', code: 'P001', name: '排程测试', packages: states.map(state => ({
    releasePackageId: state.releasePackageId, projectId: 'project', projectCode: 'P001', projectName: '排程测试', packageNumber: state.packageNumber,
    scope: state.scope, publishedAt: state.publishedAt, receivedAt: state.receivedAt, hasReleaseAttention: previewAttention.has(state.releasePackageId), status: '待排产', totalLines: state.lines.length,
    totalQuantity: state.lines.reduce((sum, line) => sum + line.requiredQuantity, 0), qualifiedLines: 0, issuedLines: 0, urgentLines: 0,
    priorityLines: 0, atRiskLines: 0, unplannedLines: 1 })) }] : []
  })
  vi.spyOn(api, 'readProductionTrackingRelease').mockImplementation(async id => records[id])
  vi.spyOn(api, 'readProductionTrackingSupplierChoices').mockResolvedValue({ companyId: 'company', categories: [], suppliers: [] })
  vi.spyOn(api, 'readProductionActions').mockResolvedValue({ actions: [], hint: '当前账号未配置可办理的扫码岗位。' })
  vi.spyOn(api, 'readProductionCapacity').mockResolvedValue({ calendar: { companyId: 'company', rowVersion: 1, days: [] }, usage: [] })
  vi.spyOn(api, 'listProductionDrawings').mockResolvedValue([])
  vi.spyOn(api, 'downloadProductionDrawingArchive').mockResolvedValue()
  vi.spyOn(api, 'downloadGeneratedFile').mockResolvedValue()
  vi.spyOn(api, 'getProjectExportMetadata').mockResolvedValue({ company: '昆山公司', title: '发布包BOM清单', scope: '当前项目', versionLabel: '资料版本', version: '—', exportedAt: '2026-10-21T10:00:00+08:00', exportedBy: '测试用户', fields: [] })
})
afterEach(() => vi.restoreAllMocks())

async function openSchedule(permissions = ['production.schedule']) {
  const wrapper = mount(ProductionWorkbench, { props: { token: 'token', username: 'admin', permissions } })
  await flushPromises()
  await wrapper.findAll('.production-workbench__tabs button').find(button => button.text() === '自制追溯')!.trigger('click')
  await flushPromises()
  return wrapper
}
async function setDateRange(wrapper: VueWrapper, value: [string, string] | null) {
  wrapper.findComponent(ElDatePicker).vm.$emit('update:modelValue', value)
  await flushPromises()
}

describe('发布跟踪快捷筛选与全部分类', () => {
  it('展开折叠总控只调整项目列表，保留当前发布包', async () => {
    const wrapper = mount(ProductionWorkbench, { props: { token: 'token', username: 'admin', permissions: [] } })
    await flushPromises()
    expect(wrapper.get('.production-workbench__all-categories').text()).toBe('全部发布包')
    const selected = wrapper.get('.production-workbench__package.selected').text()
    const button = wrapper.get('.production-workbench__toggle-projects')
    expect(button.text()).toBe('折叠')
    await button.trigger('click')
    expect(wrapper.findAll('.production-workbench__project').every(item => item.attributes('aria-expanded') === 'false')).toBe(true)
    expect(button.text()).toBe('展开')
    expect(wrapper.get('.production-workbench__package.selected').text()).toBe(selected)
    await button.trigger('click')
    expect(wrapper.findAll('.production-workbench__project').every(item => item.attributes('aria-expanded') === 'true')).toBe(true)
    expect(wrapper.get('.production-workbench__package.selected').text()).toBe(selected)
    wrapper.unmount()
  })
  it('未接收包和所属项目显示绿框，供应链接收后停止，其他包保持原状', async () => {
    records.one.state.receivedAt = null
    vi.spyOn(api, 'receiveProductionTrackingRelease').mockImplementation(async id => {
      records[id] = { ...records[id]!, state: { ...records[id]!.state, receivedAt: new Date().toISOString() } }
      return records[id]!
    })
    const wrapper = mount(ProductionWorkbench, { props: { token: 'token', username: 'admin', permissions: ['production.assign'] } })
    await flushPromises()
    const packages = () => wrapper.findAll('.production-workbench__package')
    expect(wrapper.get('.production-workbench__project').attributes('data-release-attention')).toBe('new')
    expect(packages()[0]!.attributes('data-release-attention')).toBe('new')
    expect(packages()[1]!.attributes('data-release-attention')).toBeUndefined()
    await wrapper.findAll('button').find(button => button.text() === '供应链接收')!.trigger('click')
    await flushPromises()
    expect(api.receiveProductionTrackingRelease).toHaveBeenCalledWith('one', 'token')
    expect(packages()[0]!.attributes('data-release-attention')).toBeUndefined()
    expect(wrapper.get('.production-workbench__project').attributes('data-release-attention')).toBeUndefined()
    expect(wrapper.get('.production-workbench__package.selected').text()).toContain('RP-one')
    wrapper.unmount()
  })

  it('红框优先于绿框，筛选隐藏异常包时仍保留项目提醒', async () => {
    records.one.state.receivedAt = null; records.two.state.receivedAt = null
    previewAttention.add('one')
    const wrapper = mount(ProductionWorkbench, { props: { token: 'token', username: 'admin', permissions: [] } })
    await flushPromises()
    expect(wrapper.get('.production-workbench__project').attributes('data-release-attention')).toBe('error')
    const packages = wrapper.findAll('.production-workbench__package')
    expect(packages[0]!.attributes('data-release-attention')).toBe('error')
    expect(packages[1]!.attributes('data-release-attention')).toBe('new')
    await wrapper.get('[aria-label="搜索生产发布包"]').setValue('RP-two')
    expect(wrapper.findAll('.production-workbench__package')).toHaveLength(1)
    expect(wrapper.get('.production-workbench__project').attributes('data-release-attention')).toBe('error')
    wrapper.unmount()
  })

  it('其他客户端接收后静默清除边框，保留选择且不重新加载明细', async () => {
    vi.useFakeTimers();vi.spyOn(document, 'visibilityState', 'get').mockReturnValue('visible')
    records.one.state.receivedAt = null
    const wrapper = mount(ProductionWorkbench, { props: { token: 'token', username: 'admin', permissions: [] } })
    try {
      await flushPromises()
      expect(wrapper.get('.production-workbench__package.selected').attributes('data-release-attention')).toBe('new')
      const reads = vi.mocked(api.readProductionTrackingRelease).mock.calls.length
      records.one.state.receivedAt = new Date().toISOString()
      await vi.advanceTimersByTimeAsync(60000);await flushPromises()
      expect(wrapper.get('.production-workbench__package.selected').text()).toContain('RP-one')
      expect(wrapper.get('.production-workbench__package.selected').attributes('data-release-attention')).toBeUndefined()
      expect(api.readProductionTrackingRelease).toHaveBeenCalledTimes(reads)
    } finally { wrapper.unmount();vi.useRealTimers() }
  })

  it.each(['发布跟踪', '外协追溯', '自制追溯'])('%s按项目折叠，展开不切换发布包，刷新和搜索保留选择', async tabName => {
    const base = (await api.listProductionTrackingProjects('token'))[0]!
    records.two.state.projectId = 'other'; records.two.state.projectCode = 'P002'; records.two.state.projectName = '第二项目'
    vi.mocked(api.listProductionTrackingProjects).mockResolvedValue([
      { ...base, packages: [base.packages[0]!] },
      { id: 'other', code: 'P002', name: '第二项目', packages: [{ ...base.packages[1]!, projectId: 'other', projectCode: 'P002', status: '待接收' }] },
    ])
    const wrapper = mount(ProductionWorkbench, { attachTo: document.body, props: { token: 'token', username: 'admin', permissions: ['production.schedule'] } })
    await flushPromises()
    await wrapper.findAll('.production-workbench__tabs button').find(button => button.text() === tabName)!.trigger('click')
    await flushPromises()
    const headers = () => wrapper.findAll('.production-workbench__project')
    const group = (code: string) => wrapper.get(`[aria-label="${code}的发布包"]`)
    expect(headers()[0]!.attributes('aria-expanded')).toBe('true')
    expect(headers()[1]!.attributes('aria-expanded')).toBe('false')
    expect(headers()[1]!.text()).toContain('1 包')
    expect(group('P002').isVisible()).toBe(false)
    const reads = vi.mocked(api.readProductionTrackingRelease).mock.calls.length
    await headers()[0]!.trigger('click')
    await headers()[1]!.trigger('click')
    expect(group('P001').isVisible()).toBe(false)
    expect(group('P002').isVisible()).toBe(true)
    expect(api.readProductionTrackingRelease).toHaveBeenCalledTimes(reads)
    expect(wrapper.get('.production-workbench__package.selected').text()).toContain('RP-one')
    await group('P002').get('button').trigger('click'); await flushPromises()
    expect(wrapper.get('.production-workbench__package.selected').text()).toContain('RP-two')
    await headers()[1]!.trigger('click')
    await wrapper.get('.production-workbench__toolbar button').trigger('click'); await flushPromises()
    expect(group('P002').isVisible()).toBe(false)
    expect(wrapper.get('.production-workbench__package.selected').text()).toContain('RP-two')
    await wrapper.get('[aria-label="搜索生产发布包"]').setValue('P002')
    expect(group('P002').isVisible()).toBe(true)
    expect(headers()).toHaveLength(1)
    await headers()[0]!.trigger('click')
    expect(group('P002').isVisible()).toBe(false)
    await wrapper.get('[aria-label="搜索生产发布包"]').setValue('')
    await wrapper.get('[aria-label="筛选生产状态"]').setValue('待接收')
    expect(group('P002').isVisible()).toBe(true)
    expect(wrapper.get('.production-workbench__package.selected').text()).toContain('RP-two')
    wrapper.unmount()
  })

  it('小数交付与分配不会因浮点求和误差残留在筛选结果中', async () => {
    const source = records.one.state
    source.lines = [{ ...source.lines[0], requiredQuantity: 0.9 }]
    source.lots = [{ ...source.lots[0], quantity: 0.9 }]
    source.movements = ['Issue', 'Cancelled'].map((kind, index) => ({ id: kind, requestId: kind, kind, quantity: index ? 0.6 : 0.3, lotId: 'external', actor: 'admin', occurredAt: '' }))
    const wrapper = mount(ProductionWorkbench, { props: { token: 'token', username: 'admin', permissions: [] } }); await flushPromises()
    expect(wrapper.findAll('[aria-label="发布包物料明细"] tbody tr')).toHaveLength(0)
    await wrapper.findAll('.production-workbench__quick-filter input')[1].setValue(false)
    expect(wrapper.findAll('[aria-label="发布包物料明细"] tbody tr')).toHaveLength(1)
    source.movements = []; source.lots = [{ ...source.lots[0], quantity: 0.3 }, { ...source.lots[0], id: 'remainder', quantity: 0.6 }]
    await wrapper.get('.production-workbench__toolbar button').trigger('click'); await flushPromises()
    await wrapper.findAll('.production-workbench__quick-filter input')[0].setValue(true)
    expect(wrapper.findAll('[aria-label="发布包物料明细"] tbody tr')).toHaveLength(0)
    wrapper.unmount()
  })
  it('默认仅显示未完工，两个勾选项取交集，部分分配保留且取消数量不再算待交付', async () => {
    const source = records.one.state
    source.lots[1].quantity = 10
    source.lines.push({ ...source.lines[0], id: 'unassigned', drawingNumber: 'D-NEW', name: '未分配件', requiredQuantity: 3 })
    source.movements.push(...[
      { lotId: 'external', kind: 'Issue', quantity: 10 },
      { lotId: 'self-a', kind: 'Cancelled', quantity: 8 },
      { lotId: 'self-a', kind: 'Issue', quantity: 2 },
      { lotId: 'self-b', kind: 'Pass', quantity: 3 },
    ].map((item, index) => ({ ...item, id: `completion-${index}`, requestId: `completion-${index}`, actor: 'admin', occurredAt: '' })))
    const wrapper = mount(ProductionWorkbench, { props: { token: 'token', username: 'admin', permissions: ['production.assign'] } })
    await flushPromises()
    const middle = wrapper.get('[aria-label="发布包物料明细"]')
    const open = wrapper.get<HTMLInputElement>('.production-workbench__quick-filter:last-of-type input')
    const unassigned = wrapper.findAll('.production-workbench__quick-filter input')[0]
    expect(open.element.checked).toBe(true); expect((unassigned.element as HTMLInputElement).checked).toBe(false)
    expect(middle.findAll('tbody tr').map(row => row.get('strong').text())).toEqual(['D-003', 'D-NEW'])
    await unassigned.setValue(true)
    expect(middle.findAll('tbody tr')).toHaveLength(2)
    await open.setValue(false); await unassigned.setValue(false)
    expect(middle.findAll('tbody tr')).toHaveLength(4)
    await wrapper.get('[aria-label="选择物料 D-001 W1"]').setValue(true)
    await open.setValue(true)
    expect(middle.text()).toContain('已选 0 项')
    await unassigned.setValue(true)
    await middle.findAll('button').find(button => button.text() === '重置筛选')!.trigger('click')
    expect(open.element.checked).toBe(true); expect((unassigned.element as HTMLInputElement).checked).toBe(false)
    wrapper.unmount()
  })
  it('汇总不同项目和发布包，重复物料ID不混淆，图纸与办理入口使用各自原发布包', async () => {
    records.two.state.projectId = 'other-project'; records.two.state.projectCode = 'P002'; records.two.state.projectName = '第二项目'
    records.two.state.movements.push({ id: 'issue-two', requestId: 'issue-two', lotId: 'external', kind: 'Issue', quantity: 4, actor: 'admin', occurredAt: '' })
    const projects = await api.listProductionTrackingProjects('token')
    vi.mocked(api.listProductionTrackingProjects).mockResolvedValue([
      { ...projects[0], packages: [projects[0].packages[0]] },
      { ...projects[0], id: 'other-project', code: 'P002', name: '第二项目', packages: [{ ...projects[0].packages[1], projectId: 'other-project', projectCode: 'P002', projectName: '第二项目' }] },
    ])
    const wrapper = mount(ProductionWorkbench, { props: { token: 'token', username: 'admin', permissions: ['project.content.view', 'production.assign'] } })
    await flushPromises()
    await wrapper.get('[aria-label="搜索生产发布包"]').setValue('P001')
    await wrapper.get('.production-workbench__all-categories').trigger('click'); await flushPromises()
    const middle = wrapper.get('[aria-label="发布包物料明细"]')
    expect(wrapper.get<HTMLInputElement>('[aria-label="搜索生产发布包"]').element.value).toBe('')
    expect(wrapper.get('.production-workbench__all-categories').attributes('aria-pressed')).toBe('true')
    expect(middle.findAll('tbody tr')).toHaveLength(6)
    const second = middle.findAll('tbody tr').find(row => row.text().includes('RP-two') && row.text().includes('D-001'))!
    expect(second.text()).toContain('P002'); expect(second.findAll('td')[8].text()).toBe('4')
    await second.get('[aria-label="预览图纸 D-001"]').trigger('click'); await flushPromises()
    expect(wrapper.findComponent(ProductionLineDrawingDialog).props('state').releasePackageId).toBe('two')
    wrapper.findComponent(ProductionLineDrawingDialog).vm.$emit('close'); await flushPromises()
    await wrapper.get('[aria-label="打开发布包 RP-two 的物料 D-001"]').trigger('click'); await flushPromises()
    expect(wrapper.get('.production-workbench__all-categories').attributes('aria-pressed')).toBe('false')
    expect(middle.get('header').text()).toContain('RP-two'); expect(middle.get('header').text()).toContain('第二项目')
    expect(wrapper.get('.production-workbench__detail').text()).toContain('外协板')
    wrapper.unmount()
  })
  it('读取汇总期间切换回发布包，迟到的汇总结果不能覆盖当前选择', async () => {
    let complete!: (record: ProductionReleaseRecord) => void
    vi.mocked(api.readProductionTrackingRelease).mockImplementation(async id => id === 'two'
      ? new Promise(resolve => { complete = resolve }) : records[id])
    const wrapper = mount(ProductionWorkbench, { props: { token: 'token', username: 'admin', permissions: [] } })
    await flushPromises()
    await wrapper.get('.production-workbench__all-categories').trigger('click'); await flushPromises()
    await wrapper.findAll('.production-workbench__package').find(button => button.text().includes('RP-one'))!.trigger('click'); await flushPromises()
    complete(records.two); await flushPromises()
    expect(wrapper.get('.production-workbench__all-categories').attributes('aria-pressed')).toBe('false')
    expect(wrapper.get('[aria-label="发布包物料明细"] header').text()).toContain('RP-one')
    expect(wrapper.findAll('.production-workbench__toolbar button')[0].attributes('disabled')).toBeUndefined()
    wrapper.unmount()
  })
})

describe('三栏自制排程', () => {
  it('自制追溯保留明细，不再显示右侧发布包汇总卡片', async () => {
    records.one.state.movements.push({ id: 'self-pass', requestId: 'self-pass', lotId: 'self-a', kind: 'Pass', quantity: 2, actor: 'quality', occurredAt: '' },
      { id: 'self-issue', requestId: 'self-issue', lotId: 'self-a', kind: 'Issue', quantity: 2, actor: 'material', occurredAt: '' },
      { id: 'external-pass', requestId: 'external-pass', lotId: 'external', kind: 'Pass', quantity: 10, actor: 'quality', occurredAt: '' })
    const wrapper = await openSchedule()
    await wrapper.get('.production-workbench__quick-filter input').setValue(false)
    expect(wrapper.find('.production-workbench__summary').exists()).toBe(false)
    expect(wrapper.findAll('.production-workbench__lines tbody tr')).not.toHaveLength(0)
    wrapper.unmount()
  })
  it('右侧完整记录包含所选批次全部流转，不截断也不混入其他批次', async () => {
    records.one.state.movements.push(...Array.from({ length: 12 }, (_, index) => ({ id: `trace-${index}`, requestId: `trace-${index}`, lotId: 'self-a', kind: 'Trace', quantity: 2, actor: 'operator', actorName: '车床操作员', actorRoles: '机加人员', workstationName: '车床工位', resourceGroup: '车床组', fromStage: '机加', toStage: '机加', note: `追溯说明${index}`, occurredAt: '2026-10-22T01:00:00Z' })))
    const wrapper = await openSchedule(['project.content.view'])
    await wrapper.get('[aria-label="选择排程批次 D-002 self-a"]').trigger('click'); await flushPromises()
    const history = wrapper.get('[aria-label="排程安排"] [aria-label="完整操作记录"]')
    expect(history.get('summary').text()).toBe('完整操作记录（13）')
    await history.get('summary').trigger('click')
    expect(history.findAll('li')).toHaveLength(13)
    expect(history.text()).toContain('received-self-a'); expect(history.text()).toContain('trace-11')
    expect(history.text()).toContain('车床操作员（operator）'); expect(history.text()).toContain('车床工位 · 车床组')
    expect(history.text()).toContain('追溯说明11'); expect(history.text()).not.toContain('received-self-b')
    await wrapper.get('[aria-label="选择排程批次 D-003 self-b"]').trigger('click'); await flushPromises()
    expect(wrapper.get('[aria-label="排程安排"] [aria-label="完整操作记录"]').text()).toContain('received-self-b')
    expect(wrapper.get('[aria-label="排程安排"] [aria-label="完整操作记录"]').text()).not.toContain('trace-11')
    wrapper.unmount()
  })
  it('没有操作记录的批次仍保留可展开入口和空记录提示', async () => {
    records.one.state.movements = []
    const wrapper = await openSchedule()
    await wrapper.get('[aria-label="选择排程批次 D-002 self-a"]').trigger('click'); await flushPromises()
    const history = wrapper.get('[aria-label="排程安排"] [aria-label="完整操作记录"]')
    expect(history.get('summary').text()).toBe('完整操作记录（0）')
    expect(history.text()).toContain('暂无操作记录')
    wrapper.unmount()
  })
  it('点击自制变更状态直接办理选中批次，保存后解除冻结并保留该批次选择', async () => {
    const frozen = records.one.state.lots[3]
    frozen.frozen = true; frozen.frozenByReleasePackageId = 'new-release'; frozen.freezeReason = '发布包 RP-new 的图纸或BOM已变更'
    const resolve = vi.spyOn(api, 'resolveProductionRevision').mockImplementation(async (id, input) => {
      const updated = structuredClone(records[id])
      updated.state.lots.find(lot => lot.id === input.lotId)!.frozen = false
      records[id] = updated; return updated
    })
    const wrapper = await openSchedule()
    await wrapper.get('[aria-label="处理图纸变更 D-003 self-c"]').trigger('click'); await flushPromises()
    const panel = wrapper.get('[aria-label="排程安排"] [aria-label="图纸变更处置"]')
    expect(panel.text()).toContain('RP-new'); expect(panel.text()).toContain('4 001')
    expect(wrapper.get('.production-workbench__schedule-form fieldset').attributes()).toHaveProperty('disabled')
    await panel.get('select').setValue('ContinueOld'); await panel.get('input').setValue('技术确认继续旧版')
    await panel.get('form').trigger('submit'); await flushPromises()
    expect(resolve).toHaveBeenCalledWith('one', expect.objectContaining({ lotId: 'self-c', changedReleasePackageId: 'new-release', action: 'ContinueOld', reason: '技术确认继续旧版' }), 'token')
    expect(wrapper.find('[aria-label="图纸变更处置"]').exists()).toBe(false)
    expect(wrapper.get('[aria-label="选择排程批次 D-003 self-c"]').attributes('aria-pressed')).toBe('true')
    expect(wrapper.get('.production-workbench__schedule-form fieldset').attributes('disabled')).toBeUndefined()
    wrapper.unmount()
  })
  it('发布跟踪与外协追溯提供同一处置入口，无权限者收到明确联系指引', async () => {
    records.one.state.lots[0].frozen = true; records.one.state.lots[0].frozenByReleasePackageId = 'new-release'
    const wrapper = mount(ProductionWorkbench, { props: { token: 'token', username: 'reader', permissions: ['production.schedule'] } }); await flushPromises()
    await wrapper.get('[aria-label="处理图纸变更 D-001"]').trigger('click'); await flushPromises()
    expect(wrapper.get('[aria-label="图纸变更处置"]').text()).toContain('请联系拥有“分配非标件自制与外协”权限的人员')
    expect(wrapper.find('[aria-label="图纸变更处置"] form').exists()).toBe(false)
    await wrapper.setProps({ permissions: ['production.assign'] })
    expect(wrapper.find('[aria-label="图纸变更处置"] form').exists()).toBe(true)
    await wrapper.findAll('.production-workbench__tabs button').find(button => button.text() === '外协追溯')!.trigger('click'); await flushPromises()
    await wrapper.get('[aria-label="处理图纸变更 D-001"]').trigger('click'); await flushPromises()
    expect(wrapper.find('[aria-label="图纸变更处置"] form').exists()).toBe(true)
    wrapper.unmount()
  })
  it('发布跟踪包含自制及供应商类型，组合条件匹配同一批次且保留完整BOM需求和剩余分配数量', async () => {
    const source = records.one.state
    source.flowMode = 'DepartmentHandover'
    source.lots[0] = { ...source.lots[0], supplierId: 'b', supplier: '外协乙', promisedOn: '2026-11-06' }
    source.lots[1].promisedOn = '2026-11-02'; source.lots[3].paused = true
    source.lots.push({ ...source.lots[0], id: 'mixed-external', lineId: 'line-1', quantity: 6, supplierId: 'a', supplier: '外协甲', promisedOn: '2026-11-08' })
    source.movements.push({ id: 'machining', requestId: 'machining', lotId: 'self-a', kind: 'MachiningReceived', quantity: 2, actor: '主管', occurredAt: '2026-11-01T00:00:00Z' })
    vi.mocked(api.readProductionTrackingSupplierChoices).mockResolvedValue({ companyId: 'company', categories: [{ id: 'machine', name: '机加', isActive: true }, { id: 'weld', name: '焊接', isActive: true }],
      suppliers: [{ id: 'a', code: 'A', name: '外协甲', categoryIds: ['machine'], isActive: true }, { id: 'b', code: 'B', name: '外协乙', categoryIds: ['weld'], isActive: true }] })
    const wrapper = mount(ProductionWorkbench, { props: { token: 'token', username: 'reader', permissions: ['project.content.view', 'production.assign'] } }); await flushPromises()
    const bar = wrapper.get('[aria-label="发布跟踪筛选"]'), filter = (label: string) => bar.get(`[aria-label="筛选${label}"]`)
    const middle = wrapper.get('[aria-label="发布包物料明细"]'), rows = () => middle.findAll('tbody tr'), reset = () => wrapper.findAll('button').find(button => button.text() === '重置筛选')!.trigger('click')
    expect(bar.findAll('label')).toHaveLength(6); expect(bar.findAll('label')[0].find('[aria-label="搜索本包物料"]').exists()).toBe(true)
    expect(filter('供应商').text()).toContain('自制'); expect(filter('供应商类型').text()).toContain('自制'); expect(filter('当前环节').text()).toContain('机加'); expect(filter('当前环节').text()).toContain('外协待交货')
    await filter('供应商').setValue('self'); expect(rows()).toHaveLength(2)
    await rows().find(row => row.text().includes('D-002'))!.trigger('click')
    expect(wrapper.get('.production-workbench__detail .production-workbench__lots').findAll('button')).toHaveLength(1)
    expect(wrapper.get('.production-workbench__detail').text()).toContain('未分配数量 2')
    expect(rows().find(row => row.text().includes('D-002'))!.findAll('td')[7].text()).toBe('0 / 10')
    await middle.get('[aria-label="全选当前筛选物料（跨页）"]').setValue(true)
    await filter('当前环节').setValue('机加'); expect(rows()).toHaveLength(1); expect(middle.get('[aria-label="发布包批量操作"]').text()).toContain('已选 1 项')
    await filter('日期类型').setValue('promised'); await setDateRange(wrapper, ['2026-11-08', '2026-11-08']); expect(rows()).toHaveLength(0)
    await setDateRange(wrapper, ['2026-11-02', '2026-11-02']); expect(rows()).toHaveLength(1)
    await reset(); await filter('供应商类型').setValue('self'); expect(rows()).toHaveLength(2)
    await filter('供应商类型').setValue('machine'); expect(rows()).toHaveLength(1); expect(filter('供应商').text()).not.toContain('自制')
    await filter('当前环节').setValue('机加'); expect(rows()).toHaveLength(0)
    await filter('当前环节').setValue('外协待交货'); expect(rows()).toHaveLength(1)
    await reset(); expect(rows()).toHaveLength(3); expect(source.lines[1].requiredQuantity).toBe(10); expect(source.lots).toHaveLength(5)
    wrapper.unmount()
  })
  it('发布跟踪保留未分配物料，需求和发布日期覆盖无批次物料，预计完成日期只匹配有承诺的批次', async () => {
    const source = records.one.state; source.lots = source.lots.slice(0, 2)
    source.lots[0].promisedOn = '2026-11-06'; source.lots[1].promisedOn = '2026-11-04'
    const wrapper = mount(ProductionWorkbench, { props: { token: 'token', username: 'reader', permissions: ['project.content.view'] } }); await flushPromises()
    const bar = wrapper.get('[aria-label="发布跟踪筛选"]'), filter = (label: string) => bar.get(`[aria-label="筛选${label}"]`), middle = wrapper.get('[aria-label="发布包物料明细"]')
    expect(middle.findAll('tbody tr')).toHaveLength(3); expect(filter('当前环节').text()).toContain('待分配')
    await filter('当前环节').setValue('待分配'); expect(middle.get('tbody tr').text()).toContain('轴')
    await filter('供应商').setValue('self'); expect(middle.findAll('tbody tr')).toHaveLength(0)
    await wrapper.findAll('button').find(button => button.text() === '重置筛选')!.trigger('click')
    await setDateRange(wrapper, ['2026-11-05', '2026-11-05']); expect(middle.findAll('tbody tr')).toHaveLength(3)
    await filter('日期类型').setValue('published'); await setDateRange(wrapper, ['2026-10-19', '2026-10-19']); expect(middle.findAll('tbody tr')).toHaveLength(3)
    await filter('日期类型').setValue('promised'); await setDateRange(wrapper, ['2026-11-05', '2026-11-06']); expect(middle.findAll('tbody tr')).toHaveLength(1); expect(middle.get('tbody tr').text()).toContain('外协板')
    await setDateRange(wrapper, null); expect(middle.findAll('tbody tr')).toHaveLength(3)
    wrapper.unmount()
  })
  it('发布跟踪的自制筛选不带入外协追溯，外协不增加自制选项且切回后保留发布跟踪筛选', async () => {
    const wrapper = mount(ProductionWorkbench, { props: { token: 'token', username: 'reader', permissions: ['project.content.view'] } }); await flushPromises()
    const tracking = () => wrapper.get('[aria-label="发布跟踪筛选"]')
    await tracking().get('[aria-label="筛选供应商类型"]').setValue('self'); expect(wrapper.get('[aria-label="发布包物料明细"]').findAll('tbody tr')).toHaveLength(2)
    await wrapper.findAll('.production-workbench__tabs button').find(button => button.text() === '外协追溯')!.trigger('click'); await flushPromises()
    const outside = wrapper.get('[aria-label="外协追溯筛选"]')
    expect(outside.get('[aria-label="筛选供应商"]').findAll('option').map(item => item.text())).not.toContain('自制')
    expect(outside.get('[aria-label="筛选供应商类型"]').findAll('option').map(item => item.text())).not.toContain('自制')
    expect(outside.get('[aria-label="筛选当前环节"]').findAll('option').map(item => item.text())).not.toContain('生产已签收／待排产')
    expect(wrapper.get('[aria-label="外协物料明细"]').findAll('tbody tr')).toHaveLength(1)
    await wrapper.findAll('.production-workbench__tabs button').find(button => button.text() === '发布跟踪')!.trigger('click'); await flushPromises()
    expect(tracking().get('[aria-label="筛选供应商类型"]').element).toHaveProperty('value', 'self'); expect(wrapper.get('[aria-label="发布包物料明细"]').findAll('tbody tr')).toHaveLength(2)
    wrapper.unmount()
  })
  it('自制按同一批次的多资源组、负责人、环节和交期组合筛选，数量、勾选及右侧安排同步', async () => {
    const lots = records.one.state.lots
    lots[0].assignee = '外协负责人'; lots[0].schedule = [{ resourceGroupId: '外协资源组', date: '2026-11-01', hours: 1 }]
    lots[1].assignee = '张三'
    lots[2].assignee = '张三'; lots[2].promisedOn = '2026-11-02'; lots[2].schedule.push({ resourceGroupId: 'CNC组', date: '2026-11-02', hours: 2 })
    lots[3].assignee = '李四'; lots[3].paused = true
    const wrapper = await openSchedule(['production.schedule', 'project.content.view'])
    const filters = wrapper.get('[aria-label="自制追溯筛选"]'), filter = (label: string) => filters.get(`[aria-label="筛选${label}"]`)
    const middle = wrapper.get('[aria-label="自制批次明细"]'), rows = () => middle.findAll('tbody tr'), right = () => wrapper.get('[aria-label="排程安排"]')
    expect(filters.text()).not.toContain('外协负责人'); expect(filters.text()).not.toContain('外协资源组')
    await middle.get('[aria-label="全选当前筛选自制批次（跨页）"]').setValue(true)
    await filter('资源组').setValue('CNC组')
    expect(rows()).toHaveLength(1); expect(rows()[0].findAll('td')[7].text()).toBe('0 / 3')
    expect(middle.get('[aria-label="排程批量操作"]').text()).toContain('已选 1 个批次'); expect(right().get('h3').text()).toBe('轴')
    await filter('负责人').setValue('李四'); expect(rows()).toHaveLength(0); expect(right().find('h3').exists()).toBe(false)
    expect(middle.get('[aria-label="排程批量操作"]').text()).toContain('已选 0 个批次')
    await filter('负责人').setValue('张三'); await filter('当前环节').setValue('已暂停'); expect(rows()).toHaveLength(0)
    expect(filters.findAll('label')).toHaveLength(6); expect(wrapper.findComponent(ElDatePicker).props('type')).toBe('daterange')
    await filter('当前环节').setValue(''); await filter('日期类型').setValue('promised'); await setDateRange(wrapper, ['2026-11-02', '2026-11-02']); expect(rows()).toHaveLength(1)
    await setDateRange(wrapper, ['2026-11-03', '2026-11-02']); expect(middle.get('[role="alert"]').text()).toBe('开始日期不能晚于结束日期。'); expect(rows()).toHaveLength(0)
    await wrapper.findAll('button').find(button => button.text() === '重置筛选')!.trigger('click'); expect(rows()).toHaveLength(3); expect(filter('资源组').element).toHaveProperty('value', ''); expect(wrapper.findComponent(ElDatePicker).props('modelValue')).toBeNull()
    expect(records.one.state.lots).toHaveLength(4); expect(records.one.state.lines[2].requiredQuantity).toBe(10)
    wrapper.unmount()
  })
  it('自制支持未分配资源和未指定负责人、需求及发布日期范围，筛选不串到外协且空结果不保留旧安排', async () => {
    records.one.state.lots[2].promisedOn = '2026-11-02'; records.one.state.lots[2].assignee = '张三'
    records.one.state.lines[2].requiredOn = '2026-11-06'
    const wrapper = await openSchedule(['project.content.view'])
    const filters = () => wrapper.get('[aria-label="自制追溯筛选"]'), filter = (label: string) => filters().get(`[aria-label="筛选${label}"]`)
    const middle = () => wrapper.get('[aria-label="自制批次明细"]'), rows = () => middle().findAll('tbody tr')
    await filter('资源组').setValue('unassigned'); expect(rows()).toHaveLength(1); expect(rows()[0].text()).toContain('支架')
    await filter('资源组').setValue(''); await filter('负责人').setValue('unassigned'); expect(rows()).toHaveLength(2)
    await wrapper.findAll('button').find(button => button.text() === '重置筛选')!.trigger('click'); await setDateRange(wrapper, ['2026-11-06', '2026-11-06']); expect(rows()).toHaveLength(2)
    await setDateRange(wrapper, ['2026-11-01', '2026-11-05']); expect(rows()).toHaveLength(1)
    await wrapper.findAll('button').find(button => button.text() === '重置筛选')!.trigger('click'); await filter('日期类型').setValue('published'); await setDateRange(wrapper, ['2026-10-19', '2026-10-19']); expect(rows()).toHaveLength(3)
    await wrapper.findAll('button').find(button => button.text() === '重置筛选')!.trigger('click'); await filter('日期类型').setValue('promised'); await setDateRange(wrapper, ['2026-11-01', '2026-11-03']); expect(rows()).toHaveLength(2)
    await middle().get('[aria-label="搜索自制批次"]').setValue('不存在'); expect(rows()).toHaveLength(0); expect(wrapper.get('[aria-label="排程安排"]').find('h3').exists()).toBe(false)
    await middle().get('[aria-label="搜索自制批次"]').setValue('')
    await wrapper.findAll('.production-workbench__tabs button').find(button => button.text() === '外协追溯')!.trigger('click'); await flushPromises()
    expect(wrapper.get('[aria-label="外协追溯筛选"]').find('[aria-label="筛选资源组"]').exists()).toBe(false); expect(wrapper.get('[aria-label="外协物料明细"]').findAll('tbody tr')).toHaveLength(1)
    await wrapper.findAll('.production-workbench__tabs button').find(button => button.text() === '自制追溯')!.trigger('click'); await flushPromises(); expect(rows()).toHaveLength(2)
    wrapper.unmount()
  })
  it('发布跟踪按勾选下载PDF、STEP及冻结BOM，下载本包不受搜索或勾选影响且包含包内所有正式图纸', async () => {
    records.one.state.formalReleasePackageId = 'formal'
    const items = [0, 1, 2, 3].map(index => drawingForLine(index, { releasePackageId: 'formal', releasePackageNumber: 'RP-FORMAL' }))
    vi.mocked(api.listProductionDrawings).mockResolvedValue([...items, drawingForLine(4), drawingForLine(5, { projectId: 'other', releasePackageId: 'formal' })])
    const wrapper = mount(ProductionWorkbench, { props: { token: 'token', username: 'reader', permissions: ['project.content.view'] } }); await flushPromises()
    const tools = wrapper.get('[aria-label="发布包批量操作"]'), button = (name: string) => tools.findAll('button').find(item => item.text() === name)!
    expect(button('批量下载 PDF').attributes()).toHaveProperty('disabled'); expect(button('批量下载 BOM').attributes()).toHaveProperty('disabled'); expect(button('下载本包').attributes('disabled')).toBeUndefined()
    await wrapper.get('[aria-label="搜索本包物料"]').setValue('D-001')
    await wrapper.get('[aria-label="全选当前筛选物料（跨页）"]').setValue(true)
    await button('批量下载 PDF').trigger('click'); await flushPromises()
    expect(api.downloadProductionDrawingArchive).toHaveBeenLastCalledWith('project', ['version-0'], 'Pdf', 'token', 'RP-one', expect.any(Function), [expect.objectContaining({ versionId: 'version-0', releasePackageId: 'formal' })])
    await button('批量下载 STEP').trigger('click'); await flushPromises()
    expect(api.downloadProductionDrawingArchive).toHaveBeenLastCalledWith('project', ['version-0'], 'Step', 'token', 'RP-one', expect.any(Function), [expect.objectContaining({ versionId: 'version-0', releasePackageId: 'formal' })])
    await button('批量下载 BOM').trigger('click'); await flushPromises()
    expect(api.downloadGeneratedFile).toHaveBeenLastCalledWith(expect.any(Blob), 'RP-FORMAL-选中BOM.xlsx')
    const xml = async (blob: Blob) => {
      const bytes = await new Promise<ArrayBuffer>(resolve => { const reader = new FileReader(); reader.onload = () => resolve(reader.result as ArrayBuffer); reader.readAsArrayBuffer(blob) })
      const { unzipSync, strFromU8 } = await import('three/examples/jsm/libs/fflate.module.js')
      return strFromU8(unzipSync(new Uint8Array(bytes))['xl/worksheets/sheet1.xml']!)
    }
    const selectedSheet = await xml(vi.mocked(api.downloadGeneratedFile).mock.calls.at(-1)![0])
    expect(selectedSheet).toContain('MAT-0'); expect(selectedSheet).not.toContain('MAT-1')
    await button('清空选择').trigger('click')
    await button('下载本包').trigger('click'); await flushPromises()
    expect(api.downloadProductionDrawingArchive).toHaveBeenCalledWith('project', ['version-0', 'version-1', 'version-2', 'version-3'], 'Step', 'token', 'RP-FORMAL', undefined, expect.arrayContaining([expect.objectContaining({ releasePackageId: 'formal' })]))
    expect(api.downloadProductionDrawingArchive).toHaveBeenCalledWith('project', ['version-0', 'version-1', 'version-2', 'version-3'], 'Pdf', 'token', 'RP-FORMAL', undefined, expect.arrayContaining([expect.objectContaining({ releasePackageId: 'formal' })]))
    expect(api.getProjectExportMetadata).toHaveBeenLastCalledWith('project', 'token', 'formal')
    expect(api.downloadGeneratedFile).toHaveBeenLastCalledWith(expect.any(Blob), 'RP-FORMAL-本包BOM.xlsx')
    const fullSheet = await xml(vi.mocked(api.downloadGeneratedFile).mock.calls.at(-1)![0])
    expect(fullSheet).toContain('MAT-3'); expect(fullSheet).not.toContain('MAT-4'); expect(fullSheet).not.toContain('MAT-5')
    wrapper.unmount()
  })
  it('发布跟踪BOM可独立于转换状态下载；本包未就绪或在导出前失效时不下载任何文件', async () => {
    const items = [0, 1, 2].map(index => drawingForLine(index, { pdfReady: false }))
    vi.mocked(api.listProductionDrawings).mockResolvedValue(items)
    const wrapper = mount(ProductionWorkbench, { props: { token: 'token', username: 'reader', permissions: ['project.content.view'] } }); await flushPromises()
    const tools = wrapper.get('[aria-label="发布包批量操作"]'), button = (name: string) => tools.findAll('button').find(item => item.text() === name)!
    await wrapper.get('[aria-label="全选当前筛选物料（跨页）"]').setValue(true)
    await button('批量下载 BOM').trigger('click'); await flushPromises(); expect(api.downloadGeneratedFile).toHaveBeenCalledOnce()
    vi.mocked(api.downloadGeneratedFile).mockClear()
    await button('下载本包').trigger('click'); await flushPromises(); expect(ElMessage.error).toHaveBeenLastCalledWith(expect.stringContaining('PDF 待转换'))
    vi.mocked(api.listProductionDrawings).mockResolvedValueOnce(items.map(item => ({ ...item, pdfReady: true }))).mockResolvedValueOnce([])
    await button('下载本包').trigger('click'); await flushPromises(); expect(ElMessage.error).toHaveBeenLastCalledWith(expect.stringContaining('已作废'))
    expect(api.downloadGeneratedFile).not.toHaveBeenCalled(); expect(api.downloadProductionDrawingArchive).not.toHaveBeenCalled()
    await wrapper.setProps({ permissions: ['production.assign'] })
    expect(tools.findAll('button').some(item => ['批量下载 PDF', '批量下载 STEP', '批量下载 BOM', '下载本包'].includes(item.text()))).toBe(false)
    wrapper.unmount()
  })
  it('只读账号可勾选未签收、冻结和跨页自制批次，按本包正式版去重后分别下载 PDF 和 STEP', async () => {
    records.one.state.movements = []; records.one.state.lots[1].frozen = true
    records.one.state.lots = [records.one.state.lots[0], records.one.state.lots[1], ...Array.from({ length: 50 }, (_, index) => ({ ...records.one.state.lots[2], id: `read-only-${index}` }))]
    vi.mocked(api.listProductionDrawings).mockResolvedValue([drawingForLine(1), drawingForLine(2), drawingForLine(1, { versionId: 'other-package', releasePackageId: 'two' }), drawingForLine(2, { versionId: 'other-project', projectId: 'other' })])
    const wrapper = await openSchedule(['project.content.view'])
    const tools = wrapper.get('[aria-label="排程批量操作"]'), button = (name: string) => tools.findAll('button').find(item => item.text() === name)!
    expect(button('清空选择').attributes()).toHaveProperty('disabled'); expect(button('清空选择').attributes('aria-busy')).toBeUndefined()
    await wrapper.get('[aria-label="全选当前筛选自制批次（跨页）"]').setValue(true)
    expect(tools.text()).toContain('已选 51 个批次'); expect(button('清空选择').attributes('disabled')).toBeUndefined(); expect(tools.text()).not.toContain('批量排程')
    await wrapper.get('[aria-label="自制批次分页"]').findAll('button')[1].trigger('click')
    expect(wrapper.get('[aria-label="勾选排程批次 D-003 read-only-49"]').element).toHaveProperty('checked', true)
    await button('批量下载 PDF').trigger('click'); await flushPromises()
    expect(api.downloadProductionDrawingArchive).toHaveBeenLastCalledWith('project', ['version-1', 'version-2'], 'Pdf', 'token', 'RP-one', expect.any(Function), [expect.objectContaining({ versionId: 'version-1', releasePackageId: 'one' }), expect.objectContaining({ versionId: 'version-2', releasePackageId: 'one' })])
    await button('批量下载 STEP').trigger('click'); await flushPromises()
    expect(api.downloadProductionDrawingArchive).toHaveBeenLastCalledWith('project', ['version-1', 'version-2'], 'Step', 'token', 'RP-one', expect.any(Function), [expect.objectContaining({ versionId: 'version-1', releasePackageId: 'one' }), expect.objectContaining({ versionId: 'version-2', releasePackageId: 'one' })])
    expect(api.listProductionDrawings).toHaveBeenLastCalledWith('token', true, 'project')
    await button('清空选择').trigger('click'); expect(tools.text()).toContain('已选 0 个批次'); wrapper.unmount()
  })
  it('有图纸权限的排程账号勾选未签收批次后仅可下载，不能用批量排程越过签收', async () => {
    records.one.state.movements = []
    const wrapper = await openSchedule(['project.content.view', 'production.schedule'])
    await wrapper.get('[aria-label="全选当前筛选自制批次（跨页）"]').setValue(true)
    const tools = wrapper.get('[aria-label="排程批量操作"]')
    expect(tools.findAll('button').find(item => item.text() === '批量排程')!.attributes()).toMatchObject({ disabled: '', title: expect.stringContaining('尚未签收') })
    expect(tools.findAll('button').find(item => item.text() === '批量下载 PDF')!.attributes('disabled')).toBeUndefined(); wrapper.unmount()
  })
  it('外协勾选支持格式分开下载，无同包图纸、PDF或STEP未就绪均阻止部分下载', async () => {
    records.one.state.lots.push({ ...records.one.state.lots[0], id: 'external-mixed', lineId: 'line-1' })
    const wrapper = await openSchedule(['project.content.view'])
    await wrapper.findAll('.production-workbench__tabs button').find(button => button.text() === '外协追溯')!.trigger('click'); await flushPromises()
    const tools = wrapper.get('[aria-label="外协批量操作"]'), button = (name: string) => tools.findAll('button').find(item => item.text() === name)!
    await wrapper.get('[aria-label="选择物料 D-001 W1"]').setValue(true)
    vi.mocked(api.listProductionDrawings).mockResolvedValue([drawingForLine(0), drawingForLine(1)])
    await button('批量下载 STEP').trigger('click'); await flushPromises()
    expect(api.downloadProductionDrawingArchive).toHaveBeenLastCalledWith('project', ['version-0'], 'Step', 'token', 'RP-one', expect.any(Function), [expect.objectContaining({ versionId: 'version-0', releasePackageId: 'one' })])
    vi.mocked(api.downloadProductionDrawingArchive).mockClear()
    await wrapper.get('[aria-label="全选当前筛选物料（跨页）"]').setValue(true)
    for (const [items, format, message] of [
      [[drawingForLine(0), drawingForLine(1, { releasePackageId: 'other' })], 'PDF', '没有可用的正式图纸'],
      [[drawingForLine(0), drawingForLine(1, { pdfReady: false })], 'PDF', 'PDF 待转换'],
      [[drawingForLine(0), drawingForLine(1, { modelFiles: [] })], 'STEP', '未关联同包三维模型'],
    ] as const) {
      vi.mocked(api.listProductionDrawings).mockResolvedValue([...items]); await button(`批量下载 ${format}`).trigger('click'); await flushPromises()
      expect(ElMessage.error).toHaveBeenLastCalledWith(expect.stringContaining(message)); expect(api.downloadProductionDrawingArchive).not.toHaveBeenCalled()
    }
    vi.mocked(api.listProductionDrawings).mockResolvedValue([drawingForLine(0), drawingForLine(1)])
    await button('批量下载 PDF').trigger('click'); await flushPromises(); expect(api.downloadProductionDrawingArchive).toHaveBeenLastCalledWith('project', ['version-0', 'version-1'], 'Pdf', 'token', 'RP-one', expect.any(Function), [expect.objectContaining({ versionId: 'version-0', releasePackageId: 'one' }), expect.objectContaining({ versionId: 'version-1', releasePackageId: 'one' })])
    wrapper.unmount()
  })
  it('准备下载期间防止重复提交，网络失败显示中文并恢复下载按钮', async () => {
    let fail: (cause: Error) => void = () => {}
    vi.mocked(api.listProductionDrawings).mockImplementation(() => new Promise((_resolve, reject) => { fail = reject }))
    const wrapper = await openSchedule(['project.content.view'])
    await wrapper.get('[aria-label="全选当前筛选自制批次（跨页）"]').setValue(true)
    const tools = wrapper.get('[aria-label="排程批量操作"]'), button = (name: string) => tools.findAll('button').find(item => item.text() === name)!
    await button('批量下载 PDF').trigger('click'); await button('批量下载 STEP').trigger('click')
    expect(api.listProductionDrawings).toHaveBeenCalledOnce(); expect(button('批量下载 PDF').attributes('aria-busy')).toBe('true')
    expect(button('清空选择').attributes('disabled')).toBeUndefined()
    fail(new Error('Failed to fetch')); await flushPromises()
    expect(ElMessage.error).toHaveBeenLastCalledWith('无法连接服务器，请检查网络后重试。')
    expect(button('批量下载 PDF').attributes('disabled')).toBeUndefined(); expect(button('批量下载 STEP').attributes('aria-busy')).toBe('false'); wrapper.unmount()
  })
  it('外协组合筛选限定同一批次，数量和右侧明细随筛选更新，支持重置', async () => {
    const state = records.one.state
    state.lots[0] = { ...state.lots[0], supplierId: 'a', supplier: '外协甲', promisedOn: '2026-11-01', quantity: 4 }
    state.lots.push({ ...state.lots[0], id: 'external-b', supplierId: 'b', supplier: '外协乙', promisedOn: '2026-11-06', quantity: 6, paused: true })
    vi.mocked(api.readProductionTrackingSupplierChoices).mockResolvedValue({ companyId: 'company',
      categories: [{ id: 'cut', name: '切割', isActive: true }, { id: 'machine', name: '机加', isActive: false }],
      suppliers: [{ id: 'a', code: 'A', name: '外协甲', categoryIds: ['cut', 'machine'], isActive: false }, { id: 'b', code: 'B', name: '外协乙', categoryIds: ['machine'], isActive: true }] })
    const wrapper = mount(ProductionWorkbench, { props: { token: 'token', username: 'reader', permissions: ['project.content.view'] } }); await flushPromises()
    await wrapper.findAll('.production-workbench__tabs button').find(button => button.text() === '外协追溯')!.trigger('click'); await flushPromises()
    const middle = wrapper.get('[aria-label="外协物料明细"]'), filter = (label: string) => wrapper.get(`[aria-label="筛选${label}"]`)
    await middle.get('tbody tr').trigger('click')
    await filter('供应商').setValue('a'); expect(middle.get('tbody tr').text()).toContain('0 / 4'); expect(wrapper.get('[aria-label="外协批次信息"]').text()).toContain('外协甲')
    await filter('当前环节').setValue('已暂停'); expect(middle.findAll('tbody tr')).toHaveLength(0); expect(wrapper.find('[aria-label="外协批次信息"]').exists()).toBe(false)
    await filter('当前环节').setValue(''); await filter('供应商类型').setValue('cut'); expect(filter('供应商').findAll('option').map(item => item.text())).toEqual(['全部供应商', '外协甲'])
    await filter('日期类型').setValue('promised'); await setDateRange(wrapper, ['2026-11-06', '2026-11-30']); expect(middle.findAll('tbody tr')).toHaveLength(0)
    await setDateRange(wrapper, ['2026-11-01', '2026-11-01']); expect(middle.findAll('tbody tr')).toHaveLength(1)
    await wrapper.findAll('button').find(button => button.text() === '重置筛选')!.trigger('click'); expect(middle.get('tbody tr').text()).toContain('0 / 10')
    expect(wrapper.findComponent(ElDatePicker).props('modelValue')).toBeNull(); expect(filter('供应商').element).toHaveProperty('value', '')
    wrapper.unmount()
  })
  it('外协单个日期区间筛选需求和发布日，包含边界，清除后取消限制，缺少交期不匹配', async () => {
    const wrapper = mount(ProductionWorkbench, { props: { token: 'token', username: 'reader', permissions: ['project.content.view'] } }); await flushPromises()
    await wrapper.findAll('.production-workbench__tabs button').find(button => button.text() === '外协追溯')!.trigger('click'); await flushPromises()
    const middle = wrapper.get('[aria-label="外协物料明细"]'), filter = (label: string) => wrapper.get(`[aria-label="筛选${label}"]`)
    expect(wrapper.get('[aria-label="外协追溯筛选"]').findAll('label')).toHaveLength(6)
    expect(wrapper.findComponent(ElDatePicker).props('valueFormat')).toBe('YYYY-MM-DD')
    await setDateRange(wrapper, ['2026-11-05', '2026-11-05']); expect(middle.findAll('tbody tr')).toHaveLength(1)
    await setDateRange(wrapper, ['2026-11-06', '2026-11-06']); expect(middle.findAll('tbody tr')).toHaveLength(0)
    await setDateRange(wrapper, ['2026-11-01', '2026-11-05']); expect(middle.findAll('tbody tr')).toHaveLength(1)
    await filter('日期类型').setValue('promised'); expect(middle.findAll('tbody tr')).toHaveLength(0)
    await setDateRange(wrapper, null); expect(middle.findAll('tbody tr')).toHaveLength(1)
    await filter('日期类型').setValue('published'); await setDateRange(wrapper, ['2026-10-19', '2026-10-19']); expect(middle.findAll('tbody tr')).toHaveLength(1)
    await setDateRange(wrapper, ['2026-10-20', '2026-10-19']); expect(wrapper.get('[role="alert"]').text()).toBe('开始日期不能晚于结束日期。'); expect(middle.findAll('tbody tr')).toHaveLength(0)
    wrapper.unmount()
  })
  it('部门流程领料或取消结束后按已完结筛选，不继续显示为待执行', async () => {
    const implementation = vi.mocked(api.listProductionTrackingProjects).getMockImplementation()!
    vi.mocked(api.listProductionTrackingProjects).mockImplementation(async (token, selfOnly) => (await implementation(token, selfOnly)).map(project => ({ ...project, packages: project.packages.map((item, index) => ({ ...item, status: index === 0 ? '领料完成' : '已取消' })) })))
    const wrapper = mount(ProductionWorkbench, { props: { token: 'token', username: 'admin', permissions: [] } }); await flushPromises()
    expect(wrapper.findAll('.production-workbench__package')).toHaveLength(0)
    await wrapper.findAll('.production-workbench__toolbar label').find(label => label.text() === '已完结')!.get('input').setValue(true); await flushPromises()
    expect(wrapper.findAll('.production-workbench__package')).toHaveLength(2)
    wrapper.unmount()
  })
  it('当前包纯外协时进入排程自动切到其他自制包，并显示其批次安排', async () => {
    const self = records.one.state.lots[1]
    records.one.state.lots = [records.one.state.lots[0]]
    records.two.state.lots = [{ ...self, id: 'other-self' }]
    records.two.state.lines[1].name = '第二包自制件'
    const wrapper = await openSchedule()
    expect(wrapper.findAll('.production-workbench__package')).toHaveLength(1)
    expect(wrapper.get('.production-workbench__package').text()).toContain('RP-two')
    expect(wrapper.get('[aria-label="排程安排"] h3').text()).toBe('第二包自制件')
    wrapper.unmount()
  })
  it('生产执行在PDA屏幕中加载真实移动扫码页，可独立刷新并随账号切换重置', async () => {
    const wrapper = await openSchedule()
    await wrapper.findAll('.production-workbench__tabs button').find(button => button.text() === '生产执行')!.trigger('click')
    await flushPromises()
    const frame = wrapper.get('iframe[title="PDA移动扫码页面"]')
    expect(frame.attributes('src')).toContain('?productionScan=1')
    expect(frame.attributes('allow')).toBe('camera')
    expect(wrapper.get('.production-workbench__execution-actions a').attributes('href')).toBe(frame.attributes('src'))
    expect(wrapper.find('.production-workbench__execution-actions form').exists()).toBe(false)
    await wrapper.get('[aria-label="刷新PDA页面"]').trigger('click')
    expect(wrapper.get('iframe').element).not.toBe(frame.element)
    const refreshed = wrapper.get('iframe').element
    await wrapper.setProps({ token: 'different-account-token' })
    expect(wrapper.get('iframe').element).not.toBe(refreshed)
    wrapper.unmount()
  })
  it('外协追溯位于自制追溯左侧，混合物料仅统计外协数量、交期和质量记录', async () => {
    records.one.state.lots.push({ id: 'outside-mixed', lineId: 'line-1', route: 'External', quantity: 7, supplier: '外协甲', promisedOn: '2026-12-01', paused: false, frozen: false, schedule: [] })
    records.one.state.movements.push(...['self-a', 'outside-mixed'].map((lotId,index) => ({id:'pass-'+lotId,requestId:'pass-'+lotId,lotId,kind:'Pass',quantity:index===0?2:1,actor:'admin',occurredAt:''})))
    const wrapper = mount(ProductionWorkbench,{props:{token:'token',username:'admin',permissions:['production.assign','project.content.view']}});await flushPromises()
    const tabs=wrapper.findAll('.production-workbench__tabs button').map(button=>button.text());expect(tabs.indexOf('外协追溯')).toBeLessThan(tabs.indexOf('自制追溯'));expect(tabs).not.toContain('供应商库')
    await wrapper.findAll('.production-workbench__tabs button').find(button=>button.text()==='外协追溯')!.trigger('click');await flushPromises()
    expect(api.listProductionTrackingProjects).toHaveBeenLastCalledWith('token',false,true)
    const middle=wrapper.get('[aria-label="外协物料明细"]');expect(middle.findAll('tbody tr')).toHaveLength(2);expect(middle.text()).not.toContain('轴')
    const row=middle.findAll('tbody tr')[1];expect(row.findAll('td')[7].text()).toBe('1 / 7');expect(row.findAll('td')[11].text()).toBe('2026-12-01')
    await row.trigger('click');expect(wrapper.get('[aria-label="外协批次信息"]').text()).toContain('外协甲');expect(wrapper.text()).not.toContain('自制 ·')
    expect(wrapper.find('[aria-label="外协批量操作"]').exists()).toBe(true);wrapper.unmount()
  })
  it('仅供应商查看权限可进入只读供应商库，不读取项目或暴露维护按钮', async () => {
    const read=vi.spyOn(api,'readProductionSuppliers').mockResolvedValue({companyId:'company',rowVersion:1,categories:[],suppliers:[]})
    const wrapper=mount(ProductionWorkbench,{props:{token:'token',username:'admin',permissions:['production.supplier.view']}});await flushPromises()
    expect(wrapper.findAll('.production-workbench__tabs button').map(button=>button.text())).toEqual(['供应商库']);expect(read).toHaveBeenCalled();expect(api.listProductionTrackingProjects).not.toHaveBeenCalled()
    expect(wrapper.findAll('button').some(button=>button.text()==='新建供应商')).toBe(false)
    await wrapper.setProps({permissions:['production.supplier.view','production.supplier.manage']});await flushPromises();expect(wrapper.findAll('button').some(button=>button.text()==='新建供应商')).toBe(true)
    wrapper.unmount()
  })
  it('快速切回发布跟踪时忽略晚到的自制发布包响应', async () => {
    const list = vi.mocked(api.listProductionTrackingProjects)
    const implementation = list.getMockImplementation()!
    let deliver: ((value: Awaited<ReturnType<typeof api.listProductionTrackingProjects>>) => void) | undefined
    list.mockImplementation((token, selfOnly) => selfOnly
      ? new Promise(resolve => { deliver = resolve }) : implementation(token, selfOnly))
    const wrapper = mount(ProductionWorkbench, { props: { token: 'token', username: 'admin', permissions: [] } })
    await flushPromises()
    await wrapper.findAll('.production-workbench__tabs button').find(button => button.text() === '自制追溯')!.trigger('click')
    await wrapper.findAll('.production-workbench__tabs button').find(button => button.text() === '发布跟踪')!.trigger('click')
    await flushPromises()
    deliver!(await implementation('token', true))
    await flushPromises()
    expect(wrapper.findAll('.production-workbench__package')).toHaveLength(2)
    expect(wrapper.get('[aria-label="发布包物料明细"]').text()).toContain('外协板')
    wrapper.unmount()
  })
  it('排程只显示自制发布包和批次，混合物料不混入外协数量、状态与交期，刷新后清除外协选择', async () => {
    const self = records.one.state.lots[1]
    self.promisedOn = '2026-11-04'
    records.one.state.lots.push({ ...self, id: 'external-same-line', route: 'External', quantity: 8, supplier: '不应显示的外协供应商', promisedOn: '2030-01-01' })
    records.one.state.movements.push({ id: 'external-pass', requestId: 'external-pass', lotId: 'external-same-line', kind: 'Pass', quantity: 8, actor: 'admin', occurredAt: '' })
    const wrapper = mount(ProductionWorkbench, { props: { token: 'token', username: 'admin', permissions: ['production.schedule'] } })
    await flushPromises()
    await wrapper.findAll('tbody tr')[0].trigger('click')
    await wrapper.findAll('.production-workbench__tabs button').find(button => button.text() === '自制追溯')!.trigger('click')
    await flushPromises()
    expect(api.listProductionTrackingProjects).toHaveBeenLastCalledWith('token', true)
    expect(wrapper.findAll('.production-workbench__package')).toHaveLength(1)
    expect(wrapper.get('[aria-label="筛选发布包统计"]').text()).toBe('1包2项9个')
    const middle = wrapper.get('[aria-label="自制批次明细"]')
    expect(middle.findAll('tbody tr')).toHaveLength(3)
    const cells = middle.findAll('tbody tr')[0].findAll('td')
    expect(cells[7].text()).toBe('0 / 2')
    expect(cells[9].text()).toBe('生产已签收／待排产')
    expect(cells[11].text()).toBe('2026-11-04')
    expect(wrapper.get('[aria-label="排程安排"]').get('h3').text()).toBe('支架')
    expect(wrapper.text()).not.toContain('不应显示的外协供应商')
    expect(middle.text()).not.toContain('外协板')
    records.one.state.lots = records.one.state.lots.map(lot => ({ ...lot, route: 'External' }))
    await wrapper.findAll('button').find(button => button.text() === '刷新')!.trigger('click')
    await flushPromises()
    expect(wrapper.findAll('.production-workbench__package')).toHaveLength(0)
    expect(wrapper.text()).toContain('暂无已分配自制批次的发布包')
    expect(wrapper.find('.production-workbench__schedule-form').exists()).toBe(false)
    expect(wrapper.get('[aria-label="排程安排"]').text()).not.toContain('支架')
    await wrapper.findAll('.production-workbench__tabs button').find(button => button.text() === '发布跟踪')!.trigger('click')
    await flushPromises()
    expect(api.listProductionTrackingProjects).toHaveBeenLastCalledWith('token', false)
    expect(wrapper.findAll('.production-workbench__package')).toHaveLength(2)
    expect(wrapper.get('[aria-label="发布包物料明细"]').text()).toContain('外协板')
    wrapper.unmount()
  })
  it('图号使用BOM型号，保留料号搜索并显示同一物料的名称', async () => {
    const line = records.one.state.lines[1]
    line.drawingNumber = '7002541.00-02'
    line.materialCode = '02041000013'
    line.name = '垫片'
    const wrapper = mount(ProductionWorkbench, { props: { token: 'token', username: 'admin', permissions: ['production.schedule'] } })
    await flushPromises()
    const search = wrapper.get('[aria-label="搜索本包物料"]')
    for (const query of ['7002541.00-02', '02041000013', '垫片']) {
      await search.setValue(query)
      expect(wrapper.findAll('tbody tr')).toHaveLength(1)
      const cell = wrapper.get('tbody tr td:nth-child(3)')
      expect(cell.get('strong').text()).toBe('7002541.00-02')
      expect(cell.get('small').text()).toBe('垫片')
      expect(cell.attributes('title')).toContain('物料编码：02041000013')
    }
    await wrapper.findAll('.production-workbench__tabs button').find(button => button.text() === '自制追溯')!.trigger('click')
    await flushPromises()
    await wrapper.get('[aria-label="搜索自制批次"]').setValue('02041000013')
    expect(wrapper.findAll('tbody tr')).toHaveLength(1)
    expect(wrapper.get('tbody tr td:nth-child(3)').text()).toBe('7002541.00-02垫片')
    wrapper.unmount()
  })
  it('发布包跟踪不默认选择物料，刷新保留主动选择，切包后清空详情', async () => {
    const wrapper = mount(ProductionWorkbench, { props: { token: 'token', username: 'admin', permissions: [] } })
    await flushPromises()
    const detail = () => wrapper.get('.production-workbench__detail')
    const refresh = async () => {
      await wrapper.findAll('button').find(button => button.text() === '刷新')!.trigger('click')
      await flushPromises()
    }
    expect(detail().text()).toBe('点击物料查看详细状态')
    expect(wrapper.find('tbody tr.selected').exists()).toBe(false)
    await refresh()
    expect(detail().text()).toBe('点击物料查看详细状态')
    await wrapper.get('input[aria-label="全选当前筛选物料（跨页）"]').setValue(true)
    expect(detail().text()).toBe('点击物料查看详细状态')
    await wrapper.findAll('tbody tr')[1].trigger('click')
    expect(detail().get('h3').text()).toBe('支架')
    await refresh()
    expect(detail().get('h3').text()).toBe('支架')
    await wrapper.findAll('.production-workbench__package').find(button => button.text().includes('RP-two'))!.trigger('click')
    await flushPromises()
    expect(detail().text()).toBe('点击物料查看详细状态')
    expect(wrapper.find('tbody tr.selected').exists()).toBe(false)
    await wrapper.findAll('.production-workbench__package').find(button => button.text().includes('RP-one'))!.trigger('click')
    await flushPromises()
    expect(detail().text()).toBe('点击物料查看详细状态')
    await wrapper.findAll('tbody tr')[0].trigger('click')
    expect(detail().get('h3').text()).toBe('外协板')
    records.one.state.lines = records.one.state.lines.filter(line => line.id !== 'line-0')
    await refresh()
    expect(detail().text()).toBe('点击物料查看详细状态')
    wrapper.unmount()
  })
  it('没有当前物料也可从列表批量分配，主动选择后右侧入口同样可用，数量不可编辑', async () => {
    records.one.state.receivedAt = '2026-10-19T00:00:00Z'
    const wrapper = mount(ProductionWorkbench, { props: { token: 'token', username: 'admin', permissions: ['project.content.view', 'production.assign'] } })
    await flushPromises()
    await wrapper.get('input[aria-label="全选当前筛选物料（跨页）"]').setValue(true)
    await wrapper.findAll('button').find(button => button.text() === '批量分配')!.trigger('click')
    expect(wrapper.get('[role=dialog]').findAll('tbody tr')).toHaveLength(3)
    await wrapper.get('[role=dialog]').findAll('button').find(button => button.text() === '关闭')!.trigger('click')
    await wrapper.findAll('tbody tr')[0].trigger('click')
    await wrapper.findAll('button').find(button => button.text() === '批量修改所选分配')!.trigger('click')
    expect(wrapper.get('[role=dialog]').attributes('aria-label')).toBe('批量分配自制／外协')
    expect(wrapper.get('[role=dialog]').findAll('tbody tr')).toHaveLength(3)
    expect(wrapper.get('[role=dialog]').findAll('input[type=number]')).toHaveLength(0)
    wrapper.unmount()
  })
  it('按搜索、状态和完结筛选同步汇总发布包、物料项及需求数量', async () => {
    const base = (await api.listProductionTrackingProjects('token'))[0]!
    vi.mocked(api.listProductionTrackingProjects).mockResolvedValue([
      { ...base, packages: [{ ...base.packages[0]!, totalQuantity: 30 },
        { ...base.packages[1]!, status: '待接收', totalLines: 2, totalQuantity: 5.25 }] },
      { id: 'other', code: 'P002', name: '第二组', packages: [
        { ...base.packages[0]!, releasePackageId: 'three', packageNumber: 'RP-three', totalLines: 1, totalQuantity: 0.75 },
        { ...base.packages[0]!, releasePackageId: 'closed', packageNumber: 'RP-closed', status: '已完结', totalLines: 4, totalQuantity: 12 },
      ] },
    ])
    const wrapper = mount(ProductionWorkbench, { props: { token: 'token', username: 'admin', permissions: [] } })
    await flushPromises()
    const totals = () => wrapper.get('[aria-label="筛选发布包统计"]').text()
    expect(totals()).toBe('3包6项36个')
    const search = wrapper.get('[aria-label="搜索生产发布包"]')
    await search.setValue('RP-two')
    expect(totals()).toBe('1包2项5.25个')
    await search.setValue('第二组')
    expect(totals()).toBe('1包1项0.75个')
    await wrapper.get('.production-workbench__toolbar input[type=checkbox]').setValue(true)
    expect(totals()).toBe('2包5项12.75个')
    await wrapper.get('.production-workbench__toolbar select').setValue('待排产')
    expect(totals()).toBe('1包1项0.75个')
    await search.setValue('无匹配结果')
    expect(totals()).toBe('0包0项0个')
    expect(api.readProductionTrackingRelease).toHaveBeenCalledTimes(1)
    wrapper.unmount()
  })
  it('分配数量已满时提供修改已有批次入口，发生交付后禁用该入口', async () => {
    records.one.state.receivedAt = '2026-10-19T00:00:00Z'
    const wrapper = mount(ProductionWorkbench, { props: { token: 'token', username: 'admin', permissions: ['project.content.view', 'production.assign'] } })
    await flushPromises()
    const edit = () => wrapper.findAll('button').find(button => button.text() === '修改选中批次分配')!
    await wrapper.findAll('tbody tr')[0].trigger('click')
    expect(edit().attributes('disabled')).toBeUndefined()
    expect(wrapper.findAll('button').find(button => button.text() === '确认新增分配')!.attributes()).toHaveProperty('disabled')
    records.one.state.movements.push({ id: 'delivered', requestId: 'delivered', lotId: 'external', kind: 'ExternalDelivered', quantity: 1, actor: 'admin', occurredAt: '' })
    await wrapper.findAll('button').find(button => button.text() === '刷新')!.trigger('click')
    await flushPromises()
    expect(edit().attributes()).toMatchObject({ disabled: '', title: '该批次已有加工、交付、取消或转版记录，不能修改分配。' })
    wrapper.unmount()
  })
  it('使用真实的项目内容查看权限开放两个页签的预览下载，没有该权限时显示明确的禁用原因', async () => {
    vi.spyOn(api, 'listProductionDrawings').mockResolvedValue([])
    const wrapper = mount(ProductionWorkbench, { props: { token: 'token', username: 'admin', permissions: ['project.view', 'project.content.view', 'production.schedule'] } })
    await flushPromises()
    expect(wrapper.get('button[aria-label="预览图纸 D-001"]').attributes('disabled')).toBeUndefined()
    expect(wrapper.get('button[aria-label="下载图纸 D-001"]').attributes('disabled')).toBeUndefined()
    await wrapper.get('button[aria-label="预览图纸 D-001"]').trigger('click')
    await flushPromises()
    expect(wrapper.findComponent(ProductionLineDrawingDialog).props('action')).toBe('preview')
    wrapper.findComponent(ProductionLineDrawingDialog).vm.$emit('close')
    await flushPromises()
    await wrapper.findAll('.production-workbench__tabs button').find(button => button.text() === '自制追溯')!.trigger('click')
    await flushPromises()
    expect(wrapper.get('button[aria-label="预览图纸 D-002"]').attributes('disabled')).toBeUndefined()
    await wrapper.get('button[aria-label="下载图纸 D-002"]').trigger('click')
    await flushPromises()
    expect(wrapper.findComponent(ProductionLineDrawingDialog).props('action')).toBe('download')
    wrapper.findComponent(ProductionLineDrawingDialog).vm.$emit('close')
    await wrapper.setProps({ permissions: ['production.schedule'] })
    expect(wrapper.get('button[aria-label="预览图纸 D-002"]').attributes()).toMatchObject({ disabled: '', title: '需要查看项目内容权限' })
    expect(wrapper.findComponent(ProductionLineDrawingDialog).exists()).toBe(false)
    wrapper.unmount()
  })
  it('勾选跨页批次时排除冻结项，切换发布包清空批量选择', async () => {
    const first = records.one.state.lots[1]
    records.two.state.lots = [{ ...first, id: 'other-self' }]
    records.one.state.lots = Array.from({ length: 51 }, (_, index) => ({ ...first, id: `batch-${index}`, frozen: index === 0 }))
    records.one.state.movements = records.one.state.lots.map(lot => ({ id: `received-${lot.id}`, requestId: `received-${lot.id}`, lotId: lot.id, kind: 'ProductionReceived', quantity: lot.quantity, actor: 'admin', occurredAt: '2026-10-19T00:00:00Z' }))
    const wrapper = await openSchedule()
    await wrapper.get('input[aria-label="全选当前筛选自制批次（跨页）"]').setValue(true)
    expect(wrapper.get('[aria-label="排程批量操作"]').text()).toContain('已选 50 个批次')
    const next = wrapper.get('[aria-label="自制批次分页"]').findAll('button')[1]
    await next.trigger('click')
    expect(wrapper.get('input[aria-label="勾选排程批次 D-002 batch-50"]').element).toHaveProperty('checked', true)
    await wrapper.findAll('.production-workbench__package').find(button => button.text().includes('RP-two'))!.trigger('click')
    await flushPromises()
    expect(wrapper.get('[aria-label="排程批量操作"]').text()).toContain('已选 0 个批次')
    wrapper.unmount()
  })
  it('进入页签选择自制批次，跨物料选择不回到首批，全部改外协后清空旧表单', async () => {
    const wrapper = await openSchedule()
    expect(wrapper.get('[aria-label="自制批次明细"]').findAll('tbody tr')).toHaveLength(3)
    expect(wrapper.get('[aria-label="排程安排"] h3').text()).toBe('支架')
    await wrapper.get('input[aria-label="预计工时"]').setValue(9)
    await wrapper.get('button[aria-label="选择排程批次 D-003 self-c"]').trigger('click')
    await flushPromises()
    expect(wrapper.get('select[aria-label="选择资源组"]').element).toHaveProperty('value', '磨床组')
    expect(wrapper.get('input[aria-label="预计工时"]').element).toHaveProperty('value', '5')
    expect(records.one.state.lots[1].schedule).toEqual([])
    expect(wrapper.get('button[aria-label="选择排程批次 D-003 self-c"]').attributes('aria-pressed')).toBe('true')
    expect(wrapper.findAll('.production-workbench__package').some(button => button.text().includes('RP-two'))).toBe(false)
    records.one.state.lots = [records.one.state.lots[0]]
    await wrapper.findAll('button').find(button => button.text() === '刷新')!.trigger('click')
    await flushPromises()
    expect(wrapper.find('.production-workbench__schedule-form').exists()).toBe(false)
    expect(wrapper.get('[aria-label="自制批次明细"]').text()).toContain('请选择左侧发布包')
    wrapper.unmount()
  })

  it('右侧保存指定批次的资源、日期、工时和调整原因，刷新后回显已保存安排', async () => {
    const save = vi.spyOn(api, 'scheduleProductionLot').mockImplementation(async (id, input) => {
      const updated = JSON.parse(JSON.stringify(records[id])) as ProductionReleaseRecord
      const lot = updated.state.lots.find(item => item.id === input.lotId)!
      lot.schedule = input.slots.map(slot => ({ ...slot }))
      lot.promisedOn = input.promisedOn
      records[id] = updated
      return updated
    })
    const wrapper = await openSchedule()
    await wrapper.get('button[aria-label="选择排程批次 D-003 self-c"]').trigger('click')
    await flushPromises()
    await wrapper.get('input[aria-label="预计工时"]').setValue(6)
    await wrapper.get('input[placeholder="填写排程调整原因"]').setValue('调整加工资源')
    await wrapper.get('.production-workbench__schedule-form').trigger('submit')
    await flushPromises()
    expect(save).toHaveBeenCalledWith('one', expect.objectContaining({ lotId: 'self-c', promisedOn: '2026-11-03', changeReason: '调整加工资源',
      slots: [{ resourceGroupId: '磨床组', date: '2026-11-02', hours: 6 }] }), 'token')
    expect(wrapper.get('input[aria-label="预计工时"]').element).toHaveProperty('value', '6')
    expect(wrapper.get('input[placeholder="填写排程调整原因"]').element).toHaveProperty('value', '')
    expect(wrapper.get('button[aria-label="选择排程批次 D-003 self-c"]').attributes('aria-pressed')).toBe('true')
    wrapper.unmount()
  })

  it('冻结批次和无排程权限的账号均不能提交排程', async () => {
    const save = vi.spyOn(api, 'scheduleProductionLot')
    const wrapper = await openSchedule([])
    expect(wrapper.get('.production-workbench__schedule-form fieldset').attributes()).toHaveProperty('disabled')
    await wrapper.get('.production-workbench__schedule-form').trigger('submit')
    expect(save).not.toHaveBeenCalled()
    records.one.state.lots[1].frozen = true
    records.one.state.lots[1].freezeReason = '图纸升版'
    await wrapper.setProps({ permissions: ['production.schedule'] })
    await wrapper.findAll('button').find(button => button.text() === '刷新')!.trigger('click')
    await flushPromises()
    expect(wrapper.get('[aria-label="排程安排"]').text()).toContain('图纸升版')
    expect(wrapper.get('.production-workbench__schedule-form fieldset').attributes()).toHaveProperty('disabled')
    await wrapper.get('.production-workbench__schedule-form').trigger('submit')
    expect(save).not.toHaveBeenCalled()
    wrapper.unmount()
  })
})

describe('外协与自制的未完工筛选及全部分类', () => {
  it.each(['外协追溯', '自制追溯'])('%s默认只显示未交付批次，可查看完工数据并跨项目汇总', async tabName => {
    const first = records.one.state
    first.lines[0].requiredQuantity = 12
    first.lots[0].supplier = '甲'
    first.lots.push({ ...first.lots[0], id: 'external-open', quantity: 2 })
    first.movements.push(...[
      { lotId: 'external', kind: 'Issue', quantity: 10 },
      { lotId: 'self-a', kind: 'Issue', quantity: 2 },
      { lotId: 'self-b', kind: 'Issue', quantity: 2 },
      { lotId: 'self-b', kind: 'Cancelled', quantity: 1 },
    ].map((item, index) => ({ ...item, id: `closed-${index}`, requestId: `closed-${index}`, actor: 'admin', occurredAt: '' })))
    records.two = structuredClone(records.one)
    const second = records.two.state
    second.releasePackageId = 'two'; second.packageNumber = 'RP-two'; second.projectId = 'other-project'; second.projectCode = 'P002'; second.projectName = '第二项目'
    second.lots.filter(lot => lot.route === 'External').forEach(lot => { lot.supplier = '乙' })
    second.lots.find(lot => lot.id === 'self-c')!.schedule[0].resourceGroupId = '异地资源组'
    const projects = await api.listProductionTrackingProjects('token')
    vi.mocked(api.listProductionTrackingProjects).mockResolvedValue([
      { ...projects[0], packages: [projects[0].packages[0]] },
      { ...projects[0], id: 'other-project', code: 'P002', name: '第二项目', packages: [{ ...projects[0].packages[1], projectId: 'other-project', projectCode: 'P002', projectName: '第二项目' }] },
    ])
    const wrapper = mount(ProductionWorkbench, { props: { token: 'token', username: 'admin', permissions: ['project.content.view', 'production.schedule'] } })
    await flushPromises()
    await wrapper.findAll('.production-workbench__tabs button').find(button => button.text() === tabName)!.trigger('click'); await flushPromises()
    const middle = wrapper.get(tabName === '外协追溯' ? '[aria-label="外协物料明细"]' : '[aria-label="自制批次明细"]')
    const open = middle.get<HTMLInputElement>('.production-workbench__quick-filter input')
    expect(middle.findAll('.production-workbench__quick-filter')).toHaveLength(1)
    expect(open.element.checked).toBe(true)
    expect(middle.findAll('tbody tr')).toHaveLength(1)
    if (tabName === '外协追溯') expect(middle.findAll('tbody tr')[0].findAll('td')[7].text()).toBe('0 / 2')
    else expect(middle.findAll('tbody tr')[0].find('[aria-label="选择排程批次 D-003 self-c"]').exists()).toBe(true)
    await open.setValue(false); await flushPromises()
    if (tabName === '外协追溯') expect(middle.findAll('tbody tr')[0].findAll('td')[7].text()).toBe('0 / 12')
    else expect(middle.findAll('tbody tr')).toHaveLength(3)
    await middle.findAll('button').find(button => button.text() === '重置筛选')!.trigger('click'); await flushPromises()
    expect(open.element.checked).toBe(true)
    await wrapper.get('.production-workbench__all-categories').trigger('click'); await flushPromises()
    expect(wrapper.get('.production-workbench__all-categories').attributes('aria-pressed')).toBe('true')
    expect(middle.get('header').text()).toContain('2 个发布包')
    expect(middle.findAll('tbody tr')).toHaveLength(2)
    expect(middle.text()).toContain('P002')
    const label = tabName === '外协追溯' ? '筛选供应商' : '筛选资源组'
    await middle.get(`[aria-label="${label}"]`).setValue(tabName === '外协追溯' ? '乙' : '异地资源组'); await flushPromises()
    expect(middle.findAll('tbody tr')).toHaveLength(1); expect(middle.findAll('tbody tr')[0].text()).toContain('P002')
    await middle.findAll('button').find(button => button.text() === '重置筛选')!.trigger('click'); await flushPromises()
    const target = middle.findAll('tbody tr').find(row => row.text().includes('RP-two'))!
    await target.findAll('button').find(button => button.attributes('aria-label')?.startsWith('预览图纸'))!.trigger('click'); await flushPromises()
    expect(wrapper.findComponent(ProductionLineDrawingDialog).props('state').releasePackageId).toBe('two')
    wrapper.findComponent(ProductionLineDrawingDialog).vm.$emit('close'); await flushPromises()
    await target.get('.production-workbench__open-package').trigger('click'); await flushPromises()
    expect(wrapper.get('.production-workbench__all-categories').attributes('aria-pressed')).toBe('false')
    expect(middle.get('header').text()).toContain('RP-two')
    if (tabName === '自制追溯') expect(wrapper.get('[aria-label="选择排程批次 D-003 self-c"]').attributes('aria-pressed')).toBe('true')
    wrapper.unmount()
  })
  it('三页的未完工勾选状态独立，外协和自制不显示仅未分配', async () => {
    const wrapper = mount(ProductionWorkbench, { props: { token: 'token', username: 'reader', permissions: [] } }); await flushPromises()
    await wrapper.findAll('.production-workbench__quick-filter input')[1].setValue(false)
    for (const name of ['外协追溯', '自制追溯']) {
      await wrapper.findAll('.production-workbench__tabs button').find(button => button.text() === name)!.trigger('click'); await flushPromises()
      expect(wrapper.findAll('.production-workbench__quick-filter')).toHaveLength(1)
      expect(wrapper.get<HTMLInputElement>('.production-workbench__quick-filter input').element.checked).toBe(true)
    }
    await wrapper.findAll('.production-workbench__tabs button').find(button => button.text() === '发布跟踪')!.trigger('click'); await flushPromises()
    expect((wrapper.findAll('.production-workbench__quick-filter input')[1].element as HTMLInputElement).checked).toBe(false)
    wrapper.unmount()
  })
})
