<script setup lang="ts">
import { ElMessage } from '../statusMessage'
import { ElMessageBox } from 'element-plus'
import { computed, nextTick, onBeforeUnmount, onMounted, reactive, ref, watch } from 'vue'
import MaterialEditorDialog from './MaterialEditorDialog.vue'
import {
  addStandardLibraryMaterials,
  changeApprovedMaterial,
  deleteStandardLibraryCategory,
  downloadMaterialAttachment,
  listMaterialAttachments,
  listMaterialCategories,
  listMaterials,
  listStandardLibraryCategories,
  listStandardLibraryMaterials,
  materialAttachmentObjectUrl,
  saveStandardLibraryCategory,
  setMaterialCover,
  setStandardLibraryRecommendation,
  updateMaterial,
  uploadMaterialAttachment,
} from '../api'
import type { MaterialAttachment, MaterialAttachmentKind, MaterialCategory, PdmMaterial, SaveMaterialInput, StandardLibraryCategory, StandardLibraryMaterial } from '../types'

const props = defineProps<{ token: string; canManage: boolean; canEdit: boolean }>()
type CategoryTreeNode = StandardLibraryCategory & { children: CategoryTreeNode[]; disabled: boolean }
type FlatCategoryRow = { category: StandardLibraryCategory; depth: number }
type MaterialSelectionTable = { clearSelection: () => void; toggleRowSelection: (row: PdmMaterial, selected: boolean) => void }
type StandardLibrarySelectionTable = { clearSelection: () => void }
type CategorySelectionTree = { filter: (value: string) => void; setCheckedKeys: (keys: string[]) => void }

const loading = ref(false)
const categories = ref<StandardLibraryCategory[]>([])
const materialCategories = ref<MaterialCategory[]>([])
const rows = ref<StandardLibraryMaterial[]>([])
const total = ref(0)
const page = ref(1)
const pageSize = ref(50)
const selectedCategoryId = ref('')
const query = ref('')
const brand = ref('')
const recommendedOnly = ref(false)
const selectedRows = ref<StandardLibraryMaterial[]>([])
const materialTable = ref<StandardLibrarySelectionTable | null>(null)
const coverUrls = reactive<Record<string, string>>({})

const batchEditorOpen = ref(false)
const batchSaving = ref(false)
const batchRecommendation = ref<'Keep' | 'Recommended' | 'NotRecommended'>('Keep')
const batchSelectionAdviceEnabled = ref(false)
const batchSelectionAdvice = ref('')
const batchReferencePriceEnabled = ref(false)
const batchReferencePrice = ref<number | null>(null)

const categoryManagerOpen = ref(false)
const categoryFormOpen = ref(false)
const categorySorting = ref(false)
const editingCategoryId = ref<string | null>(null)
const categoryForm = reactive({ name: '', parentId: '' as string, sortOrder: 0, isActive: true, expectedRowVersion: null as number | null })

const addDialogOpen = ref(false)
const eligibleMaterials = ref<PdmMaterial[]>([])
const eligibleQuery = ref('')
const eligibleBrand = ref('')
const categoryQuery = ref('')
const selectedEligibleIds = ref<string[]>([])
const selectedAddCategoryIds = ref<string[]>([])
const eligibleSelectionTable = ref<MaterialSelectionTable | null>(null)
const addCategoryTree = ref<CategorySelectionTree | null>(null)
const adding = ref(false)

const attachmentDialogOpen = ref(false)
const attachmentDialogTitle = ref('')
const attachmentItems = ref<MaterialAttachment[]>([])
const attachmentMaterialId = ref('')

