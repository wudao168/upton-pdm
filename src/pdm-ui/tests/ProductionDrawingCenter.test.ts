import { flushPromises, mount } from '@vue/test-utils'
import { afterEach, describe, expect, it, vi } from 'vitest'
import ProductionDrawingCenter from '../src/components/ProductionDrawingCenter.vue'
import * as api from '../src/api'
import type { ProductionDrawingItem } from '../src/types'
import { userDisplayNameKey } from '../src/userDisplay'

const current: ProductionDrawingItem = {
  projectId: 'project', projectCode: 'P-001', projectName: '测试项目', documentId: 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa',
  versionId: 'current', releasePackageId: 'package', releasePackageNumber: 'RP-2',
  drawingNumber: 'D-100', model: 'M-100', name: '装配图', revision: 'B', publishedAt: '2026-09-23T00:00:00Z',
  priority: 'Urgent', requiredOn: '2026-10-01', isCurrent: true, pdfReady: false, legacyUnverified: false,
}
const old: ProductionDrawingItem = { ...current, versionId: 'old', releasePackageId: 'package-old', releasePackageNumber: 'RP-1', revision: 'A', isCurrent: false, pdfReady: true, legacyUnverified: true }

describe('ProductionDrawingCenter', () => {  it('shows progress and blocks repeated package downloads until the request finishes', async () => {
    vi.spyOn(api, 'listProductionDrawings').mockResolvedValue([{ ...current, pdfReady: true }])
    let finish!: () => void
    const download = vi.spyOn(api, 'downloadProductionDrawingArchive').mockImplementation(async (...args) => {
      args[5]?.(50, 100)
      await new Promise<void>(resolve => { finish = resolve })
    })
    const wrapper = mount(ProductionDrawingCenter, { props: { token: 'token', canManage: false } })
    await flushPromises()
    await wrapper.get('.production-drawings__project').trigger('click')
    await wrapper.get('[aria-label="全选本包图纸"]').setValue(true)
    const button = wrapper.findAll('button').find(item => item.text() === '下载PDF')!
    await button.trigger('click')
    expect(wrapper.text()).toContain('正在下载：50%')
    expect(wrapper.get('.production-drawings__toolbar-actions > [role="status"]').text()).toBe('正在下载：50%')
    expect(wrapper.find('.production-drawings > [role="status"]').exists()).toBe(false)
    expect(button.attributes('disabled')).toBeDefined()
    await button.trigger('click')
    expect(download).toHaveBeenCalledTimes(1)
    finish()
    await flushPromises()
    expect(button.attributes('disabled')).toBeUndefined()
    wrapper.unmount()
  })
  it('loads only an index initially and selects the entire package across pages', async () => {
    const items = Array.from({ length: 121 }, (_, index) => ({ ...current, pdfReady: true, versionId: `v-${index}`, drawingNumber: `D-${index}` }))
    const list = vi.spyOn(api, 'listProductionDrawings').mockResolvedValue(items)
    const download = vi.spyOn(api, 'downloadProductionDrawingArchive').mockResolvedValue(undefined)
    const wrapper = mount(ProductionDrawingCenter, { props: { token: 'token', canManage: false } })
    await flushPromises()
    expect(list).toHaveBeenCalledTimes(1)
    expect(list).toHaveBeenLastCalledWith('token', false, undefined, true)
    expect(wrapper.find('[aria-label="按项目号筛选"]').exists()).toBe(false)
    expect(wrapper.text()).not.toContain('保存位置由浏览器选择')
    await wrapper.get('.production-drawings__project').trigger('click')
    await flushPromises()
    expect(list).toHaveBeenLastCalledWith('token', false, 'project')
    expect(wrapper.findAll('tbody tr')).toHaveLength(50)
    expect(wrapper.find('.production-drawings__content > .production-drawings__packages').exists()).toBe(false)
    expect(wrapper.find('.production-drawings__projects .production-drawings__packages').exists()).toBe(true)
    expect(wrapper.get('[aria-label="生产图纸每页条数"]').findAll('option').map(option => option.attributes('value'))).toEqual(['30', '50', '100', '200', '500'])
    await wrapper.get('[aria-label="全选本包图纸"]').setValue(true)
    expect(wrapper.text()).toContain('已选 121 张')
    await wrapper.get('[aria-label="生产图纸下一页"]').trigger('click')
    expect(wrapper.findAll('tbody tr')).toHaveLength(50)
    expect(wrapper.text()).toContain('已选 121 张')
    await wrapper.findAll('button').find(button => button.text() === '下载PDF')!.trigger('click')
    expect(download.mock.calls[0]?.[1]).toHaveLength(121)
    await wrapper.get('[aria-label="生产图纸每页条数"]').setValue('30')
    expect(wrapper.findAll('tbody tr')).toHaveLength(30)
    expect(wrapper.text()).toContain('已选 121 张')
    wrapper.unmount()
  })
  it('exports the full package and cross-page selection as frozen BOM without requiring PDF conversion', async () => {
    const items = Array.from({ length: 51 }, (_, i) => ({ ...current, versionId: `v-${i}`, bomItems: [{ id: `bom-${i}`, sequence: i + 1, drawingNumber: `MAT-${i}`, name: `零件${i}`, quantity: i + 1, unit: '个', revision: 'B', complete: true }] }))
    vi.spyOn(api, 'listProductionDrawings').mockResolvedValue(items)
    const saved = vi.spyOn(api, 'downloadGeneratedFile').mockResolvedValue(undefined)
    const wrapper = mount(ProductionDrawingCenter, { props: { token: 'token', canManage: false } })
    await flushPromises()
    await wrapper.get('.production-drawings__project').trigger('click')
    await flushPromises()
    const button = (name: string) => wrapper.findAll('button').find(item => item.text() === name)!
    expect(button('下载BOM').attributes('disabled')).toBeDefined()
    await wrapper.get('[aria-label="全选本包图纸"]').setValue(true)
    await button('下载BOM').trigger('click')
    await flushPromises()
    expect(saved).toHaveBeenLastCalledWith(expect.any(Blob), 'RP-2-选中BOM.xlsx')
    await wrapper.get('[aria-label="全选本包图纸"]').setValue(true)
    await wrapper.get('[aria-label="生产图纸下一页"]').trigger('click')
    await button('下载BOM').trigger('click')
    await flushPromises()
    expect(saved).toHaveBeenLastCalledWith(expect.any(Blob), 'RP-2-选中BOM.xlsx')
    const read = (blob: Blob) => new Promise<ArrayBuffer>(resolve => { const reader = new FileReader(); reader.onload = () => resolve(reader.result as ArrayBuffer); reader.readAsArrayBuffer(blob) })
    const { unzipSync, strFromU8 } = await import('three/examples/jsm/libs/fflate.module.js')
    const files = unzipSync(new Uint8Array(await read(saved.mock.calls[1]![0])))
    const sheet = strFromU8(files['xl/worksheets/sheet1.xml']!)
    expect(sheet.match(/<row /g)).toHaveLength(52)
    expect(sheet).toContain('MAT-50')
    wrapper.unmount()
  })
  afterEach(() => { vi.restoreAllMocks(); window.localStorage.removeItem('upton-pdm:production-drawing-columns:tester') })

  it('shows frozen BOM fields by default and saves optional columns per user', async () => {
    vi.spyOn(api, 'listProductionDrawings').mockResolvedValue([{
      ...current,
      bomItems: [{ sequence: 1, drawingNumber: 'MAT-100', name: '机架', specification: 'MODEL-X', revision: 'B', remark: '先加工', material: '铝', surfaceTreatment: '阳极', heatTreatment: '无', quantity: 2, unit: '001', complete: true, brand: '自制' }],
    }])
    const wrapper = mount(ProductionDrawingCenter, { props: { token: 'token', canManage: false, username: 'tester' } })
    await flushPromises()
    expect(wrapper.find('.production-drawings__header').exists()).toBe(false)
    expect(wrapper.get('.production-drawings__project-meta').text()).toBe('1 张图纸2026-09-23')
    expect(wrapper.get('.production-drawings__project').text()).not.toContain('发布')
    await wrapper.get('.production-drawings__project').trigger('click')
    expect(wrapper.findAll('thead th').map(cell => cell.text())).toEqual(['选择', '物料编码', '名称', '型号', '版本', '备注', '材质', '表面处理', '热处理', '数量', '图纸', '紧急程度', '需求货期'])
    expect(wrapper.get('tbody tr').text()).toContain('MAT-100')
    expect(wrapper.get('tbody tr').text()).toContain('先加工')
    expect(wrapper.get('tbody tr').text()).toContain('2')
    expect(wrapper.get('tbody tr').text()).toContain('紧急')
    expect(wrapper.get('tbody tr').text()).toContain('2026-10-01')
    await wrapper.get('.production-drawings__selection button.pdm-secondary-action').trigger('click')
    await wrapper.get('.production-drawings__column-options input[value="brand"]').setValue(true)
    await wrapper.get('.production-drawings__column-dialog footer .pdm-primary-action').trigger('click')
    expect(wrapper.findAll('thead th').map(cell => cell.text())).toContain('品牌')
    wrapper.unmount()
    const restored = mount(ProductionDrawingCenter, { props: { token: 'token', canManage: false, username: 'tester' } })
    await flushPromises()
    await restored.get('.production-drawings__project').trigger('click')
    expect(restored.findAll('thead th').map(cell => cell.text())).toContain('品牌')
    restored.unmount()
  })

  it('filters projects and release packages by publisher, division and project manager', async () => {
    const other = { ...current, projectId: 'other', projectCode: 'P-002', projectName: '其他项目',
      documentId: 'bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb', versionId: 'other-version',
      releasePackageId: 'other-package', releasePackageNumber: 'RP-3', publishedBy: 'li',
      division: '电气事业部', projectManager: 'manager-b', publishedAt: '2026-09-24T00:00:00Z' }
    vi.spyOn(api, 'listProductionDrawings').mockResolvedValue([
      { ...current, publishedBy: 'wang', division: '机械事业部', projectManager: 'manager-a' },
      { ...old, publishedBy: 'li', division: '机械事业部', projectManager: 'manager-a' }, other,
    ])
    const wrapper = mount(ProductionDrawingCenter, {
      props: { token: 'token', canManage: false },
      global: { provide: { [userDisplayNameKey as symbol]: (username?: string | null) => ({ wang: '王工', li: '李工', 'manager-a': '张经理', 'manager-b': '赵经理' }[username ?? ''] ?? username ?? '—') } },
    })
    await flushPromises()
    expect(wrapper.findAll('.production-drawings__project')).toHaveLength(2)
    await wrapper.get('input[aria-label="搜索生产图纸"]').setValue('P-002')
    expect(wrapper.findAll('.production-drawings__project')).toHaveLength(1)
    expect(wrapper.get('.production-drawings__project').text()).toContain('P-002')
    await wrapper.get('input[aria-label="搜索生产图纸"]').setValue('')
    await wrapper.get('input[aria-label="按发布日期筛选"]').setValue('2026-09-23')
    expect(wrapper.findAll('.production-drawings__project')).toHaveLength(1)
    expect(wrapper.get('.production-drawings__project').text()).toContain('P-001')
    await wrapper.get('input[aria-label="按发布日期筛选"]').setValue('')
    expect(wrapper.get('select[aria-label="按发布人筛选"] option[value="wang"]').text()).toBe('王工')
    expect(wrapper.get('select[aria-label="按项目经理筛选"] option[value="manager-a"]').text()).toBe('张经理')
    await wrapper.get('select[aria-label="按事业部筛选"]').setValue('机械事业部')
    expect(wrapper.findAll('.production-drawings__project')).toHaveLength(1)
    await wrapper.get('.production-drawings__project').trigger('click')
    expect(wrapper.findAll('.production-drawings__package')).toHaveLength(2)
    await wrapper.get('select[aria-label="按发布人筛选"]').setValue('li')
    expect(wrapper.findAll('.production-drawings__package')).toHaveLength(1)
    expect(wrapper.get('.production-drawings__package').text()).toContain('RP-1')
    await wrapper.get('select[aria-label="按项目经理筛选"]').setValue('manager-b')
    expect(wrapper.findAll('.production-drawings__project')).toHaveLength(0)
    wrapper.unmount()
  })

  it('shows only the current formal version by default and never offers an older PDF while conversion is pending', async () => {
    const list = vi.spyOn(api, 'listProductionDrawings').mockImplementation(async (_token, includeHistory) => includeHistory ? [current, old] : [current])
    const wrapper = mount(ProductionDrawingCenter, { props: { token: 'token', canManage: false } })
    await flushPromises()
    expect(list).toHaveBeenCalledWith('token', false, undefined, true)
    expect(wrapper.findAll('.production-drawings__project')).toHaveLength(1)
    expect(wrapper.findAll('tbody tr')).toHaveLength(0)
    await wrapper.get('.production-drawings__project').trigger('click')
    expect(wrapper.findAll('.production-drawings__package')).toHaveLength(1)
    expect(wrapper.findAll('tbody tr')).toHaveLength(1)
    await wrapper.get('tbody tr').trigger('click')
    expect(wrapper.get('.production-drawings__actions button').attributes('disabled')).toBeDefined()
    expect(wrapper.text()).toContain('PDF 待转换')
    await wrapper.get('input[type="checkbox"]').setValue(true)
    await flushPromises()
    expect(wrapper.findAll('.production-drawings__package')).toHaveLength(2)
    await wrapper.findAll('.production-drawings__package')[1].trigger('click')
    expect(wrapper.findAll('tbody tr')).toHaveLength(1)
    expect(wrapper.text()).toContain('已被替代')
    wrapper.unmount()
  })

  it('resolves a scanned model and revision to the immutable historical version', async () => {
    vi.spyOn(api, 'listProductionDrawings').mockImplementation(async (_token, includeHistory) => includeHistory ? [current, old] : [current])
    const wrapper = mount(ProductionDrawingCenter, { props: { token: 'token', canManage: false } })
    await flushPromises()
    await wrapper.get('input[aria-label="扫描图纸二维码"]').setValue('UPLM-DRAWING|M-100|A|aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa')
    await wrapper.get('.production-drawings__filters form').trigger('submit')
    await flushPromises()
    expect(wrapper.get('.production-drawings__project').text()).toContain('P-001')
    expect(wrapper.get('.production-drawings__package.is-selected').text()).toContain('RP-1')
    expect(wrapper.get('.production-drawings__detail').text()).toContain('历史正式版，已被替代')
    expect(wrapper.get('.production-drawings__detail').text()).toContain('历史图面未校验')
    wrapper.unmount()
  })

  it('downloads selected formal STEP files as an archive and blocks pending PDFs', async () => {
    vi.spyOn(api, 'listProductionDrawings').mockResolvedValue([{ ...current, modelFiles: [{
      documentId: 'model-document', versionId: 'model-version', drawingNumber: 'M-100', name: '三维件', revision: 'A', stepReady: true,
    }] }])
    const download = vi.spyOn(api, 'downloadProductionDrawingArchive').mockResolvedValue()
    const wrapper = mount(ProductionDrawingCenter, { props: { token: 'token', canManage: false } })
    await flushPromises()
    await wrapper.get('.production-drawings__project').trigger('click')
    await wrapper.get('input[aria-label="全选本包图纸"]').setValue(true)
    await wrapper.findAll('.production-drawings__selection button')[0].trigger('click')
    await flushPromises()
    expect(download).toHaveBeenCalledWith('project', ['current'], 'Step', 'token', 'RP-2', expect.any(Function))
    await wrapper.findAll('.production-drawings__selection button')[1].trigger('click')
    expect(download).toHaveBeenCalledTimes(1)
    expect(wrapper.get('[role="alert"]').text()).toContain('PDF 待转换')
    wrapper.unmount()
  })

  it('shows pending STEP state and never falls back to the native SolidWorks source', async () => {
    vi.spyOn(api, 'listProductionDrawings').mockResolvedValue([{ ...current, modelFiles: [{
      documentId: 'model-document', versionId: 'model-version', drawingNumber: 'M-100', name: '三维件', revision: 'A', stepReady: false,
    }] }])
    const download = vi.spyOn(api, 'downloadProductionDrawingArchive').mockResolvedValue()
    const wrapper = mount(ProductionDrawingCenter, { props: { token: 'token', canManage: false } })
    await flushPromises()
    await wrapper.get('.production-drawings__project').trigger('click')
    expect(wrapper.get('tbody tr').text()).toContain('STEP待转换')
    await wrapper.get('input[aria-label="全选本包图纸"]').setValue(true)
    await wrapper.findAll('.production-drawings__selection button')[0].trigger('click')
    expect(download).not.toHaveBeenCalled()
    expect(wrapper.get('[role="alert"]').text()).toContain('不能用 SolidWorks 源文件替代')
    wrapper.unmount()
  })

  it('downloads the published model preview as STEP from the matching row', async () => {
    vi.spyOn(api, 'listProductionDrawings').mockResolvedValue([{ ...current, modelFiles: [{
      documentId: 'model-document', versionId: 'model-version', drawingNumber: 'M-100', name: '三维件', revision: 'A', stepReady: true,
    }] }])
    const download = vi.spyOn(api, 'downloadDocumentPreviewFile').mockResolvedValue()
    const wrapper = mount(ProductionDrawingCenter, { props: { token: 'token', canManage: false } })
    await flushPromises()
    await wrapper.get('.production-drawings__project').trigger('click')
    await wrapper.get('tbody tr').trigger('click')
    await wrapper.findAll('.production-drawings__actions button')[1].trigger('click')
    expect(download).toHaveBeenCalledWith('model-document', 'model-version', 'M-100.step', 'token', expect.any(Function))
    wrapper.unmount()
  })
  it('places packages under the selected project and downloads STEP, PDF and full frozen BOM together', async () => {
    const ready = { ...current, pdfReady: true, modelFiles: [{ documentId: 'model', versionId: 'model-version', drawingNumber: 'M-100', name: '三维件', revision: 'B', stepReady: true }], bomItems: [{ id: 'bom', sequence: 1, drawingNumber: 'MAT-100', name: '零件', quantity: 2, unit: '个', revision: 'B', complete: true }] }
    vi.spyOn(api, 'listProductionDrawings').mockResolvedValue([ready])
    const archive = vi.spyOn(api, 'downloadProductionDrawingArchive').mockResolvedValue(undefined)
    const generated = vi.spyOn(api, 'downloadGeneratedFile').mockResolvedValue(undefined)
    const wrapper = mount(ProductionDrawingCenter, { props: { token: 'token', canManage: false } })
    await flushPromises(); await wrapper.get('.production-drawings__project').trigger('click'); await flushPromises()
    expect(wrapper.find('.production-drawings__projects .production-drawings__packages').exists()).toBe(true)
    expect(wrapper.find('.production-drawings__content .production-drawings__packages').exists()).toBe(false)
    await wrapper.findAll('button').find(button => button.text() === '下载本包')!.trigger('click'); await flushPromises()
    expect(archive.mock.calls.map(call => call[2])).toEqual(['Step', 'Pdf'])
    expect(archive.mock.calls.every(call => call[0] === 'project' && call[1].join() === 'current')).toBe(true)
    expect(generated).toHaveBeenCalledWith(expect.any(Blob), 'RP-2-本包BOM.xlsx')
    wrapper.unmount()
  })
  it('does not download a partial bundle when a same-package STEP is not ready', async () => {
    vi.spyOn(api, 'listProductionDrawings').mockResolvedValue([{ ...current, pdfReady: true }])
    const archive = vi.spyOn(api, 'downloadProductionDrawingArchive').mockResolvedValue(undefined)
    const generated = vi.spyOn(api, 'downloadGeneratedFile').mockResolvedValue(undefined)
    const wrapper = mount(ProductionDrawingCenter, { props: { token: 'token', canManage: false } })
    await flushPromises(); await wrapper.get('.production-drawings__project').trigger('click'); await flushPromises()
    await wrapper.findAll('button').find(button => button.text() === '下载本包')!.trigger('click'); await flushPromises()
    expect(wrapper.text()).toContain('本包部分PDF或STEP尚未就绪')
    expect(archive).not.toHaveBeenCalled(); expect(generated).not.toHaveBeenCalled()
    wrapper.unmount()
  })})
