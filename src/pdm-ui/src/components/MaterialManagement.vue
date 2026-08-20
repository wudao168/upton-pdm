<script setup lang="ts">
import { ElMessage, ElMessageBox } from 'element-plus'
import { computed, onMounted, reactive, ref, watch } from 'vue'
import {
  archiveMaterial,
  approveMaterial,
  calibrateMaterialCategoryCounter,
  changeApprovedMaterial,
  createMaterial,
  deleteMaterial,
  executeMaterialSyncTask,
  getMaterialRemovalReadiness,
  listMaterialCategories,
  listMaterials,
  listMaterialSyncTasks,
  queryU9Material,
  retryMaterialSyncTask,
  saveMaterialCategory,
  updateMaterial,
} from '../api'
import type {
  MaterialCategory,
  MaterialKind,
  MaterialRemovalReadiness,
  MaterialSupplyMode,
  MaterialSyncTask,
  PdmMaterial,
  SaveMaterialInput,
} from '../types'
import { u9UnitLabel, u9UnitOptions } from '../u9Units'

const props = defineProps<{
  token: string
  canEdit: boolean
  canApprove: boolean
  canManageIntegration: boolean
}>()

const activeTab = ref('materials')
const loading = ref(false)
const saving = ref(false)
const syncingTaskId = ref<string | null>(null)
const queryingU9 = ref(false)
type U9ValidationStatus = 'Matched' | 'SpecificationMismatch' | 'NotFound'
interface U9ValidationResult {
  status: U9ValidationStatus
  message: string
  u9Specification?: string | null
  removalLabel: string
}
const u9ValidationResults = reactive<Record<string, U9ValidationResult>>({})
const materials = ref<PdmMaterial[]>([])
const categories = ref<MaterialCategory[]>([])
const tasks = ref<MaterialSyncTask[]>([])
const query = ref('')
const brandFilter = ref('')
const showArchived = ref(false)
const pageSize = ref(50)
const currentPage = ref(1)
const editorOpen = ref(false)
const editingId = ref<string | null>(null)
const selectedMaterials = ref<PdmMaterial[]>([])
const batchEditorOpen = ref(false)
type BatchEditableField = 'supplyMode' | 'unitCode' | 'specification' | 'material' | 'brand' | 'surfaceTreatment' | 'remark'
const batchFields = ref<BatchEditableField[]>([])
const batchForm = reactive({
  supplyMode: 'Purchase' as MaterialSupplyMode,
  unitCode: '001',
  specification: '',
  material: '',
  brand: '',
  surfaceTreatment: '',
  remark: '',
})
const previewTask = ref<MaterialSyncTask | null>(null)
const previewOpen = ref(false)
const selectedMaterialCategoryCode = ref('')
const selectedCategoryCode = ref<string | null>(null)
const categoryCreating = ref(false)
const categoryDraft = reactive<MaterialCategory>({
  code: '', name: '', parentCode: null, u9CategoryId: null, pdmKind: null, defaultSupplyMode: 'Purchase',
  allowCreate: false, isVisible: true, isActive: true, numberPrefix: '', sequenceLength: 7, counterScope: '',
  sortOrder: 0, updatedBy: '', updatedAt: '', rowVersion: 0,
  currentSequence: 0,
})
const lastU9MaterialCode = ref('')

const emptyForm = (): SaveMaterialInput => ({
  materialCode: '', name: '', kind: 'Electrical', categoryCode: '', supplyMode: 'Purchase', unitCode: '001',
  specification: '', material: '', remark: '', brand: '', surfaceTreatment: '', purchaseLink: '', weight: null, weightUnit: 'kg',
})
const form = reactive<SaveMaterialInput>(emptyForm())

const filteredMaterials = computed(() => {
  const normalized = query.value.trim().toLowerCase()
  return materials.value.filter(item => {
    if (materialCategoryScopeCodes.value && !materialCategoryScopeCodes.value.has(item.categoryCode ?? item.u9CategoryCode ?? '')) return false
    if (brandFilter.value && item.brand !== brandFilter.value) return false
    if (!normalized) return true
    const categoryName = categories.value.find(category => category.code === (item.categoryCode ?? item.u9CategoryCode))?.name
    return [item.materialCode, item.name, item.specification, item.material, item.categoryCode, item.u9CategoryCode, categoryName,
      item.brand, item.surfaceTreatment, item.purchaseLink, item.remark, item.unitCode, kindLabels[item.kind], supplyLabels[item.supplyMode]]
      .some(value => value?.toLowerCase().includes(normalized))
  })
})
const pagedMaterials = computed(() => filteredMaterials.value.slice((currentPage.value - 1) * pageSize.value, currentPage.value * pageSize.value))
watch([query, brandFilter, selectedMaterialCategoryCode, showArchived], () => { currentPage.value = 1 })

const brandOptions = computed(() => [...new Set(materials.value.map(item => item.brand?.trim()).filter((brand): brand is string => Boolean(brand)))].sort((left, right) => left.localeCompare(right, 'zh-CN')))

type CategoryTreeNode = MaterialCategory & { children?: CategoryTreeNode[] }
const buildCategoryTree = (source: MaterialCategory[]) => {
  const nodes = new Map(source.map(category => [category.code, { ...category, children: [] } as CategoryTreeNode]))
  const roots: CategoryTreeNode[] = []
  for (const node of nodes.values()) {
    const parent = node.parentCode ? nodes.get(node.parentCode) : undefined
    if (parent) parent.children!.push(node)
    else roots.push(node)
  }
  const sort = (items: CategoryTreeNode[]) => items.sort((left, right) => left.sortOrder - right.sortOrder || left.code.localeCompare(right.code))
    .forEach(item => item.children && sort(item.children))
  sort(roots)
  return roots
}
const categoryTree = computed<CategoryTreeNode[]>(() => buildCategoryTree(categories.value))
const materialCategoryTree = computed<CategoryTreeNode[]>(() => {
  const prune = (items: CategoryTreeNode[]): CategoryTreeNode[] => items
    .map(item => ({ ...item, children: prune(item.children ?? []) }))
    .filter(item => item.allowCreate || (item.children?.length ?? 0) > 0)
  return prune(buildCategoryTree(categories.value.filter(category => category.isVisible && category.isActive)))
})
const materialCategoryScopeCodes = computed<Set<string> | null>(() => {
  if (!selectedMaterialCategoryCode.value) return null
  const codes = new Set([selectedMaterialCategoryCode.value])
  let changed = true
  while (changed) {
    changed = false
    for (const category of categories.value) {
      if (category.isVisible && category.isActive && category.parentCode && codes.has(category.parentCode) && !codes.has(category.code)) {
        codes.add(category.code)
        changed = true
      }
    }
  }
  return codes
})
const creatableCategories = computed(() => categories.value.filter(category => category.allowCreate && category.isVisible && category.isActive && category.pdmKind))

