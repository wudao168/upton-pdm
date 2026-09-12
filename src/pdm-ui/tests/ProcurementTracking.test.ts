import { flushPromises, mount } from '@vue/test-utils'
import ElementPlus from 'element-plus'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import ProcurementTracking from '../src/components/ProcurementTracking.vue'
import { downloadProcurementWorkbook } from '../src/procurementWorkbook'

vi.mock('../src/procurementWorkbook', () => ({ downloadProcurementWorkbook: vi.fn() }))

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
        purchaseRequisitionCreatedAt: '2026-01-12T09:30:00+08:00', buyerName: '孟丹',
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

  it('导出文件优先用子项目号，日期按中国时区且流水刷新保留、次日重置', async () => {
    const date = vi.spyOn(Date.prototype, 'toLocaleDateString').mockReturnValue('2026-09-09')
    const options = { props: { projectId: 'project-1', token: 'token', username: 'engineer' }, global: { plugins: [ElementPlus] } }
    let wrapper = mount(ProcurementTracking, options)
    const exportFile = async () => {
      await wrapper.findAll('button').find(button => button.text() === '导出 Excel')!.trigger('click')
      await flushPromises()
      return vi.mocked(downloadProcurementWorkbook).mock.calls.at(-1)![0]
    }
    try {
      await flushPromises()
      expect(await exportFile()).toBe('P701911-1_物料状态清单_20260909_01.xlsx')
      expect(await exportFile()).toBe('P701911-1_物料状态清单_20260909_02.xlsx')
      wrapper.unmount()
      wrapper = mount(ProcurementTracking, options)
      await flushPromises()
      expect(await exportFile()).toBe('P701911-1_物料状态清单_20260909_03.xlsx')
      date.mockReturnValue('2026-09-10')
      expect(await exportFile()).toBe('P701911-1_物料状态清单_20260910_01.xlsx')
      expect(date).toHaveBeenCalledWith('sv-SE', { timeZone: 'Asia/Shanghai' })
      const result = await api.getProjectProcurementTracking()
      api.getProjectProcurementTracking.mockResolvedValue({ ...result, subprojectCode: null })
      await wrapper.setProps({ projectId: 'main-project' })
      await flushPromises()
      expect(await exportFile()).toBe('P701911_物料状态清单_20260910_01.xlsx')
    } finally { wrapper.unmount(); date.mockRestore() }
  })

  it('关键词支持料号、名称、型号，五项筛选组合且采购员可匹配合并姓名', async () => {
    const result = await api.getProjectProcurementTracking()
    const first = result.items[0]
    api.getProjectProcurementTracking.mockResolvedValue({ ...result, items: [
      { ...first, materialCode: '00123', materialName: '接头', specification: 'HG-10', buyerName: '孟丹；张永珊' },
      { ...first, sequence: 2, materialCode: '00456', materialName: '气缸', specification: 'ABC-20', brand: 'FESTO', purchaseRequisitionStatus: '开立', purchaseOrderStatus: '未采购', buyerName: '孙云启' },
    ] })
    const wrapper = mount(ProcurementTracking, { props: { projectId: 'project-1', token: 'token', username: 'engineer' }, global: { plugins: [ElementPlus] } })
    await flushPromises()
    const rows = () => wrapper.findAll('.el-table__body tbody tr')
    for (const keyword of [' 00123 ', '接头', 'hg-10']) {
      await wrapper.get('[aria-label="搜索料号、名称、型号"]').setValue(keyword)
      expect(rows()).toHaveLength(1)
      expect(rows()[0].text()).toContain('00123')
    }
    await wrapper.get('[aria-label="搜索料号、名称、型号"]').setValue('')
    const setMultiFilter = async (label: string, values: string[]) => {
      await wrapper.get(`[aria-label="筛选${label}"]`).trigger('click')
      await flushPromises()
      const options = Array.from(document.querySelectorAll<HTMLInputElement>(`[aria-label="${label}多选项"] input`))
      for (const option of options) if (values.includes(option.value) !== option.checked) option.click()
      await flushPromises()
      await wrapper.get(`[aria-label="筛选${label}"]`).trigger('click')
    }
    await wrapper.get('[aria-label="筛选品牌"]').setValue('UPTON')
    await setMultiFilter('请购状态', ['已核准'])
    await setMultiFilter('采购状态', ['执行中'])
    await setMultiFilter('采购员', ['张永珊'])
    expect(rows()).toHaveLength(1)
    expect(rows()[0].text()).toContain('00123')
    await wrapper.get('[aria-label="筛选品牌"]').setValue(' upt ')
    expect(rows()).toHaveLength(1)
    expect(rows()[0].text()).toContain('00123')
    await wrapper.get('[aria-label="筛选品牌"]').setValue('FESTO')
    expect(rows()).toHaveLength(0)
    expect(wrapper.text()).toContain('没有符合筛选条件的记录')
    expect(wrapper.findAll('button').find(button => button.text() === '导出 Excel')!.attributes('disabled')).toBeDefined()
    await wrapper.get('[aria-label="筛选品牌"]').setValue('')
    await setMultiFilter('请购状态', ['已核准', '开立'])
    await setMultiFilter('采购状态', ['执行中', '未采购'])
    await setMultiFilter('采购员', ['张永珊', '孙云启'])
    expect(rows()).toHaveLength(2)
    await wrapper.findAll('button').find(button => button.text() === '重置筛选')!.trigger('click')
    expect(rows()).toHaveLength(2)
    expect(wrapper.get('[aria-label="筛选请购状态"]').text()).toContain('全部请购状态')
    expect(wrapper.get('[aria-label="筛选采购状态"]').text()).toContain('全部采购状态')
    expect(wrapper.get('[aria-label="筛选采购员"]').text()).toContain('全部采购员')
    expect(api.refreshProjectProcurementTracking).not.toHaveBeenCalled()
    wrapper.unmount()
  })

  it('材料出库和杂发共用两列、独立两行，保留日期数量来源且导出不重复采购数量', async () => {
    const result = await api.getProjectProcurementTracking()
    const first = result.items[0]
    const movementRow = { ...first, quantity: null, requestedQuantity: null, approvedQuantity: null,
      isWarehouseMovementRow: true, purchaseRequisitionStatus: '', purchaseOrderStatus: '', purchaseOrderNumbers: [],
      purchaseQuantity: 0, arrivedQuantity: 0 }
    api.getProjectProcurementTracking.mockResolvedValue({ ...result, items: [first,
      { ...movementRow, sequence: 2, warehouseMovements: [{ kind: 'ISSUE', documentNumber: 'CRKD-1', lineNumber: 10, date: '2026-09-09T03:28:38Z', quantity: 2, unit: '个' }] },
      { ...movementRow, sequence: 3, warehouseMovements: [{ kind: 'MISC', documentNumber: 'Mis1', lineNumber: 10, date: '2026-09-07T16:00:00Z', quantity: 1, unit: '盒' }] },
      { ...movementRow, sequence: 4, warehouseMovements: [{ kind: 'RCV', documentNumber: 'RCV1', lineNumber: 10, date: '2026-09-08T01:30:00Z', quantity: 5, unit: '个' }] },
    ] })
    const wrapper = mount(ProcurementTracking, { props: { projectId: 'project-1', token: 'token', username: 'engineer' }, global: { plugins: [ElementPlus] } })
    await flushPromises()
    const headers = wrapper.find('.el-table__header-wrapper').findAll('th .cell').map(cell => cell.text())
    expect(headers.filter(label => label === '出库日期')).toHaveLength(1)
    expect(headers).not.toContain('杂发日期')
    const rows = wrapper.findAll('.el-table__body tbody tr')
    expect(rows).toHaveLength(4)
    const cell = (row: number, label: string) => rows[row].findAll('td')[headers.indexOf(label)]
    expect(cell(1, '出库日期').text()).toBe('2026/9/9')
    expect(cell(1, '出库数').text()).toBe('2')
    expect(cell(1, '出库日期').get('span').attributes('title')).toContain('材料出库单·确认日期：CRKD-1')
    expect(cell(2, '出库日期').text()).toBe('2026/9/8')
    expect(cell(2, '出库数').text()).toBe('1')
    expect(cell(2, '出库日期').get('span').attributes('title')).toContain('杂发单·日期：Mis1')
    expect(cell(3, '入库日期').text()).toBe('2026/9/8')
    expect(cell(3, '入库数').text()).toBe('5')
    expect(cell(3, '出库日期').text()).toBe('—')
    expect(cell(1, '购买数').text()).toBe('—')
    expect(cell(2, '到货数').text()).toBe('—')
    await wrapper.findAll('button').find(button => button.text() === '导出 Excel')!.trigger('click')
    await flushPromises()
    const [, exportHeaders, exportRows] = vi.mocked(downloadProcurementWorkbook).mock.calls[0]
    expect(exportRows).toHaveLength(4)
    expect(exportRows[1][exportHeaders.indexOf('出库日期')]).toBe('2026/9/9')
    expect(exportRows[3][exportHeaders.indexOf('入库日期')]).toBe('2026/9/8')
    expect(exportRows[3][exportHeaders.indexOf('入库数')]).toBe(5)
    expect(exportRows[1][exportHeaders.indexOf('出库数')]).toBe(2)
    expect(exportRows[2][exportHeaders.indexOf('出库数')]).toBe(1)
    expect(exportRows[2][exportHeaders.indexOf('购买数')]).toBe('—')
    wrapper.unmount()
  })

  it('超期筛选沿用后端逐单判定，缺失交期不判超期且不重复计算数量', async () => {
    const result = await api.getProjectProcurementTracking()
    const first = result.items[0]
    api.getProjectProcurementTracking.mockResolvedValue({ ...result, items: [
      { ...first, sequence: 1, hasDeliveryDelay: false },
      { ...first, sequence: 2, hasDeliveryDelay: true, purchaseDeliveryDate: '2026-01-01', quantity: null },
      { ...first, sequence: 3, hasDeliveryDelay: undefined },
      { ...first, sequence: 4, hasDeliveryDelay: undefined, purchaseRequisitionDeliveryDate: null },
      { ...first, sequence: 5, hasDeliveryDelay: undefined, purchaseDeliveryDate: null },
    ] })
    const wrapper = mount(ProcurementTracking, { props: { projectId: 'project-1', token: 'token', username: 'engineer' }, global: { plugins: [ElementPlus] } })
    await flushPromises()
    await wrapper.get('.procurement-tracking__delay-filter input').setValue(true)
    const rows = wrapper.findAll('.el-table__body tbody tr')
    expect(rows.map(row => row.find('td').text())).toEqual(['2', '3'])
    expect(rows[0].findAll('td')[8].text()).toBe('—')
    expect(wrapper.get('[aria-label="备料明细分页"]').text()).toContain('共 2 条（全部 5 条）')
    wrapper.unmount()
  })

  it('未入库、未出库按当前行是否存在对应仓库记录筛选，组合时取交集且可重置', async () => {
    const result = await api.getProjectProcurementTracking()
    const first = result.items[0]
    api.getProjectProcurementTracking.mockResolvedValue({ ...result, items: [
      { ...first, sequence: 1, warehouseMovements: [] },
      { ...first, sequence: 2, warehouseMovements: [{ kind: 'RCV', documentNumber: 'RCV1', lineNumber: 10, date: '2026-09-08T01:30:00Z', quantity: 4, unit: '个' }] },
      { ...first, sequence: 3, warehouseMovements: [{ kind: 'ISSUE', documentNumber: 'OUT1', lineNumber: 10, date: '2026-09-09T01:30:00Z', quantity: 4, unit: '个' }] },
      { ...first, sequence: 4, warehouseMovements: [
        { kind: 'RCV', documentNumber: 'RCV2', lineNumber: 10, date: '2026-09-08T01:30:00Z', quantity: 4, unit: '个' },
        { kind: 'MISC', documentNumber: 'MIS1', lineNumber: 10, date: '2026-09-09T01:30:00Z', quantity: 4, unit: '个' },
      ] },
    ] })
    const wrapper = mount(ProcurementTracking, { props: { projectId: 'project-1', token: 'token', username: 'engineer' }, global: { plugins: [ElementPlus] } })
    await flushPromises()
    const sequences = () => wrapper.findAll('.el-table__body tbody tr').map(row => row.find('td').text())
    await wrapper.get('[aria-label="未入库"]').setValue(true)
    expect(sequences()).toEqual(['1', '3'])
    await wrapper.get('[aria-label="未出库"]').setValue(true)
    expect(sequences()).toEqual(['1'])
    await wrapper.get('[aria-label="未入库"]').setValue(false)
    expect(sequences()).toEqual(['1', '2'])
    await wrapper.findAll('button').find(button => button.text() === '重置筛选')!.trigger('click')
    expect(sequences()).toEqual(['1', '2', '3', '4'])
    expect((wrapper.get('[aria-label="未出库"]').element as HTMLInputElement).checked).toBe(false)
    wrapper.unmount()
  })

  it('筛选回首页，导出全部匹配记录并保留排序，切换项目清空条件', async () => {
    const result = await api.getProjectProcurementTracking()
    const first = result.items[0]
    api.getProjectProcurementTracking.mockResolvedValue({ ...result, items: Array.from({ length: 121 }, (_, i) => ({
      ...first, sequence: i + 1, materialCode: 'M-' + i, brand: i < 61 ? 'UPTON' : 'FESTO',
      latestDeliveryDate: i === 60 ? '2026-01-01' : first.latestDeliveryDate,
    })) })
    const wrapper = mount(ProcurementTracking, { props: { projectId: 'project-1', token: 'token', username: 'engineer' }, global: { plugins: [ElementPlus] } })
    await flushPromises()
    await wrapper.get('[aria-label="备料明细下一页"]').trigger('click')
    await wrapper.get('[aria-label="筛选品牌"]').setValue('UPTON')
    expect(wrapper.get('[aria-label="备料明细分页"]').text()).toContain('1 / 2')
    wrapper.findComponent({ name: 'ElTable' }).vm.$emit('sort-change', { prop: 'latestDeliveryDate', order: 'ascending' })
    await flushPromises()
    await wrapper.findAll('button').find(button => button.text() === '导出 Excel')!.trigger('click')
    await flushPromises()
    const [, headers, rows] = vi.mocked(downloadProcurementWorkbook).mock.calls[0]
    expect(rows).toHaveLength(61)
    expect(rows[0][headers.indexOf('物料编码')]).toBe('M-60')
    expect(rows.every(row => row[headers.indexOf('品牌')] === 'UPTON')).toBe(true)
    await wrapper.setProps({ projectId: 'project-2' })
    await flushPromises()
    expect((wrapper.get('[aria-label="筛选品牌"]').element as HTMLInputElement).value).toBe('')
    expect(wrapper.get('[aria-label="备料明细分页"]').text()).toContain('共 121 条')
    wrapper.unmount()
  }, 10_000)

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
      '请购状态', '请购日期', '需求日期', 'PO编号', '采购状态', '采购员', '购买数',
      '到货数', '采购备注', '预计交期', '最新交期',
      '入库日期', '入库数', '出库日期', '出库数',
    ])
    expect(wrapper.text()).toContain('01020000089')
    expect(wrapper.text()).not.toContain('PR2601130042')
    expect(wrapper.text()).toContain('PO2601130099')
    expect(wrapper.text()).toContain('刷新：')
    expect(wrapper.text()).toContain('2026/1/12')
    expect(wrapper.text()).toContain('孟丹')
    expect(wrapper.get('.procurement-tracking__heading').element.firstElementChild?.classList.contains('procurement-tracking__filters')).toBe(true)
    expect(wrapper.findAll('.procurement-tracking__actions button').map(button => button.text())).toEqual(['列设置', '立即刷新', '库存设置', '导出 Excel'])
    expect(wrapper.get('.procurement-tracking__heading').element.lastElementChild?.classList.contains('procurement-tracking__updated')).toBe(true)
    expect(wrapper.get('[aria-label="采购跟踪筛选"]').element.nextElementSibling).toBe(wrapper.get('.procurement-tracking__actions').element)
    expect(wrapper.findAll('.procurement-tracking__delay-filter').map(label => label.text())).toEqual(['交期不符', '未入库', '未出库'])
    expect(wrapper.get('[aria-label="筛选品牌"]').attributes('list')).toBe('procurement-brands-project-1')
    expect(wrapper.get('datalist option').attributes('value')).toBe('UPTON')
    expect(wrapper.find('.procurement-tracking__heading h2').exists()).toBe(false)
    expect(wrapper.find('.procurement-tracking__heading p').exists()).toBe(false)
    expect(wrapper.find('.el-table__expand-icon').exists()).toBe(false)
    expect(wrapper.find('.procurement-tracking__details').exists()).toBe(false)
    expect(wrapper.text()).not.toContain('价格')
    expect(wrapper.text()).not.toContain('税额')
  })

  it('日期、数量、状态和标识列使用指定固定宽度，其他列保持自适应', async () => {
    const wrapper = mount(ProcurementTracking, { props: { projectId: 'project-1', token: 'token', username: 'engineer' }, global: { plugins: [ElementPlus] } })
    await flushPromises()
    const column = (prop: string) => wrapper.findAllComponents({ name: 'ElTableColumn' }).find(item => item.props('prop') === prop)!
    for (const prop of ['purchaseRequisitionCreatedAt', 'purchaseRequisitionDeliveryDate', 'purchaseDeliveryDate', 'latestDeliveryDate', 'receiptDate', 'issueDate']) expect(column(prop).props('width')).toBe(70)
    for (const prop of ['quantity', 'inventoryQuantity', 'purchaseQuantity', 'arrivedQuantity', 'receiptQuantity', 'issueQuantity']) expect(column(prop).props('width')).toBe(40)
    for (const prop of ['purchaseRequisitionStatus', 'purchaseOrderStatus']) expect(column(prop).props('width')).toBe(60)
    for (const prop of ['projectCode', 'subprojectCode']) expect(column(prop).props('width')).toBe(70)
    expect(column('sequence').props('width')).toBe(40)
    expect(column('materialCode').props('width')).toBe(85)
    expect(column('materialName').props('width')).toBe(120)
    expect(column('buyerName').props('width')).toBe(50)
    expect(column('buyerName').props('minWidth')).toBeFalsy()
    wrapper.unmount()
  })

  it('导出全部分页记录，遵循可见列顺序及排序，补齐库存且保留数值', async () => {
    const result = await api.getProjectProcurementTracking()
    result.items = Array.from({ length: 61 }, (_, i) => ({ ...result.items[0], sequence: i + 1, materialCode: `0102000${i.toString().padStart(4, '0')}`, latestDeliveryDate: i === 60 ? '2026-01-01' : '2026-02-01' }))
    api.getProjectProcurementTracking.mockResolvedValue(result)
    const keys = ['materialCode', 'inventoryQuantity', 'quantity', 'latestDeliveryDate', 'purchaseRequisitionCreatedAt', 'buyerName']
    localStorage.setItem('upton-pdm:procurement-columns:engineer', JSON.stringify({ order: keys, visible: keys }))
    const wrapper = mount(ProcurementTracking, { props: { projectId: 'project-1', token: 'token', username: 'engineer' }, global: { plugins: [ElementPlus] } })
    await flushPromises()
    expect(api.listMaterialInventory).toHaveBeenCalledTimes(50)
    wrapper.findComponent({ name: 'ElTable' }).vm.$emit('sort-change', { prop: 'latestDeliveryDate', order: 'ascending' })
    await flushPromises()
    await wrapper.findAll('button').find(button => button.text() === '导出 Excel')!.trigger('click')
    await flushPromises()
    expect(api.listMaterialInventory).toHaveBeenCalledTimes(61)
    expect(downloadProcurementWorkbook).toHaveBeenCalledTimes(1)
    const [filename, headers, rows] = vi.mocked(downloadProcurementWorkbook).mock.calls[0]
    expect(filename).toMatch(/^P701911-1_物料状态清单_\d{8}_01\.xlsx$/)
    expect(headers).toEqual(['物料编码', '库存', '数量', '最新交期', '入库日期', '入库数', '出库日期', '出库数', '请购日期', '采购员'])
    expect(rows).toHaveLength(61)
    expect(rows[0]).toEqual(['01020000060', 0, 4, '2026/1/1', '—', '—', '—', '—', '2026/1/12', '孟丹'])
    wrapper.unmount()
  })

  it('空记录禁用导出；库存失败不导出误导性空值', async () => {
    api.listMaterialInventory.mockRejectedValue(new Error('库存快照已变化'))
    const wrapper = mount(ProcurementTracking, { props: { projectId: 'project-1', token: 'token', username: 'engineer' }, global: { plugins: [ElementPlus] } })
    await flushPromises()
    const button = () => wrapper.findAll('button').find(button => button.text() === '导出 Excel')!
    await button().trigger('click')
    await flushPromises()
    expect(downloadProcurementWorkbook).not.toHaveBeenCalled()
    api.getProjectProcurementTracking.mockResolvedValue({ items: [], hasPublishedBom: false })
    await wrapper.setProps({ projectId: 'empty' })
    await flushPromises()
    expect(button().attributes('disabled')).toBeDefined()
    wrapper.unmount()
  })

  it('等待库存期间切换项目则取消旧项目导出', async () => {
    let resolveStock!: (value: unknown) => void
    api.listMaterialInventory.mockImplementation(() => new Promise(resolve => { resolveStock = resolve }))
    const wrapper = mount(ProcurementTracking, { props: { projectId: 'project-1', token: 'token', username: 'engineer' }, global: { plugins: [ElementPlus] } })
    await flushPromises()
    await wrapper.findAll('button').find(button => button.text() === '导出 Excel')!.trigger('click')
    api.getProjectProcurementTracking.mockResolvedValue({ items: [], hasPublishedBom: false })
    await wrapper.setProps({ projectId: 'empty' })
    resolveStock({ items: [], total: 0, lastSuccessfulRefreshAt: '2026-09-08T01:00:00Z' })
    await flushPromises()
    expect(downloadProcurementWorkbook).not.toHaveBeenCalled()
    wrapper.unmount()
  })

  it('同料号多个PR批次保留分行，延续行不重复显示总量，单元格按显示日期着色', async () => {
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
    expect(rows[1].findAll('td')[13].text()).toBe('PO2')
    expect(rows[1].findAll('td')[19].classes()).not.toContain('is-delivery-delay')
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
    expect(columns.slice(1, 4).map(column => Number(column.attributes('width')))).toEqual([70, 70, 85])
    expect(Number(columns[4].attributes('width'))).toBe(120)
    expect(Number(columns[5].attributes('width'))).toBe(104)
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
  ])('仅预计交期晚于需求日期时标记该单元格（%s / %s）', async (expected, requested, delayed) => {
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
    expect(cells[19].classes().includes('is-delivery-delay')).toBe(delayed)
    expect(wrapper.findAll('td.is-delivery-delay')).toHaveLength(delayed ? 1 : 0)
    wrapper.unmount()
  })
  it.each([
    ['2026-09-09', '2026-09-12', '2026-09-13', 0, true, true],
    ['2026-09-09', '2026-09-12', '2026-09-08', 0, true, false],
    ['2026-09-09', '2026-09-12', '2026-09-11', 0, true, false],
    ['2026-09-09', '2026-09-12', '2026-09-12', 0, true, false],
    ['2026-09-09', '2026-09-07', '2026-09-08', 0, false, false],
    ['2026-09-09', '2026-09-12', '2026-09-13', 2, true, true],
    ['2026-09-09', '2026-09-12', '2026-09-13', 4, false, false],
    ['2026-09-09', '2026-09-12', '2026-09-13', 5, false, false],
    ['2026-09-09', null, '2026-09-13', 0, false, false],
    [null, '2026-09-12', '2026-09-13', 0, false, false],
    ['2026-09-09', '2026-09-12', 'invalid', 0, true, false],
  ])('按需求、预计和最新日期着色，实际全部入库才消除提示（%s/%s/%s/%s）', async (required, expected, latest, received, expectedRed, latestRed) => {
    const result = await api.getProjectProcurementTracking()
    Object.assign(result.items[0], { purchaseRequisitionDeliveryDate: required, purchaseDeliveryDate: expected,
      latestDeliveryDate: latest, arrivedQuantity: 4, hasDeliveryDelay: true,
      warehouseMovements: [{ kind: 'RCV', documentNumber: 'RCV1', lineNumber: 10, date: '2026-09-09', quantity: received }] })
    const wrapper = mount(ProcurementTracking, { props: { projectId: 'project-1', token: 'token', username: 'engineer' }, global: { plugins: [ElementPlus] } })
    await flushPromises()
    const cells = wrapper.findAll('.el-table__body tbody tr:first-child td')
    expect(cells[19].classes().includes('is-delivery-delay')).toBe(expectedRed)
    expect(cells[20].classes().includes('is-delivery-delay')).toBe(latestRed)
    expect(wrapper.findAll('th').some(cell => cell.text() === '需求日期')).toBe(true)
    wrapper.unmount()
  })

  it.each([[false, 'TRANSFER'], [true, 'TRANSFER'], [false, 'STOCKIN'], [true, 'STOCKIN']] as const)('库存入库按服务端去重汇总结果决定全部完成（%s，%s）', async (complete, kind) => {
    const result = await api.getProjectProcurementTracking()
    Object.assign(result.items[0], { isFullyReceived: complete,
      warehouseMovements: [{ kind, lineId: '1002912015526560', documentNumber: 'Tra2026090001', lineNumber: 10,
        date: '2026-09-08T16:00:00Z', quantity: 1, unit: '个' }] })
    const wrapper = mount(ProcurementTracking, { props: { projectId: 'project-1', token: 'token', username: 'engineer' }, global: { plugins: [ElementPlus] } })
    await flushPromises()
    const cells = wrapper.findAll('.el-table__body tbody tr:first-child td')
    expect(wrapper.findAll('.el-table__body tbody tr:first-child td.is-delivery-delay')).toHaveLength(complete ? 0 : 2)
    expect(cells.map(cell => cell.text())).toContain('2026/9/9')
    const hint = wrapper.findAll('[title]').find(node => node.attributes('title')?.includes('Tra2026090001'))
    expect(hint?.attributes('title')).toContain(kind === 'TRANSFER' ? '形态转换·转换后·日期' : '库存领用·视同入库·确认日期')
    expect(hint?.text()).not.toContain('个')
    const receiptCells = wrapper.findAll('.procurement-tracking__movement').filter(node => node.attributes('title')?.includes('Tra2026090001'))
    expect(receiptCells).toHaveLength(2)
    expect(receiptCells.every(node => node.classes().includes(kind === 'TRANSFER' ? 'is-transfer' : 'is-stockin'))).toBe(true)
    await wrapper.findAll('button').find(button => button.text() === '导出 Excel')!.trigger('click')
    await flushPromises()
    const exported = vi.mocked(downloadProcurementWorkbook).mock.calls.at(-1)
    expect(exported?.[2][0][exported[1].indexOf('入库数')]).toBe(1)
    expect(exported?.[2][0][exported[1].indexOf('入库日期')]).toBe('2026/9/9')
    wrapper.unmount()
  })

  it('累计同批次分次入库，重复收货不累加、其他采购批次和出库不用于取消红色', async () => {
    const result = await api.getProjectProcurementTracking()
    const first = result.items[0]
    const receipt = { kind: 'RCV', documentNumber: 'RCV1', lineNumber: 10, date: '2026-09-09', quantity: 2 }
    result.items = [
      { ...first, warehouseMovements: [receipt] },
      { ...first, sequence: 2, purchaseQuantity: 0, isWarehouseMovementRow: true, warehouseMovements: [receipt] },
      { ...first, sequence: 3, purchaseOrderNumbers: ['PO2'], details: [{ ...first.details[0], documentNumber: 'PO2' }],
        warehouseMovements: [{ ...receipt, documentNumber: 'RCV2', quantity: 20 }, { ...receipt, kind: 'ISSUE', quantity: 20 }] },
    ]
    const wrapper = mount(ProcurementTracking, { props: { projectId: 'project-1', token: 'token', username: 'engineer' }, global: { plugins: [ElementPlus] } })
    await flushPromises()
    const red = () => wrapper.findAll('.el-table__body tbody tr:first-child td.is-delivery-delay')
    expect(red()).toHaveLength(2)
    result.items[1].warehouseMovements = [{ ...receipt, documentNumber: 'RCV3' }]
    await wrapper.setProps({ projectId: 'reloaded' })
    await flushPromises()
    expect(red()).toHaveLength(0)
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
    expect(headers()).toEqual(['物料编码', '数量', '库存', '品牌', '请购日期', '采购员', '入库日期', '入库数', '出库日期', '出库数'])
    wrapper.unmount()
    window.localStorage.setItem(key, JSON.stringify({ order: ['materialCode', 'quantity', 'inventoryQuantity', 'brand', 'purchaseRequisitionCreatedAt', 'buyerName', 'receiptDate', 'receiptQuantity', 'issueDate', 'issueQuantity'], visible: ['materialCode', 'quantity', 'brand'] }))
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
