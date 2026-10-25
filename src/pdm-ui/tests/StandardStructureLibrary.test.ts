import { flushPromises, mount } from '@vue/test-utils'
import ElementPlus, { ElMessageBox } from 'element-plus'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import StandardStructureLibrary from '../src/components/StandardStructureLibrary.vue'
import { unclassifiedStructureCategoryId, type StructureNode, type StructureView } from '../src/standardStructure'

const api = vi.hoisted(() => ({ readStandardStructures: vi.fn(), readStructurePreview: vi.fn(),
  referenceStandardStructure: vi.fn(), changeStandardStructureState: vi.fn(), listStandardStructureReferences: vi.fn(), classifyStandardStructures: vi.fn(), saveStructureCategory: vi.fn(), deleteStructureCategory: vi.fn(), moveStructureCategory: vi.fn() }))
vi.mock('../src/api', () => api)
const part = (id: string, kind = 'Part'): StructureNode => ({ id, kind, drawingNumber: id, name: `名称-${id}`,
  fileName: `${id}.${kind === 'Drawing' ? 'SLDDRW' : 'SLDPRT'}`, fileKey: id, previewFileKey: `preview-${id}`, embedded: false, quantity: 1, children: [] })
function data(): StructureView {
  const revision = { id: 'revision-1', version: 1, archivedAt: '2026-10-20T01:00:00Z', archivedBy: 'std',
    root: { ...part('assembly-1', 'Assembly'), children: [part('plate'), { ...part('embedded'), embedded: true, fileKey: null, previewFileKey: null }, part('drawing', 'Drawing')] },
    files: [{ key: 'preview-assembly-1', fileName: 'assembly.step', length: 10, sha256: 'a' }],
    bom: [{ id: 'line-std', kind: 'Standard' as const, drawingNumber: 'S-1', name: '气缸', quantity: 1, unit: '个', revision: 'W1' },
      { id: 'line-nonstd', kind: 'NonStandard' as const, drawingNumber: 'N-1', name: '底板', quantity: 2, unit: '件', material: '钢', revision: 'W1' }] }
  return { canManage: true, canArchive: true, canConfigureStorage: false, catalog: { companyId: 'company', rowVersion: 3, storageRoot: 'D:/Standard',
    categories: [{ id: 'root', name: '搬运机构', parentId: null, sortOrder: 1, isActive: true },
      { id: 'child', name: '气缸升降', parentId: 'root', sortOrder: 1, isActive: true },
      { id: 'other', name: '定位夹紧', parentId: null, sortOrder: 2, isActive: true }],
    structures: [{ id: 'structure-1', categoryId: 'child', code: 'BZ-001', name: '升降组件', revisions: [revision] },
      { id: 'structure-2', categoryId: 'other', code: 'BZ-002', name: '定位组件', revisions: [{ ...revision, id: 'revision-2', root: part('assembly-2', 'Assembly') }] }] } }
}
let wrapper: ReturnType<typeof mount> | undefined
const button = (text: string) => wrapper!.findAll('button').find(b => b.text() === text)!
async function open(canReference = true) {
  wrapper = mount(StandardStructureLibrary, { attachTo: document.body,
    props: { token: 'token', projects: [{ id: 'project', code: 'P700014-1', name: '项目A' }] as never, canReference },
    global: { plugins: [ElementPlus], stubs: { StepPreviewViewer: { template: '<div>STEP预览</div>' } } } })
  await flushPromises()
  return wrapper
}
describe('StandardStructureLibrary', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    api.readStandardStructures.mockResolvedValue(data())
    api.referenceStandardStructure.mockResolvedValue({ addedCount: 1, version: 1, revisionId: 'revision-1' })
    api.readStructurePreview.mockResolvedValue({ arrayBuffer: () => Promise.resolve(new ArrayBuffer(8)) })
    api.listStandardStructureReferences.mockResolvedValue([])
  })
  afterEach(() => { wrapper?.unmount(); document.body.innerHTML = '' })

  it('renders only drawing and BOM tabs, loads independent categories, and filters descendants', async () => {
    const w = await open()
    expect(api.readStandardStructures).toHaveBeenCalledWith('token')
    expect(w.get('[aria-label="标准结构功能"]').findAll('button').map(b => b.text()).filter(Boolean)).toEqual(['图档', 'BOM'])
    expect(w.text()).not.toContain('审核')
    expect(w.text()).not.toContain('批注')
    const tree = w.findAllComponents({ name: 'ElTree' })[0]!
    tree.vm.$emit('node-click', { id: 'root' })
    await flushPromises()
    expect(w.get('.standard-structure__drawing-tree').text()).toContain('BZ-001-V01')
    expect(w.get('.standard-structure__drawing-tree').text()).not.toContain('BZ-002-V01')
    tree.vm.$emit('node-click', { id: 'other' })
    await flushPromises()
    expect(w.get('.standard-structure__properties').text()).toContain('assembly-2')
    expect(w.get('.standard-structure__breadcrumb').text()).toContain('定位夹紧')
  })

  it('shows automatic model folders below unclassified and permits editing their existing parent', async () => {
    const view = data()
    view.catalog.categories.push({ id: unclassifiedStructureCategoryId, name: '未分类', parentId: null, sortOrder: 0, isActive: true },
      { id: 'model-folder', name: 'UPST-1-0001-V01', parentId: unclassifiedStructureCategoryId, sortOrder: 1, isActive: true })
    view.catalog.structures[0]!.categoryId = 'model-folder'
    view.catalog.structures[0]!.code = 'UPST-1-0001'
    api.readStandardStructures.mockResolvedValue(view)
    const w = await open()
    const tree = w.findAllComponents({ name: 'ElTree' })[0]!
    tree.vm.$emit('node-click', { id: unclassifiedStructureCategoryId })
    await flushPromises()
    expect(w.get('.standard-structure__drawing-tree').text()).toContain('UPST-1-0001-V01')
    expect(w.get('.standard-structure__drawing-tree').text()).not.toContain('BZ-002-V01')
    tree.vm.$emit('node-click', { id: 'model-folder' })
    await flushPromises()
    expect(w.get('.standard-structure__breadcrumb').text()).toContain('未分类 › UPST-1-0001-V01')
    await button('分类维护').trigger('click'); await flushPromises()
    const manager = w.findAllComponents({ name: 'ElDialog' }).find(c => c.props('title') === '标准结构分类维护')!
    const folderRow = manager.findAll('.standard-structure__category-row').find(row => row.text().includes('UPST-1-0001-V01'))!
    await folderRow.findAll('button').find(b => b.text() === '编辑')!.trigger('click'); await flushPromises()
    expect(manager.findComponent({ name: 'ElSelect' }).props('modelValue')).toBe(unclassifiedStructureCategoryId)
    expect(manager.findAllComponents({ name: 'ElOption' }).some(option => option.props('label') === '未分类' && option.props('value') === unclassifiedStructureCategoryId)).toBe(true)
  })

  it('disables, recycles, restores and enables the whole structure without changing snapshots', async () => {
    vi.spyOn(ElMessageBox, 'confirm').mockResolvedValue('confirm' as never)
    let current = data()
    const snapshots = JSON.stringify(current.catalog.structures[0]!.revisions)
    api.readStandardStructures.mockResolvedValue(current)
    api.changeStandardStructureState.mockImplementation(async (id: string, action: string, expected: number) => {
      expect(expected).toBe(current.catalog.rowVersion)
      const next = JSON.parse(JSON.stringify(current.catalog))
      const item = next.structures.find((s: { id: string }) => s.id === id)!
      if (action === 'Disable') item.isActive = false
      if (action === 'Enable') item.isActive = true
      if (action === 'Delete') item.deletedAt = '2026-10-23T10:00:00Z'
      if (action === 'Restore') item.deletedAt = null
      next.rowVersion++; current = { ...current, catalog: next }
      return next
    })
    const w = await open()
    for (const [label, status] of [['停用', 'inactive'], ['删除', 'deleted'], ['恢复', 'inactive'], ['启用', 'active']]) {
      await button(label!).trigger('click'); await flushPromises()
      expect((w.get('select[aria-label="标准结构状态"]').element as HTMLSelectElement).value).toBe(status)
      expect(w.get('.standard-structure__properties').text()).toContain('BZ-001-V01')
      expect(JSON.stringify(current.catalog.structures[0]!.revisions)).toBe(snapshots)
    }
    expect(api.changeStandardStructureState.mock.calls.map(call => call[1])).toEqual(['Disable', 'Delete', 'Restore', 'Enable'])
    expect(ElMessageBox.confirm).toHaveBeenCalledWith(expect.stringContaining('完整图档、工程图及对应BOM'), expect.any(String), expect.any(Object))
  })

  it('blocks deletion with references and disables the reference button for an inactive parent', async () => {
    const current = data()
    current.catalog.categories[0]!.isActive = false
    api.readStandardStructures.mockResolvedValue(current)
    api.listStandardStructureReferences.mockResolvedValue([{ projectId: 'p', projectCode: 'P700016', revisionId: 'revision-1', version: 1, kind: 'NonStandard', quantity: 2, actor: 'engineer', referencedAt: '2026-10-23T10:00:00Z', pending: false }])
    const w = await open()
    await w.get('select[aria-label="标准结构状态"]').setValue('inactive'); await flushPromises()
    expect(w.get('[role="status"]').text()).toContain('所属分类已停用')
    await button('BOM').trigger('click'); await flushPromises()
    expect(button('引用到项目').attributes('disabled')).toBeDefined()
    await button('删除').trigger('click'); await flushPromises()
    expect(api.changeStandardStructureState).not.toHaveBeenCalled()
    const dialog = w.findAllComponents({ name: 'ElDialog' }).find(c => c.props('title') === 'BZ-001 · 项目引用')!
    expect(dialog.text()).toContain('P700016')
    expect(dialog.text()).toContain('V01')
  })

  it('filters drawing rows and keeps embedded hierarchy without demanding independent registration', async () => {
    const w = await open()
    await w.get('input[placeholder="搜索图号或名称"]').setValue('drawing')
    expect(w.get('.standard-structure__drawing-tree').text()).toContain('drawing')
    expect(w.get('.standard-structure__drawing-tree').text()).not.toContain('plate')
    await w.get('input[placeholder="搜索图号或名称"]').setValue('')
    const drawingTree = w.findAllComponents({ name: 'ElTree' })[1]!
    drawingTree.vm.$emit('node-click', { ...part('embedded'), structureId: 'structure-1', revisionId: 'revision-1', version: 1, embedded: true, previewFileKey: null })
    await flushPromises()
    expect(w.get('.standard-structure__preview').text()).toContain('嵌入组件随父装配完整保存')
    expect(api.readStructurePreview).not.toHaveBeenCalled()
  })

  it('shows exactly standard and nonstandard BOM and retains request identity when a reference is retried', async () => {
    const w = await open()
    await button('BOM').trigger('click')
    expect(w.get('[aria-label="标准结构BOM类型"]').text()).toContain('标准件BOM')
    expect(w.get('[aria-label="标准结构BOM类型"]').text()).toContain('非标件BOM')
    expect(w.text()).not.toContain('电气件BOM')
    expect(w.get('[aria-label="标准结构BOM"]').text()).toContain('气缸')
    await button('非标件BOM').trigger('click')
    expect(w.get('[aria-label="标准结构BOM"]').text()).toContain('底板')
    await button('引用到项目').trigger('click')
    await flushPromises()
    const dialog = w.findAllComponents({ name: 'ElDialog' }).find(c => c.props('title') === '引用标准结构BOM到项目')!
    dialog.findComponent({ name: 'ElSelect' }).vm.$emit('update:modelValue', 'project')
    dialog.findComponent({ name: 'ElInputNumber' }).vm.$emit('update:modelValue', 3)
    await flushPromises()
    api.referenceStandardStructure.mockRejectedValueOnce(new Error('网络中断，请核对后重试。'))
    const confirm = () => Array.from(document.body.querySelectorAll<HTMLButtonElement>('button')).find(b => b.textContent?.trim() === '确认引用')!
    confirm().click(); await flushPromises()
    const first = api.referenceStandardStructure.mock.calls[0]![0]
    expect(first).toMatchObject({ revisionId: 'revision-1', projectId: 'project', kind: 'NonStandard', quantity: 3 })
    expect(first.requestId).toMatch(/^[0-9a-f-]{36}$/)
    confirm().click(); await flushPromises()
    expect(api.referenceStandardStructure.mock.calls[1]![0].requestId).toBe(first.requestId)
  })

  it('gates category maintenance and project reference controls with server and project permissions', async () => {
    api.readStandardStructures.mockResolvedValue({ ...data(), canManage: false, canArchive: false })
    const w = await open(false)
    expect(w.text()).not.toContain('分类维护')
    await button('BOM').trigger('click')
    expect(w.text()).not.toContain('引用到项目')
  })

  it('reports load failures in Chinese and can recover using the retry button', async () => {
    api.readStandardStructures.mockRejectedValueOnce(new Error('Unexpected token < in JSON'))
    const w = await open()
    expect(w.get('[role="alert"]').text()).toContain('页面处理失败，请刷新页面后重试')
    expect(w.get('[role="alert"]').text()).not.toContain('Unexpected token')
    await button('重新加载').trigger('click'); await flushPromises()
    expect(w.find('[role="alert"]').exists()).toBe(false)
    expect(w.get('.standard-structure__drawing-tree').text()).toContain('BZ-001-V01')
  })

  it('moves a complete root and its BOM to the selected category while preserving its number and version', async () => {
    const moved = data()
    moved.catalog.structures[0]!.categoryId = 'other'
    moved.catalog.rowVersion = 4
    api.classifyStandardStructures.mockResolvedValue(moved.catalog)
    const w = await open()
    await button('调整分类').trigger('click'); await flushPromises()
    const dialog = w.findAllComponents({ name: 'ElDialog' }).find(c => c.props('title') === '调整标准库分类')!
    dialog.findComponent({ name: 'ElTreeSelect' }).vm.$emit('update:modelValue', 'other')
    await flushPromises()
    Array.from(document.body.querySelectorAll<HTMLButtonElement>('button')).find(b => b.textContent?.trim() === '确认调整')!.click()
    await flushPromises()
    expect(api.classifyStandardStructures).toHaveBeenCalledWith(['structure-1'], 'other', 3, 'token')
    expect(w.get('.standard-structure__breadcrumb').text()).toContain('定位夹紧')
    expect(w.get('.standard-structure__properties').text()).toContain('BZ-001-V01')
    await button('BOM').trigger('click')
    expect(w.get('[aria-label="标准结构BOM"]').text()).toContain('气缸')
  })
})