const editorOpen = ref(false)
const editingMaterial = ref<PdmMaterial | null>(null)
const editorAttachments = ref<MaterialAttachment[]>([])
const editorCoverUrl = ref('')
const saving = ref(false)
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
    result.push({ category: item, depth })
    visit(item.children, depth + 1)
  })
  visit(categoryTree.value, 0)
  return result
})
const selectedEligibleMaterials = computed(() => {
  const selected = new Set(selectedEligibleIds.value)
  return eligibleMaterials.value.filter(item => selected.has(item.id))
})
const filteredEligibleMaterials = computed(() => {
  const keyword = eligibleQuery.value.trim().toLocaleLowerCase('zh-CN')
  return eligibleMaterials.value.filter(item => {
    if (eligibleBrand.value && item.brand?.trim() !== eligibleBrand.value) return false
    return !keyword || [item.materialCode, item.name, item.specification, item.brand]
      .some(value => value?.toLocaleLowerCase('zh-CN').includes(keyword))
  })
})
const eligibleBrands = computed(() => [...new Set(eligibleMaterials.value.map(item => item.brand?.trim()).filter((item): item is string => Boolean(item)))].sort((a, b) => a.localeCompare(b, 'zh-CN')))
const brands = computed(() => [...new Set(rows.value.map(item => item.material.brand?.trim()).filter((item): item is string => Boolean(item)))].sort((a, b) => a.localeCompare(b, 'zh-CN')))
const creatableMaterialCategories = computed(() => materialCategories.value.filter(item => item.allowCreate && item.isActive && item.isVisible && item.pdmKind))
const canMaintainSelected = computed(() => selectedRows.value.length > 0 && (props.canEdit || props.canManage))
const batchHasChanges = computed(() => (props.canManage && batchRecommendation.value !== 'Keep') || (props.canEdit && (batchSelectionAdviceEnabled.value || batchReferencePriceEnabled.value)))

