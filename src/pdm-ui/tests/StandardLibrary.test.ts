import { flushPromises, mount } from '@vue/test-utils'
import ElementPlus from 'element-plus'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import StandardLibrary from '../src/components/StandardLibrary.vue'

const api = vi.hoisted(() => ({
  listStandardLibraryCategories: vi.fn(),
  listStandardLibraryMaterials: vi.fn(),
  listMaterialCategories: vi.fn(),
  listMaterials: vi.fn(),
  materialAttachmentObjectUrl: vi.fn(),
  addStandardLibraryMaterials: vi.fn(),
  saveStandardLibraryCategory: vi.fn(),
  deleteStandardLibraryCategory: vi.fn(),
  setStandardLibraryRecommendation: vi.fn(),
  listMaterialAttachments: vi.fn(),
  downloadMaterialAttachment: vi.fn(),
  changeApprovedMaterial: vi.fn(),
  updateMaterial: vi.fn(),
  uploadMaterialAttachment: vi.fn(),
  setMaterialCover: vi.fn(),
}))
vi.mock('../src/api', () => api)

const material = {
  id: 'material-1', materialCode: '01020013531', name: '真空阀', kind: 'Standard', supplyMode: 'Purchase', unitCode: '001',
  specification: '26524-KA21-BWT1', brand: 'VAT', remark: 'KF16，常闭', selectionAdvice: '优先用于洁净真空系统', isRecommended: true,
  categoryCode: '0102', u9CategoryCode: '0102', approvalStatus: 'Approved', syncStatus: 'Succeeded', u9SyncConfirmed: true,
  createdBy: 'admin', createdAt: '2026-08-25T00:00:00Z', updatedBy: 'admin', updatedAt: '2026-08-25T00:00:00Z', rowVersion: 2,
  isArchived: false, sourceSystem: 'Pdm', masterOwner: 'Pdm', referenceCount: 4, model3DAttachmentCount: 1, documentAttachmentCount: 1,
}
const secondMaterial = { ...material, id: 'material-2', materialCode: '010200075183', name: '手动真空阀', specification: '26424-KA01-0001', brand: 'SMC', isRecommended: false }

