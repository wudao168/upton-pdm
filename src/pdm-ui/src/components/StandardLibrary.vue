<script setup lang="ts">
import { ElMessage } from '../statusMessage'
import { Box, Folder } from '@lucide/vue'
import { ElMessageBox } from 'element-plus'
import { computed, defineAsyncComponent, nextTick, onBeforeUnmount, onMounted, reactive, ref, watch } from 'vue'
import MaterialEditorDialog from './MaterialEditorDialog.vue'
import MaterialImagePreview from './MaterialImagePreview.vue'
import EngineeringKitDetails from './EngineeringKitDetails.vue'
import EngineeringKitAssets from './EngineeringKitAssets.vue'
import {
  addStandardLibraryMaterials,
  archiveMaterial,
  reactivateMaterial,
  importStandardLibraryMaterials,
  readStandardLibraryImportFile,
  changeApprovedMaterial,
  deleteStandardLibraryCategory,
  downloadMaterialAttachment,
  listMaterialAttachments,
  listMaterialCategories,
  listMaterials,
  listStandardLibraryCategories,
  listStandardLibraryMaterials,
  moveEngineeringKitCategory,
  removeStandardLibraryMaterial,
  materialAttachmentObjectUrl,
  saveStandardLibraryCategory,
  setMaterialCover,
  replaceMaterialImages,
  setStandardLibraryRecommendation,
  updateMaterial,
  uploadMaterialAttachment,
} from '../api'
import type { EngineeringKit, MaterialAttachment, MaterialAttachmentKind, MaterialCategory, PdmMaterial, SaveMaterialInput, StandardLibraryCategory, StandardLibraryMaterial, StandardLibraryImportResult } from '../types'
import { userErrorMessage } from '../userErrorMessage'
import { standardCategoryCode } from '../standardLibraryCategories'
import { u9UnitOptions } from '../u9Units'
import MaterialImageAdd from './MaterialImageAdd.vue'
import { materialImageAccept, materialImageLimit } from '../materialImages'

const PdfDrawingViewer = defineAsyncComponent(() => import('./PdfDrawingViewer.vue'))
const props = defineProps<{ token: string; canManage: boolean; canEdit: boolean; active?: boolean }>()
type CategoryTreeNode = StandardLibraryCategory & { children: CategoryTreeNode[]; disabled: boolean }
type FlatCategoryRow = { category: StandardLibraryCategory; depth: number }
type MaterialSelectionTable = { clearSelection: () => void; toggleRowSelection: (row: PdmMaterial, selected: boolean) => void }
type StandardLibrarySelectionTable = { clearSelection: () => void }
type CategorySelectionTree = { filter: (value: string) => void; setCheckedKeys: (keys: string[]) => void }

const loading = ref(false)
const categories = ref<StandardLibraryCategory[]>([])
const materialCategories = ref<MaterialCategory[]>([])
const rows = ref<StandardLibraryMaterial[]>([])
const assetKit = ref<EngineeringKit | null>(null)
const detailKit = ref<EngineeringKit | null>(null)
const movingKit = ref<EngineeringKit | null>(null)
const moveCategoryId = ref('')
const movingCategory = ref(false)
const total = ref(0)
const page = ref(1)
const pageSize = ref(50)
const selectedCategoryId = ref('')
const query = ref('')
const brand = ref('')
const recommendedOnly = ref(false)
const showInactive = ref(false)
const selectedRows = ref<StandardLibraryMaterial[]>([])
const removing = ref(false)
const materialTable = ref<StandardLibrarySelectionTable | null>(null)
const coverUrls = reactive<Record<string, string>>({})
const imagePreview = ref<{ materialId: string; initialId: string } | null>(null)

const batchEditorOpen = ref(false)
const batchSaving = ref(false)
const batchRecommendation = ref<'Keep' | 'Recommended' | 'NotRecommended'>('Keep')
const batchSelectionAdviceEnabled = ref(false)
const batchSelectionAdvice = ref('')
const batchReferencePriceEnabled = ref(false)
const batchReferencePrice = ref<number | null>(null)
const batchImages = ref<Array<{ file: File; url: string }>>([])
const batchImagesEnabled = ref(false)
const batchCoverFile = ref<File | null>(null)
const batchImageInput = ref<HTMLInputElement | null>(null)
const batchError = ref('')
const batchProgress = ref('')
const batchUploadedImages = new Map<string, Map<File, MaterialAttachment>>()
const batchReplacedImages = new Map<string, string>()
const batchReadonlyFields: Array<{ key: keyof PdmMaterial; label: string }> = [
  { key: 'materialCode', label: 'PLM物料编码' }, { key: 'name', label: '物料名称' }, { key: 'categoryCode', label: 'U9C对应分类' },
  { key: 'kind', label: 'PLM业务类型' }, { key: 'unitCode', label: '计量单位' }, { key: 'specification', label: '规格/型号' },
  { key: 'material', label: '材质' }, { key: 'brand', label: '品牌' }, { key: 'surfaceTreatment', label: '表面处理' }, { key: 'weight', label: '重量' },
]
function batchValue(key: keyof PdmMaterial) {
  const values = selectedRows.value.map(({ material }) => {
    if (key === 'kind') return ({ Electrical: '电气件', Standard: '机械外购件', NonStandard: '非标机加件', Product: '产品/组件' })[material.kind]
    if (key === 'unitCode') return u9UnitOptions.find(unit => unit.code === material.unitCode)?.name ?? material.unitCode
    if (key === 'categoryCode') return material.categoryCode || material.u9CategoryCode || '—'
    if (key === 'isRecommended') return material.isRecommended ? '已推荐' : '未推荐'
    return String(material[key] ?? '').trim() || '—'
  })
  return new Set(values).size > 1 ? '多个值（分别保留）' : values[0] ?? '—'
}
function clearBatchImages() {
  for (const image of batchImages.value) URL.revokeObjectURL(image.url)
  batchImages.value = []
  batchImagesEnabled.value = false
  batchCoverFile.value = null
  batchUploadedImages.clear()
  batchReplacedImages.clear()
}
function selectBatchImages(event: Event) {
  const input = event.target as HTMLInputElement, files = Array.from(input.files ?? [])
  input.value = ''
  if (!batchCanUploadImages.value || batchSaving.value || !files.length) return
  batchError.value = ''
  if (batchImages.value.length + files.length > materialImageLimit) { batchError.value = `每个料品最多${materialImageLimit}张图片，请减少本次选择的图片数量。`; return }
  if (files.some(file => !/\.(jpe?g|png|webp)$/i.test(file.name))) { batchError.value = '请选择JPG、PNG或WebP格式的图片。'; return }
  batchImages.value.push(...files.map(file => ({ file, url: URL.createObjectURL(file) })))
  batchImagesEnabled.value = true
  batchCoverFile.value ??= files[0]!
}
function removeBatchImage(index: number) {
  const image = batchImages.value.splice(index, 1)[0]
  if (image) URL.revokeObjectURL(image.url)
  if (image?.file === batchCoverFile.value) batchCoverFile.value = batchImages.value[0]?.file ?? null
  batchError.value = ''
}
watch(batchEditorOpen, open => { if (!open) clearBatchImages() })

const categoryManagerOpen = ref(false)
const categoryFormOpen = ref(false)
const categorySorting = ref(false)
const editingCategoryId = ref<string | null>(null)
const categoryForm = reactive({ name: '', code: '', parentId: '' as string, sortOrder: 0, isActive: true, expectedRowVersion: null as number | null })

const addDialogOpen = ref(false)
const eligibleMaterials = ref<PdmMaterial[]>([])
const eligibleQuery = ref('')
const eligibleBrand = ref('')
const eligibleLoading = ref(false)
const eligibleSearchError = ref('')
let eligibleSearchTimer: ReturnType<typeof setTimeout> | undefined
let eligibleSearchController: AbortController | undefined
const categoryQuery = ref('')
const selectedEligibleMaterials = ref<PdmMaterial[]>([])
const selectedEligibleIds = computed(() => selectedEligibleMaterials.value.map(item => item.id))
const selectedAddCategoryIds = ref<string[]>([])
const eligibleSelectionTable = ref<MaterialSelectionTable | null>(null)
const addCategoryTree = ref<CategorySelectionTree | null>(null)
const adding = ref(false)
const importDialogOpen = ref(false), importing = ref(false), importText = ref(''), importError = ref('')
const importResult = ref<StandardLibraryImportResult | null>(null)
const importFileInput = ref<HTMLInputElement | null>(null)
const importCodes = computed(() => [...new Map(importText.value.split(/[\s,;，；]+/).map(code => code.trim()).filter(code => code && !['料号', '物料编码', '料品编码', 'materialcode', 'code'].includes(code.toLowerCase())).map(code => [code.toLowerCase(), code])).values()])
const isUncategorized = (category: StandardLibraryCategory) => !category.parentId && category.name === '未分类'

function openImport() {
  if (!props.canManage) return
  importText.value = ''; importError.value = ''; importResult.value = null; importDialogOpen.value = true
}
async function readImportFile(event: Event) {
  const input = event.target as HTMLInputElement, file = input.files?.[0]
  if (!file) return
  importError.value = ''; importResult.value = null; importing.value = true
  try {
    if (file.size > 2 * 1024 * 1024) throw new Error('请选择不超过2MB的料号文件。')
    if (/\.xlsx$/i.test(file.name)) importText.value = (await readStandardLibraryImportFile(file, props.token)).materialCodes.join('\n')
    else if (/\.(csv|txt)$/i.test(file.name)) importText.value = await file.text()
    else throw new Error('请选择.xlsx、.csv或.txt文件，只需一列料号。')
  } catch (error) { importError.value = userErrorMessage(error instanceof Error ? error.message : '料号文件读取失败') }
  finally { input.value = ''; importing.value = false }
}
async function importMaterials() {
  if (!props.canManage || importing.value) return
  if (!importCodes.value.length || importCodes.value.length > 200) { importError.value = '请提供1至200个料号，重复料号会自动合并。'; return }
  importing.value = true; importError.value = ''; importResult.value = null
  try {
    importResult.value = await importStandardLibraryMaterials(importCodes.value, props.token)
    selectedCategoryId.value = categories.value.find(isUncategorized)?.id ?? ''; page.value = 1; query.value = ''; brand.value = ''; recommendedOnly.value = false
    await load(false)
  } catch (error) { importError.value = userErrorMessage(error instanceof Error ? error.message : 'ERP导入未完成'); await load(false) }
  finally { importing.value = false }
}