const kindLabels: Record<MaterialKind, string> = { Electrical: '电气外购件', Standard: '机械外购件', NonStandard: '非标机加件' }
const supplyLabels: Record<MaterialSupplyMode, string> = { Purchase: '采购', Manufacture: '自制', Outsource: '委外' }
const syncLabels: Record<string, string> = {
  NotQueued: '未排队', PreviewReady: '请求预览', Pending: '待同步', Succeeded: '已同步', Failed: '失败', NeedsReview: '待复核', Superseded: '已废止',
}
const categoryLabel = (item: PdmMaterial) => {
  const code = item.categoryCode ?? item.u9CategoryCode
  if (!code) return '—'
  return `${code} ${categories.value.find(category => category.code === code)?.name ?? kindLabels[item.kind]}`
}
const weightLabel = (item: PdmMaterial) => item.weight == null ? '—' : `${item.weight}${item.weightUnit ? ` ${item.weightUnit}` : ''}`
const materialCodePlaceholder = computed(() => {
  const category = categories.value.find(item => item.code === form.categoryCode)
  if (editingId.value) return ''
  return category ? `${category.numberPrefix} + ${category.sequenceLength}位流水（保存后生成）` : '选择开放分类后自动生成'
})
const selectedMaterial = computed(() => selectedMaterials.value.length === 1 ? selectedMaterials.value[0] : null)
const canEditSelected = computed(() => Boolean(selectedMaterial.value
  && !selectedMaterial.value.isArchived
  && selectedMaterial.value.syncStatus !== 'Pending'))
const canBatchEditSelected = computed(() => selectedMaterials.value.length > 1
  && selectedMaterials.value.every(item => !item.isArchived && item.syncStatus !== 'Pending'))
const canApproveSelected = computed(() => Boolean(selectedMaterial.value
  && !selectedMaterial.value.isArchived
  && selectedMaterial.value.approvalStatus === 'Draft'))
const canArchiveSelected = computed(() => Boolean(selectedMaterial.value && !selectedMaterial.value.isArchived))
const canDeleteSelected = computed(() => selectedMaterials.value.length > 0
  && selectedMaterials.value.every(item => item.sourceSystem !== 'U9C' && item.masterOwner !== 'U9C'))

function selectMaterialCategory(code = '') {
  selectedMaterialCategoryCode.value = code
  selectedMaterials.value = []
}

function applyCategoryDefaults(categoryCode?: string | null) {
  const category = categories.value.find(item => item.code === categoryCode)
  if (!category) return
  if (category.pdmKind) form.kind = category.pdmKind
  form.supplyMode = category.defaultSupplyMode
}

function openCreate() {
  editingId.value = null
  Object.assign(form, emptyForm())
  editorOpen.value = true
}

function openEdit(item: PdmMaterial) {
  if (item.isArchived) return
  editingId.value = item.id
  Object.assign(form, {
    materialCode: item.materialCode,
    name: item.name,
    categoryCode: item.categoryCode ?? item.u9CategoryCode ?? '',
    kind: item.kind,
    supplyMode: item.supplyMode,
    unitCode: item.unitCode,
    specification: item.specification ?? '',
    material: item.material ?? '',
    remark: item.remark ?? '',
    brand: item.brand ?? '',
    surfaceTreatment: item.surfaceTreatment ?? '',
    purchaseLink: item.purchaseLink ?? '',
    weight: item.weight ?? null,
    weightUnit: item.weightUnit ?? 'kg',
    expectedRowVersion: item.rowVersion,
  })
  editorOpen.value = true
}

function openSelectedEdit() {
  if (selectedMaterial.value) openEdit(selectedMaterial.value)
}

function approveSelected() {
  if (selectedMaterial.value) void approve(selectedMaterial.value)
}

async function querySelected() {
  const targets = [...selectedMaterials.value]
  if (targets.length === 0) return
  queryingU9.value = true
  try {
    const counts: Record<U9ValidationStatus | 'Failed', number> = { Matched: 0, SpecificationMismatch: 0, NotFound: 0, Failed: 0 }
    for (const item of targets) counts[await queryU9(item, targets.length === 1)]++
    if (targets.length > 1) {
      const summary = `U9C批量校验完成：一致 ${counts.Matched}，未找到 ${counts.NotFound}，规格冲突 ${counts.SpecificationMismatch}，查询失败 ${counts.Failed}`
      if (counts.Failed) ElMessage.error(summary)
      else ElMessage.success(summary)
    }
  } finally {
    queryingU9.value = false
  }
}

function openBatchEdit() {
  batchFields.value = []
  Object.assign(batchForm, {
    supplyMode: 'Purchase', unitCode: '001', specification: '', material: '', brand: '', surfaceTreatment: '', remark: '',
  })
  batchEditorOpen.value = true
}

async function load() {
  loading.value = true
  try {
    const [loadedMaterials, loadedCategories, loadedTasks] = await Promise.all([
      listMaterials(props.token, '', showArchived.value), listMaterialCategories(props.token, props.canManageIntegration), listMaterialSyncTasks(props.token),
    ])
    materials.value = loadedMaterials
    selectedMaterials.value = []
    categories.value = loadedCategories
    tasks.value = loadedTasks
  } catch (error) {
    ElMessage.error(error instanceof Error ? error.message : '料品数据加载失败')
  } finally {
    loading.value = false
  }
}

function materialInput(item: PdmMaterial): SaveMaterialInput {
  return {
    materialCode: item.materialCode,
    name: item.name,
    kind: item.kind,
    categoryCode: item.categoryCode ?? item.u9CategoryCode ?? '',
    supplyMode: item.supplyMode,
    unitCode: item.unitCode,
    specification: item.specification ?? '',
    material: item.material ?? '',
    remark: item.remark ?? '',
    brand: item.brand ?? '',
    surfaceTreatment: item.surfaceTreatment ?? '',
    purchaseLink: item.purchaseLink ?? '',
    weight: item.weight ?? null,
    weightUnit: item.weightUnit ?? 'kg',
    expectedRowVersion: item.rowVersion,
  }
}