async function load(refreshCategories = true) {
  loading.value = true
  try {
    if (refreshCategories) categories.value = await listStandardLibraryCategories(props.token, props.canManage)
    const result = await listStandardLibraryMaterials(props.token, { categoryId: selectedCategoryId.value || undefined, query: query.value, brand: brand.value, recommendedOnly: recommendedOnly.value, page: page.value, pageSize: pageSize.value })
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

function openCategoryManager() {
  categoryManagerOpen.value = true
  categoryFormOpen.value = false
  editingCategoryId.value = null
}

function openCategoryEditor(category?: StandardLibraryCategory, parentId?: string) {
  const effectiveParentId = category?.parentId ?? parentId ?? ''
  const siblings = categories.value.filter(item => (item.parentId ?? '') === effectiveParentId)
  editingCategoryId.value = category?.id ?? null
  Object.assign(categoryForm, { name: category?.name ?? '', parentId: effectiveParentId, sortOrder: category?.sortOrder ?? Math.max(0, ...siblings.map(item => item.sortOrder)) + 10, isActive: category?.isActive ?? true, expectedRowVersion: category?.rowVersion ?? null })
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
      await saveStandardLibraryCategory({ name: item.name, parentId: item.parentId, sortOrder: nextSortOrder, isActive: item.isActive, expectedRowVersion: item.rowVersion }, props.token, item.id)
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
  try {
    eligibleMaterials.value = (await listMaterials(props.token, '', false, 500)).filter(item => !item.isArchived && item.approvalStatus === 'Approved' && item.u9SyncConfirmed)
    eligibleQuery.value = ''
    eligibleBrand.value = ''
    categoryQuery.value = ''
    selectedEligibleIds.value = []
    eligibleSelectionTable.value?.clearSelection()
    selectedAddCategoryIds.value = selectedCategoryId.value ? [selectedCategoryId.value] : []
    addDialogOpen.value = true
    await nextTick()
    addCategoryTree.value?.setCheckedKeys(selectedAddCategoryIds.value)
  } catch (error) { ElMessage.error(error instanceof Error ? error.message : '可加入料品加载失败') }
}

function updateAddCategories(_category: CategoryTreeNode, state: { checkedKeys: Array<string | number> }) {
  selectedAddCategoryIds.value = state.checkedKeys.filter((key): key is string => typeof key === 'string')
}

function updateEligibleSelection(items: PdmMaterial[]) {
  selectedEligibleIds.value = items.map(item => item.id)
}

function removeEligibleSelection(item: PdmMaterial) {
  selectedEligibleIds.value = selectedEligibleIds.value.filter(id => id !== item.id)
  eligibleSelectionTable.value?.toggleRowSelection(item, false)
}

function clearEligibleSelection() {
  selectedEligibleIds.value = []
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
  if (selectedRows.value.length === 1 && props.canEdit) {
    void openEditor(selectedRows.value[0])
    return
  }
  batchRecommendation.value = 'Keep'
  batchSelectionAdviceEnabled.value = false
  batchSelectionAdvice.value = ''
  batchReferencePriceEnabled.value = false
  batchReferencePrice.value = null
  batchEditorOpen.value = true
}

async function saveBatchMaintenance() {
  if (!batchHasChanges.value || batchSaving.value) return
  batchSaving.value = true
  let completed = 0
  try {
    for (const row of selectedRows.value) {
      let current = row.material
      if (props.canManage && batchRecommendation.value !== 'Keep') {
        const isRecommended = batchRecommendation.value === 'Recommended'
        if (current.isRecommended !== isRecommended) current = await setStandardLibraryRecommendation(current.id, isRecommended, current.rowVersion, props.token)
      }
      if (props.canEdit && (batchSelectionAdviceEnabled.value || batchReferencePriceEnabled.value)) {
        const input = materialInput(current)
        if (batchSelectionAdviceEnabled.value) input.selectionAdvice = batchSelectionAdvice.value
        if (batchReferencePriceEnabled.value) input.referencePrice = batchReferencePrice.value
        const result = current.approvalStatus === 'Approved'
          ? await changeApprovedMaterial(current.id, input, props.token)
          : { material: await updateMaterial(current.id, input, props.token), task: null }
        if (result.task) throw new Error('PLM专属字段产生了非预期的U9C任务，请人工复核')
      }
      completed += 1
    }
    batchEditorOpen.value = false
    selectedRows.value = []
    materialTable.value?.clearSelection()
    await load(false)
    ElMessage.success(`已批量维护 ${completed} 个料品`)
  } catch (error) {
    await load(false)
    ElMessage.error(`已完成 ${completed}/${selectedRows.value.length}；${error instanceof Error ? error.message : '批量维护失败'}`)
  } finally {
    batchSaving.value = false
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

async function handleEditorFiles(kind: MaterialAttachmentKind, event: Event) {
  const input = event.target as HTMLInputElement
  const files = Array.from(input.files ?? [])
  input.value = ''
  const current = editingMaterial.value
  if (!current || files.length === 0) return
  uploadingKind.value = kind
  try {
    for (const file of files) {
      const attachment = await uploadMaterialAttachment(current.id, kind, file, props.token, percent => { uploadProgress.value = percent })
      editorAttachments.value.unshift(attachment)
      if (kind === 'CoverImage') {
        const saved = await setMaterialCover(current.id, attachment.id, editingMaterial.value!.rowVersion, props.token)
        editingMaterial.value = saved
        editorForm.expectedRowVersion = saved.rowVersion
        replaceEditorCoverUrl(await materialAttachmentObjectUrl(current.id, attachment.id, props.token))
      }
    }
    ElMessage.success('附件已存档')
  } catch (error) { ElMessage.error(error instanceof Error ? error.message : '附件上传失败') } finally { uploadingKind.value = null; uploadProgress.value = 0 }
}

async function clearEditorCover() {
  if (!editingMaterial.value) return
  try {
    const saved = await setMaterialCover(editingMaterial.value.id, null, editingMaterial.value.rowVersion, props.token)
    editingMaterial.value = saved
    editorForm.expectedRowVersion = saved.rowVersion
    replaceEditorCoverUrl('')
    ElMessage.success('当前封面已清除，历史图片仍保留')
  } catch (error) { ElMessage.error(error instanceof Error ? error.message : '封面清除失败') }
}

onMounted(async () => {
  try { materialCategories.value = await listMaterialCategories(props.token, false) } catch { materialCategories.value = [] }
  await load()
})
onBeforeUnmount(() => {
  for (const url of Object.values(coverUrls)) if (url.startsWith('blob:')) URL.revokeObjectURL(url)
  replaceEditorCoverUrl('')
})
</script>

<template>
  <section class="standard-library pdm-panel" aria-label="标准库">
    <div class="standard-library__body">
      <aside class="standard-library__tree">
        <div class="tree-actions"><el-button v-if="canManage" size="small" type="primary" @click="openCategoryManager">分类维护</el-button></div>
        <button class="all-category" :class="{ active: !selectedCategoryId }" @click="selectCategory()">全部标准件</button>
        <el-tree :data="categoryTree" node-key="id" :props="{ label: 'name', children: 'children' }" :highlight-current="true" @node-click="selectCategory" />
      </aside>
      <main class="standard-library__content">
        <div class="standard-library__toolbar">
          <el-button class="add-material-button" type="primary" :disabled="!canManage" :title="canManage ? undefined : '需要标准物料管理权限'" @click="openAddDialog">新增物料</el-button>
          <div class="filters"><el-button :loading="loading" @click="load(true)">刷新</el-button><el-button type="primary" @click="searchMaterials">搜索</el-button><el-input v-model="query" clearable placeholder="名称/料号/型号/品牌" @keyup.enter="searchMaterials" /><el-select v-model="brand" clearable filterable placeholder="品牌"><el-option v-for="item in brands" :key="item" :label="item" :value="item" /></el-select><el-checkbox v-model="recommendedOnly">仅看推荐</el-checkbox></div>
          <el-button class="maintain-material-button" :disabled="!canMaintainSelected" @click="openSelectedMaintenance">维护</el-button>
        </div>
        <el-table ref="materialTable" v-loading="loading" class="material-list-table" :data="rows" row-key="material.id" height="100%" stripe @selection-change="selectedRows = $event">
          <el-table-column type="selection" width="38" align="center" />
          <el-table-column label="名称" min-width="110" show-overflow-tooltip><template #default="{ row }"><el-tag v-if="row.material.isRecommended" size="small" type="warning">推荐</el-tag> {{ row.material.name }}</template></el-table-column>
          <el-table-column label="图片" width="58"><template #default="{ row }"><el-image v-if="coverUrls[row.material.id]" class="cover-thumb" :src="coverUrls[row.material.id]" :preview-src-list="[coverUrls[row.material.id]]" preview-teleported fit="cover" /><span v-else>—</span></template></el-table-column>
          <el-table-column label="料号" min-width="105" prop="material.materialCode" show-overflow-tooltip />
          <el-table-column label="型号" min-width="120" show-overflow-tooltip><template #default="{ row }">{{ row.material.specification || '—' }}</template></el-table-column>
          <el-table-column label="品牌" min-width="78" show-overflow-tooltip><template #default="{ row }">{{ row.material.brand || '—' }}</template></el-table-column>
          <el-table-column label="备注" min-width="100" show-overflow-tooltip><template #default="{ row }">{{ row.material.remark || '—' }}</template></el-table-column>
          <el-table-column label="选型建议" min-width="115" show-overflow-tooltip><template #default="{ row }">{{ row.material.selectionAdvice || '—' }}</template></el-table-column>
          <el-table-column label="3D" width="48"><template #default="{ row }"><el-button link type="primary" :disabled="!row.material.model3DAttachmentCount" @click="openAttachments(row, 'Model3D')">{{ row.material.model3DAttachmentCount || '—' }}</el-button></template></el-table-column>
          <el-table-column label="资料" width="48"><template #default="{ row }"><el-button link type="primary" :disabled="!row.material.documentAttachmentCount" @click="openAttachments(row, 'Document')">{{ row.material.documentAttachmentCount || '—' }}</el-button></template></el-table-column>
          <el-table-column label="引用" width="54"><template #default="{ row }">{{ row.material.referenceCount }}</template></el-table-column>
          <el-table-column label="U9同步" width="78"><template #default="{ row }"><el-tag :type="row.material.syncStatus === 'Succeeded' ? 'success' : row.material.syncStatus === 'NeedsReview' ? 'danger' : 'warning'">{{ row.material.syncStatus === 'Succeeded' ? '已一致' : row.material.syncStatus === 'NeedsReview' ? '待复核' : '待同步' }}</el-tag></template></el-table-column>
        </el-table>
        <el-pagination v-model:current-page="page" v-model:page-size="pageSize" :total="total" :page-sizes="[50,100]" layout="total, sizes, prev, pager, next" @current-change="load(false)" @size-change="page = 1; load(false)" />
      </main>
    </div>

    <el-dialog v-model="categoryManagerOpen" title="标准分类维护" width="min(860px, calc(100vw - 32px))">
      <div class="category-manager">
        <section class="category-manager__list">
          <header class="category-manager__header"><div><strong>多级分类</strong><span>使用上下箭头调整同级顺序</span></div><el-button type="primary" size="small" @click="openCategoryEditor()">新增顶级分类</el-button></header>
          <div class="category-manager__rows">
            <div v-if="!flatCategoryRows.length" class="category-manager__empty">暂无标准分类</div>
            <div v-for="item in flatCategoryRows" :key="item.category.id" class="category-manager__row" :class="{ 'is-child': item.depth > 0 }">
              <div class="category-manager__name" :style="{ paddingLeft: `${item.depth * 20}px` }"><span v-if="item.depth" class="category-branch">└</span><strong>{{ item.category.name }}</strong><el-tag size="small" :type="item.category.isActive ? 'success' : 'info'">{{ item.category.isActive ? '启用' : '停用' }}</el-tag></div>
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
              <el-form-item label="上级分类"><el-select v-model="categoryForm.parentId" clearable placeholder="顶级分类"><el-option v-for="item in categories.filter(value => value.id !== editingCategoryId)" :key="item.id" :label="item.name" :value="item.id" /></el-select></el-form-item>
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
            <p>勾选需要加入标准库的料品。</p>
            <div class="eligible-material-filters"><el-input v-model="eligibleQuery" clearable placeholder="搜索料号/名称/型号/品牌" /><el-select v-model="eligibleBrand" clearable filterable placeholder="筛选品牌"><el-option v-for="item in eligibleBrands" :key="item" :label="item" :value="item" /></el-select></div>
          </header>
          <el-table ref="eligibleSelectionTable" :data="filteredEligibleMaterials" row-key="id" height="100%" stripe @selection-change="updateEligibleSelection">
            <el-table-column type="selection" width="38" reserve-selection />
            <el-table-column prop="materialCode" label="料号" width="118" show-overflow-tooltip />
            <el-table-column prop="name" label="名称" min-width="125" show-overflow-tooltip />
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
            <el-table-column prop="materialCode" label="料号" width="118" show-overflow-tooltip />
            <el-table-column prop="name" label="名称" min-width="120" show-overflow-tooltip />
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

    <el-dialog v-model="batchEditorOpen" title="批量维护标准物料" width="520px">
      <p class="batch-editor-summary">已选择 {{ selectedRows.length }} 个料品；仅批量修改PLM专属字段，不生成U9C任务。</p>
      <el-form label-position="top">
        <el-form-item v-if="canManage" label="推荐属性"><el-select v-model="batchRecommendation"><el-option label="保持不变" value="Keep" /><el-option label="设为推荐" value="Recommended" /><el-option label="取消推荐" value="NotRecommended" /></el-select></el-form-item>
        <template v-if="canEdit">
          <el-checkbox v-model="batchSelectionAdviceEnabled">修改选型建议</el-checkbox>
          <el-input v-if="batchSelectionAdviceEnabled" v-model="batchSelectionAdvice" type="textarea" :rows="3" maxlength="1000" show-word-limit />
          <el-checkbox v-model="batchReferencePriceEnabled">修改参考价格</el-checkbox>
          <el-input-number v-if="batchReferencePriceEnabled" v-model="batchReferencePrice" :min="0" :precision="2" controls-position="right" placeholder="留空表示清除" />
        </template>
      </el-form>
      <template #footer><el-button @click="batchEditorOpen = false">取消</el-button><el-button type="primary" :loading="batchSaving" :disabled="!batchHasChanges" @click="saveBatchMaintenance">批量保存</el-button></template>
    </el-dialog>

    <el-dialog v-model="attachmentDialogOpen" :title="attachmentDialogTitle" width="620px"><el-table :data="attachmentItems" max-height="420"><el-table-column prop="originalFileName" label="文件名" min-width="260" show-overflow-tooltip /><el-table-column prop="uploadedBy" label="上传人" width="100" /><el-table-column label="操作" width="80"><template #default="{ row }"><el-button link type="primary" @click="downloadAttachment(row)">下载</el-button></template></el-table-column></el-table></el-dialog>

    <MaterialEditorDialog :token="token" v-model="editorOpen" :editing-id="editingMaterial?.id" :form="editorForm" :categories="creatableMaterialCategories" material-code-placeholder="" :attachments="editorAttachments" :saving="saving" :uploading-kind="uploadingKind" :upload-progress="uploadProgress" :cover-url="editorCoverUrl" :u9-fields-locked="Boolean(editingMaterial && ['Pending', 'NeedsReview'].includes(editingMaterial.syncStatus))" :can-edit="canEdit" @category-change="applyMaterialCategory" @attachment-files="handleEditorFiles" @download-attachment="attachment => downloadAttachment(attachment, editingMaterial?.id)" @clear-cover="clearEditorCover" @save="saveEditor" />
  </section>
</template>

<style scoped>
.standard-library{display:flex;height:calc(100vh - 104px);min-height:560px;flex-direction:column;overflow:hidden}.standard-library__header{display:flex;padding:16px 20px;border-bottom:1px solid #e5eaf1;align-items:center;justify-content:space-between}.standard-library__header h1{margin:0;font-size:18px}.standard-library__header p{margin:4px 0 0;color:#64748b;font-size:12px}.standard-library__body{display:grid;min-height:0;flex:1;grid-template-columns:200px minmax(0,1fr)}.standard-library__tree{box-sizing:border-box;width:200px;max-width:200px;display:flex;min-height:0;padding:12px;border-right:1px solid #e5eaf1;flex-direction:column;overflow:auto}.tree-actions{display:flex;margin-bottom:10px;gap:6px}.all-category{margin-bottom:6px;padding:8px;border:0;border-radius:5px;background:transparent;text-align:left;cursor:pointer}.all-category.active{background:var(--pdm-theme-accent-soft);color:var(--pdm-theme-accent)}.standard-library__content{display:flex;min-width:0;max-width:100%;min-height:0;padding:12px 16px;flex-direction:column;gap:10px}.standard-library__toolbar{display:flex;align-items:center;justify-content:space-between;gap:12px}.scope-label{margin-left:12px;color:#64748b}.filters{display:flex;align-items:center;gap:8px}.filters .el-input{width:250px}.filters .el-select{width:130px}.category-tags{display:flex;flex-wrap:wrap;gap:3px}.cover-thumb{width:44px;height:44px;border-radius:5px}.standard-library__content :deep(.el-table){flex:1}.standard-library__content :deep(.el-pagination){justify-content:flex-end}.category-manager{display:grid;grid-template-columns:minmax(0,1.45fr) minmax(270px,.85fr);gap:14px;font-size:12px}.category-manager :deep(.el-button),.category-manager :deep(.el-tag),.category-manager :deep(.el-form-item__label),.category-manager :deep(.el-input__inner),.category-manager :deep(.el-select__selected-item),.category-manager :deep(.el-switch__label){font-size:12px}.category-manager__list,.category-manager__editor{min-width:0;padding:12px;border:1px solid #e2e8f0;border-radius:8px}.category-manager__header{display:flex;margin-bottom:10px;align-items:center;justify-content:space-between;gap:12px}.category-manager__header div{display:flex;flex-direction:column;gap:3px}.category-manager__header span,.category-manager__placeholder p{color:#64748b;font-size:12px}.category-manager__rows{max-height:500px;border-top:1px solid #edf1f5;overflow:auto}.category-manager__row{display:flex;min-height:42px;padding:6px 4px;border-bottom:1px solid #edf1f5;align-items:center;justify-content:space-between;gap:10px}.category-manager__name{display:flex;min-width:0;align-items:center;gap:6px}.category-manager__name strong{overflow:hidden;text-overflow:ellipsis;white-space:nowrap}.category-manager__row.is-child .category-manager__name strong{font-weight:400}.category-branch{color:#94a3b8}.category-manager__actions{display:flex;flex-shrink:0;align-items:center;gap:1px}.category-manager__actions :deep(.el-button+.el-button){margin-left:3px}.category-manager__empty{padding:40px 12px;color:#94a3b8;text-align:center}.category-manager__editor{background:#f8fafc}.category-manager__editor h3{margin:0 0 14px;font-size:15px}.category-manager__editor :deep(.el-select){width:100%}.category-manager__editor-actions{display:flex;justify-content:flex-end;gap:8px}.category-manager__placeholder{padding:34px 10px;text-align:center}.category-manager__placeholder p{line-height:1.6}:global(.standard-library-add-dialog .el-dialog__body){display:flex;min-height:0}.add-material-dialog-body{display:flex;min-height:0;flex:1}.add-material-dialog-body,.add-material-dialog-body :deep(.el-table),.add-material-dialog-body :deep(.el-button),.add-material-dialog-body :deep(.el-form-item__label),.add-material-dialog-body :deep(.el-tree-node__label){font-size:12px}.add-material-layout{display:grid;min-height:0;flex:1;grid-template-columns:minmax(0,1fr) minmax(0,1fr) 300px;gap:12px}.add-material-pane{display:flex;min-width:0;min-height:0;padding:12px;border:1px solid #e2e8f0;border-radius:8px;background:#fff;flex-direction:column}.add-material-pane--selected,.add-material-pane--categories{background:#f8fafc}.add-material-pane__header{box-sizing:border-box;display:flex;height:82px;min-height:82px;margin-bottom:10px;align-items:flex-start;justify-content:space-between;gap:12px}.add-material-pane__header div{display:flex;align-items:center;gap:8px}.add-material-pane__header strong{font-size:12px}.add-material-pane__header span{color:#64748b;font-size:12px}.add-material-pane__header p{margin:4px 0 6px;color:#64748b;font-size:12px}.add-material-pane__header:not(.selected-material-header){display:block}.searchable-material-header :deep(.el-input__wrapper){min-height:28px}.add-material-pane :deep(.el-table){min-height:0;flex:1}.add-material-target{margin-bottom:0}.required-mark{color:#f56c6c}.add-material-category-tree{box-sizing:border-box;width:100%;min-height:0;flex:1;overflow:auto;padding:6px 8px;border:1px solid #dcdfe6;border-radius:4px}.add-material-category-tree :deep(.el-tree-node__content){height:26px}.field-help{margin:5px 0 0;color:#64748b;font-size:12px;line-height:1.4}@media(max-width:1000px){.standard-library__toolbar{align-items:flex-start;flex-direction:column}.filters{width:100%;flex-wrap:wrap}.category-manager,.add-material-layout{grid-template-columns:1fr}.add-material-pane{min-height:520px}}
.standard-library__content{overflow:hidden}.standard-library__toolbar{align-items:center;justify-content:flex-start;flex-direction:row;flex-wrap:nowrap;gap:8px}.standard-library__toolbar :deep(.el-button+.el-button){margin-left:0}.standard-library__toolbar>.filters{width:auto;min-width:0;flex:1;flex-wrap:nowrap}.filters .el-input{min-width:90px;flex:1}.filters .el-select{width:90px;flex:0 0 90px}.filters .el-checkbox{flex:0 0 auto}.add-material-button,.maintain-material-button{flex-shrink:0}.material-list-table{width:100%;max-width:100%;min-width:0}.material-list-table :deep(.el-table__inner-wrapper){max-width:100%}.material-list-table :deep(.el-table__cell),.material-list-table :deep(.el-table__cell .cell),.material-list-table :deep(.el-tag),.material-list-table :deep(.el-button){font-size:11px}.material-list-table :deep(.el-table__cell){text-align:center}.material-list-table :deep(.el-table__header-wrapper .cell),.material-list-table :deep(.el-table__body-wrapper .cell){overflow:hidden;text-overflow:ellipsis;white-space:nowrap}.batch-editor-summary{margin:0 0 16px;color:#64748b;font-size:12px}.batch-editor-summary+.el-form :deep(.el-select),.batch-editor-summary+.el-form :deep(.el-input-number){width:100%}.batch-editor-summary+.el-form>.el-checkbox{display:flex;margin:12px 0 8px}.eligible-material-filters{display:grid!important;grid-template-columns:minmax(0,1fr) 110px;gap:6px}.category-filter-header{display:block}.category-filter-header>div{margin-bottom:10px}.searchable-material-header :deep(.el-input__wrapper),.category-filter-header :deep(.el-input__wrapper){min-height:28px}@media(max-width:800px){.filters .el-input{min-width:70px}.filters .el-select{width:80px;flex-basis:80px}}
</style>