const attachmentDialogOpen = ref(false)
const attachmentDialogTitle = ref('')
const attachmentItems = ref<MaterialAttachment[]>([])
const attachmentMaterialId = ref('')
const attachmentPreviewOpen = ref(false)
const attachmentPreview = ref<MaterialAttachment | null>(null)
const attachmentPreviewUrl = ref('')
const attachmentPreviewLoading = ref(false)
const attachmentPreviewError = ref('')
let attachmentPreviewRequest = 0
function canPreviewAttachment(attachment: MaterialAttachment) {
  return /\.(pdf|png|jpe?g|gif|webp|bmp)$/i.test(attachment.originalFileName)
}
function closeAttachmentPreview() {
  attachmentPreviewRequest++
  attachmentPreviewOpen.value = false
  if (attachmentPreviewUrl.value) URL.revokeObjectURL(attachmentPreviewUrl.value)
  attachmentPreviewUrl.value = ''
  attachmentPreview.value = null
  attachmentPreviewLoading.value = false
  attachmentPreviewError.value = ''
}
async function previewAttachment(attachment: MaterialAttachment) {
  if (!canPreviewAttachment(attachment)) return
  closeAttachmentPreview()
  const request = attachmentPreviewRequest
  const materialId = attachmentMaterialId.value
  attachmentPreview.value = attachment
  attachmentPreviewOpen.value = true
  attachmentPreviewLoading.value = true
  try {
    const url = await materialAttachmentObjectUrl(materialId, attachment.id, props.token)
    if (request !== attachmentPreviewRequest) { URL.revokeObjectURL(url); return }
    attachmentPreviewUrl.value = url
  } catch (error) {
    if (request === attachmentPreviewRequest) attachmentPreviewError.value = userErrorMessage(error instanceof Error ? error.message : '附件预览加载失败，请重试或下载查看。')
  } finally { if (request === attachmentPreviewRequest) attachmentPreviewLoading.value = false }
}

const editorOpen = ref(false)
const changingEditorCover = ref(false)
const editingMaterial = ref<PdmMaterial | null>(null)
const editorAttachments = ref<MaterialAttachment[]>([])
const editorCoverUrl = ref('')
const saving = ref(false)
const materialActivityPending = ref(false)
const uploadingKind = ref<MaterialAttachmentKind | null>(null)
const uploadProgress = ref(0)
const emptyForm = (): SaveMaterialInput => ({ materialCode: '', name: '', kind: 'Electrical', supplyMode: 'Purchase', unitCode: '001', categoryCode: '', specification: '', material: '', remark: '', brand: '', surfaceTreatment: '', purchaseLink: '', selectionAdvice: '', referencePrice: null, model3DLink: '', documentLink: '', isRecommended: false, weight: null, weightUnit: 'kg' })
const editorForm = reactive<SaveMaterialInput>(emptyForm())

const categoryTree = computed<CategoryTreeNode[]>(() => {
  const nodes = new Map(categories.value.map(item => [item.id, { ...item, disabled: !item.isActive, children: [] } as CategoryTreeNode]))
  const roots: CategoryTreeNode[] = []
  for (const node of nodes.values()) {
    const parent = node.parentId ? nodes.get(node.parentId) : undefined
    if (parent) parent.children.push(node)
    else roots.push(node)
  }
  const sort = (items: CategoryTreeNode[]) => items.sort((a, b) => a.sortOrder - b.sortOrder || a.name.localeCompare(b.name, 'zh-CN')).forEach(item => sort(item.children))
  sort(roots)
  return roots
})
const flatCategoryRows = computed<FlatCategoryRow[]>(() => {
  const result: FlatCategoryRow[] = []
  const visit = (items: CategoryTreeNode[], depth: number) => items.forEach(item => {
    if (!isUncategorized(item)) result.push({ category: item, depth })
    visit(item.children, depth + 1)
  })
  visit(categoryTree.value, 0)
  return result
})
const filteredEligibleMaterials = computed(() => eligibleMaterials.value.filter(item => !eligibleBrand.value || item.brand?.trim() === eligibleBrand.value))
const eligibleEmptyText = computed(() => eligibleQuery.value.trim().length < 4 ? '请输入至少4个字符后搜索料品' : eligibleLoading.value ? '正在搜索料品…' : eligibleSearchError.value || '没有符合条件的可加入料品，请调整搜索内容或品牌')
const eligibleBrands = computed(() => [...new Set(eligibleMaterials.value.map(item => item.brand?.trim()).filter((item): item is string => Boolean(item)))].sort((a, b) => a.localeCompare(b, 'zh-CN')))
const brands = computed(() => [...new Set(rows.value.map(item => item.material.brand?.trim()).filter((item): item is string => Boolean(item)))].sort((a, b) => a.localeCompare(b, 'zh-CN')))
const creatableMaterialCategories = computed(() => materialCategories.value.filter(item => item.allowCreate && item.isActive && item.isVisible && item.pdmKind))
const canMaintainSelected = computed(() => selectedRows.value.length > 0 && (props.canEdit || props.canManage) && (selectedRows.value.length === 1 || selectedRows.value.every(row => !row.kit)))
const removeDisabledReason = computed(() => !props.canManage ? '需要标准物料管理权限'
  : !selectedRows.value.length ? '请先勾选要移除的物料'
  : selectedRows.value.some(row => row.kit) ? '套件由发布自动收录，请通过维护停用或调整分类'
  : selectedRows.value.some(row => !row.categories.length) ? '物料分类信息已变化，请刷新后重试' : '')
const batchCanUploadImages = computed(() => props.canEdit && selectedRows.value.every(row => !row.material.isArchived))
const batchHasChanges = computed(() => (props.canManage && batchRecommendation.value !== 'Keep') || (props.canEdit && (batchSelectionAdviceEnabled.value || batchReferencePriceEnabled.value)) || (batchCanUploadImages.value && batchImagesEnabled.value))

async function load(refreshCategories = true) {
  loading.value = true
  try {
    if (refreshCategories) categories.value = await listStandardLibraryCategories(props.token, props.canManage)
    const result = await listStandardLibraryMaterials(props.token, { categoryId: selectedCategoryId.value || undefined, query: query.value, brand: brand.value, recommendedOnly: recommendedOnly.value, page: page.value, pageSize: pageSize.value, includeInactive: showInactive.value })
    rows.value = result.items
    total.value = result.total
    await loadCoverUrls(result.items)
  } catch (error) {
    ElMessage.error(error instanceof Error ? error.message : '标准库加载失败')
  } finally {
    loading.value = false
  }
}

async function loadCoverUrls(items: StandardLibraryMaterial[]) {
  for (const url of Object.values(coverUrls)) if (url.startsWith('blob:')) URL.revokeObjectURL(url)
  for (const key of Object.keys(coverUrls)) delete coverUrls[key]
  await Promise.all(items.filter(item => item.coverImage).map(async item => {
    try { coverUrls[item.material.id] = await materialAttachmentObjectUrl(item.material.id, item.coverImage!.id, props.token, 120) } catch { /* status remains visible without blocking the table */ }
  }))
}

function selectCategory(data?: StandardLibraryCategory) {
  selectedCategoryId.value = data?.id ?? ''
  page.value = 1
  void load(false)
}

function searchMaterials() {
  page.value = 1
  void load(false)
}

function filterCategoryNode(value: string, data: CategoryTreeNode) {
  const keyword = value.trim().toLocaleLowerCase('zh-CN')
  return !keyword || data.name.toLocaleLowerCase('zh-CN').includes(keyword)
}

watch(categoryQuery, value => addCategoryTree.value?.filter(value))
watch(() => props.active, active => { if (active) void load(false) })

function openCategoryManager() {
  categoryManagerOpen.value = true
  categoryFormOpen.value = false
  editingCategoryId.value = null
}

function openCategoryEditor(category?: StandardLibraryCategory, parentId?: string) {
  const effectiveParentId = category?.parentId ?? parentId ?? ''
  const siblings = categories.value.filter(item => (item.parentId ?? '') === effectiveParentId)
  editingCategoryId.value = category?.id ?? null
  Object.assign(categoryForm, { name: category?.name ?? '', code: category?.code ?? '', parentId: effectiveParentId, sortOrder: category?.sortOrder ?? Math.max(0, ...siblings.map(item => item.sortOrder)) + 10, isActive: category?.isActive ?? true, expectedRowVersion: category?.rowVersion ?? null })
  categoryFormOpen.value = true
}

function closeCategoryEditor() {
  categoryFormOpen.value = false
  editingCategoryId.value = null
}

async function saveCategory() {
  try {
    const parentId = categoryForm.parentId || null
    const existing = categories.value.find(item => item.id === editingCategoryId.value)
    const parentChanged = existing && existing.parentId !== parentId
    const siblings = categories.value.filter(item => item.id !== editingCategoryId.value && item.parentId === parentId)
    const sortOrder = !existing || parentChanged ? Math.max(0, ...siblings.map(item => item.sortOrder)) + 10 : categoryForm.sortOrder
    await saveStandardLibraryCategory({ ...categoryForm, parentId, sortOrder }, props.token, editingCategoryId.value ?? undefined)
    closeCategoryEditor()
    await load()
    ElMessage.success('标准分类已保存')
  } catch (error) { ElMessage.error(error instanceof Error ? error.message : '标准分类保存失败') }
}