async function saveBatchEdit() {
  if (batchFields.value.length === 0) {
    ElMessage.warning('请先勾选至少一个要批量修改的字段')
    return
  }
  saving.value = true
  const failures: string[] = []
  let savedCount = 0
  try {
    for (const item of selectedMaterials.value) {
      try {
        const input = materialInput(item)
        for (const field of batchFields.value) input[field] = batchForm[field] as never
        const changeResult = item.approvalStatus === 'Approved'
          ? await changeApprovedMaterial(item.id, input, props.token)
          : null
        const saved = changeResult?.material ?? await updateMaterial(item.id, input, props.token)
        const index = materials.value.findIndex(value => value.id === saved.id)
        if (index >= 0) materials.value[index] = saved
        if (changeResult) tasks.value.unshift(changeResult.task)
        savedCount++
      } catch (error) {
        failures.push(`${item.materialCode}：${error instanceof Error ? error.message : '修改失败'}`)
      }
    }
    if (savedCount) ElMessage.success(`已批量更新 ${savedCount} 个料品`)
    if (failures.length) ElMessage.error(`有 ${failures.length} 个料品未更新：${failures.join('；')}`)
    else batchEditorOpen.value = false
  } finally {
    saving.value = false
  }
}

async function saveMaterial() {
  if (!form.name.trim() || !form.unitCode.trim() || !form.categoryCode) {
    ElMessage.warning('物料名称、分类和计量单位不能为空')
    return
  }
  saving.value = true
  try {
    const existing = editingId.value ? materials.value.find(item => item.id === editingId.value) : undefined
    const changeResult = editingId.value && existing?.approvalStatus === 'Approved'
      ? await changeApprovedMaterial(editingId.value, { ...form }, props.token)
      : null
    const saved = changeResult?.material ?? (editingId.value
      ? await updateMaterial(editingId.value, { ...form }, props.token)
      : await createMaterial({ ...form }, props.token))
    if (changeResult) tasks.value.unshift(changeResult.task)
    const index = materials.value.findIndex(item => item.id === saved.id)
    if (index >= 0) materials.value[index] = saved
    else materials.value.push(saved)
    materials.value.sort((left, right) => left.materialCode.localeCompare(right.materialCode))
    editorOpen.value = false
    ElMessage.success(changeResult
      ? existing?.u9SyncConfirmed
        ? '料品已更新，并生成U9C修改预览'
        : '料品已更新，旧请求已废止并生成新的U9C创建预览'
      : editingId.value ? '料品已更新' : '料品草稿已创建，编码已通过U9C编码及规格校验')
  } catch (error) {
    ElMessage.error(error instanceof Error ? error.message : '料品保存失败')
  } finally {
    saving.value = false
  }
}

async function approve(item: PdmMaterial) {
  try {
    await ElMessageBox.confirm(
      `批准后将锁定料品 ${item.materialCode}，并生成U9C请求预览；批准动作本身不会写入U9C。`,
      '批准料品',
      { type: 'warning', confirmButtonText: '批准并生成预览', cancelButtonText: '取消' },
    )
    const result = await approveMaterial(item.id, item.rowVersion, props.token)
    materials.value[materials.value.findIndex(value => value.id === item.id)] = result.material
    tasks.value.unshift(result.task)
    ElMessage.success('料品已批准，U9C请求预览已生成')
  } catch (error) {
    if (error === 'cancel' || error === 'close') return
    ElMessage.error(error instanceof Error ? error.message : '料品批准失败')
  }
}

function selectCategory(category: MaterialCategory) {
  selectedCategoryCode.value = category.code
  categoryCreating.value = false
  Object.assign(categoryDraft, category)
  lastU9MaterialCode.value = category.currentSequence > 0
    ? `${category.numberPrefix}${category.currentSequence.toString().padStart(category.sequenceLength, '0')}`
    : ''
}

function startCategory(parentCode: string | null = null) {
  categoryCreating.value = true
  selectedCategoryCode.value = null
  Object.assign(categoryDraft, {
    code: '', name: '', parentCode, u9CategoryId: null, pdmKind: null, defaultSupplyMode: 'Purchase', allowCreate: false,
    isVisible: true, isActive: true, numberPrefix: '', sequenceLength: 7, counterScope: '', sortOrder: categories.value.length + 1,
    updatedBy: '', updatedAt: '', rowVersion: 0,
    currentSequence: 0,
  })
  lastU9MaterialCode.value = ''
}

async function saveCategory() {
  try {
    if (!categoryDraft.code.trim() || !categoryDraft.name.trim()) {
      ElMessage.warning('分类编码和名称不能为空')
      return
    }
    if (!categoryDraft.numberPrefix.trim()) categoryDraft.numberPrefix = categoryDraft.code.trim()
    if (!categoryDraft.counterScope.trim()) categoryDraft.counterScope = categoryDraft.code.trim()
    const saved = await saveMaterialCategory({ ...categoryDraft }, props.token, categoryCreating.value)
    const index = categories.value.findIndex(item => item.code === saved.code)
    if (index >= 0) categories.value[index] = saved
    else categories.value.push(saved)
    selectCategory(saved)
    ElMessage.success(`分类 ${saved.code} 已保存`)
  } catch (error) {
    ElMessage.error(error instanceof Error ? error.message : '分类保存失败')
  }
}

async function calibrateCounter() {
  if (categoryCreating.value || !categoryDraft.code || !lastU9MaterialCode.value.trim()) {
    ElMessage.warning('请先保存分类并填写U9C末位料号')
    return
  }
  try {
    const saved = await calibrateMaterialCategoryCounter(categoryDraft.code, lastU9MaterialCode.value.trim(), props.token)
    const index = categories.value.findIndex(item => item.code === saved.code)
    if (index >= 0) categories.value[index] = saved
    selectCategory(saved)
    ElMessage.success(`流水已校准，下一个料号为 ${saved.numberPrefix}${(saved.currentSequence + 1).toString().padStart(saved.sequenceLength, '0')}`)
  } catch (error) {
    ElMessage.error(error instanceof Error ? error.message : '分类流水校准失败')
  }
}

