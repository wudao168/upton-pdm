import { flushPromises, mount } from '@vue/test-utils'
import ElementPlus from 'element-plus'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import ProcurementTracking from '../src/components/ProcurementTracking.vue'

const api = vi.hoisted(() => ({
  getProjectProcurementTracking: vi.fn(),
  refreshProjectProcurementTracking: vi.fn(),
  listMaterialInventory: vi.fn(),
}))

vi.mock('../src/api', () => api)

describe('ProcurementTracking', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    window.localStorage.clear()
    api.listMaterialInventory.mockReset().mockResolvedValue({ items: [], total: 0, lastSuccessfulRefreshAt: '2026-09-08T01:00:00Z' })
    api.getProjectProcurementTracking.mockResolvedValue({
      projectId: 'project-1', projectCode: 'P701911', subprojectCode: 'P701911-1', hasPublishedBom: true,
      lastSuccessfulRefreshAt: '2026-09-07T01:02:03Z', lastRefreshError: null,
      items: [{
        sequence: 1, projectCode: 'P701911', subprojectCode: 'P701911-1', materialCode: '01020000089',
        materialName: '接头', specification: 'HG-10-16MM', remark: '标准件', brand: 'UPTON', quantity: 4,
        bomKind: '标准件', releasePackageNumber: 'REL-001', purchaseRequisitionNumbers: ['PR2601130042'],
        purchaseRequisitionStatus: '已核准', purchaseRequisitionDeliveryDate: '2026-01-20T00:00:00Z',
        purchaseOrderNumbers: ['PO2601130099'], purchaseOrderStatus: '执行中', purchaseQuantity: 4,
        arrivedQuantity: 2, purchaseRemark: '分批到货', purchaseDeliveryDate: '2026-01-25T00:00:00Z',
        latestDeliveryDate: '2026-01-28T00:00:00Z', requestedQuantity: 4, approvedQuantity: 4,
        details: [{ kind: '采购', documentNumber: 'PO2601130099', lineNumber: 10, lineStatus: '执行中',
          rawLineStatus: 3, isCanceled: false, quantity: 4, arrivedQuantity: 2, remark: '分批到货',
          deliveryDate: '2026-01-25T00:00:00Z', latestDeliveryDate: '2026-01-28T00:00:00Z', matchKind: '源请购行' }],
      }],
    })
    api.refreshProjectProcurementTracking.mockResolvedValue({ message: '只读刷新已启动' })
  })

  it('默认每页50条，翻页不重编序号，切换条数或项目回首页且刷新后限制页码', async () => {
    const result = await api.getProjectProcurementTracking()
    const first = result.items[0]
    const items = Array.from({ length: 121 }, (_, i) => ({ ...first, sequence: i + 1, materialCode: `M-${i + 1}` }))
    api.getProjectProcurementTracking.mockResolvedValue({ ...result, items })
    const wrapper = mount(ProcurementTracking, { props: { projectId: 'project-1', token: 'token', username: 'engineer' }, global: { plugins: [ElementPlus] } })
    await flushPromises()
    const rows = () => wrapper.findAll('.el-table__body tbody tr')
    expect(rows()).toHaveLength(50)
    expect(wrapper.get('[aria-label="备料明细分页"]').text()).toContain('共 121 条')
    expect(wrapper.get('[aria-label="备料明细上一页"]').attributes('disabled')).toBeDefined()
    await wrapper.get('[aria-label="备料明细下一页"]').trigger('click')
    expect(rows()).toHaveLength(50)
    expect(rows()[0].find('td').text()).toBe('51')
    await wrapper.get('[aria-label="备料明细下一页"]').trigger('click')
    expect(rows()).toHaveLength(21)
    expect(rows()[0].find('td').text()).toBe('101')
    expect(wrapper.get('[aria-label="备料明细下一页"]').attributes('disabled')).toBeDefined()
    await wrapper.get('[aria-label="备料明细每页条数"]').setValue('100')
    expect(rows()).toHaveLength(100)
    expect(rows()[0].find('td').text()).toBe('1')
    await wrapper.get('[aria-label="备料明细下一页"]').trigger('click')
    await wrapper.setProps({ projectId: 'project-2' })
    await flushPromises()
    expect(rows()[0].find('td').text()).toBe('1')
    await wrapper.get('[aria-label="备料明细下一页"]').trigger('click')
    wrapper.findComponent({ name: 'ElTable' }).vm.$emit('sort-change', { prop: 'purchaseDeliveryDate', order: 'ascending' })
    await flushPromises()
    expect(wrapper.get('[aria-label="备料明细分页"]').text()).toContain('1 / 2')
    await wrapper.get('[aria-label="备料明细下一页"]').trigger('click')
    api.getProjectProcurementTracking.mockResolvedValue({ ...result, items: items.slice(0, 8) })
    await wrapper.findAll('button').find(button => button.text() === '立即刷新')!.trigger('click')
    await vi.waitFor(() => expect(rows()).toHaveLength(8), { timeout: 4000 })
    expect(wrapper.get('[aria-label="备料明细分页"]').text()).toContain('1 / 1')
    wrapper.unmount()
  }, 20000)

  it('按确认顺序显示默认列和发布后的PR、PO状态', async () => {
    const wrapper = mount(ProcurementTracking, {
      props: { projectId: 'project-1', token: 'token', username: 'engineer' },
      global: { plugins: [ElementPlus] },
    })
    await flushPromises()

    const headers = wrapper.find('.el-table__header-wrapper').findAll('th .cell').map(item => item.text().trim()).filter(Boolean)
    expect(headers).toEqual([
      '序号', '项目号', '子项目号', '物料编码', '物料名称', '型号', '备注', '品牌', '数量', '库存',
      '请购状态', '请购交期', 'PO编号', '采购状态', '购买数',
      '到货数', '采购备注', '预计交期', '最新交期',
    ])
    expect(wrapper.text()).toContain('01020000089')
    expect(wrapper.text()).not.toContain('PR2601130042')
    expect(wrapper.text()).toContain('PO2601130099')
    expect(wrapper.text()).toContain('最近成功刷新')
    expect(wrapper.get('.procurement-tracking__heading').element.firstElementChild?.classList.contains('procurement-tracking__actions')).toBe(true)
    expect(wrapper.findAll('.procurement-tracking__actions button').map(button => button.text())).toEqual(['列设置', '立即刷新', '库存设置'])
    expect(wrapper.get('.procurement-tracking__heading').element.lastElementChild?.classList.contains('procurement-tracking__updated')).toBe(true)
    expect(wrapper.find('.procurement-tracking__heading h2').exists()).toBe(false)
    expect(wrapper.find('.procurement-tracking__heading p').exists()).toBe(false)
    expect(wrapper.find('.el-table__expand-icon').exists()).toBe(false)
    expect(wrapper.find('.procurement-tracking__details').exists()).toBe(false)
    expect(wrapper.text()).not.toContain('价格')
    expect(wrapper.text()).not.toContain('税额')
  })

  it('同料号多个PR批次保留分行，延续行不重复显示总量，逐明细延期仍报警', async () => {
    const result = await api.getProjectProcurementTracking()
    const first = result.items[0]
    result.items = [first, { ...first, sequence: 2, quantity: null, requestedQuantity: null, approvedQuantity: null,
      purchaseOrderNumbers: ['PO2'], hasDeliveryDelay: true,
      purchaseDeliveryDate: '2026-09-10', purchaseRequisitionDeliveryDate: '2026-09-20' }]
    api.getProjectProcurementTracking.mockResolvedValue(result)
    const wrapper = mount(ProcurementTracking, {
      props: { projectId: 'project-1', token: 'token', username: 'engineer' }, global: { plugins: [ElementPlus] },
    })
    await flushPromises()
    const rows = wrapper.findAll('.el-table__body tbody tr')
    expect(rows).toHaveLength(2)
    expect(rows[0].findAll('td')[8].text()).toBe('4')
    expect(rows[1].findAll('td')[8].text()).toBe('—')
    expect(rows[1].findAll('td')[12].text()).toBe('PO2')
    expect(rows[1].findAll('td')[17].classes()).toContain('is-delivery-delay')
    wrapper.unmount()
  })

  it('五个标识列保留全文及溢出提示并按比例自适应列宽', async () => {
    const result = await api.getProjectProcurementTracking()
    Object.assign(result.items[0], {
      projectCode: 'P700005', subprojectCode: 'P700005-3', materialCode: '01020000087',
      materialName: '内六角圆柱头螺钉 M4x18（加长完整名称验证）',
      specification: 'SDBT-MSB-1L-PU-K-0.3-M8-超长型号完整显示验证',
    })
    api.getProjectProcurementTracking.mockResolvedValue(result)
    const wrapper = mount(ProcurementTracking, {
      props: { projectId: 'project-1', token: 'token', username: 'engineer' }, global: { plugins: [ElementPlus] },
    })
    await flushPromises()
    const cells = wrapper.findAll('.el-table__body tbody tr:first-child td .cell').slice(1, 6)
    expect(cells.map(cell => cell.text())).toEqual([
      result.items[0].projectCode, result.items[0].subprojectCode, result.items[0].materialCode,
      result.items[0].materialName, result.items[0].specification,
    ])
    expect(cells.every(cell => cell.classes().includes('el-tooltip'))).toBe(true)
    expect(cells.every(cell => cell.get('span').attributes('title') === cell.text())).toBe(true)
    const columns = wrapper.findAll('.el-table__body colgroup col')
    expect(columns.slice(1, 4).map(column => Number(column.attributes('width')))).toEqual([85, 85, 100])
    expect(Number(columns[4].attributes('width'))).toBe(24)
    expect(Number(columns[5].attributes('width'))).toBe(26)
    wrapper.unmount()
  })

  it.each(['purchaseRequisitionDeliveryDate', 'purchaseDeliveryDate', 'latestDeliveryDate'] as const)('%s按日期排序，空日期始终置后，取消恢复原顺序', async prop => {
    const result = await api.getProjectProcurementTracking()
    const first = result.items[0]
    result.items = [
      { ...first, sequence: 1, [prop]: '2026-10-01' },
      { ...first, sequence: 2, [prop]: null },
      { ...first, sequence: 3, [prop]: '2026-02-01' },
    ]
    api.getProjectProcurementTracking.mockResolvedValue(result)
    const wrapper = mount(ProcurementTracking, {
      props: { projectId: 'project-1', token: 'token', username: 'engineer' }, global: { plugins: [ElementPlus] },
    })
    await flushPromises()
    expect(wrapper.findAll('th.is-sortable')).toHaveLength(3)
    const table = wrapper.findComponent({ name: 'ElTable' })
    const sequences = () => wrapper.findAll('.el-table__body tbody tr').map(row => row.find('td').text())
    for (const [order, expected] of [['ascending', ['3', '1', '2']], ['descending', ['1', '3', '2']], [null, ['1', '2', '3']]] as const) {
      table.vm.$emit('sort-change', { prop, order })
      await flushPromises()
      expect(sequences()).toEqual(expected)
    }
    expect(result.items.map((item: { sequence: number }) => item.sequence)).toEqual([1, 2, 3])
    wrapper.unmount()
  })

  it('PR默认隐藏，手动开启后保存偏好，恢复默认后重新隐藏', async () => {
    const options = {
      props: { projectId: 'project-1', token: 'token', username: 'engineer' },
      global: { plugins: [ElementPlus], stubs: { teleport: true } },
    }
    let wrapper = mount(ProcurementTracking, options)
    await flushPromises()
    await wrapper.findAll('button').find(button => button.text().trim() === '列设置')!.trigger('click')
    await flushPromises()
    const prCheckbox = wrapper.findAll('.el-checkbox').find(item => item.text() === 'PR编号')!.find('input')
    expect((prCheckbox.element as HTMLInputElement).checked).toBe(false)
    await prCheckbox.setValue(true)
    await wrapper.findAll('button').find(button => button.text().trim() === '保存')!.trigger('click')
    await flushPromises()
    expect(wrapper.find('.el-table__header-wrapper').text()).toContain('PR编号')
    expect(JSON.parse(window.localStorage.getItem('upton-pdm:procurement-columns:engineer')!).visible).toContain('purchaseRequisitionNumbers')
    wrapper.unmount()
    wrapper = mount(ProcurementTracking, options)
    await flushPromises()
    expect(wrapper.find('.el-table__header-wrapper').text()).toContain('PR编号')
    await wrapper.findAll('button').find(button => button.text().trim() === '列设置')!.trigger('click')
    await flushPromises()
    await wrapper.findAll('button').find(button => button.text().trim() === '恢复默认')!.trigger('click')
    await flushPromises()
    expect(wrapper.find('.el-table__header-wrapper').text()).not.toContain('PR编号')
    wrapper.unmount()
  })

  it('立即刷新只调用只读采购刷新接口', async () => {
    const wrapper = mount(ProcurementTracking, {
      props: { projectId: 'project-1', token: 'token', username: 'engineer' },
      global: { plugins: [ElementPlus] },
    })
    await flushPromises()
    await wrapper.findAll('button').find(button => button.text().trim() === '立即刷新')!.trigger('click')
    await flushPromises()

    expect(api.refreshProjectProcurementTracking).toHaveBeenCalledWith('project-1', 'token')
  })

  it.each([false, true])('空数据只显示一条提示（已发布BOM：%s）', async hasPublishedBom => {
    api.getProjectProcurementTracking.mockResolvedValue({ hasPublishedBom, items: [] })
    const wrapper = mount(ProcurementTracking, {
      props: { projectId: 'project-1', token: 'token', username: 'engineer' },
      global: { plugins: [ElementPlus] },
    })
    await flushPromises()
    expect(wrapper.findAll('.procurement-tracking__empty-tip')).toHaveLength(1)
    expect(wrapper.find('.el-table__empty-block').text()).toBe(hasPublishedBom
      ? '暂无采购跟踪数据'
      : '当前项目尚无已发布的标准件或电气件BOM，发布后将自动进入采购跟踪。')
    wrapper.unmount()
  })

  it.each([
    ['2026-09-08T00:00:00', '2026-09-07T00:00:00', true],
    ['2026-09-07T23:00:00', '2026-09-07T01:00:00', false],
    ['2026-09-06', '2026-09-07', false],
    [null, '2026-09-07', false],
    ['2026-09-08', null, false],
    ['invalid', '2026-09-07', false],
    ['2026-09-08', 'invalid', false],
  ])('仅预计交期晚于请购日期时标记该单元格（%s / %s）', async (expected, requested, delayed) => {
    const result = await api.getProjectProcurementTracking()
    result.items[0].purchaseDeliveryDate = expected
    result.items[0].purchaseRequisitionDeliveryDate = requested
    api.getProjectProcurementTracking.mockResolvedValue(result)
    const wrapper = mount(ProcurementTracking, {
      props: { projectId: 'project-1', token: 'token', username: 'engineer' },
      global: { plugins: [ElementPlus] },
    })
    await flushPromises()
    const cells = wrapper.findAll('.el-table__body tbody tr:first-child td')
    expect(cells[17].classes().includes('is-delivery-delay')).toBe(delayed)
    expect(wrapper.findAll('td.is-delivery-delay')).toHaveLength(delayed ? 1 : 0)
    wrapper.unmount()
  })
  it('库存逐页合计含负库存，同料号不同采购批次只查询一次', async () => {
    const result = await api.getProjectProcurementTracking()
    result.items.push({ ...result.items[0], sequence: 2, quantity: null })
    api.listMaterialInventory.mockImplementation(async ({ page }) => ({
      items: [{ materialCode: '01020000089', warehouseName: '2号常备仓', stockQuantity: page === 1 ? 8.5 : -2 }], total: 2,
      lastSuccessfulRefreshAt: '2026-09-08T01:00:00Z',
    }))
    const wrapper = mount(ProcurementTracking, { props: { projectId: 'project-1', token: 'token', username: 'engineer' }, global: { plugins: [ElementPlus] } })
    await flushPromises()
    expect(wrapper.findAll('.el-table__body tbody tr').map(row => row.findAll('td')[9].text())).toEqual(['6.5', '6.5'])
    expect(api.listMaterialInventory).toHaveBeenCalledTimes(2)
    expect(api.listMaterialInventory).toHaveBeenLastCalledWith({ materialCode: '01020000089', positiveStockOnly: false, page: 2, pageSize: 200 }, 'token')
    expect(wrapper.findAll('.el-table__body tbody tr')[0].findAll('td')[9].get('span').attributes('title')).toContain('不可重复累加')
    wrapper.unmount()
  })

  it.each(['zero', 'missing', 'failure', 'partial'])('库存状态%s不把未知或不完整结果显示为零', async state => {
    if (state === 'missing') api.listMaterialInventory.mockResolvedValue({ items: [], total: 0 })
    if (state === 'failure') api.listMaterialInventory.mockRejectedValue(new Error('无权查询'))
    if (state === 'partial') api.listMaterialInventory.mockResolvedValue({ items: [], total: 1 })
    const wrapper = mount(ProcurementTracking, { props: { projectId: 'project-1', token: 'token', username: 'engineer' }, global: { plugins: [ElementPlus] } })
    await flushPromises()
    expect(wrapper.findAll('.el-table__body tbody tr')[0].findAll('td')[9].text()).toBe(state === 'zero' ? '0' : '—')
    wrapper.unmount()
  })

  it('旧列设置自动在数量后加入库存，用户隐藏后不再自动开启', async () => {
    const key = 'upton-pdm:procurement-columns:engineer'
    window.localStorage.setItem(key, JSON.stringify({ order: ['materialCode', 'quantity', 'brand'], visible: ['materialCode', 'quantity', 'brand'] }))
    const options = { props: { projectId: 'project-1', token: 'token', username: 'engineer' }, global: { plugins: [ElementPlus] } }
    let wrapper = mount(ProcurementTracking, options)
    await flushPromises()
    const headers = () => wrapper.findAll('th .cell').map(cell => cell.text().trim())
    expect(headers()).toEqual(['物料编码', '数量', '库存', '品牌'])
    wrapper.unmount()
    window.localStorage.setItem(key, JSON.stringify({ order: ['materialCode', 'quantity', 'inventoryQuantity', 'brand'], visible: ['materialCode', 'quantity', 'brand'] }))
    wrapper = mount(ProcurementTracking, options)
    await flushPromises()
    expect(headers()).toEqual(['物料编码', '数量', '品牌'])
    wrapper.unmount()
  })

  it('切换项目后忽略上一项目未完成的库存响应', async () => {
    let finish!: (value: unknown) => void
    api.listMaterialInventory.mockImplementationOnce(() => new Promise(resolve => { finish = resolve }))
    const wrapper = mount(ProcurementTracking, { props: { projectId: 'project-1', token: 'token', username: 'engineer' }, global: { plugins: [ElementPlus] } })
    await flushPromises()
    await wrapper.setProps({ projectId: 'project-2' })
    await flushPromises()
    finish({ items: [{ materialCode: '01020000089', stockQuantity: 999 }], total: 1, lastSuccessfulRefreshAt: '2026-09-08T01:00:00Z' })
    await flushPromises()
    expect(wrapper.findAll('.el-table__body tbody tr')[0].findAll('td')[9].text()).toBe('0')
    wrapper.unmount()
  })

  it('库存设置默认全选，取消不影响库存，保存勾选后更新并按账号记忆', async () => {
    api.listMaterialInventory.mockResolvedValue({ items: [
      { materialCode: '01020000089', warehouseName: '2号常备仓', stockQuantity: 10 },
      { materialCode: '01020000089', warehouseName: '昆山应急仓', stockQuantity: 3 },
    ], total: 2, warehouseNames: ['2号项目仓', '零成本呆滞仓', '2号常备仓', '项目退料仓', '昆山应急仓', '机加工仓库'], lastSuccessfulRefreshAt: '2026-09-08T01:00:00Z' })
    const options = { props: { projectId: 'project-1', token: 'token', username: 'engineer' }, global: { plugins: [ElementPlus], stubs: { teleport: true } } }
    let wrapper = mount(ProcurementTracking, options)
    const stock = () => wrapper.findAll('.el-table__body tbody tr')[0].findAll('td')[9].text()
    const open = async () => { await wrapper.findAll('button').find(b => b.text() === '库存设置')!.trigger('click'); await flushPromises() }
    const dialog = () => wrapper.findAll('.el-dialog').find(d => d.text().includes('库存汇总设置'))!
    await flushPromises()
    expect(stock()).toBe('13')
    await open()
    expect(dialog().findAll('input:checked')).toHaveLength(5)
    expect(dialog().findAll('.el-checkbox')).toHaveLength(6)
    await dialog().findAll('.el-checkbox').find(c => c.text() === '2号常备仓')!.get('input').setValue(false)
    await dialog().findAll('button').find(b => b.text() === '取消')!.trigger('click')
    expect(stock()).toBe('13')
    await open()
    await dialog().findAll('.el-checkbox').find(c => c.text() === '2号常备仓')!.get('input').setValue(false)
    await dialog().findAll('button').find(b => b.text() === '保存')!.trigger('click')
    await flushPromises()
    expect(stock()).toBe('3')
    expect(api.listMaterialInventory).toHaveBeenCalledTimes(3)
    wrapper.unmount()
    wrapper = mount(ProcurementTracking, options)
    await flushPromises()
    expect(stock()).toBe('3')
    await wrapper.setProps({ username: 'other', token: 'other-token' })
    await flushPromises()
    expect(stock()).toBe('13')
    wrapper.unmount()
  })

  it('每次打开设置动态获取全部仓库，新出现的非默认仓库也可选择', async () => {
    api.listMaterialInventory.mockResolvedValue({ items: [], total: 0, warehouseNames: ['2号常备仓'], lastSuccessfulRefreshAt: '2026-09-08T01:00:00Z' })
    const wrapper = mount(ProcurementTracking, { props: { projectId: 'project-1', token: 'token', username: 'engineer' }, global: { plugins: [ElementPlus], stubs: { teleport: true } } })
    await flushPromises()
    const open = async () => { await wrapper.findAll('button').find(b => b.text() === '库存设置')!.trigger('click'); await flushPromises() }
    await open()
    expect(wrapper.get('[aria-label="库存汇总仓库"]').text()).toBe('2号常备仓')
    await wrapper.findAll('.el-dialog').find(d => d.text().includes('库存汇总设置'))!.findAll('button').find(b => b.text() === '取消')!.trigger('click')
    api.listMaterialInventory.mockResolvedValue({ items: [], total: 0, warehouseNames: ['2号常备仓', '新成品仓'] })
    await open()
    expect(wrapper.get('[aria-label="库存汇总仓库"]').text()).toContain('新成品仓')
    const newWarehouse = wrapper.get('[aria-label="库存汇总仓库"]').findAll('.el-checkbox').find(c => c.text() === '新成品仓')!
    expect((newWarehouse.get('input').element as HTMLInputElement).checked).toBe(false)
    await newWarehouse.get('input').setValue(true)
    expect((newWarehouse.get('input').element as HTMLInputElement).checked).toBe(true)
    wrapper.unmount()
  })
})