async function deleteCategory(category: StandardLibraryCategory) {
  try {
    await ElMessageBox.confirm(`仅无子分类且无成员时可删除“${category.name}”。`, '删除标准分类', { type: 'warning' })
    await deleteStandardLibraryCategory(category.id, category.rowVersion, props.token)
    if (selectedCategoryId.value === category.id) selectedCategoryId.value = ''
    if (editingCategoryId.value === category.id) closeCategoryEditor()
    await load()
    ElMessage.success('标准分类已删除')
  } catch (error) { if (error !== 'cancel') ElMessage.error(error instanceof Error ? error.message : '标准分类删除失败') }
}

function categorySiblings(category: StandardLibraryCategory) {
  return categories.value
    .filter(item => item.parentId === category.parentId)
    .sort((a, b) => a.sortOrder - b.sortOrder || a.name.localeCompare(b.name, 'zh-CN'))
}

function canMoveCategory(category: StandardLibraryCategory, direction: -1 | 1) {
  const siblings = categorySiblings(category)
  const index = siblings.findIndex(item => item.id === category.id)
  return index >= 0 && index + direction >= 0 && index + direction < siblings.length
}

async function moveCategory(category: StandardLibraryCategory, direction: -1 | 1) {
  if (!canMoveCategory(category, direction) || categorySorting.value) return
  const reordered = categorySiblings(category)
  const index = reordered.findIndex(item => item.id === category.id)
  ;[reordered[index], reordered[index + direction]] = [reordered[index + direction], reordered[index]]
  categorySorting.value = true
  try {
    for (const [orderIndex, item] of reordered.entries()) {
      const nextSortOrder = (orderIndex + 1) * 10
      if (item.sortOrder === nextSortOrder) continue
      await saveStandardLibraryCategory({ name: item.name, code: item.code, parentId: item.parentId, sortOrder: nextSortOrder, isActive: item.isActive, expectedRowVersion: item.rowVersion }, props.token, item.id)
    }
    await load()
    ElMessage.success('分类顺序已更新')
  } catch (error) {
    await load()
    ElMessage.error(error instanceof Error ? error.message : '分类排序失败')
  } finally {
    categorySorting.value = false
  }
}

async function openAddDialog() {
  if (!props.canManage) return
  eligibleMaterials.value = []
  eligibleQuery.value = ''
  eligibleBrand.value = ''
  categoryQuery.value = ''
  selectedEligibleMaterials.value = []
  eligibleSelectionTable.value?.clearSelection()
  selectedAddCategoryIds.value = selectedCategoryId.value ? [selectedCategoryId.value] : []
  addDialogOpen.value = true
  await nextTick()
  addCategoryTree.value?.setCheckedKeys(selectedAddCategoryIds.value)
}

function cancelEligibleSearch() {
  clearTimeout(eligibleSearchTimer)
  eligibleSearchController?.abort()
  eligibleLoading.value = false
}

function queueEligibleSearch() {
  cancelEligibleSearch()
  eligibleMaterials.value = []
  eligibleBrand.value = ''
  eligibleSearchError.value = ''
  const keyword = eligibleQuery.value.trim()
  if (!addDialogOpen.value || keyword.length < 4) return
  const controller = new AbortController()
  eligibleSearchController = controller
  eligibleLoading.value = true
  eligibleSearchTimer = setTimeout(async () => {
    try {
      const items = await listMaterials(props.token, keyword, false, 100, '', controller.signal)
      if (!controller.signal.aborted) eligibleMaterials.value = items.filter(item => !item.isArchived && item.approvalStatus === 'Approved' && item.u9SyncConfirmed)
    } catch (error) {
      if (!controller.signal.aborted) eligibleSearchError.value = userErrorMessage(error instanceof Error ? error.message : '料品搜索失败，请重试。')
    } finally {
      if (!controller.signal.aborted) eligibleLoading.value = false
    }
  }, 300)
}

watch([() => eligibleQuery.value.trim(), addDialogOpen], queueEligibleSearch, { flush: 'sync' })
watch(() => props.token, () => { addDialogOpen.value = false })

function updateAddCategories(_category: CategoryTreeNode, state: { checkedKeys: Array<string | number> }) {
  selectedAddCategoryIds.value = state.checkedKeys.filter((key): key is string => typeof key === 'string')
}

function updateEligibleSelection(items: PdmMaterial[]) {
  selectedEligibleMaterials.value = items
}

function removeEligibleSelection(item: PdmMaterial) {
  selectedEligibleMaterials.value = selectedEligibleMaterials.value.filter(selected => selected.id !== item.id)
  eligibleSelectionTable.value?.toggleRowSelection(item, false)
}

function clearEligibleSelection() {
  selectedEligibleMaterials.value = []
  eligibleSelectionTable.value?.clearSelection()
}

async function addMaterials() {
  if (!selectedAddCategoryIds.value.length || !selectedEligibleIds.value.length) return ElMessage.warning('请选择分类和料品')
  adding.value = true
  try {
    await addStandardLibraryMaterials(selectedAddCategoryIds.value, selectedEligibleIds.value, props.token)
    addDialogOpen.value = false
    await load(false)
    ElMessage.success('料品已加入标准库')
  } catch (error) { ElMessage.error(error instanceof Error ? error.message : '加入标准库失败') } finally { adding.value = false }
}

function openSelectedMaintenance() {
  if (!canMaintainSelected.value) return
  if (selectedRows.value.length === 1 && selectedRows.value[0]?.kit) { assetKit.value = selectedRows.value[0].kit; return }
  if (selectedRows.value.length === 1 && props.canEdit) {
    void openEditor(selectedRows.value[0])
    return
  }
  batchRecommendation.value = 'Keep'
  batchSelectionAdviceEnabled.value = false
  batchSelectionAdvice.value = ''
  batchReferencePriceEnabled.value = false
  batchReferencePrice.value = null
  clearBatchImages()
  batchError.value = ''; batchProgress.value = ''
  batchEditorOpen.value = true
}

async function removeSelectedMaterials() {
  if (removeDisabledReason.value || removing.value || loading.value) return
  const selected = selectedRows.value.map(row => ({ materialId: row.material.id, categoryIds: [...new Set(row.categories.map(category => category.id))] }))
  const category = categories.value.find(item => item.id === selectedCategoryId.value)
  const scope = category ? `“${category.name}”及其下级分类` : '全部收录分类'
  removing.value = true
  try {
    try {
      await ElMessageBox.confirm(`将从${scope}中移除所选 ${selected.length} 个真实物料。料品主档、附件和已有BOM引用均保留，可通过“新增物料”重新收录。`, '移出标准库', { type: 'warning', confirmButtonText: '确认移除', cancelButtonText: '取消' })
    } catch { return }
    let completed = 0
    try {
      for (const row of selected) {
        for (const categoryId of row.categoryIds) await removeStandardLibraryMaterial(categoryId, row.materialId, props.token)
        completed++
      }
      ElMessage.success(`已从标准库当前范围移除 ${completed} 个物料`)
    } catch (error) {
      ElMessage.error(`移除未全部完成（完成 ${completed}/${selected.length} 项）：${userErrorMessage(error instanceof Error ? error.message : '移除失败，请刷新后重试')}`)
    }
    selectedRows.value = []
    materialTable.value?.clearSelection()
    await load(false)
    if (page.value > 1 && rows.value.length === 0) { page.value = Math.max(1, Math.ceil(total.value / pageSize.value)); await load(false) }
  } finally { removing.value = false }
}


function openKitCategory(kit: EngineeringKit) {
  movingKit.value = kit
  moveCategoryId.value = kit.standardLibraryCategoryId ?? ''
}

async function applyKitActivity(kit: EngineeringKit) {
  assetKit.value = kit
  if (detailKit.value?.id === kit.id) detailKit.value = kit
  selectedRows.value = []
  materialTable.value?.clearSelection()
  await load(false)
}

async function toggleMaterialActive() {
  const material = editingMaterial.value
  if (!material || !props.canEdit || materialActivityPending.value || saving.value || uploadingKind.value) return
  materialActivityPending.value = true
  try {
    const activate = material.isArchived
    try {
      await ElMessageBox.confirm(activate ? '恢复启用此料品，保留原料号和审批记录。' : '停用后禁止新的BOM引用，保留料品主档、附件和已有BOM引用；只停用PLM料品，不会停用或删除U9C料品。',
        activate ? '恢复启用料品' : '停用料品', { type: 'warning', confirmButtonText: activate ? '确认启用' : '确认停用', cancelButtonText: '取消' })
    } catch { return }
    const saved = await (activate ? reactivateMaterial : archiveMaterial)(material.id, material.rowVersion, props.token)
    editingMaterial.value = saved
    editorForm.expectedRowVersion = saved.rowVersion
    selectedRows.value = []
    materialTable.value?.clearSelection()
    await load(false)
    if (page.value > 1 && !rows.value.length) { page.value = Math.max(1, Math.ceil(total.value / pageSize.value)); await load(false) }
    ElMessage.success(activate ? '料品已恢复启用' : '料品已停用')
  } catch (error) { ElMessage.error(userErrorMessage(error instanceof Error ? error.message : '料品启停操作失败，请刷新后重试')) }
  finally { materialActivityPending.value = false }
}

async function saveKitCategory() {
  if (!movingKit.value || !moveCategoryId.value || movingCategory.value) return
  movingCategory.value = true
  try {
    const saved = await moveEngineeringKitCategory(movingKit.value.id, moveCategoryId.value, movingKit.value.rowVersion, props.token)
    if (assetKit.value?.id === saved.id) assetKit.value = saved
    movingKit.value = null
    await load(false)
    ElMessage.success('套件分类已更新，已发布型号保持不变')
  } catch (error) { ElMessage.error(error instanceof Error ? error.message : '套件分类更新失败') }
  finally { movingCategory.value = false }
}