async function archiveSelected() {
  const item = selectedMaterial.value
  if (!item) return
  try {
    await ElMessageBox.confirm(
      `停用后将禁止新BOM引用 ${item.materialCode}，历史BOM不受影响；本操作只停用PDM料品，不会停用或物理删除U9C料品。`,
      '停用料品',
      { type: 'warning', confirmButtonText: '确认停用', cancelButtonText: '取消' },
    )
    const result = await archiveMaterial(item.id, item.rowVersion, props.token)
    if (!showArchived.value) materials.value = materials.value.filter(value => value.id !== result.id)
    else materials.value[materials.value.findIndex(value => value.id === result.id)] = result
    selectedMaterials.value = []
    ElMessage.success('料品已停用')
  } catch (error) {
    if (error === 'cancel' || error === 'close') return
    ElMessage.error(error instanceof Error ? error.message : '料品停用失败')
  }
}

async function deleteSelected() {
  const targets = [...selectedMaterials.value]
  if (targets.length === 0) return
  try {
    await ElMessageBox.confirm(
      `将检查选中的 ${targets.length} 个料品。仅PDM主控且未被BOM引用的料品可删除；若U9C存在，将先请求U9C删除，U9C因业务引用拒绝时PDM保持不变，只有U9C删除成功并回查不存在后才删除PDM主档。`,
      '安全删除料品',
      { type: 'warning', confirmButtonText: '确认删除', cancelButtonText: '取消' },
    )
    const failures: string[] = []
    const failedTargets: PdmMaterial[] = []
    let deletedCount = 0
    for (const item of targets) {
      try {
        await deleteMaterial(item.id, item.rowVersion, props.token)
        materials.value = materials.value.filter(value => value.id !== item.id)
        tasks.value = tasks.value.filter(task => task.materialId !== item.id)
        deletedCount++
      } catch (error) {
        failedTargets.push(item)
        failures.push(`${item.materialCode}：${error instanceof Error ? error.message : '删除失败'}`)
      }
    }
    selectedMaterials.value = failedTargets
    if (deletedCount) ElMessage.success(`已安全删除 ${deletedCount} 个料品`)
    if (failures.length) ElMessage.error(`有 ${failures.length} 个料品未删除：${failures.join('；')}`)
  } catch (error) {
    if (error === 'cancel' || error === 'close') return
    ElMessage.error(error instanceof Error ? error.message : '料品删除失败')
  }
}

function removalStatus(readiness: MaterialRemovalReadiness, u9Exists: boolean) {
  if (!readiness.isPdmMaster) return { label: 'U9主控', message: readiness.decision }
  if (readiness.pdmReferenceCount > 0) return { label: `PDM引用${readiness.pdmReferenceCount}`, message: readiness.decision }
  if (!u9Exists) return { label: '可安全删除', message: 'PDM未发现引用，且U9C实时查询未找到该料品。' }
  if (readiness.synchronizedDeleteAvailable) return { label: '可同步删除', message: readiness.decision }
  return { label: '同步删除未启用', message: readiness.decision }
}

function validationLabel(result: U9ValidationResult) {
  const status = result.status === 'Matched' ? '一致' : result.status === 'SpecificationMismatch' ? '规格冲突' : 'U9未找到'
  return `${status}·${result.removalLabel}`
}

async function queryU9(item: PdmMaterial, notify = true): Promise<U9ValidationStatus | 'Failed'> {
  try {
    const [result, readiness] = await Promise.all([
      queryU9Material(item.materialCode, props.token),
      getMaterialRemovalReadiness(item.id, props.token).catch(() => null),
    ])
    const normalizedCode = item.materialCode.trim().toLowerCase()
    const codeMatches = result.items.filter(value => value.u9ItemCode?.trim().toLowerCase() === normalizedCode)
    const removal = readiness
      ? removalStatus(readiness, codeMatches.length > 0)
      : item.sourceSystem === 'U9C' || item.masterOwner === 'U9C'
        ? { label: 'U9主控', message: 'U9C主控料品不允许从PDM发起物理删除。' }
        : { label: '删除校验不可用', message: '未取得PDM引用状态，删除判定保持关闭。' }
    if (codeMatches.length === 0) {
      const message = `U9C未找到编码 ${item.materialCode} 的料品；删除检查：${removal.message}`
      u9ValidationResults[item.id] = { status: 'NotFound', message, removalLabel: removal.label }
      if (notify) ElMessage.warning(message)
      return 'NotFound'
    }

    const normalizeSpecification = (value?: string | null) => (value ?? '').trim().replace(/\s+/g, ' ').toLowerCase()
    const matched = codeMatches.find(value => normalizeSpecification(value.u9Specification) === normalizeSpecification(item.specification))
    if (matched) {
      const message = `U9C料品 ${matched.u9ItemCode} 编码及规格一致（ID：${matched.u9ItemId ?? '未返回'}）；删除检查：${removal.message}`
      u9ValidationResults[item.id] = { status: 'Matched', message, u9Specification: matched.u9Specification, removalLabel: removal.label }
      if (notify) ElMessage.success(message)
      return 'Matched'
    }

    const u9Specifications = [...new Set(codeMatches.map(value => value.u9Specification?.trim() || '空'))]
    const message = `编码 ${item.materialCode} 的规格冲突：PDM为“${item.specification?.trim() || '空'}”，U9C为“${u9Specifications.join('、')}”；删除检查：${removal.message}`
    u9ValidationResults[item.id] = {
      status: 'SpecificationMismatch',
      message,
      u9Specification: u9Specifications.join('、'),
      removalLabel: removal.label,
    }
    if (notify) ElMessage.error(message)
    return 'SpecificationMismatch'
  } catch (error) {
    const message = error instanceof Error ? error.message : 'U9C料品查询失败'
    if (notify) ElMessage.error(message)
    return 'Failed'
  }
}

async function retryTask(task: MaterialSyncTask) {
  try {
    const saved = await retryMaterialSyncTask(task.id, props.token)
    tasks.value[tasks.value.findIndex(item => item.id === saved.id)] = saved
    ElMessage.success('同步任务已重新生成请求预览')
  } catch (error) {
    ElMessage.error(error instanceof Error ? error.message : '同步任务重试失败')
  }
}

