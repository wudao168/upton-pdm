import { flushPromises, mount } from '@vue/test-utils'
import ElementPlus from 'element-plus'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import ProductionLineDrawingDialog from '../src/components/ProductionLineDrawingDialog.vue'
import * as api from '../src/api'
import type { ProductionDrawingItem } from '../src/types'
import type { ProductionLine, ProductionReleaseState } from '../src/productionTracking'

vi.mock('../src/components/PdfDrawingViewer.vue', () => ({ default: { name: 'PdfDrawingViewer', props: { url: String, fillHeight: Boolean }, template: '<div class="test-pdf">{{ url }}<slot name="toolbar" /></div>' } }))
vi.mock('../src/statusMessage', () => ({ ElMessage: { success: vi.fn() } }))
const line: ProductionLine = { id: 'line', materialKey: 'M', drawingNumber: 'D001', name: '机架', revision: 'W1', unit: '001', requiredQuantity: 2, priority: 'Normal' }
const state = { projectId: 'project', releasePackageId: 'opening', formalReleasePackageId: 'frozen-package', packageNumber: 'RP001' } as ProductionReleaseState
const drawing = (overrides = {}): ProductionDrawingItem => ({ projectId: 'project', projectCode: 'P001', projectName: '项目', documentId: 'drawing', versionId: 'frozen-version', releasePackageId: 'frozen-package', releasePackageNumber: 'RP001', drawingNumber: 'D001', model: '', name: '机架', revision: 'W1', publishedAt: '', priority: 'Normal', isCurrent: false, legacyUnverified: false, pdfReady: true, bomQuantities: [{ materialKey: 'M' } as any], ...overrides })
const create = (action: 'preview' | 'download' = 'preview') => mount(ProductionLineDrawingDialog, { props: { token: 'token', state, line, action }, global: { plugins: [ElementPlus], stubs: { teleport: true } } })
beforeEach(() => {
  vi.stubGlobal('URL', class extends URL { static createObjectURL = vi.fn(() => 'blob:test-pdf'); static revokeObjectURL = vi.fn() })
  vi.spyOn(api, 'readProductionDrawingPreview').mockResolvedValue(new Blob(['pdf'], { type: 'application/pdf' }))
  vi.spyOn(api, 'downloadProductionDrawingFile').mockResolvedValue()
})
afterEach(() => { vi.restoreAllMocks(); vi.unstubAllGlobals() })

describe('批次列表正式图纸', () => {
  it('只预览所属正式发布包冻结的图纸，关闭时释放预览地址', async () => {
    const frozen = drawing()
    vi.spyOn(api, 'listProductionDrawings').mockResolvedValue([drawing({ releasePackageId: 'new-package', versionId: 'latest', isCurrent: true }), frozen])
    const wrapper = create()
    await flushPromises()
    expect(api.readProductionDrawingPreview).toHaveBeenCalledWith(frozen, 'token')
    expect(wrapper.get('.test-pdf').text()).toContain('blob:test-pdf')
    expect(wrapper.getComponent({ name: 'PdfDrawingViewer' }).props('fillHeight')).toBe(true)
    wrapper.unmount()
    expect(URL.revokeObjectURL).toHaveBeenCalledWith('blob:test-pdf')
  })
  it('下载使用相同冻结版本，没有同包图纸时不退回新版', async () => {
    const frozen = drawing()
    vi.spyOn(api, 'listProductionDrawings').mockResolvedValue([frozen])
    const wrapper = create('download')
    await flushPromises()
    expect(api.downloadProductionDrawingFile).toHaveBeenCalledWith(frozen, 'Pdf', 'token')
    expect(wrapper.emitted('close')).toEqual([[]])
    wrapper.unmount()
    vi.mocked(api.listProductionDrawings).mockResolvedValue([drawing({ releasePackageId: 'new-package' })])
    vi.mocked(api.readProductionDrawingPreview).mockClear()
    const missing = create()
    await flushPromises()
    expect(missing.get('[role="alert"]').text()).toContain('此发布包中没有可用的正式图纸')
    expect(api.readProductionDrawingPreview).not.toHaveBeenCalled()
    missing.unmount()
  })
  it('PDF 待转换时显示状态，不读取其他版本', async () => {
    vi.spyOn(api, 'listProductionDrawings').mockResolvedValue([drawing({ pdfReady: false })])
    const wrapper = create()
    await flushPromises()
    expect(wrapper.text()).toContain('PDF 待转换')
    expect(api.readProductionDrawingPreview).not.toHaveBeenCalled()
    wrapper.unmount()
  })
})