async function saveBatchMaintenance() {
  if (!batchHasChanges.value || batchSaving.value) return
  batchSaving.value = true
  batchError.value = ''
  const selected = [...selectedRows.value]
  let completed = 0
  try {
    for (const row of selected) {
      batchProgress.value = `正在保存 ${completed + 1}/${selected.length}：${row.material.materialCode}`
      let current = row.material
      if (props.canManage && batchRecommendation.value !== 'Keep') {
        const isRecommended = batchRecommendation.value === 'Recommended'
        if (current.isRecommended !== isRecommended) current = await setStandardLibraryRecommendation(current.id, isRecommended, current.rowVersion, props.token)
        row.material = current
      }
      if (props.canEdit && (batchSelectionAdviceEnabled.value || batchReferencePriceEnabled.value)) {
        const input = materialInput(current)
        if (batchSelectionAdviceEnabled.value) input.selectionAdvice = batchSelectionAdvice.value
        if (batchReferencePriceEnabled.value) input.referencePrice = batchReferencePrice.value
        const result = current.approvalStatus === 'Approved'
          ? await changeApprovedMaterial(current.id, input, props.token)
          : { material: await updateMaterial(current.id, input, props.token), task: null }
        if (result.task) throw new Error('PLM专属字段产生了非预期的U9C任务，请人工复核')
        current = result.material
        row.material = current
      }
      if (batchCanUploadImages.value && batchImagesEnabled.value) {
        let uploaded = batchUploadedImages.get(current.id)
        if (!uploaded) { uploaded = new Map(); batchUploadedImages.set(current.id, uploaded) }
        const replacements: MaterialAttachment[] = []
        for (const image of batchImages.value) {
          let attachment = uploaded.get(image.file)
          if (!attachment) {
            attachment = await uploadMaterialAttachment(current.id, 'CoverImage', image.file, props.token, percent => {
              batchProgress.value = `正在上传 ${completed + 1}/${selected.length}：${current.materialCode} · ${image.file.name} ${percent}%`
            })
            uploaded.set(image.file, attachment)
          }
          replacements.push(attachment)
        }
        const cover = batchCoverFile.value ? uploaded.get(batchCoverFile.value) ?? null : null
        const signature = JSON.stringify([replacements.map(image => image.id), cover?.id ?? null])
        if (batchReplacedImages.get(current.id) !== signature) {
          current = await replaceMaterialImages(current.id, replacements.map(image => image.id), cover?.id ?? null, current.rowVersion, props.token)
          row.material = current
          row.coverImage = cover
          batchReplacedImages.set(current.id, signature)
        }
      }
      completed += 1
    }
    batchEditorOpen.value = false
    selectedRows.value = []
    materialTable.value?.clearSelection()
    await load(false)
    ElMessage.success(`已批量维护 ${completed} 个料品`)
  } catch (error) {
    batchError.value = `已完成 ${completed}/${selected.length}；${userErrorMessage(error instanceof Error ? error.message : '批量维护失败，请重试。')}`
    ElMessage.error(batchError.value)
  } finally {
    batchSaving.value = false
    batchProgress.value = ''
  }
}

async function openAttachments(row: StandardLibraryMaterial, kind: 'Model3D' | 'Document') {
  try {
    attachmentMaterialId.value = row.material.id
    attachmentDialogTitle.value = `${row.material.materialCode} · ${kind === 'Model3D' ? '3D' : '资料'}`
    attachmentItems.value = await listMaterialAttachments(row.material.id, props.token, kind)
    attachmentDialogOpen.value = true
  } catch (error) { ElMessage.error(error instanceof Error ? error.message : '附件加载失败') }
}

async function downloadAttachment(attachment: MaterialAttachment, materialId = attachmentMaterialId.value || editingMaterial.value?.id || '') {
  try { await downloadMaterialAttachment(materialId, attachment, props.token) } catch (error) { ElMessage.error(error instanceof Error ? error.message : '附件下载失败') }
}

function materialInput(item: PdmMaterial): SaveMaterialInput {
  return { materialCode: item.materialCode, name: item.name, kind: item.kind, supplyMode: item.supplyMode, unitCode: item.unitCode, categoryCode: item.categoryCode ?? item.u9CategoryCode ?? '', specification: item.specification ?? '', material: item.material ?? '', remark: item.remark ?? '', brand: item.brand ?? '', surfaceTreatment: item.surfaceTreatment ?? '', purchaseLink: item.purchaseLink ?? '', selectionAdvice: item.selectionAdvice ?? '', referencePrice: item.referencePrice ?? null, model3DLink: item.model3DLink ?? '', documentLink: item.documentLink ?? '', isRecommended: item.isRecommended ?? false, weight: item.weight && item.weight > 0 ? item.weight : null, weightUnit: item.weightUnit ?? 'kg', expectedRowVersion: item.rowVersion }
}

async function openEditor(row: StandardLibraryMaterial) {
  if (row.kit) { assetKit.value = row.kit; return }
  editingMaterial.value = row.material
  Object.assign(editorForm, materialInput(row.material))
  try {
    editorAttachments.value = await listMaterialAttachments(row.material.id, props.token)
  } catch (error) {
    editorAttachments.value = []
    ElMessage.warning(error instanceof Error ? `附件列表加载失败：${error.message}` : '附件列表加载失败')
  }
  try {
    replaceEditorCoverUrl(row.coverImage ? await materialAttachmentObjectUrl(row.material.id, row.coverImage.id, props.token) : '')
  } catch (error) {
    replaceEditorCoverUrl('')
    ElMessage.warning(error instanceof Error ? `封面加载失败：${error.message}` : '封面加载失败')
  }
  editorOpen.value = true
}

function replaceEditorCoverUrl(next: string) {
  if (editorCoverUrl.value.startsWith('blob:')) URL.revokeObjectURL(editorCoverUrl.value)
  editorCoverUrl.value = next
}

function applyMaterialCategory(code: string) {
  const category = materialCategories.value.find(item => item.code === code)
  if (!category) return
  if (category.pdmKind) editorForm.kind = category.pdmKind
  editorForm.supplyMode = category.defaultSupplyMode
}

async function saveEditor() {
  const current = editingMaterial.value
  if (!current) return
  saving.value = true
  try {
    const result = current.approvalStatus === 'Approved'
      ? await changeApprovedMaterial(current.id, { ...editorForm }, props.token)
      : { material: await updateMaterial(current.id, { ...editorForm }, props.token), task: null }
    editingMaterial.value = result.material
    Object.assign(editorForm, materialInput(result.material))
    editorOpen.value = false
    await load(false)
    ElMessage.success(result.task ? '料品已更新并生成U9C修改预览，需明确确认后执行' : 'PLM专属字段已更新，不生成U9C任务')
  } catch (error) { ElMessage.error(error instanceof Error ? error.message : '料品保存失败') } finally { saving.value = false }
}

async function applyEditorCover(saved: PdmMaterial, attachment: MaterialAttachment | null) {
  editingMaterial.value = saved
  editorForm.expectedRowVersion = saved.rowVersion
  for (const row of [...rows.value, ...selectedRows.value]) {
    if (row.material.id !== saved.id) continue
    row.material = saved
    row.coverImage = attachment
  }
  if (coverUrls[saved.id]) URL.revokeObjectURL(coverUrls[saved.id])
  delete coverUrls[saved.id]
  replaceEditorCoverUrl('')
  if (!attachment) return
  replaceEditorCoverUrl(await materialAttachmentObjectUrl(saved.id, attachment.id, props.token))
  coverUrls[saved.id] = await materialAttachmentObjectUrl(saved.id, attachment.id, props.token, 120)
}

async function handleEditorFiles(kind: MaterialAttachmentKind, event: Event) {
  const input = event.target as HTMLInputElement
  const files = Array.from(input.files ?? [])
  input.value = ''
  const current = editingMaterial.value
  if (!current || files.length === 0 || uploadingKind.value || changingEditorCover.value) return
  if (kind === 'CoverImage' && editorAttachments.value.filter(item => item.kind === 'CoverImage').length + files.length > materialImageLimit) {
    ElMessage.warning(`每个料品最多上传${materialImageLimit}张图片，请减少本次选择的图片数量。`)
    return
  }
  uploadingKind.value = kind
  try {
    for (const file of files) {
      const attachment = await uploadMaterialAttachment(current.id, kind, file, props.token, percent => { uploadProgress.value = percent })
      editorAttachments.value.unshift(attachment)
      if (kind === 'CoverImage' && !editingMaterial.value?.coverImageAttachmentId && !editorCoverUrl.value) {
        const saved = await setMaterialCover(current.id, attachment.id, editingMaterial.value!.rowVersion, props.token)
        await applyEditorCover(saved, attachment)
      }
    }
    ElMessage.success('附件已存档')
  } catch (error) { ElMessage.error(error instanceof Error ? error.message : '附件上传失败') } finally { uploadingKind.value = null; uploadProgress.value = 0 }
}

function editorAttachmentDeleted(attachment: MaterialAttachment) {
  editorAttachments.value = editorAttachments.value.filter(item => item.id !== attachment.id)
  const key = attachment.kind === 'Model3D' ? 'model3DAttachmentCount' : 'documentAttachmentCount'
  const count = editorAttachments.value.filter(item => item.kind === attachment.kind).length
  if (editingMaterial.value?.id === attachment.materialId) editingMaterial.value[key] = count
  for (const row of [...rows.value, ...selectedRows.value]) {
    if (row.material.id === attachment.materialId) row.material[key] = count
  }
}

