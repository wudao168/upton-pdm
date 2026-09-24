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

describe('ProductionDrawingCenter', () => {
  afterEach(() => { vi.restoreAllMocks(); window.localStorage.removeItem('upton-pdm:production-drawing-columns:tester') })

  it('shows frozen BOM fields by default and saves optional columns per user', async () => {
    vi.spyOn(api, 'listProductionDrawings').mockResolvedValue([{
      ...current,
      bomItems: [{ sequence: 1, drawingNumber: 'MAT-100', name: '机架', specification: 'MODEL-X', revision: 'B', remark: '先加工', material: '铝', surfaceTreatment: '阳极', heatTreatment: '无', quantity: 2, unit: '001', complete: true, brand: '自制' }],
    }])
    const wrapper = mount(ProductionDrawingCenter, { props: { token: 'token', canManage: false, username: 'tester' } })
    await flushPromises()
    expect(wrapper.find('.production-drawings__header').exists()).toBe(false)
    await wrapper.get('.production-drawings__project').trigger('click')
    expect(wrapper.findAll('thead th').map(cell => cell.text())).toEqual(['选择', '物料编码', '名称', '型号', '版本', '备注', '材质', '表面处理', '热处理', '数量', '图纸'])
    expect(wrapper.get('tbody tr').text()).toContain('MAT-100')
    expect(wrapper.get('tbody tr').text()).toContain('先加工')
    expect(wrapper.get('tbody tr').text()).toContain('2')
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
      division: '电气事业部', projectManager: 'manager-b' }
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
    expect(list).toHaveBeenCalledWith('token', false)
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

  it('downloads selected formal sources as an archive and blocks pending PDFs', async () => {
    vi.spyOn(api, 'listProductionDrawings').mockResolvedValue([current])
    const download = vi.spyOn(api, 'downloadProductionDrawingArchive').mockResolvedValue()
    const wrapper = mount(ProductionDrawingCenter, { props: { token: 'token', canManage: false } })
    await flushPromises()
    await wrapper.get('.production-drawings__project').trigger('click')
    await wrapper.get('input[aria-label="全选本包图纸"]').setValue(true)
    await wrapper.findAll('.production-drawings__selection button')[0].trigger('click')
    await flushPromises()
    expect(download).toHaveBeenCalledWith('project', ['current'], 'Source', 'token')
    await wrapper.findAll('.production-drawings__selection button')[1].trigger('click')
    expect(download).toHaveBeenCalledTimes(1)
    expect(wrapper.get('[role="alert"]').text()).toContain('PDF 待转换')
    wrapper.unmount()
  })
})