async function executeTask(task: MaterialSyncTask) {
  try {
    const operationText = task.operation === 'Update' ? '修改' : '创建'
    await ElMessageBox.confirm(
      `系统将先按料号查询 U9C，再${operationText} ${materials.value.find(item => item.id === task.materialId)?.materialCode ?? '该料品'}。是否继续？`,
      '同步到 U9C',
      { type: 'warning', confirmButtonText: '查询并同步', cancelButtonText: '取消' },
    )
    syncingTaskId.value = task.id
    const result = await executeMaterialSyncTask(task.id, props.token)
    const taskIndex = tasks.value.findIndex(item => item.id === result.task.id)
    if (taskIndex >= 0) tasks.value[taskIndex] = result.task
    const materialIndex = materials.value.findIndex(item => item.id === result.material.id)
    if (materialIndex >= 0) materials.value[materialIndex] = result.material
    ElMessage.success(result.updated ? 'U9C料品修改并回查成功' : result.alreadyExisted ? 'U9C已存在同料号，已完成幂等关联' : 'U9C料品创建并关联成功')
  } catch (error) {
    if (error === 'cancel' || error === 'close') return
    ElMessage.error(error instanceof Error ? error.message : 'U9C料品同步失败')
    await load()
  } finally {
    syncingTaskId.value = null
  }
}

function showPreview(task: MaterialSyncTask) {
  previewTask.value = task
  previewOpen.value = true
}

onMounted(load)
</script>