async function changeEditorCover(attachment: MaterialAttachment | null) {
  if (!editingMaterial.value || changingEditorCover.value || uploadingKind.value || !props.canEdit) return
  changingEditorCover.value = true
  try {
    const saved = await setMaterialCover(editingMaterial.value.id, attachment?.id ?? null, editingMaterial.value.rowVersion, props.token)
    await applyEditorCover(saved, attachment)
    ElMessage.success(attachment ? '已设为封面' : '当前封面已清除，历史图片仍保留')
  } catch (error) { ElMessage.error(error instanceof Error ? error.message : '封面设置失败') }
  finally { changingEditorCover.value = false }
}

onMounted(async () => {
  try { materialCategories.value = await listMaterialCategories(props.token, false) } catch { materialCategories.value = [] }
  await load()
})
onBeforeUnmount(() => {
  clearBatchImages()
  closeAttachmentPreview()
  cancelEligibleSearch()
  for (const url of Object.values(coverUrls)) if (url.startsWith('blob:')) URL.revokeObjectURL(url)
  replaceEditorCoverUrl('')
})
</script>

<template>
  <section class="standard-library" aria-label="标准库">
    <div class="standard-library__body pdm-project-list-layout">
      <aside class="standard-library__tree pdm-panel">
        <strong class="standard-library__category-title">分类筛选</strong>
        <button type="button" class="standard-library__all" :class="{ 'is-active': !selectedCategoryId }" @click="selectCategory()">全部分类</button>
        <el-tree :data="categoryTree" node-key="id" :current-node-key="selectedCategoryId || undefined" :props="{ label: 'name', children: 'children' }" :highlight-current="true" @node-click="selectCategory">
          <template #default="{ data }"><span class="standard-library__category-label" :class="{ 'is-inactive': !data.isActive }"><Folder :size="15" /><span>{{ data.name }}</span><small v-if="!data.isActive">停用</small></span></template>
        </el-tree>
        <div v-if="canManage" class="standard-library__category-actions"><el-button size="small" type="primary" @click="openCategoryManager">分类维护</el-button></div>
      </aside>
      <main class="standard-library__content pdm-panel pdm-project-table-panel">
        <div class="standard-library__toolbar">
          <el-button class="add-material-button" type="primary" :disabled="!canManage" :title="canManage ? undefined : '需要标准物料管理权限'" @click="openAddDialog">新增物料</el-button>
          <el-button class="import-material-button" :disabled="!canManage" :title="canManage ? undefined : '需要标准物料管理权限'" @click="openImport">导入</el-button>
          <el-button class="maintain-material-button" :disabled="!canMaintainSelected" @click="openSelectedMaintenance">维护</el-button>
          <el-button class="remove-material-button" type="danger" plain :disabled="!!removeDisabledReason || loading || removing" :title="removeDisabledReason || '移除标准库收录关系，保留料品主档及BOM引用'" :loading="removing" @click="removeSelectedMaterials">移除</el-button>
          <div class="filters"><el-checkbox v-model="recommendedOnly">仅看推荐</el-checkbox><el-checkbox v-model="showInactive" title="勾选后包含已停用的物料和套件" @change="searchMaterials">已停用</el-checkbox><el-input v-model="query" clearable placeholder="名称/料号/型号/品牌" @keyup.enter="searchMaterials" /><el-select v-model="brand" clearable filterable placeholder="品牌"><el-option v-for="item in brands" :key="item" :label="item" :value="item" /></el-select><el-button :loading="loading" @click="load(true)">刷新</el-button><el-button type="primary" @click="searchMaterials">搜索</el-button></div>
        </div>
        <el-table ref="materialTable" v-loading="loading" class="material-list-table" :data="rows" row-key="material.id" height="100%" stripe @selection-change="selectedRows = $event">
          <el-table-column type="selection" width="38" align="center" />
          <el-table-column label="名称" min-width="110" show-overflow-tooltip><template #default="{ row }"><el-tag v-if="row.material.isArchived || row.kit?.isActive === false" size="small" type="info">已停用</el-tag><el-tag v-if="row.material.isRecommended" size="small" type="warning">推荐</el-tag> <span v-stocked-material="row.material.materialCode">{{ row.material.name }}</span></template></el-table-column>
          <el-table-column label="图片" width="58"><template #default="{ row }"><button v-if="coverUrls[row.material.id]" class="cover-preview-button" type="button" :aria-label="`${row.material.materialCode} · 查看图片`" @click="imagePreview = { materialId: row.material.id, initialId: row.coverImage.id }"><el-image class="cover-thumb" :src="coverUrls[row.material.id]" fit="cover" /></button><span v-else>—</span></template></el-table-column>
          <el-table-column label="料号" min-width="105" prop="material.materialCode" show-overflow-tooltip ><template #default="{ row }"><span v-stocked-material="row.material.materialCode">{{ row.material.materialCode }}</span></template></el-table-column>
          <el-table-column label="型号" min-width="120" show-overflow-tooltip><template #default="{ row }">{{ row.material.specification || '—' }}</template></el-table-column>
          <el-table-column label="品牌" min-width="78" show-overflow-tooltip><template #default="{ row }">{{ row.material.brand || '—' }}</template></el-table-column>
          <el-table-column label="备注" min-width="100" show-overflow-tooltip><template #default="{ row }">{{ row.material.remark || '—' }}</template></el-table-column>
          <el-table-column label="选型建议" min-width="115" show-overflow-tooltip><template #default="{ row }">{{ row.material.selectionAdvice || '—' }}</template></el-table-column>
          <el-table-column label="3D" width="48"><template #default="{ row }"><el-button v-if="row.material.model3DAttachmentCount > 0" link type="primary" :aria-label="`${row.material.materialCode} · ${row.material.model3DAttachmentCount} 个3D文件`" :title="`${row.material.model3DAttachmentCount} 个3D文件，点击查看`" @click="openAttachments(row, 'Model3D')"><Box :size="16" aria-hidden="true" /></el-button></template></el-table-column>
          <el-table-column label="资料" width="48"><template #default="{ row }"><el-button v-if="row.material.documentAttachmentCount > 0" link type="primary" :aria-label="`${row.material.materialCode} · ${row.material.documentAttachmentCount} 份资料`" :title="`${row.material.documentAttachmentCount} 份资料，点击查看`" @click="openAttachments(row, 'Document')"><Folder :size="16" aria-hidden="true" /></el-button></template></el-table-column>
          <el-table-column label="引用" width="54"><template #default="{ row }">{{ row.material.referenceCount }}</template></el-table-column>
          <el-table-column label="U9同步" width="78"><template #default="{ row }"><span v-if="row.kit">—</span><span v-else-if="row.material.syncStatus === 'Succeeded'" class="material-sync-consistent">已一致</span><el-tag v-else :type="row.material.syncStatus === 'NeedsReview' ? 'danger' : 'warning'">{{ row.material.syncStatus === 'NeedsReview' ? '待复核' : '待同步' }}</el-tag></template></el-table-column>
        </el-table>
        <el-pagination v-model:current-page="page" v-model:page-size="pageSize" :total="total" :page-sizes="[50,100]" layout="total, sizes, prev, pager, next" @current-change="load(false)" @size-change="page = 1; load(false)" />
      </main>
    </div>

    <el-dialog v-model="importDialogOpen" title="按料号从ERP导入标准物料" width="min(900px, calc(100vw - 32px))" :close-on-click-modal="!importing" :close-on-press-escape="!importing" :show-close="!importing">
      <div class="standard-library-import">
        <p>只需提供料号，名称、型号、品牌、单位等信息从ERP料品资料读取。新加入的物料归入“未分类”；已在标准库的物料保留原分类。</p>
        <div class="standard-library-import__file"><el-button :disabled="importing" @click="importFileInput?.click()">选择料号文件</el-button><span>支持.xlsx、.csv、.txt；Excel只需“料号”列，建议按文本保存，保留前导0。</span><input ref="importFileInput" type="file" hidden accept=".xlsx,.csv,.txt" aria-label="选择料号文件" @change="readImportFile"></div>
        <el-input v-model="importText" type="textarea" :rows="5" :disabled="importing" aria-label="待导入料号" placeholder="每行一个料号，也可用逗号、空格或分号分隔" />
        <p>已去重：{{ importCodes.length }} 个料号，单次最多200个。</p>
        <p v-if="importError" role="alert" class="pdm-inline-error">{{ importError }}</p>
        <template v-if="importResult">
          <el-alert :type="importResult.failedCount ? 'warning' : 'success'" :closable="false" :title="`已导入 ${importResult.importedCount} 个，已存在 ${importResult.existingCount} 个，失败 ${importResult.failedCount} 个`" />
          <el-table :data="importResult.rows" max-height="280" class="standard-library-import__results">
            <el-table-column prop="materialCode" label="料号" width="135" ><template #default="{ row }"><span v-stocked-material="row.materialCode">{{ row.materialCode }}</span></template></el-table-column><el-table-column prop="name" label="名称" min-width="120" show-overflow-tooltip ><template #default="{ row }"><span v-stocked-material="row.materialCode">{{ row.name }}</span></template></el-table-column><el-table-column prop="specification" label="型号" min-width="120" show-overflow-tooltip /><el-table-column prop="brand" label="品牌" width="95" /><el-table-column prop="message" label="导入结果" min-width="230" />
          </el-table>
        </template>
      </div>
      <template #footer><el-button :disabled="importing" @click="importDialogOpen = false">关闭</el-button><el-button type="primary" :disabled="!canManage || !importCodes.length" :loading="importing" @click="importMaterials">从ERP导入</el-button></template>
    </el-dialog>

    <EngineeringKitAssets v-if="assetKit" :kit="assetKit" :token="token" :can-edit="canEdit || canManage" :can-manage="canManage" @close="assetKit = null" @changed="load(false)" @details="detailKit = assetKit" @category="openKitCategory(assetKit)" @activity-changed="applyKitActivity" />
    <EngineeringKitDetails :kit="detailKit" @close="detailKit = null" />
    <MaterialImagePreview :model-value="!!imagePreview" :material-id="imagePreview?.materialId ?? ''" :initial-id="imagePreview?.initialId" :token="token" @update:model-value="imagePreview = null" />
    <el-dialog :model-value="!!movingKit" title="调整套件标准分类" width="480px" @close="movingKit = null">
      <el-select v-model="moveCategoryId" filterable placeholder="选择标准物料分类" style="width:100%"><el-option v-for="item in categories.filter(item => item.isActive && !isUncategorized(item))" :key="item.id" :label="item.code ? `${item.name}（${item.code}）` : item.name" :value="item.id" /></el-select>
      <template #footer><el-button :disabled="movingCategory" @click="movingKit = null">取消</el-button><el-button type="primary" :disabled="!moveCategoryId" :loading="movingCategory" @click="saveKitCategory">保存分类</el-button></template>
    </el-dialog>

    <el-dialog v-model="categoryManagerOpen" title="标准分类维护" width="min(860px, calc(100vw - 32px))">
      <div class="category-manager">
        <section class="category-manager__list">
          <header class="category-manager__header"><div><strong>多级分类</strong><span>使用上下箭头调整同级顺序</span></div><el-button type="primary" size="small" @click="openCategoryEditor()">新增顶级分类</el-button></header>
          <div class="category-manager__rows">
            <div v-if="!flatCategoryRows.length" class="category-manager__empty">暂无标准分类</div>
            <div v-for="item in flatCategoryRows" :key="item.category.id" class="category-manager__row" :class="{ 'is-child': item.depth > 0 }">
              <div class="category-manager__name" :style="{ paddingLeft: `${item.depth * 20}px` }"><span v-if="item.depth" class="category-branch">└</span><strong>{{ item.category.name }}</strong><span v-if="!item.depth">{{ item.category.code }}</span><el-tag size="small" :type="item.category.isActive ? 'success' : 'info'">{{ item.category.isActive ? '启用' : '停用' }}</el-tag></div>
              <div class="category-manager__actions">
                <el-button link :disabled="categorySorting || !canMoveCategory(item.category, -1)" @click="moveCategory(item.category, -1)">上移</el-button>
                <el-button link :disabled="categorySorting || !canMoveCategory(item.category, 1)" @click="moveCategory(item.category, 1)">下移</el-button>
                <el-button link type="primary" @click="openCategoryEditor(undefined, item.category.id)">添加子级</el-button>
                <el-button link @click="openCategoryEditor(item.category)">编辑</el-button>
                <el-button link type="danger" @click="deleteCategory(item.category)">删除</el-button>
              </div>
            </div>
          </div>
        </section>
        <section class="category-manager__editor">
          <template v-if="categoryFormOpen">
            <h3>{{ editingCategoryId ? '编辑分类' : '新增分类' }}</h3>
            <el-form label-position="top">
              <el-form-item label="名称" required><el-input v-model="categoryForm.name" /></el-form-item>
              <el-form-item label="分类编号"><el-input :model-value="categoryForm.parentId ? standardCategoryCode(categories, categoryForm.parentId) : categoryForm.code" readonly :placeholder="categoryForm.parentId ? '沿用所属一级分类编号' : '保存时自动生成，如 UTJ-01'" /></el-form-item>
              <el-form-item label="上级分类"><el-select v-model="categoryForm.parentId" clearable placeholder="顶级分类"><el-option v-for="item in categories.filter(value => value.id !== editingCategoryId && !isUncategorized(value))" :key="item.id" :label="item.name" :value="item.id" /></el-select></el-form-item>
              <el-form-item><el-switch v-model="categoryForm.isActive" active-text="启用" inactive-text="停用" /></el-form-item>
            </el-form>
            <div class="category-manager__editor-actions"><el-button @click="closeCategoryEditor">取消</el-button><el-button type="primary" @click="saveCategory">保存</el-button></div>
          </template>
          <div v-else class="category-manager__placeholder"><strong>分类编辑</strong><p>在左侧新增顶级分类、添加子级或选择“编辑”。排序号由系统自动维护。</p></div>
        </section>
      </div>
      <template #footer><el-button @click="categoryManagerOpen = false">关闭</el-button></template>
    </el-dialog>

    <el-dialog v-model="addDialogOpen" class="standard-library-add-dialog" title="从料品加入标准库" width="min(1380px, calc(100vw - 32px))">
      <div class="add-material-dialog-body">
        <div class="add-material-layout">
        <section class="add-material-pane">
          <header class="add-material-pane__header searchable-material-header">
            <div><strong>可选料品</strong><span>{{ eligibleMaterials.length }} 项</span></div>
            <p>满4个字符后搜索，每次最多100项；可细化搜索。</p>
            <div class="eligible-material-filters"><el-input v-model="eligibleQuery" clearable placeholder="搜索料号/名称/型号/品牌" @keyup.enter="queueEligibleSearch" /><el-select v-model="eligibleBrand" clearable filterable :disabled="!eligibleMaterials.length" placeholder="筛选品牌"><el-option v-for="item in eligibleBrands" :key="item" :label="item" :value="item" /></el-select></div>
          </header>
          <el-table ref="eligibleSelectionTable" v-loading="eligibleLoading" :data="filteredEligibleMaterials" :empty-text="eligibleEmptyText" row-key="id" height="100%" stripe @selection-change="updateEligibleSelection">
            <el-table-column type="selection" width="38" reserve-selection />
            <el-table-column prop="materialCode" label="料号" width="118" show-overflow-tooltip ><template #default="{ row }"><span v-stocked-material="row.materialCode">{{ row.materialCode }}</span></template></el-table-column>
            <el-table-column prop="name" label="名称" min-width="125" show-overflow-tooltip ><template #default="{ row }"><span v-stocked-material="row.materialCode">{{ row.name }}</span></template></el-table-column>
            <el-table-column prop="specification" label="型号" min-width="150" show-overflow-tooltip />
            <el-table-column prop="brand" label="品牌" width="86" show-overflow-tooltip />
          </el-table>
        </section>
        <section class="add-material-pane add-material-pane--selected">
          <header class="add-material-pane__header selected-material-header">
            <div><strong>已选料品</strong><span>{{ selectedEligibleMaterials.length }} 项</span></div>
            <el-button link type="primary" :disabled="!selectedEligibleMaterials.length" @click="clearEligibleSelection">清空</el-button>
          </header>
          <el-table :data="selectedEligibleMaterials" row-key="id" height="100%" empty-text="请从左侧勾选料品">
            <el-table-column prop="materialCode" label="料号" width="118" show-overflow-tooltip ><template #default="{ row }"><span v-stocked-material="row.materialCode">{{ row.materialCode }}</span></template></el-table-column>
            <el-table-column prop="name" label="名称" min-width="120" show-overflow-tooltip ><template #default="{ row }"><span v-stocked-material="row.materialCode">{{ row.name }}</span></template></el-table-column>
            <el-table-column prop="specification" label="型号" min-width="145" show-overflow-tooltip />
            <el-table-column prop="brand" label="品牌" width="82" show-overflow-tooltip />
            <el-table-column label="操作" width="58" fixed="right"><template #default="{ row }"><el-button link type="danger" @click="removeEligibleSelection(row)">移除</el-button></template></el-table-column>
          </el-table>
        </section>
        <section class="add-material-pane add-material-pane--categories add-material-target">
          <header class="add-material-pane__header selected-material-header category-filter-header">
            <div><strong><span class="required-mark">*</span> 目标标准分类</strong><span>{{ selectedAddCategoryIds.length }} 项</span></div>
            <el-input v-model="categoryQuery" clearable placeholder="筛选分类名称" />
          </header>
          <el-tree ref="addCategoryTree" class="add-material-category-tree" :data="categoryTree" node-key="id" show-checkbox check-strictly default-expand-all :filter-node-method="filterCategoryNode" :props="{ label: 'name', children: 'children', disabled: 'disabled' }" empty-text="暂无可选分类" @check="updateAddCategories" />
          <p class="field-help">所选分类将批量应用到中间全部已选料品。</p>
        </section>
        </div>
      </div>
      <template #footer>
        <el-button @click="addDialogOpen = false">取消</el-button>
        <el-button type="primary" :loading="adding" :disabled="!selectedEligibleIds.length || !selectedAddCategoryIds.length" @click="addMaterials">批量加入指定分类</el-button>
      </template>
    </el-dialog>

    <MaterialEditorDialog v-if="batchEditorOpen" v-model="batchEditorOpen" title="批量维护标准物料" :token="token" :form="editorForm" :categories="materialCategories" material-code-placeholder="" :attachments="[]" :saving="batchSaving" :can-edit="!batchSaving && (canEdit || canManage)">
      <template #intro><p class="batch-material-note">已选择 {{ selectedRows.length }} 个料品；仅批量修改PLM专属字段，不生成U9C任务。其他字段只读显示，不同值分别保留。</p></template>
      <template #images>
        <section class="batch-material-images" aria-label="批量料品图片">
          <header><strong>图片（{{ batchImages.length }}/{{ materialImageLimit }}）</strong><el-checkbox v-model="batchImagesEnabled" :disabled="!batchCanUploadImages || batchSaving">修改图片与封面</el-checkbox><el-button v-if="batchImagesEnabled && batchCoverFile" link :disabled="batchSaving" @click="batchCoverFile = null">清除封面</el-button><el-button :disabled="!batchCanUploadImages || batchSaving || batchImages.length >= materialImageLimit" @click="batchImageInput?.click()">上传图片</el-button></header>
          <input ref="batchImageInput" class="batch-material-file-input" type="file" :accept="materialImageAccept" multiple :disabled="!batchCanUploadImages || batchSaving" aria-label="选择批量料品图片" @change="selectBatchImages">
          <div class="batch-material-image-cards"><article v-for="(image, index) in batchImages" :key="image.url"><img :src="image.url" :alt="image.file.name"><span :title="image.file.name">{{ image.file.name }}</span><span v-if="image.file === batchCoverFile" class="batch-material-cover">封面</span><el-button v-else link type="primary" :disabled="batchSaving" :aria-label="`将 ${image.file.name} 设为封面`" @click="batchCoverFile = image.file; batchImagesEnabled = true">设为封面</el-button><el-button link type="danger" :disabled="batchSaving" :aria-label="`移除本次图片 ${image.file.name}`" @click="removeBatchImage(index)">移除</el-button></article><MaterialImageAdd v-if="batchImages.length < materialImageLimit" :disabled="!batchCanUploadImages || batchSaving" @click="batchImageInput?.click()" /></div>
          <p class="batch-material-note">勾选后，以上图片及封面将统一替换所有已选料品的原图片和封面；未勾选则保持各自原值。每个料品最多{{ materialImageLimit }}张，勾选但不保留图片时将清空图片与封面。</p>
          <p v-if="!batchCanUploadImages" class="batch-material-note">{{ canEdit ? '所选料品包含已停用料品，图片仅供查看。' : '当前权限下图片仅供查看。' }}</p>
        </section>
      </template>
      <template #fields>
        <el-form-item v-for="field in batchReadonlyFields" :key="field.key" :label="field.label"><el-input :model-value="batchValue(field.key)" :title="batchValue(field.key)" disabled /></el-form-item>
        <el-form-item label="参考价格"><template #label><el-checkbox v-model="batchReferencePriceEnabled" :disabled="!canEdit">修改参考价格</el-checkbox></template><el-input-number v-if="batchReferencePriceEnabled && canEdit" v-model="batchReferencePrice" :min="0" :precision="2" :step="10" controls-position="right" placeholder="留空表示清除" /><el-input v-else :model-value="batchValue('referencePrice')" disabled /></el-form-item>
        <el-form-item label="推荐属性"><el-select v-if="canManage" v-model="batchRecommendation" :disabled="batchSaving"><el-option label="保持不变" value="Keep" /><el-option label="设为推荐" value="Recommended" /><el-option label="取消推荐" value="NotRecommended" /></el-select><el-input v-else :model-value="batchValue('isRecommended')" disabled /></el-form-item>
        <el-form-item label="料品采购链接"><el-input :model-value="batchValue('purchaseLink')" disabled /></el-form-item>
        <el-form-item class="material-editor-grid__wide" label="备注"><el-input :model-value="batchValue('remark')" disabled /></el-form-item>
        <el-form-item label="选型建议"><template #label><el-checkbox v-model="batchSelectionAdviceEnabled" :disabled="!canEdit">修改选型建议</el-checkbox></template><el-input v-if="batchSelectionAdviceEnabled && canEdit" v-model="batchSelectionAdvice" maxlength="1000" show-word-limit /><el-input v-else :model-value="batchValue('selectionAdvice')" disabled /></el-form-item>
      </template>
      <template #assets><section class="batch-material-assets" aria-label="料品附件（只读）"><section v-for="kind in ['Model3D', 'Document']" :key="kind"><header><strong>{{ kind === 'Model3D' ? '3D' : '资料' }}附件</strong><span>只读，按料品分别保留</span></header><table><thead><tr><th>料号</th><th>附件数量</th></tr></thead><tbody><tr v-for="row in selectedRows" :key="row.material.id"><td>{{ row.material.materialCode }}</td><td>{{ (kind === 'Model3D' ? row.material.model3DAttachmentCount : row.material.documentAttachmentCount) ?? 0 }}</td></tr></tbody></table></section></section></template>
      <template #footer><p v-if="batchProgress" class="batch-material-note" role="status">{{ batchProgress }}</p><el-alert v-if="batchError" class="batch-material-error" type="error" :closable="false" show-icon :title="batchError" /><el-button :disabled="batchSaving" @click="batchEditorOpen = false">取消</el-button><el-button type="primary" :loading="batchSaving" :disabled="!batchHasChanges" @click="saveBatchMaintenance">批量保存</el-button></template>
    </MaterialEditorDialog>

    <el-dialog v-model="attachmentDialogOpen" :title="attachmentDialogTitle" width="620px" @close="closeAttachmentPreview"><el-table :data="attachmentItems" max-height="420"><el-table-column prop="originalFileName" label="文件名" min-width="260" show-overflow-tooltip /><el-table-column prop="uploadedBy" label="上传人" width="100" /><el-table-column label="操作" width="140"><template #default="{ row }"><el-button link type="primary" :disabled="!canPreviewAttachment(row)" :title="canPreviewAttachment(row) ? '预览附件' : '此格式暂不支持在线预览，请下载查看'" @click="previewAttachment(row)">预览</el-button><el-button link type="primary" @click="downloadAttachment(row)">下载</el-button></template></el-table-column></el-table></el-dialog>

    <el-dialog v-model="attachmentPreviewOpen" :title="attachmentPreview?.originalFileName || '附件预览'" width="min(1100px, 95vw)" append-to-body @close="closeAttachmentPreview">
      <p v-if="attachmentPreviewLoading" role="status">正在加载附件预览…</p>
      <p v-else-if="attachmentPreviewError" role="alert">{{ attachmentPreviewError }}</p>
      <PdfDrawingViewer v-else-if="attachmentPreviewUrl && /\.pdf$/i.test(attachmentPreview?.originalFileName || '')" :url="attachmentPreviewUrl" />
      <img v-else-if="attachmentPreviewUrl" :src="attachmentPreviewUrl" :alt="attachmentPreview?.originalFileName" style="display:block;max-width:100%;max-height:75vh;margin:auto" @error="attachmentPreviewError = '图片预览失败，请重试或下载查看。'" />
      <template #footer><el-button v-if="attachmentPreview" @click="previewAttachment(attachmentPreview)">重新加载</el-button><el-button v-if="attachmentPreview" type="primary" @click="downloadAttachment(attachmentPreview)">下载</el-button></template>
    </el-dialog>

    <MaterialEditorDialog :token="token" v-model="editorOpen" :editing-id="editingMaterial?.id" :form="editorForm" :categories="creatableMaterialCategories" material-code-placeholder="" :attachments="editorAttachments" :saving="saving || materialActivityPending || changingEditorCover" :uploading-kind="uploadingKind" :upload-progress="uploadProgress" :cover-url="editorCoverUrl" :cover-attachment-id="editingMaterial?.coverImageAttachmentId" :changing-cover="changingEditorCover" :u9-fields-locked="Boolean(editingMaterial && ['Pending', 'NeedsReview'].includes(editingMaterial.syncStatus))" :can-edit="canEdit && !editingMaterial?.isArchived && !materialActivityPending" @category-change="applyMaterialCategory" @attachment-files="handleEditorFiles" :can-delete-attachments="canEdit && !editingMaterial?.isArchived" @attachment-deleted="editorAttachmentDeleted" @download-attachment="attachment => downloadAttachment(attachment, editingMaterial?.id)" @clear-cover="changeEditorCover(null)" @set-cover="changeEditorCover" @save="saveEditor">
      <template #actions><el-button v-if="editingMaterial && canEdit" class="standard-material-activity" :type="editingMaterial.isArchived ? 'primary' : 'danger'" :loading="materialActivityPending" :disabled="saving || !!uploadingKind || materialActivityPending" @click="toggleMaterialActive">{{ editingMaterial.isArchived ? '恢复启用' : '停用' }}</el-button></template>
    </MaterialEditorDialog>
  </section>
