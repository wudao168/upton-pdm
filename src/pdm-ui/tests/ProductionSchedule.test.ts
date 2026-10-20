import { flushPromises, mount } from '@vue/test-utils'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import ProductionWorkbench from '../src/components/ProductionWorkbench.vue'
import ProductionLineDrawingDialog from '../src/components/ProductionLineDrawingDialog.vue'
import * as api from '../src/api'
import { selfProductionState, type ProductionReleaseRecord } from '../src/productionTracking'

vi.mock('../src/statusMessage', () => ({ ElMessage: { success: vi.fn(), warning: vi.fn(), error: vi.fn() } }))

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
beforeEach(() => {
  records = { one: release(), two: release('two') }
  records.two.state.lots = [records.two.state.lots[0]]
  vi.spyOn(api, 'listProductionTrackingProjects').mockImplementation(async (_token, selfOnly) => {
    const states = Object.values(records).map(({ state }) => selfOnly ? selfProductionState(state) : state).filter(state => !!state)
    return states.length ? [{ id: 'project', code: 'P001', name: '排程测试', packages: states.map(state => ({
    releasePackageId: state.releasePackageId, projectId: 'project', projectCode: 'P001', projectName: '排程测试', packageNumber: state.packageNumber,
    scope: state.scope, publishedAt: state.publishedAt, status: '待排产', totalLines: state.lines.length,
    totalQuantity: state.lines.reduce((sum, line) => sum + line.requiredQuantity, 0), qualifiedLines: 0, issuedLines: 0, urgentLines: 0,
    priorityLines: 0, atRiskLines: 0, unplannedLines: 1 })) }] : []
  })
  vi.spyOn(api, 'readProductionTrackingRelease').mockImplementation(async id => records[id])
  vi.spyOn(api, 'readProductionActions').mockResolvedValue({ actions: [], hint: '当前账号未配置可办理的扫码岗位。' })
  vi.spyOn(api, 'readProductionCapacity').mockResolvedValue({ calendar: { companyId: 'company', rowVersion: 1, days: [] }, usage: [] })
})
afterEach(() => vi.restoreAllMocks())

async function openSchedule(permissions = ['production.schedule']) {
  const wrapper = mount(ProductionWorkbench, { props: { token: 'token', username: 'admin', permissions } })
  await flushPromises()
  await wrapper.findAll('.production-workbench__tabs button').find(button => button.text() === '排程计划')!.trigger('click')
  await flushPromises()
  return wrapper
}

describe('三栏自制排程', () => {
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
  it('离开排程进入扫码执行后恢复完整授权发布包，仍可定位外协任务', async () => {
    vi.spyOn(api, 'resolveProductionScan').mockResolvedValue([{ releasePackageId: 'two', lineId: 'line-0', lotId: 'external',
      packageNumber: 'RP-two', drawingNumber: 'D-001', name: '外协板', revision: 'W1', status: '外协待交货' }])
    const wrapper = await openSchedule()
    await wrapper.findAll('.production-workbench__tabs button').find(button => button.text() === '生产执行')!.trigger('click')
    await flushPromises()
    expect(api.listProductionTrackingProjects).toHaveBeenLastCalledWith('token', false)
    await wrapper.get('[aria-label="扫描正式图纸或批次流转卡"]').setValue('扫码内容')
    await wrapper.get('.production-workbench__execution-actions form').trigger('submit')
    await flushPromises()
    expect(wrapper.get('.production-workbench__execution h3').text()).toContain('RP-two · 外协板')
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
    await wrapper.findAll('.production-workbench__tabs button').find(button => button.text() === '排程计划')!.trigger('click')
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
    await wrapper.findAll('.production-workbench__tabs button').find(button => button.text() === '排程计划')!.trigger('click')
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
    await wrapper.findAll('.production-workbench__tabs button').find(button => button.text() === '排程计划')!.trigger('click')
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
    await wrapper.findAll('.production-workbench__tabs button').find(button => button.text() === '排程计划')!.trigger('click')
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