<template>
  <section class="material-page pdm-panel" v-loading="loading">
    <el-tabs v-model="activeTab" class="material-tabs">
      <el-tab-pane label="料品主档" name="materials">
        <div class="material-master-layout">
          <aside class="material-category-nav" aria-label="料品分类">
            <div class="material-category-nav__title">料品分类</div>
            <button type="button" class="material-category-all" :class="{ 'is-active': !selectedMaterialCategoryCode }" @click="selectMaterialCategory()">全部料品</button>
            <el-tree :data="materialCategoryTree" node-key="code" default-expand-all highlight-current :current-node-key="selectedMaterialCategoryCode || undefined" :expand-on-click-node="false" @node-click="selectMaterialCategory($event.code)">
              <template #default="{ data }"><span class="material-category-node">{{ data.code }} {{ data.name }}</span></template>
            </el-tree>
          </aside>
          <section class="material-master-content" aria-label="料品列表">
            <div class="material-toolbar">
              <div class="material-toolbar__actions"><el-button @click="load">刷新</el-button><el-button v-if="canEdit" type="primary" @click="openCreate">新增料品</el-button><el-button v-if="canEdit" :disabled="selectedMaterials.length > 1 ? !canBatchEditSelected : !canEditSelected" @click="selectedMaterials.length > 1 ? openBatchEdit() : openSelectedEdit()">{{ selectedMaterials.length > 1 ? '批量编辑' : '编辑' }}</el-button><el-button v-if="canApprove" :disabled="!canApproveSelected" @click="approveSelected">批准</el-button><el-button :disabled="selectedMaterials.length === 0" :loading="queryingU9" @click="querySelected">查询U9C</el-button><el-button v-if="canEdit" :disabled="!canArchiveSelected" @click="archiveSelected">停用</el-button><el-button v-if="canEdit" type="danger" :disabled="!canDeleteSelected" @click="deleteSelected">删除</el-button></div>
              <div class="material-toolbar__filters"><el-checkbox v-model="showArchived" @change="load">显示已停用</el-checkbox><el-select v-model="brandFilter" class="material-brand-filter" clearable filterable placeholder="筛选品牌"><el-option v-for="brand in brandOptions" :key="brand" :label="brand" :value="brand" /></el-select><el-input v-model="query" clearable placeholder="搜索编码、名称、规格、品牌或分类" /></div>
            </div>
            <div class="material-table-shell">
              <el-table class="material-table" :data="pagedMaterials" height="100%" stripe row-key="id" table-layout="fixed" :fit="true" empty-text="尚未创建PDM料品" @selection-change="selectedMaterials = $event">
          <el-table-column type="selection" width="38" />
          <el-table-column prop="materialCode" label="PDM物料编码" min-width="100" show-overflow-tooltip />
          <el-table-column prop="name" label="名称" min-width="112" show-overflow-tooltip />
          <el-table-column label="来源/主控" min-width="76"><template #default="{ row }">{{ row.sourceSystem === 'U9C' ? 'U9C/U9C' : 'PDM/PDM' }}</template></el-table-column>
          <el-table-column label="U9C对应分类" min-width="96" show-overflow-tooltip><template #default="{ row }">{{ categoryLabel(row) }}</template></el-table-column>
          <el-table-column label="计量单位" min-width="76"><template #default="{ row }">{{ u9UnitLabel(row.unitCode) }}</template></el-table-column>
          <el-table-column label="规格" min-width="220" show-overflow-tooltip><template #default="{ row }">{{ row.specification || '—' }}</template></el-table-column>
          <el-table-column label="材质" min-width="52" show-overflow-tooltip><template #default="{ row }">{{ row.material || '—' }}</template></el-table-column>
          <el-table-column label="品牌" min-width="56" show-overflow-tooltip><template #default="{ row }">{{ row.brand || '—' }}</template></el-table-column>
          <el-table-column label="表面处理" min-width="64" show-overflow-tooltip><template #default="{ row }">{{ row.surfaceTreatment || '—' }}</template></el-table-column>
          <el-table-column label="重量" min-width="54" show-overflow-tooltip><template #default="{ row }">{{ weightLabel(row) }}</template></el-table-column>
          <el-table-column label="备注" min-width="64" show-overflow-tooltip><template #default="{ row }">{{ row.remark || '—' }}</template></el-table-column>
          <el-table-column label="料品采购链接" min-width="80"><template #default="{ row }"><el-link v-if="row.purchaseLink" :href="row.purchaseLink" target="_blank" rel="noopener noreferrer" type="primary" underline="never">打开</el-link><span v-else>—</span></template></el-table-column>
          <el-table-column label="状态" min-width="56"><template #default="{ row }"><el-tag :type="row.isArchived ? 'info' : row.approvalStatus === 'Approved' ? 'success' : 'info'">{{ row.isArchived ? '已停用' : row.approvalStatus === 'Approved' ? '已批准' : '草稿' }}</el-tag></template></el-table-column>
          <el-table-column label="同步" min-width="52"><template #default="{ row }">{{ row.syncStatus === 'Succeeded' && !row.u9SyncConfirmed ? '待校正' : syncLabels[row.syncStatus] }}</template></el-table-column>
          <el-table-column label="U9/删除校验" min-width="112"><template #default="{ row }"><el-tooltip v-if="u9ValidationResults[row.id]" :content="u9ValidationResults[row.id].message" placement="top"><div class="u9-validation" :aria-label="u9ValidationResults[row.id].message"><el-tag :type="u9ValidationResults[row.id].status === 'Matched' ? 'success' : u9ValidationResults[row.id].status === 'SpecificationMismatch' ? 'danger' : 'warning'">{{ validationLabel(u9ValidationResults[row.id]) }}</el-tag></div></el-tooltip><span v-else class="u9-unchecked">未校验</span></template></el-table-column>
              </el-table>
            </div>
            <el-pagination v-model:current-page="currentPage" v-model:page-size="pageSize" class="material-pagination" :page-sizes="[50, 100, 200]" :total="filteredMaterials.length" layout="total, sizes, prev, pager, next" @size-change="currentPage = 1" />
          </section>
        </div>
      </el-tab-pane>

      <el-tab-pane label="同步任务" name="tasks">
        <el-table :data="tasks" row-key="id" empty-text="尚无同步任务">
          <el-table-column prop="correlationId" label="关联号" min-width="220" />
          <el-table-column label="状态" width="110"><template #default="{ row }"><el-tag :type="row.status === 'Succeeded' ? 'success' : row.status === 'Failed' ? 'danger' : 'warning'">{{ syncLabels[row.status] }}</el-tag></template></el-table-column>
          <el-table-column prop="attemptCount" label="重试次数" width="90" />
          <el-table-column prop="payloadSha256" label="请求SHA-256" min-width="220" show-overflow-tooltip />
          <el-table-column prop="lastError" label="错误" min-width="180" show-overflow-tooltip />
          <el-table-column label="操作" width="240"><template #default="{ row }"><el-button link type="primary" @click="showPreview(row)">查看请求</el-button><el-button v-if="canApprove && !['Succeeded', 'Pending', 'Superseded'].includes(row.status)" link @click="retryTask(row)">重试</el-button><el-button v-if="canApprove && !['Succeeded', 'Pending', 'Superseded'].includes(row.status)" link type="danger" :loading="syncingTaskId === row.id" @click="executeTask(row)">同步到U9C</el-button></template></el-table-column>
        </el-table>
      </el-tab-pane>

      <el-tab-pane label="分类维护" name="rules">
        <div class="category-layout">
          <aside class="category-tree-panel">
            <div class="category-actions"><el-button v-if="canManageIntegration" size="small" @click="startCategory(null)">新增根分类</el-button><el-button v-if="canManageIntegration" size="small" :disabled="!selectedCategoryCode" @click="startCategory(selectedCategoryCode)">新增下级</el-button></div>
            <el-tree :data="categoryTree" node-key="code" default-expand-all :expand-on-click-node="false" @node-click="selectCategory">
              <template #default="{ data }"><span class="category-node"><span>{{ data.code }} {{ data.name }}</span><el-tag v-if="data.allowCreate" size="small" type="success">可创建</el-tag><el-tag v-else size="small" type="info">屏蔽</el-tag></span></template>
            </el-tree>
          </aside>
          <section class="category-editor">
            <div v-if="!selectedCategoryCode && !categoryCreating" class="category-empty">请选择分类查看设置，或新增分类。</div>
            <el-form v-else label-position="top">
              <div class="form-grid">
                <el-form-item label="分类编码" required><el-input v-model="categoryDraft.code" :disabled="!categoryCreating || !canManageIntegration" /></el-form-item>
                <el-form-item label="分类名称" required><el-input v-model="categoryDraft.name" :disabled="!canManageIntegration" /></el-form-item>
                <el-form-item label="上级分类"><el-select v-model="categoryDraft.parentCode" clearable filterable :disabled="!canManageIntegration"><el-option v-for="item in categories.filter(item => item.code !== categoryDraft.code)" :key="item.code" :label="`${item.code} ${item.name}`" :value="item.code" /></el-select></el-form-item>
                <el-form-item label="U9C分类ID"><el-input v-model="categoryDraft.u9CategoryId" :disabled="!canManageIntegration" placeholder="同步后保存稳定ID" /></el-form-item>
                <el-form-item label="PDM业务分类"><el-select v-model="categoryDraft.pdmKind" clearable :disabled="!canManageIntegration"><el-option label="电气件" value="Electrical" /><el-option label="机械外购件" value="Standard" /><el-option label="非标机加件" value="NonStandard" /></el-select></el-form-item>
                <el-form-item label="默认供给方式"><el-select v-model="categoryDraft.defaultSupplyMode" :disabled="!canManageIntegration"><el-option label="采购" value="Purchase" /><el-option label="自制" value="Manufacture" /><el-option label="委外" value="Outsource" /></el-select></el-form-item>
                <el-form-item label="编号前缀"><el-input v-model="categoryDraft.numberPrefix" :disabled="!canManageIntegration" :placeholder="categoryDraft.code" /></el-form-item>
                <el-form-item label="流水位数"><el-input-number v-model="categoryDraft.sequenceLength" :min="1" :max="9" :disabled="!canManageIntegration" /></el-form-item>
                <el-form-item label="流水范围"><el-input v-model="categoryDraft.counterScope" :disabled="!canManageIntegration" :placeholder="categoryDraft.code" /></el-form-item>
                <el-form-item label="排序号"><el-input-number v-model="categoryDraft.sortOrder" :disabled="!canManageIntegration" /></el-form-item>
                <el-form-item label="U9C末位料号"><el-input v-model="lastU9MaterialCode" :disabled="!canManageIntegration || categoryCreating" :placeholder="`${categoryDraft.numberPrefix || categoryDraft.code}${'0'.repeat(categoryDraft.sequenceLength)}`"><template #append><el-button @click="calibrateCounter">校准流水</el-button></template></el-input><p class="field-help">只允许向前校准；系统不会通过U9C精确查询接口猜测最大流水。</p></el-form-item>
              </div>
              <div class="category-switches"><el-switch v-model="categoryDraft.allowCreate" :disabled="!canManageIntegration" active-text="开放创建" inactive-text="屏蔽创建" /><el-switch v-model="categoryDraft.isVisible" :disabled="!canManageIntegration" active-text="PDM可见" /><el-switch v-model="categoryDraft.isActive" :disabled="!canManageIntegration" active-text="U9C有效" /></div>
              <p class="field-help">开放创建只影响新增料品；屏蔽分类中的现有料品仍可查询并供历史BOM引用。当前流水：{{ categoryDraft.currentSequence }}；下一个编号：{{ categoryDraft.numberPrefix || categoryDraft.code }}{{ (categoryDraft.currentSequence + 1).toString().padStart(categoryDraft.sequenceLength, '0') }}</p>
              <el-button v-if="canManageIntegration" type="primary" @click="saveCategory">保存分类</el-button>
            </el-form>
          </section>
        </div>
      </el-tab-pane>

    </el-tabs>

    <el-dialog v-model="editorOpen" class="material-editor-dialog" :title="editingId ? '编辑或变更料品' : '新增料品草稿'" width="720px">
      <el-form label-position="top">
        <div class="form-grid"><el-form-item label="PDM物料编码"><el-input v-model="form.materialCode" disabled :placeholder="materialCodePlaceholder" /><p class="field-help">保存时从分类当前流水起逐号只读查询U9C，跳过编码占用及规格冲突后预留可用编号；创建后不可修改。</p></el-form-item><el-form-item label="物料名称" required><el-input v-model="form.name" /></el-form-item><el-form-item label="U9C对应分类" required><el-select v-model="form.categoryCode" filterable placeholder="请选择U9C对应分类" @change="applyCategoryDefaults"><el-option v-for="category in creatableCategories" :key="category.code" :label="`${category.code} ${category.name}`" :value="category.code" /></el-select><p class="field-help">这里只显示已开放创建的有效分类，新增时必须主动选择。</p></el-form-item><el-form-item label="PDM业务类型"><el-select v-model="form.kind" disabled><el-option label="电气件" value="Electrical" /><el-option label="机械外购件" value="Standard" /><el-option label="非标机加件" value="NonStandard" /></el-select></el-form-item><el-form-item label="供给方式" required><el-select v-model="form.supplyMode"><el-option label="采购" value="Purchase" /><el-option label="自制" value="Manufacture" /><el-option label="委外" value="Outsource" /></el-select></el-form-item><el-form-item label="计量单位" required><el-select v-model="form.unitCode" filterable placeholder="请选择U9C计量单位"><el-option v-for="unit in u9UnitOptions" :key="unit.code" :label="`${unit.code} ${unit.name}`" :value="unit.code" /></el-select><p class="field-help">PDM直接保存并使用U9C计量单位编码，不再进行单位映射；创建料品前会主动校验U9C单位档案。</p></el-form-item><el-form-item label="规格"><el-input v-model="form.specification" /></el-form-item><el-form-item label="材质"><el-input v-model="form.material" /></el-form-item><el-form-item label="品牌"><el-input v-model="form.brand" /></el-form-item><el-form-item label="表面处理"><el-input v-model="form.surfaceTreatment" /></el-form-item><el-form-item label="料品采购链接"><el-input v-model="form.purchaseLink" type="url" placeholder="https://..." /></el-form-item><el-form-item label="重量"><el-input-number v-model="form.weight" :min="0" :precision="6" /><el-input v-model="form.weightUnit" class="weight-unit" /></el-form-item></div>
        <el-form-item label="备注"><el-input v-model="form.remark" type="textarea" :rows="3" /></el-form-item>
      </el-form>
      <template #footer><el-button @click="editorOpen = false">取消</el-button><el-button type="primary" :loading="saving" @click="saveMaterial">{{ editingId ? '保存修改' : '保存草稿' }}</el-button></template>
    </el-dialog>

    <el-dialog v-model="batchEditorOpen" title="批量编辑料品" width="680px">
      <p class="batch-editor-note">已选择 {{ selectedMaterials.length }} 个料品。只会修改勾选的字段；逐条提交，失败料品保持原数据。</p>
      <el-form label-position="top" class="batch-editor-form">
        <el-checkbox-group v-model="batchFields">
          <div class="form-grid">
            <el-form-item><template #label><el-checkbox value="supplyMode">供给方式</el-checkbox></template><el-select v-model="batchForm.supplyMode" :disabled="!batchFields.includes('supplyMode')"><el-option label="采购" value="Purchase" /><el-option label="自制" value="Manufacture" /><el-option label="委外" value="Outsource" /></el-select></el-form-item>
            <el-form-item><template #label><el-checkbox value="unitCode">计量单位</el-checkbox></template><el-select v-model="batchForm.unitCode" filterable :disabled="!batchFields.includes('unitCode')"><el-option v-for="unit in u9UnitOptions" :key="unit.code" :label="`${unit.code} ${unit.name}`" :value="unit.code" /></el-select></el-form-item>
            <el-form-item><template #label><el-checkbox value="specification">规格</el-checkbox></template><el-input v-model="batchForm.specification" :disabled="!batchFields.includes('specification')" /></el-form-item>
            <el-form-item><template #label><el-checkbox value="material">材质</el-checkbox></template><el-input v-model="batchForm.material" :disabled="!batchFields.includes('material')" /></el-form-item>
            <el-form-item><template #label><el-checkbox value="brand">品牌</el-checkbox></template><el-input v-model="batchForm.brand" :disabled="!batchFields.includes('brand')" /></el-form-item>
            <el-form-item><template #label><el-checkbox value="surfaceTreatment">表面处理</el-checkbox></template><el-input v-model="batchForm.surfaceTreatment" :disabled="!batchFields.includes('surfaceTreatment')" /></el-form-item>
          </div>
          <el-form-item><template #label><el-checkbox value="remark">备注</el-checkbox></template><el-input v-model="batchForm.remark" type="textarea" :rows="3" :disabled="!batchFields.includes('remark')" /></el-form-item>
        </el-checkbox-group>
      </el-form>
      <template #footer><el-button @click="batchEditorOpen = false">取消</el-button><el-button type="primary" :loading="saving" @click="saveBatchEdit">保存批量修改</el-button></template>
    </el-dialog>

    <el-dialog v-model="previewOpen" title="U9C请求预览" width="760px"><div v-if="previewTask" class="preview-meta"><span>关联号：{{ previewTask.correlationId }}</span><span>SHA-256：{{ previewTask.payloadSha256 }}</span></div><pre v-if="previewTask" class="payload-preview">{{ previewTask.payloadJson }}</pre></el-dialog>
  </section>