</template>

<style scoped>
.batch-material-note{margin:6px 0 12px;color:var(--pdm-muted);font-size:12px;line-height:1.6}.batch-material-images{margin-bottom:12px}.batch-material-images header,.batch-material-assets header{display:flex;align-items:center;gap:8px;margin-bottom:8px}.batch-material-images header strong,.batch-material-assets header strong{flex:1}.batch-material-image-cards{display:flex;flex-wrap:wrap;gap:8px}.batch-material-image-cards article{display:flex;flex-direction:column;align-items:center;box-sizing:border-box;width:112px;padding:4px;border:1px solid var(--pdm-border);border-radius:4px;gap:3px;overflow:hidden}.batch-material-image-cards img{width:102px;height:82px;object-fit:contain;background:var(--pdm-surface-muted)}.batch-material-image-cards article>span{width:100%;font-size:11px;text-align:center;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}.batch-material-cover{display:flex;align-items:center;justify-content:center;height:24px;color:var(--pdm-theme-accent);font-size:12px}.batch-material-image-cards :deep(.el-button){height:24px;min-height:0}.batch-material-file-input{position:absolute;width:1px;height:1px;opacity:0}.batch-material-assets{display:flex;flex-direction:column;gap:12px;margin-top:12px}.batch-material-assets header>span{color:var(--pdm-muted);font-size:12px}.batch-material-assets table{width:100%;border-collapse:collapse;table-layout:fixed;font-size:12px}.batch-material-assets th,.batch-material-assets td{height:30px;padding:0 8px;border-bottom:1px solid var(--pdm-border-soft);text-align:left}.batch-material-assets th{background:var(--pdm-table-header)}.batch-material-assets th:last-child,.batch-material-assets td:last-child{width:128px;text-align:center}.material-editor-grid__wide{grid-column:span 2}.batch-material-error{margin-bottom:10px;text-align:left;overflow-wrap:anywhere}@media(max-width:760px){.material-editor-grid__wide{grid-column:auto}}
.cover-preview-button{display:inline-flex;align-items:center;justify-content:center;width:26px;height:26px;margin:0;padding:0;border:0;background:transparent;vertical-align:middle;cursor:pointer}
.cover-preview-button .cover-thumb{width:26px;height:26px;border-radius:3px}
.material-list-table :deep(.el-table__body .el-button){height:24px;min-height:0;padding-top:0;padding-bottom:0;vertical-align:middle}
.material-sync-consistent{color:var(--el-color-success,var(--pdm-green))}
.standard-library{font-size:var(--pdm-font-size-body);display:flex;flex:1 1 auto;height:100%;min-height:0;flex-direction:column;overflow:hidden}.standard-library__header{display:flex;padding:16px 20px;border-bottom:1px solid var(--pdm-border-soft);align-items:center;justify-content:space-between}.standard-library__header h1{margin:0;font-size:18px}.standard-library__header p{margin:4px 0 0;color:var(--pdm-muted);font-size:12px}.standard-library__body{display:grid;min-height:0;flex:1}.standard-library .standard-library__tree{box-sizing:border-box;width:100%;max-width:none;display:flex;min-height:0;padding:10px 6px;flex-direction:column;overflow:auto;font-size:var(--pdm-font-size-body)}.standard-library .standard-library__tree :deep(.el-tree-node__content){border-radius:0}.standard-library .standard-library__tree :deep(.el-tree--highlight-current .el-tree-node.is-current > .el-tree-node__content){color:inherit}.standard-library__category-title{padding:5px 10px 12px;border-bottom:1px solid var(--pdm-border-soft)}.standard-library__all{border:0;border-radius:5px;background:transparent;text-align:left;padding:10px;margin:6px 0;color:var(--pdm-text);font:inherit;cursor:pointer}.standard-library__all.is-active{background:var(--pdm-theme-accent-soft);color:var(--pdm-theme-accent)}.standard-library__category-label{width:100%;display:flex;gap:6px;align-items:center;overflow:hidden}.standard-library__category-label svg{flex-shrink:0;color:var(--pdm-theme-accent)}.standard-library__category-label.is-inactive,.standard-library__category-label small{color:var(--pdm-muted)}.standard-library__category-actions{margin-top:auto;padding:14px 8px 5px}.standard-library .standard-library__tree .standard-library__category-actions :deep(.el-button){font-size:12px}.all-category{margin-bottom:6px;padding:8px;border:0;border-radius:5px;background:transparent;text-align:left;cursor:pointer}.all-category.active{background:var(--pdm-theme-accent-soft);color:var(--pdm-theme-accent)}.standard-library__content{display:flex;min-width:0;max-width:100%;min-height:0;padding:12px;flex-direction:column;gap:10px}.standard-library__toolbar{display:flex;align-items:center;justify-content:space-between;gap:12px}.scope-label{margin-left:12px;color:var(--pdm-muted)}.filters{display:flex;align-items:center;gap:8px}.filters .el-input{width:250px}.filters .el-select{width:130px}.category-tags{display:flex;flex-wrap:wrap;gap:3px}.cover-thumb{width:44px;height:44px;border-radius:5px}.standard-library__content :deep(.el-table){flex:1}.standard-library__content :deep(.el-pagination){justify-content:flex-end}.category-manager{display:grid;grid-template-columns:minmax(0,1.45fr) minmax(270px,.85fr);gap:14px;font-size:12px}.category-manager :deep(.el-button),.category-manager :deep(.el-tag),.category-manager :deep(.el-form-item__label),.category-manager :deep(.el-input__inner),.category-manager :deep(.el-select__selected-item),.category-manager :deep(.el-switch__label){font-size:12px}.category-manager__list,.category-manager__editor{min-width:0;padding:12px;border:1px solid var(--pdm-border-soft);border-radius:8px}.category-manager__header{display:flex;margin-bottom:10px;align-items:center;justify-content:space-between;gap:12px}.category-manager__header div{display:flex;flex-direction:column;gap:3px}.category-manager__header span,.category-manager__placeholder p{color:var(--pdm-muted);font-size:12px}.category-manager__rows{max-height:500px;border-top:1px solid var(--pdm-surface-muted);overflow:auto}.category-manager__row{display:flex;min-height:42px;padding:6px 4px;border-bottom:1px solid var(--pdm-surface-muted);align-items:center;justify-content:space-between;gap:10px}.category-manager__name{display:flex;min-width:0;align-items:center;gap:6px}.category-manager__name strong{overflow:hidden;text-overflow:ellipsis;white-space:nowrap}.category-manager__row.is-child .category-manager__name strong{font-weight:400}.category-branch{color:var(--pdm-muted)}.category-manager__actions{display:flex;flex-shrink:0;align-items:center;gap:1px}.category-manager__actions :deep(.el-button+.el-button){margin-left:3px}.category-manager__empty{padding:40px 12px;color:var(--pdm-muted);text-align:center}.category-manager__editor{background:var(--pdm-table-header)}.category-manager__editor h3{margin:0 0 14px;font-size:14px}.category-manager__editor :deep(.el-select){width:100%}.category-manager__editor-actions{display:flex;justify-content:flex-end;gap:8px}.category-manager__placeholder{padding:34px 10px;text-align:center}.category-manager__placeholder p{line-height:1.4}:global(.standard-library-add-dialog .el-dialog__body){display:flex;min-height:0}.add-material-dialog-body{display:flex;min-height:0;flex:1}.add-material-dialog-body,.add-material-dialog-body :deep(.el-table),.add-material-dialog-body :deep(.el-button),.add-material-dialog-body :deep(.el-form-item__label),.add-material-dialog-body :deep(.el-tree-node__label){font-size:12px}.add-material-layout{display:grid;min-height:0;flex:1;grid-template-columns:minmax(0,1fr) minmax(0,1fr) 300px;gap:12px}.add-material-pane{display:flex;min-width:0;min-height:0;padding:12px;border:1px solid var(--pdm-border-soft);border-radius:8px;background:var(--pdm-surface);flex-direction:column}.add-material-pane--selected,.add-material-pane--categories{background:var(--pdm-table-header)}.add-material-pane__header{box-sizing:border-box;display:flex;height:82px;min-height:82px;margin-bottom:10px;align-items:flex-start;justify-content:space-between;gap:12px}.add-material-pane__header div{display:flex;align-items:center;gap:8px}.add-material-pane__header strong{font-size:12px}.add-material-pane__header span{color:var(--pdm-muted);font-size:12px}.add-material-pane__header p{margin:4px 0 6px;color:var(--pdm-muted);font-size:12px}.add-material-pane__header:not(.selected-material-header){display:block}.searchable-material-header :deep(.el-input__wrapper){min-height:28px}.add-material-pane :deep(.el-table){min-height:0;flex:1}.add-material-target{margin-bottom:0}.required-mark{color:var(--pdm-danger)}.add-material-category-tree{box-sizing:border-box;width:100%;min-height:0;flex:1;overflow:auto;padding:6px 8px;border:1px solid var(--pdm-border);border-radius:4px}.add-material-category-tree :deep(.el-tree-node__content){height:26px}.field-help{margin:5px 0 0;color:var(--pdm-muted);font-size:12px;line-height:1.4}@media(max-width:1000px){.standard-library__toolbar{align-items:flex-start;flex-direction:column}.filters{width:100%;flex-wrap:wrap}.category-manager,.add-material-layout{grid-template-columns:1fr}.add-material-pane{min-height:520px}}
.standard-library__content{overflow:hidden}.standard-library__toolbar{align-items:center;justify-content:flex-start;flex-direction:row;flex-wrap:nowrap;gap:8px}.standard-library__toolbar :deep(.el-button+.el-button){margin-left:0}.standard-library__toolbar>.filters{width:auto;min-width:0;flex:1;flex-wrap:nowrap}.filters .el-input{min-width:90px;flex:1}.filters .el-select{width:150px;flex:0 0 150px}.filters .el-checkbox{flex:0 0 auto}.add-material-button,.maintain-material-button{flex-shrink:0}.material-list-table{width:100%;max-width:100%;min-width:0}.material-list-table :deep(.el-table__inner-wrapper){max-width:100%}.material-list-table :deep(.el-table__cell),.material-list-table :deep(.el-table__cell .cell),.material-list-table :deep(.el-tag),.material-list-table :deep(.el-button){font-size:11px}.material-list-table :deep(.el-table__cell){text-align:center}.material-list-table :deep(.el-table__header-wrapper .cell),.material-list-table :deep(.el-table__body-wrapper .cell){overflow:hidden;text-overflow:ellipsis;white-space:nowrap}.batch-editor-summary{margin:0 0 16px;color:var(--pdm-muted);font-size:12px}.batch-editor-summary+.el-form :deep(.el-select),.batch-editor-summary+.el-form :deep(.el-input-number){width:100%}.batch-editor-summary+.el-form>.el-checkbox{display:flex;margin:12px 0 8px}.eligible-material-filters{display:grid!important;grid-template-columns:minmax(0,1fr) 110px;gap:6px}.category-filter-header{display:block}.category-filter-header>div{margin-bottom:10px}.searchable-material-header :deep(.el-input__wrapper),.category-filter-header :deep(.el-input__wrapper){min-height:28px}@media(max-width:800px){.filters .el-input{min-width:70px}}
@media(max-width:700px){.standard-library .standard-library__tree{max-height:190px}}
</style>

<style scoped>
.standard-library-import>p,.standard-library-import__file>span{font-size:12px;color:var(--pdm-muted);line-height:1.6}.standard-library-import__file{display:flex;align-items:center;gap:12px;margin:12px 0;flex-wrap:wrap}.standard-library-import__results{margin-top:12px}.import-material-button{flex-shrink:0}
@media(max-width:1520px){.standard-library__toolbar{flex-wrap:wrap}.standard-library__toolbar>.filters{flex-basis:100%;order:2}}
@media(max-width:700px){.standard-library-import__file>span{flex-basis:100%}}
@media(max-width:700px){.standard-library .standard-library__tree{max-height:190px}}
</style>
