import { flushPromises, mount, type VueWrapper } from '@vue/test-utils'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { strFromU8, unzipSync } from 'three/examples/jsm/libs/fflate.module.js'
import ReleaseCenter from '../src/components/ReleaseCenter.vue'
import * as api from '../src/api'
import type { BomItem, ReleasePackageSummary } from '../src/types'

vi.mock('../src/components/ReleaseExecutionPanel.vue', () => ({ default: { template: '<section />' } }))

const item = (id: string, quantity: number, extra: Partial<BomItem> = {}): BomItem => ({
  id, kind: 'Standard', sequence: 1, drawingNumber: id, name: `物料${id}`, specification: 'M5',
  brand: '国优', quantity, unit: '个', revision: 'A', complete: true, ...extra,
})
const release = (items: BomItem[], scope: ReleasePackageSummary['scope'] = 'StandardSupplement'): ReleasePackageSummary => ({
  id: 'release-1', number: 'RP-TEST-001', state: '草稿', scope, workflowVersion: 1, selectedBomItemIds: [], steps: [],
  standardBomSnapshot: items, nonStandardBomSnapshot: items, electricalBomSnapshot: items,
  createsManufacturingBaseline: false, locksDocuments: false,
})
const props = { username: 'reviewer', token: 'test-token', projectId: 'project-1', pending: false, progress: 0, error: '', canManage: true, canDecide: false }
let wrapper: VueWrapper | undefined
const button = () => wrapper!.findAll('button').find(button => /^(导出BOM|导出中…)$/.test(button.text()))!
async function exportedRows(firstRow = 8) {
  const blob = vi.mocked(api.downloadGeneratedFile).mock.calls.at(-1)![0]
  const buffer = await new Promise<ArrayBuffer>(resolve => {
    const reader = new FileReader()
    reader.onload = () => resolve(reader.result as ArrayBuffer)
    reader.readAsArrayBuffer(blob)
  })
  const files = unzipSync(new Uint8Array(buffer))
  const xml = new DOMParser().parseFromString(strFromU8(files['xl/worksheets/sheet1.xml']!), 'application/xml')
  return [...xml.getElementsByTagName('row')].filter(row => Number(row.getAttribute('r')) >= firstRow)
    .map(row => [...row.getElementsByTagName('c')].map(cell => cell.getAttribute('t') === 'inlineStr'
      ? cell.textContent! : Number(cell.getElementsByTagName('v')[0]!.textContent)))
}