describe('StandardLibrary', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    api.listStandardLibraryCategories.mockResolvedValue([
      { id: 'root', name: '内部标准件', parentId: null, sortOrder: 1, isActive: true, createdBy: 'admin', createdAt: '', updatedBy: 'admin', updatedAt: '', rowVersion: 1 },
      { id: 'child', name: '真空元器件', parentId: 'root', sortOrder: 1, isActive: true, createdBy: 'admin', createdAt: '', updatedBy: 'admin', updatedAt: '', rowVersion: 1 },
    ])
    api.listMaterialCategories.mockResolvedValue([])
    api.listMaterials.mockResolvedValue([material, secondMaterial])
    api.listStandardLibraryMaterials.mockResolvedValue({ items: [{ material, categories: [{ id: 'child', name: '真空元器件', parentId: 'root', sortOrder: 1, isActive: true, createdBy: 'admin', createdAt: '', updatedBy: 'admin', updatedAt: '', rowVersion: 1 }], coverImage: null }], total: 1, page: 1, pageSize: 50 })
  })

  it('renders the category tree and engineer selection fields without creating selection records', async () => {
    const wrapper = mount(StandardLibrary, { props: { token: 'token', canManage: true, canEdit: true }, global: { plugins: [ElementPlus] } })
    await flushPromises()

    expect(wrapper.text()).toContain('内部标准件')
    expect(wrapper.text()).toContain('真空阀')
    expect(wrapper.text()).toContain('26524-KA21-BWT1')
    expect(wrapper.text()).toContain('优先用于洁净真空系统')
    expect(wrapper.text()).toContain('引用')
    expect(wrapper.text()).not.toContain('引用次数')
    expect(wrapper.text()).toContain('已一致')
    const mainTable = wrapper.findAllComponents({ name: 'ElTable' }).find(table => (table.props('data') as unknown[])?.length === 1)!
    const mainLabels = mainTable.findAllComponents({ name: 'ElTableColumn' }).map(column => column.props('label')).filter(Boolean)
    expect(mainLabels).not.toContain('标准分类')
    expect(mainLabels).not.toContain('操作')
    expect(mainLabels).toContain('引用')
    expect(mainTable.classes()).toContain('material-list-table')
    expect(wrapper.findAllComponents({ name: 'ElTree' })[0].props('defaultExpandAll')).toBe(false)
  })

  it('keeps the compact toolbar order and category tree data stable while selecting a category', async () => {
    const wrapper = mount(StandardLibrary, { props: { token: 'token', canManage: true, canEdit: true }, global: { plugins: [ElementPlus] } })
    await flushPromises()

    const toolbar = wrapper.find('.standard-library__toolbar')
    expect(toolbar.findAll('button').map(button => button.text().trim())).toEqual(['新增物料', '刷新', '搜索', '维护'])
    expect(wrapper.find('.standard-library__header').exists()).toBe(false)
    expect(wrapper.find('.scope-label').exists()).toBe(false)

    const categoryTree = wrapper.findAllComponents({ name: 'ElTree' })[0]
    categoryTree.vm.$emit('node-click', { id: 'root' })
    await flushPromises()
    expect(api.listStandardLibraryCategories).toHaveBeenCalledTimes(1)
    expect(api.listStandardLibraryMaterials).toHaveBeenCalledTimes(2)
  })

  it('keeps the material entry visible but disabled for view-only users', async () => {
    const wrapper = mount(StandardLibrary, { props: { token: 'token', canManage: false, canEdit: false }, global: { plugins: [ElementPlus] } })
    await flushPromises()

    const addButton = wrapper.findAll('button').find(button => button.text() === '新增物料')!
    expect((addButton.element as HTMLButtonElement).disabled).toBe(true)
    expect((wrapper.findAll('button').find(button => button.text() === '维护')!.element as HTMLButtonElement).disabled).toBe(true)
    await addButton.trigger('click')
    expect(api.listMaterials).not.toHaveBeenCalled()
    expect(wrapper.findAll('button').some(button => button.text() === '分类维护')).toBe(false)
  })

  it('moves maintenance above the table and opens PLM-only batch editing for multiple selections', async () => {
    const wrapper = mount(StandardLibrary, { attachTo: document.body, props: { token: 'token', canManage: true, canEdit: true }, global: { plugins: [ElementPlus] } })
    await flushPromises()

    const mainTable = wrapper.findAllComponents({ name: 'ElTable' }).find(table => (table.props('data') as unknown[])?.length === 1)!
    mainTable.vm.$emit('selection-change', [
      { material, categories: [], coverImage: null },
      { material: secondMaterial, categories: [], coverImage: null },
    ])
    await flushPromises()
    const maintainButton = wrapper.findAll('button').find(button => button.text() === '维护')!
    expect((maintainButton.element as HTMLButtonElement).disabled).toBe(false)
    await maintainButton.trigger('click')
    await flushPromises()

    expect(document.body.textContent).toContain('批量维护标准物料')
    expect(document.body.textContent).toContain('已选择 2 个料品')
    expect(document.body.textContent).toContain('仅批量修改PLM专属字段，不生成U9C任务')
    const batchSave = wrapper.findAll('button').find(button => button.text() === '批量保存')!
    expect((batchSave.element as HTMLButtonElement).disabled).toBe(true)
    wrapper.unmount()
  })

  it('still opens single-material maintenance when the attachment list cannot be loaded', async () => {
    api.listMaterialAttachments.mockRejectedValueOnce(new Error('附件服务不可用'))
    const wrapper = mount(StandardLibrary, { props: { token: 'token', canManage: true, canEdit: true }, global: { plugins: [ElementPlus] } })
    await flushPromises()

    const mainTable = wrapper.findAllComponents({ name: 'ElTable' }).find(table => (table.props('data') as unknown[])?.length === 1)!
    mainTable.vm.$emit('selection-change', [{ material, categories: [], coverImage: null }])
    await flushPromises()
    await wrapper.findAll('button').find(button => button.text() === '维护')!.trigger('click')
    await flushPromises()

    const editor = wrapper.findComponent({ name: 'MaterialEditorDialog' })
    expect(editor.props('modelValue')).toBe(true)
    expect(editor.props('attachments')).toEqual([])
  })

  it('shows available materials on the left and mirrors the batch selection on the right', async () => {
    const wrapper = mount(StandardLibrary, { attachTo: document.body, props: { token: 'token', canManage: true, canEdit: true }, global: { plugins: [ElementPlus] } })
    await flushPromises()

    await wrapper.findAll('button').find(button => button.text() === '新增物料')!.trigger('click')
    await flushPromises()

    expect(document.body.textContent).toContain('可选料品')
    expect(document.body.textContent).toContain('已选料品')
    expect(document.body.textContent).toContain('批量加入指定分类')
    const categorySelectionTree = wrapper.findAllComponents({ name: 'ElTree' }).find(tree => tree.props('showCheckbox'))
    expect(categorySelectionTree).toBeTruthy()
    expect(categorySelectionTree!.props('checkStrictly')).toBe(true)
    expect((categorySelectionTree!.props('data') as Array<{ children: unknown[] }>)[0].children).toHaveLength(1)
    const panes = document.body.querySelectorAll('.add-material-layout > .add-material-pane')
    expect(panes).toHaveLength(3)
    expect(panes[2].classList.contains('add-material-target')).toBe(true)
    const eligibleTable = wrapper.findAllComponents({ name: 'ElTable' }).find(table => (table.props('data') as unknown[])?.length === 2)
    expect(eligibleTable).toBeTruthy()
    const selectedTable = wrapper.findAllComponents({ name: 'ElTable' }).find(table => (table.props('data') as unknown[])?.length === 0)
    expect(eligibleTable!.props('height')).toBe('100%')
    expect(selectedTable?.props('height')).toBe('100%')

    const search = wrapper.find('input[placeholder="搜索料号/名称/型号/品牌"]')
    await search.setValue('075183')
    await flushPromises()
    expect(eligibleTable!.props('data')).toEqual([secondMaterial])
    await search.setValue('')
    await flushPromises()

    const brandFilter = wrapper.findAllComponents({ name: 'ElSelect' }).find(select => select.props('placeholder') === '筛选品牌')
    expect(brandFilter).toBeTruthy()
    expect(brandFilter!.props('filterable')).toBe(true)
    brandFilter!.vm.$emit('update:modelValue', 'SMC')
    await flushPromises()
    expect(eligibleTable!.props('data')).toEqual([secondMaterial])
    await search.setValue('13531')
    await flushPromises()
    expect(eligibleTable!.props('data')).toEqual([])
    brandFilter!.vm.$emit('update:modelValue', '')
    await search.setValue('')
    await flushPromises()

    const categorySearch = wrapper.find('input[placeholder="筛选分类名称"]')
    expect(categorySearch.exists()).toBe(true)
    const categoryFilter = categorySelectionTree!.props('filterNodeMethod') as (value: string, data: { name: string }) => boolean
    expect(categoryFilter('真空', { name: '真空元器件' })).toBe(true)
    expect(categoryFilter('真空', { name: '内部标准件' })).toBe(false)
    await categorySearch.setValue('真空')
    await flushPromises()

    eligibleTable!.vm.$emit('selection-change', [material])
    await flushPromises()
    const selectedPane = document.body.querySelector('.add-material-pane--selected')
    expect(selectedPane?.textContent).toContain('1 项')
    expect(selectedPane?.textContent).toContain('01020013531')
    expect(api.addStandardLibraryMaterials).not.toHaveBeenCalled()
    wrapper.unmount()
  })

  it('keeps all classification actions in one manager and hides the manual sort number', async () => {
    const wrapper = mount(StandardLibrary, { attachTo: document.body, props: { token: 'token', canManage: true, canEdit: true }, global: { plugins: [ElementPlus] } })
    await flushPromises()

    await wrapper.findAll('button').find(button => button.text() === '分类维护')!.trigger('click')
    await flushPromises()

    expect(document.body.textContent).toContain('标准分类维护')
    expect(document.body.textContent).toContain('新增顶级分类')
    expect(document.body.textContent).toContain('添加子级')
    expect(document.body.textContent).toContain('上移')
    expect(document.body.textContent).toContain('下移')
    expect(document.body.textContent).toContain('排序号由系统自动维护')
    expect(document.body.querySelectorAll('.category-manager__row.is-child')).toHaveLength(1)
    expect(document.body.querySelector('.category-manager__row.is-child')?.textContent).toContain('真空元器件')
    expect(wrapper.findComponent({ name: 'ElInputNumber' }).exists()).toBe(false)
    wrapper.unmount()
  })
})
