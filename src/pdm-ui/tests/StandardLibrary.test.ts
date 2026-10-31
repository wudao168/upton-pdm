import { flushPromises, mount } from '@vue/test-utils'
import ElementPlus, { ElMessageBox } from 'element-plus'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import StandardLibrary from '../src/components/StandardLibrary.vue'
import type { PdmMaterial } from '../src/types'

const api = vi.hoisted(() => ({
  listStandardLibraryCategories: vi.fn(),
  listStandardLibraryMaterials: vi.fn(),
  listStandardLibraryKits: vi.fn(),
  moveEngineeringKitCategory: vi.fn(),
  setEngineeringKitActive: vi.fn(),
  removeStandardLibraryMaterial: vi.fn(),
  listMaterialCategories: vi.fn(),
  listMaterials: vi.fn(),
  materialAttachmentObjectUrl: vi.fn(),
  addStandardLibraryMaterials: vi.fn(),
  archiveMaterial: vi.fn(),
  reactivateMaterial: vi.fn(),
  importStandardLibraryMaterials: vi.fn(),
  readStandardLibraryImportFile: vi.fn(),
  saveStandardLibraryCategory: vi.fn(),
  deleteStandardLibraryCategory: vi.fn(),
  setStandardLibraryRecommendation: vi.fn(),
  listMaterialAttachments: vi.fn(),
  downloadMaterialAttachment: vi.fn(),
  changeApprovedMaterial: vi.fn(),
  updateMaterial: vi.fn(),
  uploadMaterialAttachment: vi.fn(),
  setMaterialCover: vi.fn(),
  replaceMaterialImages: vi.fn(),
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

let assetPreviewWrapper: ReturnType<typeof mount> | undefined
describe('StandardLibrary', () => {
  afterEach(() => { assetPreviewWrapper?.unmount(); assetPreviewWrapper = undefined })
  beforeEach(() => {
    vi.clearAllMocks()
    vi.spyOn(ElMessageBox, 'confirm').mockResolvedValue('confirm' as never)
    api.removeStandardLibraryMaterial.mockReset().mockResolvedValue(undefined)
    api.archiveMaterial.mockReset()
    api.reactivateMaterial.mockReset()
    api.listStandardLibraryCategories.mockResolvedValue([
      { id: 'uncategorized', name: '未分类', parentId: null, sortOrder: -2147483648, isActive: true, createdBy: 'system', createdAt: '', updatedBy: 'system', updatedAt: '', rowVersion: 1 },
      { id: 'root', name: '内部标准件', code: 'UTJ-01', parentId: null, sortOrder: 1, isActive: true, createdBy: 'admin', createdAt: '', updatedBy: 'admin', updatedAt: '', rowVersion: 1 },
      { id: 'child', name: '真空元器件', parentId: 'root', sortOrder: 1, isActive: true, createdBy: 'admin', createdAt: '', updatedBy: 'admin', updatedAt: '', rowVersion: 1 },
    ])
    api.listStandardLibraryKits.mockResolvedValue([])
    api.listMaterialCategories.mockResolvedValue([])
    api.listMaterials.mockResolvedValue([material, secondMaterial])
    api.listStandardLibraryMaterials.mockResolvedValue({ items: [{ material, categories: [{ id: 'child', name: '真空元器件', parentId: 'root', sortOrder: 1, isActive: true, createdBy: 'admin', createdAt: '', updatedBy: 'admin', updatedAt: '', rowVersion: 1 }], coverImage: null }], total: 1, page: 1, pageSize: 50 })
  })

  it('shows only nonempty asset icons and previews PDF while retaining downloads for other formats', async () => {
    api.listStandardLibraryMaterials.mockResolvedValue({ items: [{ material, categories: [], coverImage: null }, { material: { ...secondMaterial, model3DAttachmentCount: 0, documentAttachmentCount: 0 }, categories: [], coverImage: null }], total: 2, page: 1, pageSize: 50 })
    const pdf = { id: 'pdf', materialId: material.id, originalFileName: '产品资料.PDF', kind: 'Document', uploadedBy: 'admin' }
    const doc = { ...pdf, id: 'doc', originalFileName: '说明.docx' }
    api.listMaterialAttachments.mockResolvedValue([pdf, doc])
    api.materialAttachmentObjectUrl.mockResolvedValue('blob:material-pdf')
    const revoke = vi.spyOn(URL, 'revokeObjectURL')
    const wrapper = assetPreviewWrapper = mount(StandardLibrary, { attachTo: document.body, props: { token: 'token', canManage: true, canEdit: true }, global: { plugins: [ElementPlus], stubs: { PdfDrawingViewer: { props: ['url'], template: '<div class="test-pdf-preview">{{ url }}</div>' } } } })
    await flushPromises()
    const icons = wrapper.findAll('.material-list-table button[aria-label]')
    expect(icons.filter(icon => /份资料/.test(icon.attributes('aria-label') ?? ''))).toHaveLength(1)
    expect(wrapper.get(`button[aria-label="${material.materialCode} · 3D模型版本"]`).text()).toBe('未启用')
    expect(wrapper.get(`button[aria-label="${material.materialCode} · 2D图纸版本"]`).text()).toBe('维护')
    const documentIcon = wrapper.get(`button[aria-label="${material.materialCode} · 1 份资料"]`)
    expect(documentIcon.text()).toBe('')
    expect(documentIcon.find('svg').exists()).toBe(true)
    expect(documentIcon.attributes('title')).toContain('1 份资料')
    await documentIcon.trigger('click'); await flushPromises()
    const list = wrapper.findAllComponents({ name: 'ElDialog' }).find(dialog => dialog.props('title') === `${material.materialCode} · 资料`)!
    const previewButtons = list.findAll('button').filter(button => button.text() === '预览')
    expect(previewButtons[1]!.attributes('disabled')).toBeDefined()
    await previewButtons[0]!.trigger('click'); await flushPromises()
    expect(api.materialAttachmentObjectUrl).toHaveBeenCalledWith(material.id, 'pdf', 'token')
    expect(document.body.querySelector('.test-pdf-preview')?.textContent).toBe('blob:material-pdf')
    expect(api.downloadMaterialAttachment).not.toHaveBeenCalled()
    await list.findAll('button').filter(button => button.text() === '下载')[1]!.trigger('click'); await flushPromises()
    expect(api.downloadMaterialAttachment).toHaveBeenCalledWith(material.id, doc, 'token')
    wrapper.unmount()
    expect(revoke).toHaveBeenCalledWith('blob:material-pdf')
  })

  it('reports preview failure in Chinese and releases late responses after closing', async () => {
    const file = { id: 'image', materialId: material.id, originalFileName: '产品图.png', kind: 'Document', uploadedBy: 'admin' }
    api.listMaterialAttachments.mockResolvedValue([file])
    api.materialAttachmentObjectUrl.mockRejectedValueOnce(new Error('附件服务暂不可用，请稍后重试。'))
    const revoke = vi.spyOn(URL, 'revokeObjectURL')
    const wrapper = assetPreviewWrapper = mount(StandardLibrary, { attachTo: document.body, props: { token: 'token', canManage: true, canEdit: true }, global: { plugins: [ElementPlus] } })
    await flushPromises()
    await wrapper.get(`button[aria-label="${material.materialCode} · 1 份资料"]`).trigger('click'); await flushPromises()
    await wrapper.findAll('button').find(button => button.text() === '预览')!.trigger('click'); await flushPromises()
    expect(document.body.querySelector('[role="alert"]')?.textContent).toContain('附件服务暂不可用')
    let finish!: (url: string) => void
    api.materialAttachmentObjectUrl.mockImplementationOnce(() => new Promise<string>(resolve => { finish = resolve }))
    Array.from(document.body.querySelectorAll<HTMLButtonElement>('button')).find(button => button.textContent === '重新加载')!.click(); await flushPromises()
    const preview = wrapper.findAllComponents({ name: 'ElDialog' }).find(dialog => dialog.props('title') === file.originalFileName)!
    preview.vm.$emit('close'); await flushPromises()
    finish('blob:late-image'); await flushPromises()
    expect(revoke).toHaveBeenCalledWith('blob:late-image')
    expect(wrapper.find('img[src="blob:late-image"]').exists()).toBe(false)
    wrapper.unmount()
  })

  it('shows kits in the same table and routes their maintenance away from the material master editor', async () => {
    const kit = { id:'kit-1',code:'UKIT-000001',name:'支架组件',model:'R722.S2.12-00',brand:'UPTON',description:'套件说明',
      revisions:[],createdBy:'admin',createdAt:'',updatedBy:'admin',updatedAt:'',rowVersion:2,referenceCount:3,standardLibraryCategoryId:'child' }
    const kitRow = {material:{...material,id:kit.id,materialCode:kit.code,name:kit.name,specification:kit.model,brand:kit.brand,remark:kit.description,isRecommended:false,referenceCount:3},categories:[],coverImage:null,kit}
    api.listStandardLibraryMaterials.mockResolvedValue({items:[{material,categories:[],coverImage:null},kitRow],total:2,page:1,pageSize:50})
    api.listMaterialAttachments.mockResolvedValue([])
    const wrapper = mount(StandardLibrary,{props:{token:'token',canManage:true,canEdit:true},global:{plugins:[ElementPlus]}});await flushPromises()
    expect(wrapper.find('.standard-library-kits').exists()).toBe(false)
    const table=wrapper.findAllComponents({name:'ElTable'}).find(x=>x.classes().includes('material-list-table'))!
    expect(table.props('data')).toHaveLength(2)
    expect(wrapper.text()).toContain(kit.model);expect(wrapper.text()).toContain(kit.description)
    expect(table.findAll('button').some(button => button.text() === kit.name)).toBe(false)
    expect(api.listStandardLibraryKits).not.toHaveBeenCalled()
    table.vm.$emit('selection-change',[kitRow]);await flushPromises()
    await wrapper.findAll('button').find(x=>x.text()==='维护')!.trigger('click');await flushPromises()
    expect(wrapper.findComponent({name:'EngineeringKitAssets'}).exists()).toBe(true)
    expect(wrapper.findAllComponents({name:'MaterialEditorDialog'}).find(dialog => !dialog.props('kitMode'))!.props('modelValue')).toBe(false)
    expect(api.listMaterialAttachments).toHaveBeenCalledWith(kit.id,'token')
    api.moveEngineeringKitCategory.mockResolvedValue({...kit,rowVersion:3})
    const assets=wrapper.findComponent({name:'EngineeringKitAssets'})
    assets.vm.$emit('category');await flushPromises()
    await wrapper.findAll('button').find(x=>x.text()==='保存分类')!.trigger('click');await flushPromises()
    expect(assets.props('kit').rowVersion).toBe(3)
    assets.vm.$emit('category');await flushPromises()
    await wrapper.findAll('button').find(x=>x.text()==='保存分类')!.trigger('click');await flushPromises()
    expect(api.moveEngineeringKitCategory).toHaveBeenLastCalledWith(kit.id,'child',3,'token')
    const stoppedKit = { ...kit, rowVersion: 4, isActive: false }
    api.listStandardLibraryMaterials.mockResolvedValue({items:[],total:0,page:1,pageSize:50})
    assets.vm.$emit('activityChanged',stoppedKit);await flushPromises()
    expect(assets.props('kit').isActive).toBe(false)
    expect(assets.props('kit').rowVersion).toBe(4)
    expect(wrapper.find('.material-list-table').text()).not.toContain(kit.code)
    api.listStandardLibraryMaterials.mockResolvedValue({items:[{...kitRow,kit:stoppedKit}],total:1,page:1,pageSize:50})
    const inactiveCheckbox=wrapper.findAllComponents({name:'ElCheckbox'}).find(item=>item.text()==='已停用')!
    inactiveCheckbox.vm.$emit('update:modelValue',true);inactiveCheckbox.vm.$emit('change',true);await flushPromises()
    expect(api.listStandardLibraryMaterials).toHaveBeenLastCalledWith('token',expect.objectContaining({includeInactive:true,page:1}))
    expect(wrapper.find('.material-list-table').text()).toContain('已停用')
    table.vm.$emit('selection-change',[kitRow,{material,categories:[],coverImage:null}]);await flushPromises()
    expect(wrapper.get('.maintain-material-button').attributes('disabled')).toBeDefined()
    expect(api.changeApprovedMaterial).not.toHaveBeenCalled();wrapper.unmount()
  })

  it('普通标准物料维护页支持停用恢复，停用时只读并保留原料号与数据版本', async () => {
    const initial = { material, categories: [{ id: 'child' }], coverImage: null }
    api.listStandardLibraryMaterials.mockResolvedValue({items:[initial],total:1,page:1,pageSize:50})
    api.listMaterialAttachments.mockResolvedValue([])
    const wrapper=mount(StandardLibrary,{props:{token:'token',canManage:true,canEdit:true},global:{plugins:[ElementPlus]}})
    await flushPromises()
    expect(wrapper.findAllComponents({name:'ElCheckbox'}).map(item=>item.text()).slice(0,2)).toEqual(['仅看推荐','已停用'])
    expect(api.listStandardLibraryMaterials).toHaveBeenLastCalledWith('token',expect.objectContaining({includeInactive:false}))
    wrapper.getComponent({name:'ElTable'}).vm.$emit('selection-change',[initial]);await flushPromises()
    await wrapper.get('.maintain-material-button').trigger('click');await flushPromises()
    const editor=wrapper.getComponent({name:'MaterialEditorDialog'})
    vi.mocked(ElMessageBox.confirm).mockRejectedValueOnce('cancel')
    await wrapper.get('.standard-material-activity').trigger('click');await flushPromises()
    expect(api.archiveMaterial).not.toHaveBeenCalled()
    const archived={...material,isArchived:true,rowVersion:3}
    api.archiveMaterial.mockResolvedValue(archived)
    api.listStandardLibraryMaterials.mockResolvedValue({items:[],total:0,page:1,pageSize:50})
    await wrapper.get('.standard-material-activity').trigger('click');await flushPromises()
    expect(api.archiveMaterial).toHaveBeenCalledExactlyOnceWith(material.id,material.rowVersion,'token')
    expect(editor.props('canEdit')).toBe(false)
    expect(editor.props('form').expectedRowVersion).toBe(3)
    expect(wrapper.get('.standard-material-activity').text()).toBe('恢复启用')
    expect(wrapper.find('.material-list-table').text()).not.toContain(material.materialCode)
    api.reactivateMaterial.mockResolvedValue({...material,rowVersion:4})
    api.listStandardLibraryMaterials.mockResolvedValue({items:[initial],total:1,page:1,pageSize:50})
    await wrapper.get('.standard-material-activity').trigger('click');await flushPromises()
    expect(api.reactivateMaterial).toHaveBeenCalledExactlyOnceWith(material.id,3,'token')
    expect(editor.props('canEdit')).toBe(true)
    expect(editor.props('form').expectedRowVersion).toBe(4)
    expect(wrapper.get('.standard-material-activity').text()).toBe('停用')
    expect(api.updateMaterial).not.toHaveBeenCalled();expect(api.changeApprovedMaterial).not.toHaveBeenCalled();wrapper.unmount()
  })

  it('removes selected real materials from every returned membership in the all-categories view', async () => {
    const selected = [
      { material, categories: [{ id: 'child' }, { id: 'other' }], coverImage: null },
      { material: secondMaterial, categories: [{ id: 'root' }], coverImage: null },
    ]
    const wrapper = mount(StandardLibrary, { props: { token: 'token', canManage: true, canEdit: true }, global: { plugins: [ElementPlus] } })
    await flushPromises()
    expect(wrapper.get('.remove-material-button').attributes('disabled')).toBeDefined()
    wrapper.getComponent({ name: 'ElTable' }).vm.$emit('selection-change', selected)
    await flushPromises()
    await wrapper.get('.remove-material-button').trigger('click'); await flushPromises()
    expect(ElMessageBox.confirm).toHaveBeenCalledWith(expect.stringContaining('全部收录分类'), '移出标准库', expect.any(Object))
    expect(ElMessageBox.confirm).toHaveBeenCalledWith(expect.stringContaining('料品主档、附件和已有BOM引用均保留'), '移出标准库', expect.any(Object))
    expect(api.removeStandardLibraryMaterial.mock.calls).toEqual([['child', material.id, 'token'], ['other', material.id, 'token'], ['root', secondMaterial.id, 'token']])
    expect(api.listStandardLibraryMaterials).toHaveBeenCalledTimes(2)
    expect(wrapper.get('.remove-material-button').attributes('disabled')).toBeDefined()
    expect(api.updateMaterial).not.toHaveBeenCalled()
    wrapper.unmount()
  })

  it('only removes memberships returned within the selected category scope and does nothing on cancel', async () => {
    const wrapper = mount(StandardLibrary, { props: { token: 'token', canManage: true, canEdit: true }, global: { plugins: [ElementPlus] } })
    await flushPromises()
    wrapper.findAllComponents({ name: 'ElTree' })[0].vm.$emit('node-click', { id: 'root' })
    await flushPromises()
    wrapper.getComponent({ name: 'ElTable' }).vm.$emit('selection-change', [{ material, categories: [{ id: 'child' }], coverImage: null }])
    await flushPromises()
    vi.mocked(ElMessageBox.confirm).mockRejectedValueOnce('cancel')
    await wrapper.get('.remove-material-button').trigger('click'); await flushPromises()
    expect(api.removeStandardLibraryMaterial).not.toHaveBeenCalled()
    await wrapper.get('.remove-material-button').trigger('click'); await flushPromises()
    expect(ElMessageBox.confirm).toHaveBeenLastCalledWith(expect.stringContaining('“内部标准件”及其下级分类'), '移出标准库', expect.any(Object))
    expect(api.removeStandardLibraryMaterial).toHaveBeenCalledExactlyOnceWith('child', material.id, 'token')
    wrapper.unmount()
  })

  it('prevents kit or unauthorized removal and refreshes after a partial failure without reporting success', async () => {
    const wrapper = mount(StandardLibrary, { props: { token: 'token', canManage: false, canEdit: true }, global: { plugins: [ElementPlus] } })
    await flushPromises()
    const table = wrapper.getComponent({ name: 'ElTable' })
    table.vm.$emit('selection-change', [{ material, categories: [{ id: 'child' }], coverImage: null }]); await flushPromises()
    expect(wrapper.get('.remove-material-button').attributes('disabled')).toBeDefined()
    await wrapper.setProps({ canManage: true })
    table.vm.$emit('selection-change', [{ material, categories: [{ id: 'child' }], kit: { id: 'kit' } }]); await flushPromises()
    expect(wrapper.get('.remove-material-button').attributes('title')).toContain('套件由发布自动收录')
    expect(wrapper.get('.remove-material-button').attributes('disabled')).toBeDefined()
    table.vm.$emit('selection-change', [{ material, categories: [{ id: 'child' }, { id: 'other' }] }]); await flushPromises()
    api.removeStandardLibraryMaterial.mockResolvedValueOnce(undefined).mockRejectedValueOnce(new Error('服务器暂时不可用，请重试'))
    await wrapper.get('.remove-material-button').trigger('click'); await flushPromises()
    expect(api.removeStandardLibraryMaterial).toHaveBeenCalledTimes(2)
    expect(api.listStandardLibraryMaterials).toHaveBeenCalledTimes(2)
    expect(wrapper.get('.remove-material-button').attributes('disabled')).toBeDefined()
    wrapper.unmount()
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
    expect(wrapper.get('.material-sync-consistent').classes()).not.toContain('el-tag')
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
    expect(toolbar.findAll('button').map(button => button.text().trim())).toEqual(['新增物料', '导入', '维护', '移除', '刷新', '搜索'])
    expect(wrapper.find('.standard-library__tree').findAll('button').map(button => button.text().trim())).not.toContain('配套物料模板')
    expect(wrapper.find('.standard-library__header').exists()).toBe(false)
    expect(wrapper.find('.scope-label').exists()).toBe(false)

    const categoryTree = wrapper.findAllComponents({ name: 'ElTree' })[0]
    categoryTree.vm.$emit('node-click', { id: 'root' })
    await flushPromises()
    expect(api.listStandardLibraryCategories).toHaveBeenCalledTimes(1)
    expect(api.listStandardLibraryMaterials).toHaveBeenCalledTimes(2)
    await wrapper.get('.standard-library__all').trigger('click')
    await flushPromises()
    expect(api.listStandardLibraryMaterials).toHaveBeenLastCalledWith('token', expect.objectContaining({categoryId:undefined,page:1}))
    expect(api.listStandardLibraryCategories).toHaveBeenCalledTimes(1)
  })

  it('keeps the material entry visible but disabled for view-only users', async () => {
    const wrapper = mount(StandardLibrary, { props: { token: 'token', canManage: false, canEdit: false }, global: { plugins: [ElementPlus] } })
    await flushPromises()

    const addButton = wrapper.findAll('button').find(button => button.text() === '新增物料')!
    expect((addButton.element as HTMLButtonElement).disabled).toBe(true)
    expect((wrapper.findAll('button').find(button => button.text() === '导入')!.element as HTMLButtonElement).disabled).toBe(true)
    expect((wrapper.findAll('button').find(button => button.text() === '维护')!.element as HTMLButtonElement).disabled).toBe(true)
    await addButton.trigger('click')
    expect(api.listMaterials).not.toHaveBeenCalled()
    expect(wrapper.findAll('button').some(button => button.text() === '分类维护')).toBe(false)
  })

  it('opens the original-image gallery from the table thumbnail', async () => {
    const cover = { id: 'cover', materialId: material.id, originalFileName: '封面.png', kind: 'CoverImage' }
    api.listStandardLibraryMaterials.mockResolvedValueOnce({ items: [{ material, categories: [], coverImage: cover }], total: 1, page: 1, pageSize: 50 })
    api.listMaterialAttachments.mockResolvedValueOnce([cover, { ...cover, id: 'second' }])
    api.materialAttachmentObjectUrl.mockResolvedValueOnce('blob:thumbnail').mockResolvedValueOnce('blob:original')
    const wrapper = assetPreviewWrapper = mount(StandardLibrary, { attachTo: document.body, props: { token: 'token', canManage: true, canEdit: true }, global: { plugins: [ElementPlus] } })
    await flushPromises()
    expect(api.materialAttachmentObjectUrl).toHaveBeenCalledWith(material.id, 'cover', 'token', 120)
    await wrapper.get(`button[aria-label="${material.materialCode} · 查看图片"]`).trigger('click'); await flushPromises()
    expect(api.materialAttachmentObjectUrl).toHaveBeenLastCalledWith(material.id, 'cover', 'token')
    expect(document.querySelector('.material-image-stage img')?.getAttribute('src')).toBe('blob:original')
    expect(document.querySelector('.material-image-navigation')?.textContent).toContain('1 / 2')
  })

  it('uploads every selected image and retains them in the editor gallery', async () => {
    api.listMaterialAttachments.mockResolvedValueOnce([])
    const first = { id: 'first', materialId: material.id, originalFileName: '第一张.png', kind: 'CoverImage' }
    const second = { ...first, id: 'second', originalFileName: '第二张.png' }
    api.uploadMaterialAttachment.mockResolvedValueOnce(first).mockResolvedValueOnce(second)
    api.setMaterialCover.mockResolvedValueOnce({ ...material, rowVersion: 3, coverImageAttachmentId: 'first' }).mockResolvedValueOnce({ ...material, rowVersion: 4, coverImageAttachmentId: 'second' })
    api.materialAttachmentObjectUrl.mockResolvedValue('blob:cover')
    const wrapper = assetPreviewWrapper = mount(StandardLibrary, { attachTo: document.body, props: { token: 'token', canManage: true, canEdit: true }, global: { plugins: [ElementPlus] } })
    await flushPromises()
    wrapper.getComponent({ name: 'ElTable' }).vm.$emit('selection-change', [{ material, categories: [], coverImage: null }]); await flushPromises()
    await wrapper.findAll('button').find(button => button.text() === '维护')!.trigger('click'); await flushPromises()
    const input = document.querySelector<HTMLInputElement>('input[accept=".jpg,.jpeg,.png,.webp"]')!
    expect(input.multiple).toBe(true)
    const files = [new File(['one'], '第一张.png'), new File(['two'], '第二张.png')]
    Object.defineProperty(input, 'files', { configurable: true, value: files })
    input.dispatchEvent(new Event('change')); await flushPromises()
    expect(api.uploadMaterialAttachment).toHaveBeenCalledWith(material.id, 'CoverImage', files[0], 'token', expect.any(Function))
    expect(api.uploadMaterialAttachment).toHaveBeenCalledWith(material.id, 'CoverImage', files[1], 'token', expect.any(Function))
    expect(api.setMaterialCover).toHaveBeenNthCalledWith(1, material.id, 'first', 2, 'token')
    expect(api.setMaterialCover).toHaveBeenCalledTimes(1)
    const editor = wrapper.getComponent({ name: 'MaterialEditorDialog' })
    expect(editor.props('attachments')).toEqual([second, first])
    expect(editor.props('coverAttachmentId')).toBe('first')
    editor.vm.$emit('setCover', second); await flushPromises()
    expect(api.setMaterialCover).toHaveBeenNthCalledWith(2, material.id, 'second', 3, 'token')
    expect(editor.props('coverAttachmentId')).toBe('second')
    editor.vm.$emit('update:modelValue', false); await flushPromises()
    await wrapper.findAll('button').find(button => button.text() === '维护')!.trigger('click'); await flushPromises()
    expect(editor.props('coverAttachmentId')).toBe('second')
    expect(editor.props('form').expectedRowVersion).toBe(4)
  })

  it('updates attachment counts after a model or document is removed', async () => {
    const model = { id: 'model-delete', materialId: material.id, kind: 'Model3D', originalFileName: '模型.step' }
    const document = { ...model, id: 'document-delete', kind: 'Document', originalFileName: '资料.pdf' }
    api.listMaterialAttachments.mockResolvedValueOnce([model, document])
    const wrapper = assetPreviewWrapper = mount(StandardLibrary, { props: { token: 'token', canManage: true, canEdit: true }, global: { plugins: [ElementPlus] } })
    await flushPromises()
    const table = wrapper.getComponent({ name: 'ElTable' })
    table.vm.$emit('selection-change', [{ material, categories: [], coverImage: null }]); await flushPromises()
    await wrapper.findAll('button').find(button => button.text() === '维护')!.trigger('click'); await flushPromises()
    const editor = wrapper.getComponent({ name: 'MaterialEditorDialog' })
    editor.vm.$emit('attachmentDeleted', model); await flushPromises()
    expect(editor.props('attachments')).toEqual([document])
    expect(table.props('data')[0].material.model3DAttachmentCount).toBe(0)
    editor.vm.$emit('attachmentDeleted', document); await flushPromises()
    expect(editor.props('attachments')).toEqual([])
    expect(table.props('data')[0].material.documentAttachmentCount).toBe(0)
  })

  it('rejects picture uploads past the limit in the parent handler too', async () => {
    api.listMaterialAttachments.mockResolvedValueOnce(['one', 'two', 'three', 'four', 'five'].map(id => ({ id, materialId: material.id, kind: 'CoverImage', originalFileName: `${id}.png` })))
    api.materialAttachmentObjectUrl.mockResolvedValue('blob:image')
    const wrapper = assetPreviewWrapper = mount(StandardLibrary, { attachTo: document.body, props: { token: 'token', canManage: true, canEdit: true }, global: { plugins: [ElementPlus] } })
    await flushPromises()
    wrapper.getComponent({ name: 'ElTable' }).vm.$emit('selection-change', [{ material, categories: [], coverImage: null }]); await flushPromises()
    await wrapper.findAll('button').find(button => button.text() === '维护')!.trigger('click'); await flushPromises()
    wrapper.getComponent({ name: 'MaterialEditorDialog' }).vm.$emit('attachmentFiles', 'CoverImage', { target: { files: [new File(['image'], '第六张.png')], value: 'selected' } }); await flushPromises()
    expect(api.uploadMaterialAttachment).not.toHaveBeenCalled()
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

  async function openImageBatch(canEdit = true) {
    URL.createObjectURL = vi.fn(() => 'blob:batch-image')
    URL.revokeObjectURL = vi.fn()
    const selected = [material, secondMaterial].map(item => ({ material: { ...item } as PdmMaterial, categories: [], coverImage: null }))
    api.listStandardLibraryMaterials.mockResolvedValue({ items: selected, total: 2, page: 1, pageSize: 50 })
    api.listMaterialAttachments.mockReset().mockResolvedValue([])
    api.uploadMaterialAttachment.mockReset()
    api.setMaterialCover.mockReset().mockImplementation(async (id, attachmentId, version) => ({ ...selected.find(row => row.material.id === id)!.material, rowVersion: version + 1, coverImageAttachmentId: attachmentId }))
    api.replaceMaterialImages.mockReset().mockImplementation(async (id, _images, coverId, version) => ({ ...selected.find(row => row.material.id === id)!.material, rowVersion: version + 1, coverImageAttachmentId: coverId }))
    const wrapper = assetPreviewWrapper = mount(StandardLibrary, { attachTo: document.body, props: { token: 'token', canManage: true, canEdit }, global: { plugins: [ElementPlus] } })
    await flushPromises()
    wrapper.getComponent({ name: 'ElTable' }).vm.$emit('selection-change', selected); await flushPromises()
    await wrapper.findAll('button').find(button => button.text() === '维护')!.trigger('click'); await flushPromises()
    const input = document.querySelector<HTMLInputElement>('[aria-label="选择批量料品图片"]')!
    const selectImages = async (names = ['共同图片.png']) => {
      Object.defineProperty(input, 'files', { configurable: true, value: names.map(name => new File(['image'], name, { type: 'image/png' })) })
      input.dispatchEvent(new Event('change')); await flushPromises()
    }
    const save = async () => { await wrapper.findAll('button').find(button => button.text() === '批量保存')!.trigger('click'); await flushPromises() }
    return { wrapper, selected, input, selectImages, save }
  }

  it('replaces images and the selected cover for every material including those with an existing cover', async () => {
    const { wrapper, selected, selectImages, save } = await openImageBatch()
    selected[1]!.material.coverImageAttachmentId = 'existing-cover'
    api.uploadMaterialAttachment.mockImplementation(async (id, kind, file) => ({ id: `${id}-${file.name}`, materialId: id, kind, originalFileName: file.name }))
    await selectImages(['正面.png', '侧面.png'])
    await wrapper.findAll('button').find(button => button.attributes('aria-label') === '将 侧面.png 设为封面')!.trigger('click')
    expect(api.uploadMaterialAttachment).not.toHaveBeenCalled()
    const fields = wrapper.findAllComponents({ name: 'ElFormItem' })
    expect(fields.find(field => field.props('label') === '物料名称')!.get('input').attributes('disabled')).toBeDefined()
    expect((fields.find(field => field.props('label') === '规格/型号')!.get('input').element as HTMLInputElement).value).toBe('多个值（分别保留）')
    await save()
    expect(api.uploadMaterialAttachment).toHaveBeenCalledTimes(4)
    for (const row of selected) expect(api.replaceMaterialImages).toHaveBeenCalledWith(row.material.id, [`${row.material.id}-正面.png`, `${row.material.id}-侧面.png`], `${row.material.id}-侧面.png`, material.rowVersion, 'token')
    expect(api.setMaterialCover).not.toHaveBeenCalled()
    expect(api.changeApprovedMaterial).not.toHaveBeenCalled()
    expect(api.updateMaterial).not.toHaveBeenCalled()
  })

  it('replaces an already full gallery without counting the old images towards the new limit', async () => {
    const { selectImages, save } = await openImageBatch()
    api.listMaterialAttachments.mockImplementation(async id => id === secondMaterial.id ? ['1', '2', '3', '4', '5'].map(id => ({ id, kind: 'CoverImage' })) : [])
    api.uploadMaterialAttachment.mockImplementation(async (id, kind, file) => ({ id: `image-${id}`, materialId: id, kind, originalFileName: file.name }))
    await selectImages(); await save()
    expect(api.uploadMaterialAttachment).toHaveBeenCalledTimes(2)
    expect(api.replaceMaterialImages).toHaveBeenCalledTimes(2)
  })

  it('retries an interrupted batch without uploading completed images twice', async () => {
    const { selectImages, save } = await openImageBatch()
    const stored = new Map<string, unknown[]>()
    api.listMaterialAttachments.mockImplementation(async id => stored.get(id) ?? [])
    let failed = false
    api.uploadMaterialAttachment.mockImplementation(async (id, kind, file) => {
      if (id === secondMaterial.id && !failed) { failed = true; throw new Error('网络中断，请重试。') }
      const attachment = { id: `image-${id}`, materialId: id, kind, originalFileName: file.name }
      stored.set(id, [attachment]); return attachment
    })
    await selectImages(); await save(); await save()
    expect(api.uploadMaterialAttachment.mock.calls.filter(call => call[0] === material.id)).toHaveLength(1)
    expect(api.uploadMaterialAttachment.mock.calls.filter(call => call[0] === secondMaterial.id)).toHaveLength(2)
    expect(api.replaceMaterialImages).toHaveBeenCalledTimes(2)
  })

  it('applies only changed metadata and preserves image sets when the image option is not enabled', async () => {
    const { wrapper, save } = await openImageBatch()
    const field = wrapper.findAllComponents({ name: 'ElFormItem' }).find(item => item.props('label') === '选型建议')!
    field.getComponent({ name: 'ElCheckbox' }).vm.$emit('update:modelValue', true); await flushPromises()
    field.getComponent({ name: 'ElInput' }).vm.$emit('update:modelValue', '统一选型建议'); await flushPromises()
    api.changeApprovedMaterial.mockImplementation(async (id, input) => ({ material: { ...material, ...input, id, rowVersion: input.expectedRowVersion + 1 }, task: null }))
    await save()
    expect(api.changeApprovedMaterial).toHaveBeenCalledTimes(2)
    expect(api.changeApprovedMaterial.mock.calls.every(call => call[1].selectionAdvice === '统一选型建议')).toBe(true)
    expect(api.replaceMaterialImages).not.toHaveBeenCalled()
    expect(api.uploadMaterialAttachment).not.toHaveBeenCalled()
  })

  it('clears all selected image sets and covers only when explicitly enabled with no images', async () => {
    const { wrapper, save } = await openImageBatch()
    wrapper.findAllComponents({ name: 'ElCheckbox' }).find(item => item.text() === '修改图片与封面')!.vm.$emit('update:modelValue', true); await flushPromises()
    await save()
    expect(api.uploadMaterialAttachment).not.toHaveBeenCalled()
    for (const item of [material, secondMaterial]) expect(api.replaceMaterialImages).toHaveBeenCalledWith(item.id, [], null, item.rowVersion, 'token')
  })

  it('does not upload when batch maintenance is cancelled or editing permission is absent', async () => {
    const { wrapper, selectImages } = await openImageBatch()
    await selectImages()
    const batch = wrapper.findAllComponents({ name: 'MaterialEditorDialog' }).find(editor => editor.props('title') === '批量维护标准物料')!
    batch.vm.$emit('update:modelValue', false); await flushPromises()
    expect(api.uploadMaterialAttachment).not.toHaveBeenCalled()
    wrapper.unmount(); assetPreviewWrapper = undefined
    const readonly = await openImageBatch(false)
    expect(readonly.input.disabled).toBe(true)
    await readonly.selectImages()
    expect((readonly.wrapper.findAll('button').find(button => button.text() === '批量保存')!.element as HTMLButtonElement).disabled).toBe(true)
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
    expect((categorySelectionTree!.props('data') as Array<{ id: string; children: unknown[] }>).find(row => row.id === 'root')!.children).toHaveLength(1)
    const panes = document.body.querySelectorAll('.add-material-layout > .add-material-pane')
    expect(panes).toHaveLength(3)
    expect(panes[2].classList.contains('add-material-target')).toBe(true)
    expect(api.listMaterials).not.toHaveBeenCalled()
    expect(panes[0].textContent).toContain('请输入至少4个字符后搜索料品')
    const search = wrapper.find('input[placeholder="搜索料号/名称/型号/品牌"]')
    await search.setValue(' 010 ')
    await new Promise(resolve => setTimeout(resolve, 330))
    expect(api.listMaterials).not.toHaveBeenCalled()
    await search.setValue('0102')
    await search.setValue('01020')
    await search.setValue(' 010200 ')
    await new Promise(resolve => setTimeout(resolve, 330))
    await flushPromises()
    expect(api.listMaterials).toHaveBeenCalledTimes(1)
    expect(api.listMaterials).toHaveBeenLastCalledWith('token', '010200', false, 100, '', expect.any(AbortSignal))
    const eligibleTable = wrapper.findAllComponents({ name: 'ElTable' }).find(table => (table.props('data') as unknown[])?.length === 2)
    expect(eligibleTable).toBeTruthy()
    const selectedTable = wrapper.findAllComponents({ name: 'ElTable' }).find(table => (table.props('data') as unknown[])?.length === 0)
    expect(eligibleTable!.props('height')).toBe('100%')
    expect(selectedTable?.props('height')).toBe('100%')

    await eligibleTable!.find('.el-table__body input[type="checkbox"]').setValue(true)
    await flushPromises()
    api.listMaterials.mockResolvedValueOnce([secondMaterial])
    await search.setValue('075183')
    await new Promise(resolve => setTimeout(resolve, 330))
    await flushPromises()
    expect(eligibleTable!.props('data')).toEqual([secondMaterial])
    await eligibleTable!.find('.el-table__body input[type="checkbox"]').setValue(true)
    await flushPromises()
    expect(document.body.querySelector('.add-material-pane--selected')?.textContent).toContain('2 项')

    const brandFilter = wrapper.findAllComponents({ name: 'ElSelect' }).find(select => select.props('placeholder') === '筛选品牌')
    expect(brandFilter).toBeTruthy()
    expect(brandFilter!.props('filterable')).toBe(true)
    brandFilter!.vm.$emit('update:modelValue', 'SMC')
    await flushPromises()
    expect(eligibleTable!.props('data')).toEqual([secondMaterial])
    await wrapper.find('.add-material-pane--selected').findAll('button').find(button => button.text() === '移除')!.trigger('click')
    await flushPromises()
    await search.setValue('')
    await flushPromises()
    expect(eligibleTable!.props('data')).toEqual([])

    const categorySearch = wrapper.find('input[placeholder="筛选分类名称"]')
    expect(categorySearch.exists()).toBe(true)
    const categoryFilter = categorySelectionTree!.props('filterNodeMethod') as (value: string, data: { name: string }) => boolean
    expect(categoryFilter('真空', { name: '真空元器件' })).toBe(true)
    expect(categoryFilter('真空', { name: '内部标准件' })).toBe(false)
    await categorySearch.setValue('真空')
    await flushPromises()

    const selectedPane = document.body.querySelector('.add-material-pane--selected')
    expect(selectedPane?.textContent).toContain('1 项')
    expect(selectedPane?.textContent).toContain('010200075183')
    expect(api.addStandardLibraryMaterials).not.toHaveBeenCalled()
    categorySelectionTree!.vm.$emit('check', {}, { checkedKeys: ['root'] })
    await flushPromises()
    await wrapper.findAll('button').find(button => button.text() === '批量加入指定分类')!.trigger('click')
    await flushPromises()
    expect(api.addStandardLibraryMaterials).toHaveBeenCalledWith(['root'], ['material-2'], 'token')
    wrapper.unmount()
  })

  it('ignores stale search responses after typing, clearing, closing and reopening', async () => {
    let finishOld!: (items: unknown[]) => void
    let finishClosed!: (items: unknown[]) => void
    api.listMaterials.mockImplementationOnce(() => new Promise(resolve => { finishOld = resolve }))
      .mockResolvedValueOnce([secondMaterial])
      .mockImplementationOnce(() => new Promise(resolve => { finishClosed = resolve }))
    const wrapper = mount(StandardLibrary, { attachTo: document.body, props: { token: 'token', canManage: true, canEdit: true }, global: { plugins: [ElementPlus] } })
    await flushPromises()
    await wrapper.findAll('button').find(button => button.text() === '新增物料')!.trigger('click')
    await flushPromises()
    const search = wrapper.get('input[placeholder="搜索料号/名称/型号/品牌"]')
    const eligibleTable = wrapper.findAllComponents({ name: 'ElTable' }).find(table => table.props('emptyText') === '请输入至少4个字符后搜索料品')!
    await search.setValue('13531')
    await new Promise(resolve => setTimeout(resolve, 330))
    const oldSignal = api.listMaterials.mock.calls[0][5] as AbortSignal
    await search.setValue('075183')
    expect(oldSignal.aborted).toBe(true)
    await new Promise(resolve => setTimeout(resolve, 330))
    await flushPromises()
    finishOld([material]); await flushPromises()
    expect(eligibleTable.props('data')).toEqual([secondMaterial])
    await search.setValue('13531')
    await new Promise(resolve => setTimeout(resolve, 330))
    await search.setValue('')
    expect((api.listMaterials.mock.calls[2][5] as AbortSignal).aborted).toBe(true)
    await wrapper.findAll('button').find(button => button.text() === '取消')!.trigger('click')
    await wrapper.findAll('button').find(button => button.text() === '新增物料')!.trigger('click')
    finishClosed([material]); await flushPromises()
    expect(eligibleTable.props('data')).toEqual([])
    expect(eligibleTable.props('emptyText')).toBe('请输入至少4个字符后搜索料品')
    expect(api.listMaterials).toHaveBeenCalledTimes(3)
    wrapper.unmount()
  })

  it('shows a Chinese search error and retries on Enter without fetching short input', async () => {
    api.listMaterials.mockRejectedValueOnce(new Error('Failed to fetch')).mockResolvedValueOnce([material])
    const wrapper = mount(StandardLibrary, { attachTo: document.body, props: { token: 'token', canManage: true, canEdit: true }, global: { plugins: [ElementPlus] } })
    await flushPromises()
    await wrapper.findAll('button').find(button => button.text() === '新增物料')!.trigger('click')
    await flushPromises()
    const search = wrapper.get('input[placeholder="搜索料号/名称/型号/品牌"]')
    const eligibleTable = wrapper.findAllComponents({ name: 'ElTable' }).find(table => table.props('emptyText') === '请输入至少4个字符后搜索料品')!
    await search.setValue('13531'); await new Promise(resolve => setTimeout(resolve, 330)); await flushPromises()
    expect(eligibleTable.props('emptyText')).toBe('无法连接服务器，请检查网络后重试。')
    await search.trigger('keyup.enter'); await new Promise(resolve => setTimeout(resolve, 330)); await flushPromises()
    expect(eligibleTable.props('data')).toEqual([material])
    await search.setValue('abc'); await search.trigger('keyup.enter'); await new Promise(resolve => setTimeout(resolve, 330)); await flushPromises()
    expect(api.listMaterials).toHaveBeenCalledTimes(2)
    expect(eligibleTable.props('data')).toEqual([])
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
    const rootRow = document.body.querySelector('.category-manager__row:not(.is-child)')!
    expect(rootRow.textContent).toContain('UTJ-01')
    const addChild = [...rootRow.querySelectorAll('button')].find(button => button.textContent?.trim() === '添加子级')!
    addChild.click()
    await flushPromises()
    const codeInput = document.body.querySelector('.category-manager__editor input[readonly]') as HTMLInputElement
    expect(codeInput.value).toBe('UTJ-01')
    expect(document.body.textContent).toContain('分类编号')
    wrapper.unmount()
  })

  it('filters uncategorized and imports only deduplicated codes with ERP results on the same page', async () => {
    api.importStandardLibraryMaterials.mockResolvedValue({ importedCount: 1, existingCount: 0, failedCount: 1, rows: [{ materialCode: '01020000001', status: 'Imported', name: 'ERP轴承', specification: 'ERP型号', brand: 'ERP品牌', message: '已从ERP导入，归入未分类。' }, { materialCode: 'missing', status: 'Failed', message: 'ERP中未找到该料号，未导入。' }] })
    const wrapper = mount(StandardLibrary, { attachTo: document.body, props: { token: 'token', canManage: true, canEdit: true }, global: { plugins: [ElementPlus] } }); await flushPromises()
    expect(wrapper.text()).toContain('未分类')
    wrapper.findAllComponents({ name: 'ElTree' })[0].vm.$emit('node-click', { id: 'uncategorized' }); await flushPromises()
    expect(api.listStandardLibraryMaterials).toHaveBeenLastCalledWith('token', expect.objectContaining({ categoryId: 'uncategorized' }))
    await wrapper.findAll('button').find(button => button.text() === '导入')!.trigger('click'); await flushPromises()
    await wrapper.get('textarea[aria-label="待导入料号"]').setValue('料号\n01020000001,01020000001\nmissing')
    await wrapper.findAll('button').find(button => button.text() === '从ERP导入')!.trigger('click'); await flushPromises()
    expect(api.importStandardLibraryMaterials).toHaveBeenCalledWith(['01020000001', 'missing'], 'token')
    expect(document.body.textContent).toContain('ERP轴承'); expect(document.body.textContent).toContain('ERP中未找到该料号')
    expect(document.body.textContent).toContain('已导入 1 个，已存在 0 个，失败 1 个')
    wrapper.unmount()
  })

  it('标准化在料品明细的3D上方上传2D新版本，不自动启用或覆盖旧图', async () => {
    const activeMaterial = { ...material, isArchived: false }
    api.listStandardLibraryMaterials.mockResolvedValue({ items: [{ material: activeMaterial, categories: [], coverImage: null }], total: 1, page: 1, pageSize: 50 })
    const old = { id: 'old-2d', materialId: material.id, kind: 'Drawing2D', originalFileName: '旧图.pdf', versionNumber: 1, isEnabled: true }
    const uploaded = { ...old, id: 'new-2d', originalFileName: '新图.pdf', versionNumber: 2, isEnabled: false }
    api.listMaterialAttachments.mockResolvedValueOnce([old])
    api.uploadMaterialAttachment.mockResolvedValueOnce(uploaded)
    const wrapper = assetPreviewWrapper = mount(StandardLibrary, { attachTo: document.body, props: { token: 'token', canManage: true, canEdit: true }, global: { plugins: [ElementPlus] } })
    await flushPromises()
    wrapper.getComponent({ name: 'ElTable' }).vm.$emit('selection-change', [{ material: activeMaterial, categories: [], coverImage: null }]); await flushPromises()
    await wrapper.findAll('button').find(button => button.text() === '维护')!.trigger('click'); await flushPromises()
    const editor = wrapper.getComponent({ name: 'MaterialEditorDialog' })
    expect(editor.props('canManageDrawings')).toBe(true)
    expect(editor.findAll('.material-editor-file-list').map(section => section.attributes('aria-label'))).toEqual(['2D图纸附件', '3D附件', '资料附件'])
    const input = editor.get('[aria-label="选择2D附件"]'), file = new File(['drawing'], '新图.pdf')
    Object.defineProperty(input.element,'files',{value:[file],configurable:true})
    await input.trigger('change'); await flushPromises()
    expect(api.uploadMaterialAttachment).toHaveBeenCalledWith(material.id, 'Drawing2D', file, 'token', expect.any(Function))
    expect(editor.props('attachments')).toEqual([uploaded, old])
    expect(editor.get('[aria-label="2D图纸附件"]').text()).toContain('V01 · 已启用')
    expect(editor.get('[aria-label="2D图纸附件"]').text()).toContain('V02')
    wrapper.unmount()
  })

  it('reads code-only Excel through the parser and preserves text after an import error', async () => {
    api.readStandardLibraryImportFile.mockResolvedValue({ materialCodes: ['00001234'] })
    api.importStandardLibraryMaterials.mockRejectedValue(new Error('ERP暂时无法连接，请稍后重试。'))
    const wrapper = mount(StandardLibrary, { attachTo: document.body, props: { token: 'token', canManage: true, canEdit: true }, global: { plugins: [ElementPlus] } }); await flushPromises()
    await wrapper.findAll('button').find(button => button.text() === '导入')!.trigger('click'); await flushPromises()
    const input = wrapper.get('input[type="file"][aria-label="选择料号文件"]'), file = new File(['test'], '料号.xlsx')
    Object.defineProperty(input.element, 'files', { value: [file], configurable: true }); await input.trigger('change'); await flushPromises()
    expect(api.readStandardLibraryImportFile).toHaveBeenCalledWith(file, 'token')
    await wrapper.findAll('button').find(button => button.text() === '从ERP导入')!.trigger('click'); await flushPromises()
    expect(wrapper.get('textarea').element).toHaveProperty('value', '00001234')
    expect(wrapper.get('[role="alert"]').text()).toContain('ERP暂时无法连接')
    wrapper.unmount()
  })
})