</template>

<style scoped>
.material-master-layout{display:grid;grid-template-columns:190px minmax(0,1fr);gap:var(--pdm-container-gap);min-width:0;background:var(--shell-content-bg)}.material-category-nav,.material-master-content{min-width:0;padding:10px;border:1px solid #e2e8f0;border-radius:8px;background:#fff}.material-category-nav{overflow:auto;font-size:11px}.material-category-nav__title{margin:0 4px 8px;color:#334155;font-weight:600}.material-category-all{width:100%;height:28px;margin-bottom:4px;padding:0 8px;border:0;border-radius:5px;background:transparent;color:#475569;font:inherit;text-align:left;cursor:pointer}.material-category-all:hover,.material-category-all.is-active{background:#eaf3ff;color:#409eff}.material-category-nav :deep(.el-tree){background:#fff;color:#475569;font-size:11px}.material-category-nav :deep(.el-tree-node__content){height:28px;border-radius:5px}.material-category-node{overflow:hidden;text-overflow:ellipsis;white-space:nowrap}.material-master-content{overflow:hidden}
.material-page{min-width:0;min-height:calc(100vh - 112px);overflow:hidden;padding:5px 28px 28px}.material-tabs{min-width:0;max-width:100%}.material-tabs :deep(.el-tabs__content),.material-tabs :deep(.el-tab-pane){min-width:0;max-width:100%;overflow:hidden}.material-toolbar{display:flex;min-width:0;align-items:center;justify-content:flex-start;flex-wrap:nowrap;gap:5px;margin-bottom:14px;font-size:11px}.material-toolbar__actions,.material-toolbar__filters{display:flex;min-width:0;align-items:center;flex-wrap:nowrap;gap:5px}.material-toolbar__actions{flex:0 1 auto}.material-toolbar__filters{flex:1 1 260px}.material-toolbar :deep(.el-button),.material-toolbar :deep(.el-checkbox__label),.material-toolbar :deep(.el-input__inner),.material-toolbar :deep(.el-select__placeholder),.material-toolbar :deep(.el-select__selected-item){font-size:11px}.material-toolbar__actions :deep(.el-button){width:clamp(60px,5vw,80px);height:30px;flex:1 1 60px;margin-left:0;padding:0}.material-toolbar__filters :deep(.el-checkbox){flex:0 0 auto}.material-brand-filter{width:110px;min-width:80px;flex:0 1 110px}.material-toolbar .el-input{width:auto;min-width:80px;flex:1 1 180px}.material-table{width:100%;min-width:0;max-width:100%;box-sizing:border-box}.material-table :deep(.el-table__inner-wrapper),.material-table :deep(.el-scrollbar),.material-table :deep(.el-scrollbar__wrap){max-width:100%}.material-table :deep(.el-scrollbar__wrap){overflow-x:auto}.material-table :deep(.el-table__cell){font-size:11px;text-align:center}.material-table :deep(.cell){overflow:hidden;padding:0 6px;text-overflow:ellipsis;white-space:nowrap}.material-table :deep(.el-button),.material-table :deep(.el-tag){font-size:11px}.u9-validation{display:flex;align-items:center;justify-content:center;white-space:nowrap}.u9-unchecked{color:#64748b;font-size:11px}.batch-editor-note{margin:0 0 14px;color:#64748b;font-size:11px}.batch-editor-form :deep(.el-checkbox){margin-right:0}.category-layout{display:grid;grid-template-columns:minmax(280px,35%) 1fr;gap:18px;min-height:520px}.category-tree-panel,.category-editor{padding:18px;border:1px solid #e2e8f0;border-radius:14px;background:#f8fafc}.category-actions{display:flex;gap:8px;margin-bottom:14px}.category-node{display:flex;align-items:center;justify-content:space-between;gap:12px;width:100%;padding-right:8px}.category-empty{display:grid;min-height:420px;place-items:center;color:#94a3b8}.category-switches{display:flex;flex-wrap:wrap;gap:24px;margin:2px 0 14px}.form-grid{display:grid;grid-template-columns:repeat(2,minmax(0,1fr));column-gap:18px}.field-help{width:100%;margin:6px 0 0;color:#64748b;font-size:11px;line-height:1.5}.weight-unit{width:76px;margin-left:8px}.preview-meta{display:grid;gap:6px;margin-bottom:12px;color:#64748b;font-size:12px;word-break:break-all}.payload-preview{max-height:480px;overflow:auto;padding:18px;border-radius:10px;background:#0f172a;color:#dbeafe;font:12px/1.6 Consolas,monospace;white-space:pre-wrap;word-break:break-all}.material-tabs :deep(.el-tabs__content),.material-tabs :deep(.el-tabs__content *){font-size:11px}:global(.material-editor-dialog),:global(.material-editor-dialog *){font-size:11px}:global(.material-editor-dialog .el-dialog__title){font-size:11px!important}@media(max-width:1000px){.material-page{padding:5px 18px 18px}.material-toolbar__actions :deep(.el-button){width:52px;min-width:52px;flex-basis:52px}.material-brand-filter{width:70px;min-width:70px;flex-basis:70px}.material-toolbar .el-input{min-width:70px;flex-basis:70px}.category-layout{grid-template-columns:1fr}.form-grid{grid-template-columns:1fr}}
.material-page{height:100%;min-height:0;display:flex;flex-direction:column}.material-tabs{min-height:0;flex:1 1 auto;display:flex;flex-direction:column}.material-tabs :deep(.el-tabs__content){min-height:0;flex:1 1 auto}.material-tabs :deep(.el-tab-pane){height:100%;min-height:0}.material-master-layout{height:100%;min-height:0}.material-master-content{display:flex;flex-direction:column}.material-toolbar{flex:0 0 auto;margin-bottom:5px}.material-table-shell{min-height:0;flex:1 1 auto}.material-table{height:100%}.material-pagination{flex:0 0 auto;justify-content:flex-end;margin-top:5px}.material-pagination :deep(.el-pagination__total),.material-pagination :deep(.el-select__selected-item),.material-pagination :deep(button),.material-pagination :deep(.number){font-size:11px}
.material-table :deep(.el-table__body tr.el-table__row){height:30px}.material-table :deep(.el-table__body td.el-table__cell){height:30px;padding:0}.material-table :deep(.el-table__body .el-tag){height:20px;padding-top:0;padding-bottom:0;line-height:18px}
@media(max-width:1000px){.material-master-layout{grid-template-columns:1fr}.material-category-nav{max-height:220px}}
</style>