describe('发布BOM变更导出', () => {
  beforeEach(() => {
    vi.spyOn(api, 'listReleaseItemComments').mockResolvedValue([])
    vi.spyOn(api, 'readProjectPlan').mockResolvedValue(null as never)
    vi.spyOn(api, 'getProjectExportMetadata').mockResolvedValue({ title: '', company: '普顿', scope: '标准件',
      versionLabel: '', version: '', exportedAt: '2026-10-20T01:00:00Z', exportedBy: 'reviewer', fields: [] })
    vi.spyOn(api, 'downloadGeneratedFile').mockResolvedValue(undefined)
  })
  afterEach(() => { wrapper?.unmount(); wrapper = undefined; vi.restoreAllMocks() })

  it.each(['StandardSupplement', 'NonStandardSupplement', 'ElectricalSupplement'] as const)('%s导出新增、增加、减少、属性修改和删除的净数量', async scope => {
    const previous = [item('up', 2), item('down', 5), item('attribute', 3), item('delete', 5), item('unchanged', 1)]
    const current = [item('add', 4), item('up', 5), item('down', 3), item('attribute', 3, { brand: '新品牌' }), item('unchanged', 1)]
    const selected = release(current, scope)
    const original = JSON.stringify({ selected, previous })
    wrapper = mount(ReleaseCenter, { props: { ...props, releasePackage: selected, previousVersionItems: previous, releaseItems: [item('工作区后续修改', 999)] } })
    await button().trigger('click')
    await flushPromises()
    const rows = await exportedRows()
    expect(rows.map(row => [row[1], row[10], row[11], row[12], row[14]])).toEqual([
      ['add', 4, 0, 4, '新增'], ['up', 3, 2, 5, '修改'], ['down', -2, 5, 3, '修改'],
      ['attribute', 0, 3, 3, '修改'], ['delete', -5, 5, 0, '删除'],
    ])
    expect(api.getProjectExportMetadata).toHaveBeenCalledWith('project-1', 'test-token', 'release-1')
    expect(vi.mocked(api.downloadGeneratedFile).mock.calls[0]![1]).toBe('RP-TEST-001-BOM变更.xlsx')
    expect(JSON.stringify({ selected, previous })).toBe(original)
  })

  it('按物料及单位汇总实例并应用各自发布倍率，保留小数净变化', async () => {
    const previous = [item('old-a', 0.1, { drawingNumber: 'MAT' }), item('old-b', 0.2, { drawingNumber: 'MAT' }), item('fixed', 2)]
    const current = [item('new-a', 0.2, { drawingNumber: 'MAT' }), item('new-b', 0.3, { drawingNumber: 'MAT' }), item('fixed', 2)]
    wrapper = mount(ReleaseCenter, { props: { ...props, releasePackage: { ...release(current), wholeSetMultiplier: 3 }, previousVersionItems: previous, previousVersionMultiplier: 2 } })
    await button().trigger('click')
    await flushPromises()
    expect((await exportedRows()).map(row => [row[1], row[10], row[11], row[12]])).toEqual([
      ['MAT', 0.9, 0.6, 1.5], ['fixed', 2, 4, 6],
    ])
  })

  it('换料号和换单位分别导出新旧物料的正负数量，不被相同实例标识抵消', async () => {
    const previous = [item('same-id', 5, { drawingNumber: 'OLD' }), item('unit', 3)]
    const current = [item('same-id', 5, { drawingNumber: 'NEW' }), item('unit', 3, { unit: '米' })]
    wrapper = mount(ReleaseCenter, { props: { ...props, releasePackage: release(current), previousVersionItems: previous } })
    await button().trigger('click')
    await flushPromises()
    expect((await exportedRows()).map(row => [row[1], row[9], row[10]])).toEqual([
      ['NEW', '个', 5], ['unit', '米', 3], ['OLD', '个', -5], ['unit', '个', -3],
    ])
  })

  it('第二页和完整BOM视图都导出全部51条变更，排除未变更物料', async () => {
    const current = [item('unchanged', 1), ...Array.from({ length: 51 }, (_, index) => item(`new-${index}`, index + 1))]
    wrapper = mount(ReleaseCenter, { props: { ...props, releasePackage: release(current), previousVersionItems: [current[0]!] } })
    await wrapper.get('button[aria-label="固化快照下一页"]').trigger('click')
    await button().trigger('click')
    await flushPromises()
    expect(await exportedRows()).toHaveLength(51)
    await wrapper.findAll('button').find(button => button.text() === '查看完整BOM')!.trigger('click')
    expect(wrapper.find('[aria-label="审批快照显示方式"]').exists()).toBe(false)
    await button().trigger('click')
    await flushPromises()
    const rows = await exportedRows()
    expect(rows).toHaveLength(51)
    expect(rows.at(-1)![1]).toBe('new-50')
    expect(rows.some(row => row[1] === 'unchanged')).toBe(false)
  })

  it('导出失败显示中文并可重试，取消保存不显示失败', async () => {
    vi.spyOn(console, 'error').mockImplementation(() => undefined)
    vi.mocked(api.getProjectExportMetadata).mockRejectedValueOnce(new Error('Failed to fetch'))
    wrapper = mount(ReleaseCenter, { props: { ...props, releasePackage: release([item('new', 1)]) } })
    await button().trigger('click')
    await flushPromises()
    expect(wrapper.get('.pdm-release-frozen-snapshot [role="alert"]').text()).toContain('导出BOM失败，请检查网络后重试')
    expect(wrapper.text()).not.toContain('Failed to fetch')
    vi.mocked(api.downloadGeneratedFile).mockRejectedValueOnce(new DOMException('Cancelled', 'AbortError'))
    await button().trigger('click')
    await flushPromises()
    expect(wrapper.find('.pdm-release-frozen-snapshot [role="alert"]').exists()).toBe(false)
    expect(button().attributes('disabled')).toBeUndefined()
  })

  it('防止重复导出；等待期间切换发布包不下载旧包', async () => {
    let resolve!: (value: Awaited<ReturnType<typeof api.getProjectExportMetadata>>) => void
    const metadata = await api.getProjectExportMetadata('project-1', 'test-token')
    vi.mocked(api.getProjectExportMetadata).mockClear().mockImplementation(() => new Promise(done => { resolve = done }))
    wrapper = mount(ReleaseCenter, { props: { ...props, releasePackage: release([item('new', 1)]) } })
    await button().trigger('click')
    await button().trigger('click')
    expect(api.getProjectExportMetadata).toHaveBeenCalledTimes(1)
    expect(button().attributes('disabled')).toBeDefined()
    await wrapper.setProps({ releasePackage: { ...release([item('other', 1)]), id: 'release-2' } })
    resolve(metadata)
    await flushPromises()
    expect(api.downloadGeneratedFile).not.toHaveBeenCalled()
    expect(button().attributes('disabled')).toBeUndefined()
  })

  it.each(['StandardLongLead', 'NonStandardLongLead', 'ElectricalLongLead'] as const)('%s导出本包冻结的完整BOM及发布数量', async scope => {
    const current = [item('a', 1, { drawingNumber: 'MAT' }), item('b', 3, { drawingNumber: 'MAT' }), item('other', 2)]
    wrapper = mount(ReleaseCenter, { props: { ...props, releasePackage: release(current, scope),
      previousVersionItems: [item('不属于本包', 999)], releaseItems: [item('工作区修改', 999)] } })
    await button().trigger('click')
    await flushPromises()
    const rows = await exportedRows(7)
    expect(rows[0]!.slice(10)).toEqual(['数量', '备注'])
    expect(rows.slice(1).map(row => [row[1], row[10]])).toEqual([['MAT', 4], ['other', 2]])
    expect(vi.mocked(api.downloadGeneratedFile).mock.calls[0]![1]).toBe('RP-TEST-001-BOM.xlsx')
  })

  it.each(['StandardFormal', 'NonStandardWithDrawing', 'ElectricalFormal'] as const)('%s按汇总导出BOM总量、前期长交期已发布及本次新增下发', async scope => {
    const current = [item('a', 1, { drawingNumber: 'MAT' }), item('b', 3, { drawingNumber: 'MAT' }), item('fully-issued', 1)]
    const longLeadScope = scope === 'StandardFormal' ? 'StandardLongLead' : scope === 'NonStandardWithDrawing' ? 'NonStandardLongLead' : 'ElectricalLongLead'
    const history = [
      { ...release([item('old-a', 2, { drawingNumber: 'MAT', unit: '001' }), item('fully-issued', 5)], longLeadScope), id: 'early', state: '已发布', publishedAt: '2026-09-01T00:00:00Z' },
      { ...release([item('future', 99, { drawingNumber: 'MAT' })], longLeadScope), id: 'future', state: '已发布', publishedAt: '2026-11-01T00:00:00Z' },
    ]
    wrapper = mount(ReleaseCenter, { props: { ...props, releasePackage: { ...release(current, scope), wholeSetMultiplier: 3, publishedAt: '2026-10-01T00:00:00Z' }, releasePackages: history } })
    expect(wrapper.find('[aria-label="审批快照显示方式"]').exists()).toBe(false)
    await button().trigger('click')
    await flushPromises()
    const rows = await exportedRows(7)
    expect(rows[0]!.slice(10)).toEqual(['BOM总量', '前期已发布', '本次新增下发', '备注'])
    expect(rows.slice(1).map(row => [row[1], ...row.slice(10, 13)])).toEqual([['MAT', 12, 2, 10], ['fully-issued', 3, 5, 0]])
    expect(vi.mocked(api.downloadGeneratedFile).mock.calls[0]![1]).toBe('RP-TEST-001-BOM.xlsx')
  })

  it('正式发布第二页仍导出全部冻结物料，不只导出当前页', async () => {
    const items = Array.from({ length: 51 }, (_, index) => item(`part-${index}`, index + 1))
    wrapper = mount(ReleaseCenter, { props: { ...props, releasePackage: release(items, 'StandardFormal') } })
    await wrapper.get('button[aria-label="固化快照下一页"]').trigger('click')
    expect(wrapper.findAll('.pdm-release-frozen-table tbody tr')).toHaveLength(1)
    await button().trigger('click')
    await flushPromises()
    const rows = await exportedRows()
    expect(rows).toHaveLength(51)
    expect(rows.at(-1)!.slice(10, 13)).toEqual([51, 0, 51])
  })

  it('无变更时禁用变更导出，正式与长交期为空时禁用，旧合并类型不增加入口', async () => {
    const items = [item('same', 1)]
    wrapper = mount(ReleaseCenter, { props: { ...props, releasePackage: release(items), previousVersionItems: items } })
    expect(button().attributes('disabled')).toBeDefined()
    await wrapper.setProps({ releasePackage: release(items, 'StandardFormal') })
    expect(button().attributes('disabled')).toBeUndefined()
    for (const scope of ['StandardFormal', 'StandardLongLead'] as const) {
      await wrapper.setProps({ releasePackage: release([], scope) })
      expect(button().attributes('disabled')).toBeDefined()
    }
    await wrapper.setProps({ releasePackage: release(items, 'LegacyCombined') })
    expect(button()).toBeUndefined()
  })
})
