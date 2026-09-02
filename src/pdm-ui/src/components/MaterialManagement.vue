<script setup lang="ts">
import { ElMessage, ElMessageBox } from 'element-plus'
import { computed, onMounted, reactive, ref, watch } from 'vue'
import SquareLoader from './SquareLoader.vue'
import MaterialEditorDialog from './MaterialEditorDialog.vue'
import {
  archiveMaterial,
  approveMaterial,
  changeApprovedMaterial,
  createMaterialSyncBatch,
  createMaterial,
  deleteMaterial,
  downloadMaterialAttachment,
  executeMaterialSyncTask,
  getMaterialSyncBatch,
  getMaterialRemovalReadiness,
  getMaterialNumberingSettings,
  getMaterialDuplicateRules,
  listMaterialCategories,
  listMaterialAttachments,
  listMaterialCodeApplications,
  listMaterialPage,
  listMaterialSyncTasks,
  listMaterialSyncBatches,
  materialAttachmentObjectUrl,
  queryU9Material,
  decideMaterialCodeApplication,
  saveMaterialCategory,
  setMaterialCover,
  updateMaterial,
  updateMaterialDuplicateRules,
  updateMaterialNumberingSettings,
  uploadMaterialAttachment,
} from '../api'
import type {
  MaterialAttachment,
  MaterialAttachmentKind,
  MaterialCategory,
  MaterialCodeApplication,
  MaterialKind,
  MaterialDuplicateField,
  MaterialDuplicateRule,
  MaterialRemovalReadiness,
  MaterialSyncBatch,
  MaterialSupplyMode,
  MaterialSyncTask,
  PdmMaterial,
  SaveMaterialInput,
} from '../types'
import { u9UnitLabel, u9UnitOptions } from '../u9Units'
import { useUserDisplayName } from '../userDisplay'

const displayUserName = useUserDisplayName()

const props = withDefaults(defineProps<{
  token: string
  canEdit: boolean
  canApprove: boolean
  canDecideMaterialCode?: boolean
  canManageIntegration: boolean
  requestedTab?: string
}>(), { canDecideMaterialCode: false, requestedTab: 'materials' })
const emit = defineEmits<{ noticeCountsChange: [counts: { syncTasks: number; codeApprovals: number }] }>()

const activeTab = ref('materials')
const categoryNavCollapsed = ref(true)
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
const materialTotal = ref(0)
const materialPageLoading = ref(false)
const categories = ref<MaterialCategory[]>([])
const tasks = ref<MaterialSyncTask[]>([])
type SyncTaskSelectionTable = { clearSelection: () => void }
const syncTaskSelectionTable = ref<SyncTaskSelectionTable | null>(null)
const selectedSyncTasks = ref<MaterialSyncTask[]>([])
const batchSyncingTasks = ref(false)
type MaterialCodeApprovalRow = MaterialCodeApplication & { groupedApplications?: MaterialCodeApplication[] }
const codeApplications = ref<MaterialCodeApplication[]>([])
const codeApprovalView = ref<'pending' | 'history'>('pending')
const workflowPageSize = 50
const pendingApprovalPage = ref(1)
const pendingSyncPage = ref(1)
const approvalHistoryPage = ref(1)
const approvalHistoryPageSize = ref(20)
const syncHistoryPage = ref(1)
const syncHistoryPageSize = ref(20)
const decidingApplicationId = ref<string | null>(null)
const selectedCodeApplications = ref<MaterialCodeApprovalRow[]>([])
const batchDecidingApplications = ref(false)
const approvalProgressText = ref('')
const syncProgressText = ref('')
type WorkflowResultLevel = 'success' | 'warning' | 'error'
interface WorkflowResult {
  level: WorkflowResultLevel
  title: string
  summary: string
  details: string[]
}
const approvalResult = ref<WorkflowResult | null>(null)
const syncResult = ref<WorkflowResult | null>(null)
const query = ref('')
const brandFilter = ref('')
const showArchived = ref(false)
const pageSize = ref(50)
const currentPage = ref(1)
const editorOpen = ref(false)
const editingId = ref<string | null>(null)
const editorAttachments = ref<MaterialAttachment[]>([])
const uploadingAttachmentKind = ref<MaterialAttachmentKind | null>(null)
const attachmentUploadProgress = ref(0)
const coverUrl = ref('')
const attachmentViewerOpen = ref(false)
const attachmentViewerLoading = ref(false)
const attachmentViewerMaterial = ref<PdmMaterial | null>(null)
const attachmentViewerKind = ref<MaterialAttachmentKind>('Model3D')
const attachmentViewerItems = ref<MaterialAttachment[]>([])
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
const numberingStartSequence = ref(1000000)
const savingNumberingSettings = ref(false)
const duplicateRules = ref<MaterialDuplicateRule[]>([])
const savingDuplicateRules = ref(false)
const duplicateFieldOptions: Array<{ value: MaterialDuplicateField; label: string }> = [
  { value: 'Name', label: '名称' },
  { value: 'Specification', label: '型号' },
  { value: 'Brand', label: '品牌' },
]
const categoryDraft = reactive<MaterialCategory>({
  code: '', name: '', parentCode: null, u9CategoryId: null, pdmKind: null, defaultSupplyMode: 'Purchase',
  allowCreate: false, isVisible: true, isActive: true, numberPrefix: '', sequenceLength: 7, counterScope: '',
  sortOrder: 0, updatedBy: '', updatedAt: '', rowVersion: 0,
  currentSequence: 0,
})
const applicationWorkflowCompleted = (application: MaterialCodeApplication) => application.status === 'Rejected'
  || application.status === 'Approved' && application.workflowState === 'Completed'
const approvedApplicationsAwaitingSync = computed(() => codeApplications.value
  .filter(application => application.status === 'Approved' && !applicationWorkflowCompleted(application)))
const applicationsForSyncTask = (task: MaterialSyncTask) => approvedApplicationsAwaitingSync.value
  .filter(application => application.materialId === task.materialId)
const currentSynchronizationTasks = computed(() => {
  const seenMaterials = new Set<string>()
  return tasks.value.filter(task => {
    if (task.status === 'Superseded' || seenMaterials.has(task.materialId)) return false
    seenMaterials.add(task.materialId)
    return task.status !== 'Succeeded' || applicationsForSyncTask(task).length > 0
  }).sort((left, right) => {
    const leftCode = left.materialCode?.trim() ?? ''
    const rightCode = right.materialCode?.trim() ?? ''
    if (!leftCode || !rightCode) return leftCode ? -1 : rightCode ? 1 : 0
    return leftCode.localeCompare(rightCode, undefined, { numeric: true })
  })
})
const pagedCurrentSynchronizationTasks = computed(() => currentSynchronizationTasks.value.slice(
  (pendingSyncPage.value - 1) * workflowPageSize,
  pendingSyncPage.value * workflowPageSize,
))
const executableSyncStatuses = new Set<MaterialSyncTask['status']>(['PreviewReady', 'Failed', 'NeedsReview'])
const selectedExecutableSyncTasks = computed(() => {
  const selectedIds = new Set(selectedSyncTasks.value.map(task => task.id))
  return currentSynchronizationTasks.value.filter(task => selectedIds.has(task.id) && executableSyncStatuses.has(task.status))
})
const syncTaskNoticeCount = computed(() => currentSynchronizationTasks.value.length)
const codeApprovalNoticeCount = computed(() => props.canDecideMaterialCode
  ? codeApplications.value.filter(application => application.status === 'Pending' && application.applicationType !== 'BomHeader').length
    + new Set(codeApplications.value
      .filter(application => application.status === 'Pending' && application.applicationType === 'BomHeader')
      .map(application => application.projectId)).size
  : 0)
const pendingCodeApplicationCount = computed(() => codeApplications.value.filter(application => application.status === 'Pending').length)
const currentWorkCount = computed(() => pendingCodeApplicationCount.value + currentSynchronizationTasks.value.length)
const historyCodeApplicationRows = computed(() => codeApplications.value.filter(application => applicationWorkflowCompleted(application)))
const synchronizationHistoryTasks = computed(() => tasks.value
  .filter(task => task.status === 'Succeeded' && applicationsForSyncTask(task).length === 0)
  .sort((left, right) => right.updatedAt.localeCompare(left.updatedAt)))
const historyWorkCount = computed(() => historyCodeApplicationRows.value.length + synchronizationHistoryTasks.value.length)
const pagedApprovalHistoryRows = computed(() => historyCodeApplicationRows.value.slice(
  (approvalHistoryPage.value - 1) * approvalHistoryPageSize.value,
  approvalHistoryPage.value * approvalHistoryPageSize.value,
))
const pagedSynchronizationHistoryTasks = computed(() => synchronizationHistoryTasks.value.slice(
  (syncHistoryPage.value - 1) * syncHistoryPageSize.value,
  syncHistoryPage.value * syncHistoryPageSize.value,
))
watch([syncTaskNoticeCount, codeApprovalNoticeCount], ([syncTasks, codeApprovals]) => {
  emit('noticeCountsChange', { syncTasks, codeApprovals })
}, { immediate: true })

const emptyForm = (): SaveMaterialInput => ({
  materialCode: '', name: '', kind: 'Electrical', categoryCode: '', supplyMode: 'Purchase', unitCode: '001',
  specification: '', material: '', remark: '', brand: '', surfaceTreatment: '', purchaseLink: '', weight: null, weightUnit: 'kg',
  selectionAdvice: '', referencePrice: null, model3DLink: '', documentLink: '', isRecommended: false,
})
const form = reactive<SaveMaterialInput>(emptyForm())

const pagedMaterials = computed(() => materials.value)

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
const creatableCategories = computed(() => categories.value.filter(category => category.allowCreate && category.isVisible && category.isActive && category.pdmKind))

const kindLabels: Record<MaterialKind, string> = { Electrical: '电气外购件', Standard: '机械外购件', NonStandard: '非标机加件', Product: '产品/组件' }
const supplyLabels: Record<MaterialSupplyMode, string> = { Purchase: '采购', Manufacture: '自制', Outsource: '委外' }
const syncLabels: Record<string, string> = {
  NotQueued: '未排队', PreviewReady: '请求预览', Pending: '待同步', Succeeded: '已同步', Failed: '失败', NeedsReview: '待复核', Superseded: '已废止',
}
const workflowLabels: Record<string, string> = {
  PendingMaterialSync: '待同步料品', MaterialSyncFailed: '料品同步失败', PendingBomSync: '待同步A1 BOM', BomSyncFailed: 'A1 BOM同步失败',
}
const bomHeaderLabels: Record<string, string> = { Master: '项目主BOM', Standard: '标准件BOM', NonStandard: '非标件BOM', Electrical: '电气BOM' }
const bomHeaderOrder: Record<string, number> = { Master: 0, Standard: 1, NonStandard: 2, Electrical: 3 }
const pendingCodeApplicationRows = computed<MaterialCodeApprovalRow[]>(() => {
  const pendingGroups = new Map<string, MaterialCodeApplication[]>()
  for (const application of codeApplications.value) {
    if (application.status !== 'Pending' || application.applicationType !== 'BomHeader') continue
    const group = pendingGroups.get(application.projectId) ?? []
    group.push(application)
    pendingGroups.set(application.projectId, group)
  }

  const emittedProjects = new Set<string>()
  return codeApplications.value.flatMap(application => {
    if (application.status !== 'Pending') return []
    if (application.applicationType !== 'BomHeader') return [application]
    if (emittedProjects.has(application.projectId)) return []
    emittedProjects.add(application.projectId)
    const groupedApplications = [...(pendingGroups.get(application.projectId) ?? [application])]
      .sort((left, right) => (bomHeaderOrder[left.bomHeaderKind ?? ''] ?? 99) - (bomHeaderOrder[right.bomHeaderKind ?? ''] ?? 99))
    if (groupedApplications.length === 1) return [application]
    const categoryCodes = [...new Set(groupedApplications.map(item => item.categoryCode).filter(Boolean))]
    return [{
      ...application,
      id: `bom-header-group:${application.projectId}`,
      categoryCode: categoryCodes.join('、'),
      applicationName: `${application.projectCode || application.projectName || '项目'} ${groupedApplications.length} 个BOM料号`,
      groupedApplications,
    }]
  })
})
const pagedPendingCodeApplicationRows = computed(() => pendingCodeApplicationRows.value.slice(
  (pendingApprovalPage.value - 1) * workflowPageSize,
  pendingApprovalPage.value * workflowPageSize,
))
watch(() => pendingCodeApplicationRows.value.length, total => {
  pendingApprovalPage.value = Math.min(pendingApprovalPage.value, Math.max(1, Math.ceil(total / workflowPageSize)))
})
watch(() => currentSynchronizationTasks.value.length, total => {
  pendingSyncPage.value = Math.min(pendingSyncPage.value, Math.max(1, Math.ceil(total / workflowPageSize)))
})
const applicationsForApprovalRow = (application: MaterialCodeApprovalRow) => application.groupedApplications ?? [application]
const applicationTypeLabel = (application: MaterialCodeApprovalRow) => application.groupedApplications
  ? `BOM料号（${application.groupedApplications.length}项）`
  : application.applicationType === 'BomHeader' ? 'BOM料号' : '标准件料号'
const applicationTargetLabel = (application: MaterialCodeApprovalRow) => application.groupedApplications
  ? application.groupedApplications.map(item => item.bomHeaderKind ? bomHeaderLabels[item.bomHeaderKind] : 'BOM').join('、')
  : application.bomHeaderKind ? bomHeaderLabels[application.bomHeaderKind] : '标准件BOM物料'
const applicationMaterialCodeLabel = (application: MaterialCodeApprovalRow) => application.groupedApplications
  ? '—'
  : application.materialCode || application.requestedMaterialCode || '—'
const categoryLabel = (item: PdmMaterial) => {
  const code = item.categoryCode ?? item.u9CategoryCode
  if (!code) return '—'
  return `${code} ${categories.value.find(category => category.code === code)?.name ?? kindLabels[item.kind]}`
}
const positiveWeight = (item: PdmMaterial) => item.weight != null && item.weight > 0 ? item.weight : null
const weightLabel = (item: PdmMaterial) => {
  const weight = positiveWeight(item)
  return weight == null ? '—' : `${weight}${item.weightUnit ? ` ${item.weightUnit}` : ''}`
}
const referencePriceLabel = (item: PdmMaterial) => item.referencePrice == null ? '—' : item.referencePrice.toLocaleString('zh-CN', { minimumFractionDigits: 2, maximumFractionDigits: 2 })
const dateTimeLabel = (value: string) => {
  const date = new Date(value)
  return Number.isNaN(date.getTime()) ? '—' : date.toLocaleString('zh-CN', { hour12: false })
}
const materialCodePlaceholder = computed(() => {
  const category = categories.value.find(item => item.code === form.categoryCode)
  if (editingId.value) return ''
  return category ? `${category.numberPrefix} + ${category.sequenceLength}位流水（保存后生成）` : '选择开放分类后自动生成'
})
const selectedMaterial = computed(() => selectedMaterials.value.length === 1 ? selectedMaterials.value[0] : null)
const canEditSelected = computed(() => Boolean(selectedMaterial.value
  && !selectedMaterial.value.isArchived))
const canBatchEditSelected = computed(() => selectedMaterials.value.length > 1
  && selectedMaterials.value.every(item => !item.isArchived && !['Pending', 'NeedsReview'].includes(item.syncStatus)))
const editorU9FieldsLocked = computed(() => Boolean(editingId.value && ['Pending', 'NeedsReview'].includes(materials.value.find(item => item.id === editingId.value)?.syncStatus ?? '')))
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
  editorAttachments.value = []
  Object.assign(form, emptyForm())
  replaceCoverUrl('')
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
    selectionAdvice: item.selectionAdvice ?? '',
    referencePrice: item.referencePrice ?? null,
    model3DLink: item.model3DLink ?? '',
    documentLink: item.documentLink ?? '',
    isRecommended: item.isRecommended ?? false,
    weight: positiveWeight(item),
    weightUnit: positiveWeight(item) == null ? '' : item.weightUnit ?? 'kg',
    expectedRowVersion: item.rowVersion,
  })
  editorOpen.value = true
  editorAttachments.value = []
  void loadEditorAttachments(item.id)
}

function attachmentCount(item: PdmMaterial, kind: MaterialAttachmentKind) {
  return kind === 'Model3D' ? item.model3DAttachmentCount ?? 0 : item.documentAttachmentCount ?? 0
}

async function loadEditorAttachments(materialId: string) {
  try {
    editorAttachments.value = await listMaterialAttachments(materialId, props.token)
    const material = materials.value.find(item => item.id === materialId)
    const coverId = material?.coverImageAttachmentId
    replaceCoverUrl(coverId ? await materialAttachmentObjectUrl(materialId, coverId, props.token) : '')
  } catch (error) {
    editorAttachments.value = []
    ElMessage.error(error instanceof Error ? error.message : '附件列表加载失败')
  }
}

function replaceCoverUrl(next: string) {
  if (coverUrl.value.startsWith('blob:')) URL.revokeObjectURL(coverUrl.value)
  coverUrl.value = next
}

async function handleAttachmentFiles(kind: MaterialAttachmentKind, event: Event) {
  const input = event.target as HTMLInputElement
  const files = Array.from(input.files ?? [])
  input.value = ''
  if (!editingId.value || files.length === 0) return
  const materialId = editingId.value
  uploadingAttachmentKind.value = kind
  attachmentUploadProgress.value = 0
  const failures: string[] = []
  let uploaded = 0
  try {
    for (let index = 0; index < files.length; index++) {
      const file = files[index]
      try {
        const attachment = await uploadMaterialAttachment(materialId, kind, file, props.token, percent => {
          attachmentUploadProgress.value = Math.round(((index + percent / 100) / files.length) * 100)
        })
        editorAttachments.value.unshift(attachment)
        if (kind === 'CoverImage') {
          const current = materials.value.find(item => item.id === materialId)
          if (!current) throw new Error('料品主档已刷新，请重新打开编辑器')
          const saved = await setMaterialCover(materialId, attachment.id, current.rowVersion, props.token)
          const materialIndex = materials.value.findIndex(item => item.id === materialId)
          if (materialIndex >= 0) materials.value[materialIndex] = saved
          form.expectedRowVersion = saved.rowVersion
          replaceCoverUrl(await materialAttachmentObjectUrl(materialId, attachment.id, props.token))
        }
        uploaded++
      } catch (error) {
        failures.push(`${file.name}：${error instanceof Error ? error.message : '上传失败'}`)
      }
    }
    const material = materials.value.find(item => item.id === materialId)
    if (material && uploaded > 0) {
      if (kind === 'Model3D') material.model3DAttachmentCount = (material.model3DAttachmentCount ?? 0) + uploaded
      if (kind === 'Document') material.documentAttachmentCount = (material.documentAttachmentCount ?? 0) + uploaded
    }
    if (uploaded > 0) ElMessage.success(kind === 'CoverImage' ? '封面图片已存档并设为当前封面' : `已上传 ${uploaded} 个${kind === 'Model3D' ? '3D' : '资料'}附件`)
    if (failures.length > 0) ElMessage.error(failures.join('；'))
  } finally {
    uploadingAttachmentKind.value = null
    attachmentUploadProgress.value = 0
  }
}

async function clearCover() {
  if (!editingId.value) return
  try {
    const current = materials.value.find(item => item.id === editingId.value)
    if (!current) return
    const saved = await setMaterialCover(current.id, null, current.rowVersion, props.token)
    const index = materials.value.findIndex(item => item.id === current.id)
    if (index >= 0) materials.value[index] = saved
    form.expectedRowVersion = saved.rowVersion
    replaceCoverUrl('')
    ElMessage.success('当前封面已清除，历史图片仍保留')
  } catch (error) {
    ElMessage.error(error instanceof Error ? error.message : '封面清除失败')
  }
}

const attachmentViewerTitle = computed(() => {
  const kind = attachmentViewerKind.value === 'Model3D' ? '3D' : '资料'
  return `${attachmentViewerMaterial.value?.materialCode ?? ''} ${kind}附件`.trim()
})

async function openAttachmentViewer(material: PdmMaterial, kind: MaterialAttachmentKind) {
  attachmentViewerMaterial.value = material
  attachmentViewerKind.value = kind
  attachmentViewerItems.value = []
  attachmentViewerOpen.value = true
  attachmentViewerLoading.value = true
  try {
    attachmentViewerItems.value = await listMaterialAttachments(material.id, props.token, kind)
  } catch (error) {
    ElMessage.error(error instanceof Error ? error.message : '附件列表加载失败')
  } finally {
    attachmentViewerLoading.value = false
  }
}

async function downloadAttachment(attachment: MaterialAttachment) {
  try {
    await downloadMaterialAttachment(attachment.materialId, attachment, props.token)
  } catch (error) {
    ElMessage.error(error instanceof Error ? error.message : '附件下载失败')
  }
}

function fileSizeLabel(bytes: number) {
  if (bytes < 1024) return `${bytes} B`
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`
  if (bytes < 1024 * 1024 * 1024) return `${(bytes / 1024 / 1024).toFixed(1)} MB`
  return `${(bytes / 1024 / 1024 / 1024).toFixed(1)} GB`
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
    const [materialPage, loadedCategories, loadedTasks, loadedCodeApplications, numberingSettings, loadedDuplicateRules] = await Promise.all([
      listMaterialPage(props.token, { query: query.value, categoryCode: selectedMaterialCategoryCode.value, brand: brandFilter.value, includeArchived: showArchived.value, page: currentPage.value, pageSize: pageSize.value }),
      listMaterialCategories(props.token, props.canManageIntegration), listMaterialSyncTasks(props.token), listMaterialCodeApplications(props.token),
      props.canManageIntegration ? getMaterialNumberingSettings(props.token) : Promise.resolve(null),
      props.canManageIntegration ? getMaterialDuplicateRules(props.token) : Promise.resolve([]),
    ])
    materials.value = materialPage.items
    materialTotal.value = materialPage.total
    selectedMaterials.value = []
    categories.value = loadedCategories
    tasks.value = loadedTasks
    selectedSyncTasks.value = []
    codeApplications.value = loadedCodeApplications
    selectedCodeApplications.value = []
    if (numberingSettings) numberingStartSequence.value = numberingSettings.startSequence
    duplicateRules.value = loadedDuplicateRules
  } catch (error) {
    ElMessage.error(error instanceof Error ? error.message : '料品数据加载失败')
  } finally {
    loading.value = false
  }
}

async function loadMaterialPage() {
  materialPageLoading.value = true
  try {
    const result = await listMaterialPage(props.token, {
      query: query.value,
      categoryCode: selectedMaterialCategoryCode.value,
      brand: brandFilter.value,
      includeArchived: showArchived.value,
      page: currentPage.value,
      pageSize: pageSize.value,
    })
    materials.value = result.items
    materialTotal.value = result.total
    selectedMaterials.value = []
  } catch (error) {
    ElMessage.error(error instanceof Error ? error.message : '料品主档查询失败')
  } finally {
    materialPageLoading.value = false
  }
}

function resetAndLoadMaterialPage() {
  if (currentPage.value !== 1) currentPage.value = 1
  else void loadMaterialPage()
}

let materialQueryTimer: ReturnType<typeof setTimeout> | undefined
watch(query, () => {
  if (materialQueryTimer) clearTimeout(materialQueryTimer)
  materialQueryTimer = setTimeout(resetAndLoadMaterialPage, 250)
})
watch([brandFilter, selectedMaterialCategoryCode, showArchived], resetAndLoadMaterialPage)
watch(currentPage, () => { void loadMaterialPage() })
watch(pageSize, resetAndLoadMaterialPage)

async function loadCodeApplications() {
  try {
    codeApplications.value = await listMaterialCodeApplications(props.token)
    selectedCodeApplications.value = []
  }
  catch (error) { ElMessage.error(error instanceof Error ? error.message : '料号审批加载失败') }
}

watch(activeTab, value => { if (value === 'code-approvals' && codeApplications.value.length === 0) void loadCodeApplications() })
watch(codeApprovalView, () => {
  selectedCodeApplications.value = []
  selectedSyncTasks.value = []
})

function changePendingApprovalPage(page: number) {
  pendingApprovalPage.value = page
  selectedCodeApplications.value = []
}

function changePendingSyncPage(page: number) {
  pendingSyncPage.value = page
  selectedSyncTasks.value = []
  syncTaskSelectionTable.value?.clearSelection()
}

function showSyncResult(level: WorkflowResultLevel, title: string, summary: string, details: string[] = []) {
  syncResult.value = { level, title, summary, details }
}

function showApprovalResult(
  approved: boolean,
  total: number,
  succeeded: number,
  failures: string[],
  automationWarnings: string[],
) {
  const action = approved ? '批准' : '退回'
  const summary = `共 ${total} 项，已${action} ${succeeded} 项，失败 ${failures.length} 项${automationWarnings.length ? `，后续待处理 ${automationWarnings.length} 项` : ''}。`
  const level: WorkflowResultLevel = failures.length > 0
    ? succeeded > 0 ? 'warning' : 'error'
    : automationWarnings.length > 0 ? 'warning' : 'success'
  approvalResult.value = { level, title: failures.length || automationWarnings.length ? `审批${succeeded ? '部分完成' : '失败'}` : '审批完成', summary, details: [
    ...automationWarnings.map(message => `待处理：${message}`),
    ...failures.map(message => `失败：${message}`),
  ] }
}

async function decideCodeApplication(application: MaterialCodeApprovalRow, approved: boolean) {
  const targets = applicationsForApprovalRow(application)
  let comment = ''
  if (!approved) {
    try {
      const result = await ElMessageBox.prompt(
        targets.length > 1 ? `将退回该项目的 ${targets.length} 项BOM料号申请，请填写统一原因。` : '请填写退回原因。',
        '退回料号申请',
        { inputType: 'textarea', confirmButtonText: '退回', cancelButtonText: '取消' },
      )
      comment = result.value
    } catch { return }
  }
  decidingApplicationId.value = application.id
  let succeeded = 0
  const failures: string[] = []
  const automationWarnings: string[] = []
  try {
    for (const [index, target] of targets.entries()) {
      try {
        approvalProgressText.value = approved
          ? `正在处理第 ${index + 1}/${targets.length} 项：按PLM基线分配料号，批准后请在本页选择对应记录完成U9C同步。`
          : `正在退回第 ${index + 1}/${targets.length} 项料号申请。`
        const result = await decideMaterialCodeApplication(target.id, target.rowVersion, approved, comment, props.token)
        succeeded++
        if (approved && (result.automation?.stage === 'ItemSyncFailed' || result.automation?.stage === 'BomSyncFailed'))
          automationWarnings.push(`${bomHeaderLabels[target.bomHeaderKind ?? ''] || target.applicationName || target.id}：${result.automation.message}`)
      } catch (error) {
        failures.push(`${bomHeaderLabels[target.bomHeaderKind ?? ''] || target.applicationName || target.id}：${error instanceof Error ? error.message : '处理失败'}`)
      }
    }
    if (succeeded) await load()
    showApprovalResult(approved, targets.length, succeeded, failures, automationWarnings)
  } finally {
    decidingApplicationId.value = null
    approvalProgressText.value = ''
  }
}

function canSelectCodeApplication(application: MaterialCodeApprovalRow) {
  return applicationsForApprovalRow(application).every(item => item.status === 'Pending')
    && props.canDecideMaterialCode && !batchDecidingApplications.value && decidingApplicationId.value === null
}

async function decideSelectedCodeApplications(approved: boolean) {
  const targets = [...new Map(selectedCodeApplications.value
    .flatMap(applicationsForApprovalRow)
    .filter(application => application.status === 'Pending')
    .map(application => [application.id, application])).values()]
  if (targets.length === 0 || batchDecidingApplications.value || decidingApplicationId.value !== null) return

  let comment = ''
  try {
    if (approved) {
      await ElMessageBox.confirm(
        `确认批量批准已选择的 ${targets.length} 项料号申请？批准后系统分配PLM料号，对应记录将留在本页等待勾选同步到U9C。`,
        '批量批准料号申请',
        { type: 'warning', confirmButtonText: '批量批准', cancelButtonText: '取消' },
      )
    } else {
      const result = await ElMessageBox.prompt(
        `将批量退回已选择的 ${targets.length} 项料号申请，请填写统一退回原因。`,
        '批量退回料号申请',
        {
          inputType: 'textarea', confirmButtonText: '批量退回', cancelButtonText: '取消',
          inputValidator: value => Boolean(value.trim()) || '请填写退回原因',
        },
      )
      comment = result.value.trim()
    }
  } catch { return }

  batchDecidingApplications.value = true
  let succeeded = 0
  const failures: string[] = []
  const automationWarnings: string[] = []
  try {
    for (const [index, application] of targets.entries()) {
      try {
        approvalProgressText.value = approved
          ? `正在批量处理第 ${index + 1}/${targets.length} 项：按PLM基线分配料号，随后在本页执行第二步U9C同步。`
          : `正在批量退回第 ${index + 1}/${targets.length} 项料号申请。`
        const result = await decideMaterialCodeApplication(application.id, application.rowVersion, approved, comment, props.token)
        succeeded++
        if (approved && (result.automation?.stage === 'ItemSyncFailed' || result.automation?.stage === 'BomSyncFailed'))
          automationWarnings.push(`${application.projectCode || application.applicationName || application.id}：${result.automation.message}`)
      } catch (error) {
        failures.push(`${application.projectCode || application.applicationName || application.id}：${error instanceof Error ? error.message : '处理失败'}`)
      }
    }

    if (succeeded) await load()
    showApprovalResult(approved, targets.length, succeeded, failures, automationWarnings)
  } finally {
    batchDecidingApplications.value = false
    approvalProgressText.value = ''
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
    selectionAdvice: item.selectionAdvice ?? '',
    referencePrice: item.referencePrice ?? null,
    model3DLink: item.model3DLink ?? '',
    documentLink: item.documentLink ?? '',
    isRecommended: item.isRecommended ?? false,
    weight: positiveWeight(item),
    weightUnit: positiveWeight(item) == null ? '' : item.weightUnit ?? 'kg',
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
        if (changeResult?.task) tasks.value.unshift(changeResult.task)
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
    const wasCreating = !editingId.value
    const existing = editingId.value ? materials.value.find(item => item.id === editingId.value) : undefined
    const changeResult = editingId.value && existing?.approvalStatus === 'Approved'
      ? await changeApprovedMaterial(editingId.value, { ...form }, props.token)
      : null
    const saved = changeResult?.material ?? (editingId.value
      ? await updateMaterial(editingId.value, { ...form }, props.token)
      : await createMaterial({ ...form }, props.token))
    if (changeResult?.task) tasks.value.unshift(changeResult.task)
    const index = materials.value.findIndex(item => item.id === saved.id)
    if (index >= 0) materials.value[index] = saved
    else materials.value.push(saved)
    materials.value.sort((left, right) => Number(Boolean(right.isRecommended)) - Number(Boolean(left.isRecommended)) || right.referenceCount - left.referenceCount || left.materialCode.localeCompare(right.materialCode))
    if (wasCreating) {
      editingId.value = saved.id
      Object.assign(form, materialInput(saved))
      editorAttachments.value = []
    } else {
      editorOpen.value = false
    }
    ElMessage.success(changeResult?.task
      ? existing?.u9SyncConfirmed
        ? '料品已更新，并生成U9C修改预览'
        : '料品已更新，旧请求已废止并生成新的U9C创建预览'
      : wasCreating ? '料品草稿已创建，可以继续上传3D和资料' : changeResult ? 'PLM专属字段已更新，不生成U9C任务' : '料品已更新')
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
}

async function saveCategory() {
  try {
    if (!categoryDraft.code.trim() || !categoryDraft.name.trim()) {
      ElMessage.warning('分类编码和名称不能为空')
      return
    }
    if (!categoryDraft.numberPrefix.trim()) categoryDraft.numberPrefix = categoryDraft.code.trim()
    if (!categoryDraft.counterScope.trim()) categoryDraft.counterScope = categoryDraft.code.trim()
    if (categoryDraft.allowCreate) categoryDraft.sequenceLength = 7
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

async function saveNumberingSettings() {
  savingNumberingSettings.value = true
  try {
    const saved = await updateMaterialNumberingSettings(numberingStartSequence.value, props.token)
    numberingStartSequence.value = saved.startSequence
    ElMessage.success(`PLM全局起始流水已保存为 ${saved.startSequence}`)
  } catch (error) {
    ElMessage.error(error instanceof Error ? error.message : 'PLM起始流水保存失败')
  } finally {
    savingNumberingSettings.value = false
  }
}

async function saveDuplicateRules() {
  savingDuplicateRules.value = true
  try {
    if (duplicateRules.value.some(rule => rule.fields.length === 0)) throw new Error('每个料品分类至少选择一个查重字段')
    duplicateRules.value = await updateMaterialDuplicateRules(duplicateRules.value, props.token)
    ElMessage.success('各分类料品查重规则已保存')
  } catch (error) {
    ElMessage.error(error instanceof Error ? error.message : '料品查重规则保存失败')
  } finally {
    savingDuplicateRules.value = false
  }
}

async function archiveSelected() {
  const item = selectedMaterial.value
  if (!item) return
  try {
    await ElMessageBox.confirm(
      `停用后将禁止新BOM引用 ${item.materialCode}，历史BOM不受影响；本操作只停用PLM料品，不会停用或物理删除U9C料品。`,
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
      `将检查选中的 ${targets.length} 个料品。仅PLM主控且未被BOM引用的料品可删除；若U9C存在，将先请求U9C删除，U9C因业务引用拒绝时PLM保持不变，只有U9C删除成功并回查不存在后才删除PLM主档。`,
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
  if (readiness.pdmReferenceCount > 0) return { label: `PLM引用${readiness.pdmReferenceCount}`, message: readiness.decision }
  if (!u9Exists) return { label: '可安全删除', message: 'PLM未发现引用，且U9C实时查询未找到该料品。' }
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
        ? { label: 'U9主控', message: 'U9C主控料品不允许从PLM发起物理删除。' }
        : { label: '删除校验不可用', message: '未取得PLM引用状态，删除判定保持关闭。' }
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
    const message = `编码 ${item.materialCode} 的规格冲突：PLM为“${item.specification?.trim() || '空'}”，U9C为“${u9Specifications.join('、')}”；删除检查：${removal.message}`
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

function syncTaskWorkflowState(task: MaterialSyncTask) {
  const states = applicationsForSyncTask(task).map(application => application.workflowState)
  return states.find(state => state === 'MaterialSyncFailed' || state === 'BomSyncFailed')
    ?? states.find(state => state === 'PendingBomSync')
    ?? states.find(state => state === 'PendingMaterialSync')
}

function syncTaskStatusLabel(task: MaterialSyncTask) {
  const state = syncTaskWorkflowState(task)
  return state ? workflowLabels[state] : syncLabels[task.status]
}

function syncTaskTagType(task: MaterialSyncTask) {
  const state = syncTaskWorkflowState(task)
  if (state === 'MaterialSyncFailed' || state === 'BomSyncFailed' || task.status === 'Failed') return 'danger'
  if (state === 'PendingMaterialSync' || state === 'PendingBomSync' || task.status === 'PreviewReady') return 'warning'
  return task.status === 'Succeeded' ? 'success' : 'info'
}

function syncTaskError(task: MaterialSyncTask) {
  return applicationsForSyncTask(task).find(application => application.syncError)?.syncError
    ?? applicationsForSyncTask(task).find(application => application.workflowMessage)?.workflowMessage
    ?? task.lastError
    ?? '—'
}

function isMaterialCodeConflictTask(task: MaterialSyncTask) {
  return task.operation === 'Create'
    && ['Failed', 'NeedsReview'].includes(task.status)
    && syncTaskError(task).includes('U9C已存在料号')
}

function syncTaskActionLabel(task: MaterialSyncTask) {
  return isMaterialCodeConflictTask(task) ? '重新分配并同步' : '同步'
}

const batchSyncActionLabel = computed(() => selectedExecutableSyncTasks.value.some(isMaterialCodeConflictTask)
  ? '批量重新分配并同步'
  : '批量同步到U9C')

async function executeTask(task: MaterialSyncTask) {
  const reassigning = isMaterialCodeConflictTask(task)
  const targetLabel = task.materialCode || task.materialName || '该料品'
  try {
    await ElMessageBox.confirm(
      reassigning
        ? `确认按最新分类流水为 ${targetLabel} 重新分配料号，并同步到U9C？BOM申请还会继续创建或追加A1 BOM。`
        : `确认将 ${targetLabel} 同步到U9C？若料号已被占用，系统会自动换号并重试；BOM申请还会继续创建或追加A1 BOM。`,
      reassigning ? '重新分配并同步' : '同步到 U9C',
      { type: 'warning', confirmButtonText: reassigning ? '确认重新分配并同步' : '确认同步', cancelButtonText: '取消' },
    )
    syncingTaskId.value = task.id
    syncProgressText.value = `正在同步 ${task.materialCode || task.materialName || '该料品'} 到U9C。`
    const result = await executeMaterialSyncTask(task.id, props.token)
    await load()
    showSyncResult(
      result.completed ? 'success' : 'warning',
      result.completed ? '同步完成' : '同步部分完成',
      result.message || (result.completed ? '料号审批与U9C同步流程已完成。' : 'U9C料品已同步，A1 BOM仍待后续条件满足。'),
    )
  } catch (error) {
    if (error === 'cancel' || error === 'close') return
    showSyncResult('error', '同步失败', error instanceof Error ? error.message : 'U9C料品同步失败')
    await load()
  } finally {
    syncingTaskId.value = null
    syncProgressText.value = ''
  }
}

function canExecuteSyncTask(task: MaterialSyncTask) {
  return (props.canApprove || props.canDecideMaterialCode)
    && !batchSyncingTasks.value
    && syncingTaskId.value === null
    && currentSynchronizationTasks.value.some(item => item.id === task.id)
    && !['Pending', 'Superseded'].includes(task.status)
}

function canSelectSyncTask(task: MaterialSyncTask) {
  return canExecuteSyncTask(task) && executableSyncStatuses.has(task.status)
}

async function executeSelectedTasks() {
  const targets = selectedExecutableSyncTasks.value
  if (!(props.canApprove || props.canDecideMaterialCode) || targets.length === 0 || batchSyncingTasks.value || syncingTaskId.value !== null) return
  const reassigning = targets.some(isMaterialCodeConflictTask)

  try {
    await ElMessageBox.confirm(
      reassigning
        ? `确认批量处理已选择的 ${targets.length} 个任务？重复料号失败项将先按最新分类流水重新分配，再同步到U9C；其他任务正常同步，单条失败不会中断其余任务。`
        : `确认批量同步已选择的 ${targets.length} 个任务到U9C？系统会逐条处理，重复料号自动换号；单条失败不会中断其余任务。`,
      reassigning ? '批量重新分配并同步' : '批量同步到 U9C',
      { type: 'warning', confirmButtonText: reassigning ? '确认批量重新分配并同步' : '确认批量同步', cancelButtonText: '取消' },
    )
  } catch { return }

  batchSyncingTasks.value = true
  try {
    const batch = await createMaterialSyncBatch(targets.map(task => task.id), props.token)
    await monitorMaterialSyncBatch(batch)
  } catch (error) {
    showSyncResult('error', '同步批次提交失败', error instanceof Error ? error.message : 'U9C批量同步任务提交失败')
  } finally {
    batchSyncingTasks.value = false
    selectedSyncTasks.value = []
    syncTaskSelectionTable.value?.clearSelection()
    syncProgressText.value = ''
  }
}

async function monitorMaterialSyncBatch(initial: MaterialSyncBatch) {
  let batch = initial
  while (batch.status === 'Queued' || batch.status === 'Running') {
    syncProgressText.value = batch.status === 'Queued'
      ? `批次已提交，等待后台执行（${batch.completedCount}/${batch.totalCount}）。`
      : `正在按料号升序同步第 ${Math.min(batch.completedCount + 1, batch.totalCount)}/${batch.totalCount} 项：${batch.currentMaterialCode || '正在读取任务'}。`
    await new Promise(resolve => window.setTimeout(resolve, 1000))
    batch = await getMaterialSyncBatch(batch.id, props.token)
  }

  await load()
  const itemDetails = batch.items
    .filter(item => item.status === 'Waiting' || item.status === 'Failed')
    .map(item => `${item.status === 'Waiting' ? '待处理' : '失败'}：${item.message || item.taskId}`)
  const level: WorkflowResultLevel = batch.status === 'Succeeded' ? 'success'
    : batch.status === 'Failed' ? 'error' : 'warning'
  showSyncResult(
    level,
    batch.status === 'Succeeded' ? '同步完成' : batch.status === 'Failed' ? '同步失败' : '同步部分完成',
    `共 ${batch.totalCount} 项，完成 ${batch.succeededCount} 项，等待A1 BOM ${batch.waitingCount} 项，失败 ${batch.failedCount} 项。`,
    itemDetails,
  )
}

async function resumeMaterialSyncBatch() {
  if (batchSyncingTasks.value) return
  try {
    const active = (await listMaterialSyncBatches(props.token))
      .find(batch => batch.status === 'Queued' || batch.status === 'Running')
    if (!active) return
    batchSyncingTasks.value = true
    await monitorMaterialSyncBatch(active)
  } catch (error) {
    showSyncResult('error', '同步状态读取失败', error instanceof Error ? error.message : 'U9C批量同步状态读取失败')
  } finally {
    batchSyncingTasks.value = false
    syncProgressText.value = ''
  }
}

function showPreview(task: MaterialSyncTask) {
  previewTask.value = task
  previewOpen.value = true
}

const normalizeRequestedTab = (value: string) => value === 'tasks' ? 'code-approvals' : value
watch(() => props.requestedTab, value => { if (value) activeTab.value = normalizeRequestedTab(value) })
onMounted(() => {
  activeTab.value = normalizeRequestedTab(props.requestedTab)
  void load()
  void resumeMaterialSyncBatch()
})
</script>

<template>
  <section class="material-page pdm-panel pdm-loading-host">
    <SquareLoader v-if="loading" overlay label="正在加载料品数据" />
    <el-tabs v-model="activeTab" class="material-tabs">
      <el-tab-pane label="料品主档" name="materials">
        <div class="material-master-layout" :class="{ 'is-category-collapsed': categoryNavCollapsed }">
          <aside class="material-category-nav" aria-label="料品分类">
            <div class="material-category-nav__title"><span v-if="!categoryNavCollapsed">料品分类</span><button type="button" class="material-category-nav__toggle" :aria-label="categoryNavCollapsed ? '展开料品分类' : '收起料品分类'" :aria-expanded="!categoryNavCollapsed" @click="categoryNavCollapsed = !categoryNavCollapsed">{{ categoryNavCollapsed ? '›' : '‹' }}</button></div>
            <button v-if="!categoryNavCollapsed" type="button" class="material-category-all" :class="{ 'is-active': !selectedMaterialCategoryCode }" @click="selectMaterialCategory()">全部料品</button>
            <el-tree v-if="!categoryNavCollapsed" :data="materialCategoryTree" node-key="code" default-expand-all highlight-current :current-node-key="selectedMaterialCategoryCode || undefined" :expand-on-click-node="false" @node-click="selectMaterialCategory($event.code)">
              <template #default="{ data }"><span class="material-category-node">{{ data.code }} {{ data.name }}</span></template>
            </el-tree>
          </aside>
          <section class="material-master-content" aria-label="料品列表">
            <div class="material-toolbar">
              <div class="material-toolbar__actions"><el-button @click="load">刷新</el-button><el-button v-if="canEdit" type="primary" @click="openCreate">新增料品</el-button><el-button v-if="canEdit" :disabled="selectedMaterials.length > 1 ? !canBatchEditSelected : !canEditSelected" @click="selectedMaterials.length > 1 ? openBatchEdit() : openSelectedEdit()">{{ selectedMaterials.length > 1 ? '批量编辑' : '编辑' }}</el-button><el-button v-if="canApprove" :disabled="!canApproveSelected" @click="approveSelected">批准</el-button><el-button :disabled="selectedMaterials.length === 0" :loading="queryingU9" @click="querySelected">查询U9C</el-button><el-button v-if="canEdit" :disabled="!canArchiveSelected" @click="archiveSelected">停用</el-button><el-button v-if="canEdit" type="danger" :disabled="!canDeleteSelected" @click="deleteSelected">删除</el-button></div>
              <div class="material-toolbar__filters"><el-checkbox v-model="showArchived">显示已停用</el-checkbox><el-select v-model="brandFilter" class="material-brand-filter" clearable filterable placeholder="筛选品牌"><el-option v-for="brand in brandOptions" :key="brand" :label="brand" :value="brand" /></el-select><el-input v-model="query" clearable placeholder="搜索编码、名称、规格、品牌或分类" /></div>
            </div>
            <div class="material-table-shell pdm-loading-host">
              <SquareLoader v-if="materialPageLoading" overlay label="正在查询料品主档" />
              <el-table class="material-table" :data="pagedMaterials" height="100%" stripe row-key="id" table-layout="fixed" :fit="true" empty-text="尚未创建PLM料品" @selection-change="selectedMaterials = $event">
          <el-table-column type="selection" width="38" />
          <el-table-column prop="materialCode" label="物料编码" min-width="100" show-overflow-tooltip />
          <el-table-column prop="name" label="名称" min-width="112" show-overflow-tooltip><template #default="{ row }"><el-tag v-if="row.isRecommended" size="small" type="warning">推荐</el-tag> {{ row.name }}</template></el-table-column>
          <el-table-column label="引用" min-width="48"><template #default="{ row }">{{ row.referenceCount ?? 0 }}</template></el-table-column>
          <el-table-column label="规格" min-width="220" show-overflow-tooltip><template #default="{ row }">{{ row.specification || '—' }}</template></el-table-column>
          <el-table-column label="品牌" min-width="56" show-overflow-tooltip><template #default="{ row }">{{ row.brand || '—' }}</template></el-table-column>
          <el-table-column label="材质" min-width="52" show-overflow-tooltip><template #default="{ row }">{{ row.material || '—' }}</template></el-table-column>
          <el-table-column label="表面处理" min-width="64" show-overflow-tooltip><template #default="{ row }">{{ row.surfaceTreatment || '—' }}</template></el-table-column>
          <el-table-column label="重量" min-width="54" show-overflow-tooltip><template #default="{ row }">{{ weightLabel(row) }}</template></el-table-column>
          <el-table-column label="备注" min-width="180" show-overflow-tooltip><template #default="{ row }">{{ row.remark || '—' }}</template></el-table-column>
          <el-table-column label="选型建议" min-width="100" show-overflow-tooltip><template #default="{ row }">{{ row.selectionAdvice || '—' }}</template></el-table-column>
          <el-table-column label="参考价格" min-width="72" show-overflow-tooltip><template #default="{ row }">{{ referencePriceLabel(row) }}</template></el-table-column>
          <el-table-column label="3D" min-width="48"><template #default="{ row }"><el-button link type="primary" :disabled="attachmentCount(row, 'Model3D') === 0" @click.stop="openAttachmentViewer(row, 'Model3D')">{{ attachmentCount(row, 'Model3D') || '—' }}</el-button></template></el-table-column>
          <el-table-column label="资料" min-width="48"><template #default="{ row }"><el-button link type="primary" :disabled="attachmentCount(row, 'Document') === 0" @click.stop="openAttachmentViewer(row, 'Document')">{{ attachmentCount(row, 'Document') || '—' }}</el-button></template></el-table-column>
          <el-table-column label="创建人" min-width="70" show-overflow-tooltip><template #default="{ row }">{{ displayUserName(row.createdBy) }}</template></el-table-column>
          <el-table-column label="创建时间" min-width="130" show-overflow-tooltip><template #default="{ row }">{{ dateTimeLabel(row.createdAt) }}</template></el-table-column>
          <el-table-column label="计量单位" min-width="76"><template #default="{ row }">{{ u9UnitLabel(row.unitCode) }}</template></el-table-column>
          <el-table-column label="来源/主控" min-width="76"><template #default="{ row }">{{ row.sourceSystem === 'U9C' ? 'U9C/U9C' : 'PLM/PLM' }}</template></el-table-column>
          <el-table-column label="状态" min-width="56"><template #default="{ row }"><el-tag :type="row.isArchived ? 'info' : row.approvalStatus === 'Approved' ? 'success' : 'info'">{{ row.isArchived ? '已停用' : row.approvalStatus === 'Approved' ? '已批准' : '草稿' }}</el-tag></template></el-table-column>
          <el-table-column label="同步" min-width="52"><template #default="{ row }">{{ row.syncStatus === 'Succeeded' && !row.u9SyncConfirmed ? '待校正' : syncLabels[row.syncStatus] }}</template></el-table-column>
          <el-table-column label="U9/删除校验" min-width="112"><template #default="{ row }"><el-tooltip v-if="u9ValidationResults[row.id]" :content="u9ValidationResults[row.id].message" placement="top"><div class="u9-validation" :aria-label="u9ValidationResults[row.id].message"><el-tag :type="u9ValidationResults[row.id].status === 'Matched' ? 'success' : u9ValidationResults[row.id].status === 'SpecificationMismatch' ? 'danger' : 'warning'">{{ validationLabel(u9ValidationResults[row.id]) }}</el-tag></div></el-tooltip><span v-else class="u9-unchecked">未校验</span></template></el-table-column>
              </el-table>
            </div>
            <el-pagination v-model:current-page="currentPage" v-model:page-size="pageSize" class="material-pagination" :page-sizes="[50, 100, 200]" :total="materialTotal" layout="total, sizes, prev, pager, next" />
          </section>
        </div>
      </el-tab-pane>

      <el-tab-pane name="code-approvals">
        <template #label><span class="material-tab-label">料号审批<em v-if="currentWorkCount">{{ currentWorkCount }}</em></span></template>
        <div class="material-code-approval-workflow">
          <div class="material-code-approval-note">本页按两步完成料号流程：第一步批准并按PLM分类基线分配料号；第二步勾选对应记录同步到U9C。重复料号时系统会刷新同类U9C最新流水并在后台连续换号重试；U9C料品及适用的A1 BOM全部完成后才进入审批历史。</div>
          <el-tabs v-model="codeApprovalView" class="material-code-approval-subtabs">
          <el-tab-pane name="pending">
            <template #label><span class="material-code-approval-subtab-label">当前处理 <em>{{ currentWorkCount }}</em></span></template>
            <div class="material-code-workflow-columns">
              <section class="material-code-workflow-stage" aria-label="第一步料号审批">
                <div class="material-code-workflow-stage__title"><strong>第一步：料号审批</strong><span>批准后分配PLM料号，并转入右侧等待同步。</span></div>
                <section class="material-step-feedback material-approval-feedback" :class="approvalResult ? `is-${approvalResult.level}` : 'is-empty'" aria-label="料号审批状态与结果">
                  <div class="material-step-feedback__status" role="status" aria-live="polite"><strong>运行状态</strong><span :class="{ 'is-running': approvalProgressText }">{{ approvalProgressText || '空闲' }}</span></div>
                  <div class="material-step-feedback__result" role="status" aria-live="polite">
                    <header><strong>处理结果</strong><span>{{ approvalResult ? approvalResult.title : '暂无结果' }}</span></header>
                    <p>{{ approvalResult ? approvalResult.summary : '完成料号批准或退回后，结果将在此固定显示。' }}</p>
                    <ul v-if="approvalResult?.details.length"><li v-for="detail in approvalResult.details" :key="detail">{{ detail }}</li></ul>
                  </div>
                </section>
                <div v-if="canDecideMaterialCode" class="material-code-approval-toolbar">
                  <div class="material-code-approval-toolbar__actions">
                    <el-button type="primary" :disabled="selectedCodeApplications.length === 0 || decidingApplicationId !== null" :loading="batchDecidingApplications" @click="decideSelectedCodeApplications(true)">批量批准</el-button>
                    <el-button type="danger" plain :disabled="selectedCodeApplications.length === 0 || batchDecidingApplications || decidingApplicationId !== null" @click="decideSelectedCodeApplications(false)">批量退回</el-button>
                  </div>
                  <span>已选择 {{ selectedCodeApplications.length }} 项待审批申请</span>
                </div>
                <div class="material-code-approval-table-shell">
                  <el-table class="material-code-approval-table material-code-approval-table--pending" :data="pagedPendingCodeApplicationRows" row-key="id" stripe table-layout="fixed" :fit="true" empty-text="当前没有待审批申请" @selection-change="selectedCodeApplications = $event">
                    <el-table-column v-if="canDecideMaterialCode" type="selection" width="38" :selectable="canSelectCodeApplication" />
                    <el-table-column label="申请类型" width="64"><template #default="{ row }">{{ applicationTypeLabel(row) }}</template></el-table-column>
                    <el-table-column label="来源项目" width="116" show-overflow-tooltip><template #default="{ row }">{{ row.projectCode ? `${row.projectCode} · ${row.projectName || '未命名项目'}` : row.projectId }}</template></el-table-column>
                    <el-table-column label="BOM层级" width="82" show-overflow-tooltip><template #default="{ row }">{{ applicationTargetLabel(row) }}</template></el-table-column>
                    <el-table-column prop="categoryCode" label="料号分类" width="64"><template #default="{ row }">{{ row.categoryCode || '—' }}</template></el-table-column>
                    <el-table-column prop="applicationName" label="申请对象" width="110" show-overflow-tooltip><template #default="{ row }">{{ row.applicationName || row.bomItemName || '—' }}</template></el-table-column>
                    <el-table-column prop="specification" label="型号" width="78" show-overflow-tooltip><template #default="{ row }">{{ row.specification || '—' }}</template></el-table-column>
                    <el-table-column prop="brand" label="品牌" width="62" show-overflow-tooltip><template #default="{ row }">{{ row.brand || '—' }}</template></el-table-column>
                    <el-table-column prop="remark" label="备注" width="72" show-overflow-tooltip><template #default="{ row }">{{ row.remark || '—' }}</template></el-table-column>
                    <el-table-column label="申请人" width="70" show-overflow-tooltip><template #default="{ row }">{{ displayUserName(row.requestedBy) }}</template></el-table-column>
                    <el-table-column label="申请时间" width="116" show-overflow-tooltip><template #default="{ row }">{{ dateTimeLabel(row.requestedAt) }}</template></el-table-column>
                    <el-table-column label="状态" width="72"><template #default><el-tag type="warning">待审批</el-tag></template></el-table-column>
                    <el-table-column prop="materialCode" label="审批料号" width="98" show-overflow-tooltip><template #default="{ row }">{{ applicationMaterialCodeLabel(row) }}</template></el-table-column>
                    <el-table-column label="操作" width="92"><template #default="{ row }"><el-button v-if="canDecideMaterialCode" link type="primary" :loading="decidingApplicationId === row.id" :disabled="batchDecidingApplications" @click="decideCodeApplication(row, true)">批准</el-button><el-button v-if="canDecideMaterialCode" link type="danger" :disabled="decidingApplicationId === row.id || batchDecidingApplications" @click="decideCodeApplication(row, false)">退回</el-button><span v-if="!canDecideMaterialCode">—</span></template></el-table-column>
                  </el-table>
                </div>
                <el-pagination v-model:current-page="pendingApprovalPage" class="material-workflow-pagination material-pending-approval-pagination" :page-size="workflowPageSize" :total="pendingCodeApplicationRows.length" layout="total, prev, pager, next" size="small" @current-change="changePendingApprovalPage" />
              </section>
              <section class="material-code-workflow-stage material-code-sync-stage" aria-label="第二步同步到U9C">
                <div class="material-code-workflow-stage__title"><strong>第二步：同步到U9C</strong><span>失败记录保留在此，可选择后再次同步。</span></div>
                <section class="material-step-feedback material-sync-feedback" :class="syncResult ? `is-${syncResult.level}` : 'is-empty'" aria-label="U9C同步状态与结果">
                  <div class="material-step-feedback__status" role="status" aria-live="polite"><strong>运行状态</strong><span :class="{ 'is-running': syncProgressText }">{{ syncProgressText || '空闲' }}</span></div>
                  <div class="material-step-feedback__result" role="status" aria-live="polite">
                    <header><strong>处理结果</strong><span>{{ syncResult ? syncResult.title : '暂无结果' }}</span></header>
                    <p>{{ syncResult ? syncResult.summary : '完成U9C同步后，结果将在此固定显示。' }}</p>
                    <ul v-if="syncResult?.details.length"><li v-for="detail in syncResult.details" :key="detail">{{ detail }}</li></ul>
                  </div>
                </section>
                <div v-if="canApprove || canDecideMaterialCode" class="material-sync-toolbar">
                  <el-button type="primary" :disabled="selectedExecutableSyncTasks.length === 0 || batchSyncingTasks || syncingTaskId !== null" :loading="batchSyncingTasks" @click="executeSelectedTasks">{{ batchSyncActionLabel }}</el-button>
                  <span>已选择 {{ selectedExecutableSyncTasks.length }} 个可执行任务</span>
                </div>
                <el-table ref="syncTaskSelectionTable" class="material-sync-table material-code-sync-table--pending" :data="pagedCurrentSynchronizationTasks" row-key="id" stripe table-layout="fixed" :fit="true" empty-text="当前没有待同步记录" @selection-change="selectedSyncTasks = $event">
                  <el-table-column v-if="canApprove || canDecideMaterialCode" type="selection" width="38" :selectable="canSelectSyncTask" />
                  <el-table-column label="来源" width="150" show-overflow-tooltip><template #default="{ row }">{{ row.projectCode ? `${row.projectCode} · ${row.projectName || '未命名项目'}` : '料品主档' }}</template></el-table-column>
                  <el-table-column label="审批对象" width="130" show-overflow-tooltip><template #default="{ row }">{{ row.bomHeaderKind ? bomHeaderLabels[row.bomHeaderKind] : row.materialName || '普通料品' }}</template></el-table-column>
                  <el-table-column prop="materialCode" label="PLM料号" width="110"><template #default="{ row }">{{ row.materialCode || '—' }}</template></el-table-column>
                  <el-table-column label="申请人" width="74"><template #default="{ row }">{{ displayUserName(row.requestedBy) }}</template></el-table-column>
                  <el-table-column label="申请时间" width="124"><template #default="{ row }">{{ row.requestedAt ? dateTimeLabel(row.requestedAt) : '—' }}</template></el-table-column>
                  <el-table-column label="流程状态" width="112"><template #default="{ row }"><el-tag :type="syncTaskTagType(row)">{{ syncTaskStatusLabel(row) }}</el-tag></template></el-table-column>
                  <el-table-column label="说明" min-width="210" show-overflow-tooltip><template #default="{ row }">{{ syncTaskError(row) }}</template></el-table-column>
                  <el-table-column label="操作" width="172"><template #default="{ row }"><el-button link type="primary" @click="showPreview(row)">查看请求</el-button><el-button v-if="canExecuteSyncTask(row)" link type="primary" :disabled="batchSyncingTasks" :loading="syncingTaskId === row.id" @click="executeTask(row)">{{ syncTaskActionLabel(row) }}</el-button></template></el-table-column>
                </el-table>
                <el-pagination v-model:current-page="pendingSyncPage" class="material-workflow-pagination material-pending-sync-pagination" :page-size="workflowPageSize" :total="currentSynchronizationTasks.length" layout="total, prev, pager, next" size="small" @current-change="changePendingSyncPage" />
              </section>
            </div>
          </el-tab-pane>
          <el-tab-pane name="history">
            <template #label><span class="material-code-approval-subtab-label">审批/同步历史 <em>{{ historyWorkCount }}</em></span></template>
            <div class="material-code-workflow-columns material-code-history-columns">
              <section class="material-code-workflow-stage" aria-label="第一步料号审批历史">
                <div class="material-code-workflow-stage__title"><strong>第一步：审批历史</strong><span>已批准或已退回记录，只读。</span></div>
                <div class="material-code-approval-table-shell">
                  <el-table class="material-code-approval-table material-code-approval-table--history" :data="pagedApprovalHistoryRows" row-key="id" stripe table-layout="fixed" :fit="true" empty-text="尚无审批历史">
                    <el-table-column label="申请类型" width="64"><template #default="{ row }">{{ applicationTypeLabel(row) }}</template></el-table-column>
                    <el-table-column label="来源项目" width="116" show-overflow-tooltip><template #default="{ row }">{{ row.projectCode ? `${row.projectCode} · ${row.projectName || '未命名项目'}` : row.projectId }}</template></el-table-column>
                    <el-table-column label="BOM层级" width="82" show-overflow-tooltip><template #default="{ row }">{{ applicationTargetLabel(row) }}</template></el-table-column>
                    <el-table-column prop="categoryCode" label="料号分类" width="64"><template #default="{ row }">{{ row.categoryCode || '—' }}</template></el-table-column>
                    <el-table-column prop="applicationName" label="申请对象" width="110" show-overflow-tooltip><template #default="{ row }">{{ row.applicationName || row.bomItemName || '—' }}</template></el-table-column>
                    <el-table-column label="申请人" width="70" show-overflow-tooltip><template #default="{ row }">{{ displayUserName(row.requestedBy) }}</template></el-table-column>
                    <el-table-column label="申请时间" width="116" show-overflow-tooltip><template #default="{ row }">{{ dateTimeLabel(row.requestedAt) }}</template></el-table-column>
                    <el-table-column label="状态" width="72"><template #default="{ row }"><el-tag :type="row.status === 'Approved' ? 'success' : 'danger'">{{ row.status === 'Approved' ? '已批准' : '已退回' }}</el-tag></template></el-table-column>
                    <el-table-column prop="materialCode" label="审批料号" width="98" show-overflow-tooltip><template #default="{ row }">{{ applicationMaterialCodeLabel(row) }}</template></el-table-column>
                    <el-table-column label="审批人" width="92" show-overflow-tooltip><template #default="{ row }">{{ displayUserName(row.decidedBy) }}</template></el-table-column>
                  </el-table>
                </div>
                <el-pagination v-model:current-page="approvalHistoryPage" v-model:page-size="approvalHistoryPageSize" class="material-history-pagination material-approval-history-pagination" :page-sizes="[10, 20, 50]" :total="historyCodeApplicationRows.length" layout="total, sizes, prev, pager, next" size="small" />
              </section>
              <section class="material-code-workflow-stage" aria-label="第二步U9C同步历史">
                <div class="material-code-workflow-stage__title"><strong>第二步：同步历史</strong><span>仅显示已完成U9C同步的记录，只读。</span></div>
                <el-table class="material-sync-table material-sync-table--history" :data="pagedSynchronizationHistoryTasks" row-key="id" stripe table-layout="fixed" :fit="true" empty-text="尚无同步历史">
                  <el-table-column label="来源" width="150" show-overflow-tooltip><template #default="{ row }">{{ row.projectCode ? `${row.projectCode} · ${row.projectName || '未命名项目'}` : '料品主档' }}</template></el-table-column>
                  <el-table-column label="同步对象" width="130" show-overflow-tooltip><template #default="{ row }">{{ row.bomHeaderKind ? bomHeaderLabels[row.bomHeaderKind] : row.materialName || '普通料品' }}</template></el-table-column>
                  <el-table-column prop="materialCode" label="PLM料号" width="110"><template #default="{ row }">{{ row.materialCode || '—' }}</template></el-table-column>
                  <el-table-column label="申请人" width="74"><template #default="{ row }">{{ displayUserName(row.requestedBy) }}</template></el-table-column>
                  <el-table-column label="完成时间" width="124"><template #default="{ row }">{{ dateTimeLabel(row.updatedAt) }}</template></el-table-column>
                  <el-table-column label="同步结果" width="90"><template #default><el-tag type="success">已同步</el-tag></template></el-table-column>
                </el-table>
                <el-pagination v-model:current-page="syncHistoryPage" v-model:page-size="syncHistoryPageSize" class="material-history-pagination material-sync-history-pagination" :page-sizes="[10, 20, 50]" :total="synchronizationHistoryTasks.length" layout="total, sizes, prev, pager, next" size="small" />
              </section>
            </div>
          </el-tab-pane>
          </el-tabs>
        </div>
      </el-tab-pane>

      <el-tab-pane v-if="canManageIntegration" label="取号设置" name="numbering-settings">
        <section class="material-numbering-settings" aria-label="PLM料号基线设置">
          <div>
            <strong>PLM全局料号基线</strong>
            <p>所有开放创建分类统一使用7位流水；新料号只按PLM主档与本地计数器向后生成，不在审批或同步时扫描U9C。</p>
          </div>
          <el-input-number v-model="numberingStartSequence" :min="1000000" :max="9999999" :step="1" :precision="0" />
          <el-button type="primary" :loading="savingNumberingSettings" @click="saveNumberingSettings">保存基线设置</el-button>
        </section>
        <section class="material-duplicate-settings" aria-label="料品查重规则设置">
          <header><div><strong>分类查重规则</strong><p>申请或创建料号前只查询PLM料品主档；每类至少选择一个字段，所选字段全部相同时复用现有料品。</p></div><el-button type="primary" :loading="savingDuplicateRules" @click="saveDuplicateRules">保存查重规则</el-button></header>
          <div class="material-duplicate-rule-list">
            <div v-for="rule in duplicateRules" :key="rule.categoryCode" class="material-duplicate-rule-row">
              <span>{{ rule.categoryCode }} {{ categories.find(category => category.code === rule.categoryCode)?.name || '' }}</span>
              <el-checkbox-group v-model="rule.fields"><el-checkbox v-for="field in duplicateFieldOptions" :key="field.value" :value="field.value">{{ field.label }}</el-checkbox></el-checkbox-group>
            </div>
          </div>
        </section>
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
                <el-form-item label="PLM业务分类"><el-select v-model="categoryDraft.pdmKind" clearable :disabled="!canManageIntegration"><el-option label="电气件" value="Electrical" /><el-option label="机械外购件" value="Standard" /><el-option label="非标机加件" value="NonStandard" /><el-option label="产品/组件" value="Product" /></el-select></el-form-item>
                <el-form-item label="默认供给方式"><el-select v-model="categoryDraft.defaultSupplyMode" :disabled="!canManageIntegration"><el-option label="采购" value="Purchase" /><el-option label="自制" value="Manufacture" /><el-option label="委外" value="Outsource" /></el-select></el-form-item>
                <el-form-item label="编号前缀"><el-input v-model="categoryDraft.numberPrefix" :disabled="!canManageIntegration" :placeholder="categoryDraft.code" /></el-form-item>
                <el-form-item label="流水位数"><el-input-number v-model="categoryDraft.sequenceLength" :min="1" :max="9" :disabled="!canManageIntegration || categoryDraft.allowCreate" /><p class="field-help">开放创建的分类固定为7位。</p></el-form-item>
                <el-form-item label="流水范围"><el-input v-model="categoryDraft.counterScope" :disabled="!canManageIntegration" :placeholder="categoryDraft.code" /></el-form-item>
                <el-form-item label="排序号"><el-input-number v-model="categoryDraft.sortOrder" :disabled="!canManageIntegration" /></el-form-item>
              </div>
              <div class="category-switches"><el-switch v-model="categoryDraft.allowCreate" :disabled="!canManageIntegration" active-text="开放创建" inactive-text="屏蔽创建" /><el-switch v-model="categoryDraft.isVisible" :disabled="!canManageIntegration" active-text="PLM可见" /><el-switch v-model="categoryDraft.isActive" :disabled="!canManageIntegration" active-text="U9C有效" /></div>
              <p class="field-help">开放创建只影响新增料品；屏蔽分类中的现有料品仍可查询并供历史BOM引用。当前流水：{{ categoryDraft.currentSequence }}；下一个编号：{{ categoryDraft.numberPrefix || categoryDraft.code }}{{ Math.max(categoryDraft.currentSequence + 1, numberingStartSequence).toString().padStart(categoryDraft.sequenceLength, '0') }}</p>
              <el-button v-if="canManageIntegration" type="primary" @click="saveCategory">保存分类</el-button>
            </el-form>
          </section>
        </div>
      </el-tab-pane>

    </el-tabs>

    <MaterialEditorDialog
      v-model="editorOpen"
      :editing-id="editingId"
      :form="form"
      :categories="creatableCategories"
      :material-code-placeholder="materialCodePlaceholder"
      :attachments="editorAttachments"
      :saving="saving"
      :uploading-kind="uploadingAttachmentKind"
      :upload-progress="attachmentUploadProgress"
      :cover-url="coverUrl"
      :u9-fields-locked="editorU9FieldsLocked"
      @category-change="applyCategoryDefaults"
      @attachment-files="handleAttachmentFiles"
      @download-attachment="downloadAttachment"
      @clear-cover="clearCover"
      @save="saveMaterial"
    />

    <el-dialog v-model="attachmentViewerOpen" :title="attachmentViewerTitle" width="620px">
      <el-table v-loading="attachmentViewerLoading" :data="attachmentViewerItems" empty-text="暂无附件" max-height="420">
        <el-table-column prop="originalFileName" label="文件名" min-width="210" show-overflow-tooltip />
        <el-table-column label="大小" width="90"><template #default="{ row }">{{ fileSizeLabel(row.fileLength) }}</template></el-table-column>
        <el-table-column prop="uploadedBy" label="上传人" width="100" show-overflow-tooltip />
        <el-table-column label="上传时间" width="145"><template #default="{ row }">{{ dateTimeLabel(row.uploadedAt) }}</template></el-table-column>
        <el-table-column label="操作" width="64"><template #default="{ row }"><el-button link type="primary" @click="downloadAttachment(row)">下载</el-button></template></el-table-column>
      </el-table>
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
.material-master-layout{display:grid;grid-template-columns:190px minmax(0,1fr);gap:var(--pdm-container-gap);min-width:0;background:var(--shell-content-bg)}.material-master-layout.is-category-collapsed{grid-template-columns:34px minmax(0,1fr)}.material-category-nav,.material-master-content{min-width:0;padding:10px;border:1px solid #e2e8f0;border-radius:8px;background:#fff}.material-category-nav{overflow:auto;font-size:11px}.material-category-nav__title{display:flex;align-items:center;justify-content:space-between;gap:4px;margin:0 4px 8px;color:#334155;font-weight:600;white-space:nowrap}.material-category-nav__toggle{width:22px;height:22px;display:inline-flex;flex:0 0 22px;align-items:center;justify-content:center;padding:0;border:1px solid var(--shell-accent-border);border-radius:5px;background:var(--pdm-blue-soft);color:var(--pdm-blue);font-size:16px;line-height:1;cursor:pointer}.material-category-nav__toggle:hover,.material-category-nav__toggle:focus-visible{border-color:var(--pdm-blue);background:var(--pdm-blue-soft);outline:none}.material-master-layout.is-category-collapsed .material-category-nav{padding:5px}.material-master-layout.is-category-collapsed .material-category-nav__title{justify-content:center;margin:0}.material-category-all{width:100%;height:28px;margin-bottom:4px;padding:0 8px;border:0;border-radius:5px;background:transparent;color:#475569;font:inherit;text-align:left;cursor:pointer}.material-category-all:hover,.material-category-all.is-active{background:var(--pdm-blue-soft);color:var(--pdm-blue)}.material-category-nav :deep(.el-tree){background:#fff;color:#475569;font-size:11px}.material-category-nav :deep(.el-tree-node__content){height:28px;border-radius:5px}.material-category-node{overflow:hidden;text-overflow:ellipsis;white-space:nowrap}.material-master-content{overflow:hidden}
.material-page{min-width:0;min-height:calc(100vh - 112px);overflow:hidden;padding:5px 28px 28px}.material-tabs{min-width:0;max-width:100%}.material-tabs :deep(.el-tabs__content),.material-tabs :deep(.el-tab-pane){min-width:0;max-width:100%;overflow:hidden}.material-toolbar{display:flex;min-width:0;align-items:center;justify-content:flex-start;flex-wrap:nowrap;gap:5px;margin-bottom:14px;font-size:11px}.material-toolbar__actions,.material-toolbar__filters{display:flex;min-width:0;align-items:center;flex-wrap:nowrap;gap:5px}.material-toolbar__actions{flex:0 1 auto}.material-toolbar__filters{flex:1 1 260px}.material-toolbar :deep(.el-button),.material-toolbar :deep(.el-checkbox__label),.material-toolbar :deep(.el-input__inner),.material-toolbar :deep(.el-select__placeholder),.material-toolbar :deep(.el-select__selected-item){font-size:11px}.material-toolbar__actions :deep(.el-button){width:clamp(60px,5vw,80px);height:30px;flex:1 1 60px;margin-left:0;padding:0}.material-toolbar__filters :deep(.el-checkbox){flex:0 0 auto}.material-brand-filter{width:110px;min-width:80px;flex:0 1 110px}.material-toolbar .el-input{width:auto;min-width:80px;flex:1 1 180px}.material-table{width:100%;min-width:0;max-width:100%;box-sizing:border-box}.material-table :deep(.el-table__inner-wrapper),.material-table :deep(.el-scrollbar),.material-table :deep(.el-scrollbar__wrap){max-width:100%}.material-table :deep(.el-scrollbar__wrap){overflow-x:auto}.material-table :deep(.el-table__cell){font-size:11px;text-align:center}.material-table :deep(.cell){overflow:hidden;padding:0 6px;text-overflow:ellipsis;white-space:nowrap}.material-table :deep(.el-button),.material-table :deep(.el-tag){font-size:11px}.u9-validation{display:flex;align-items:center;justify-content:center;white-space:nowrap}.u9-unchecked{color:#64748b;font-size:11px}.batch-editor-note{margin:0 0 14px;color:#64748b;font-size:11px}.batch-editor-form :deep(.el-checkbox){margin-right:0}.material-numbering-settings{display:flex;align-items:center;gap:12px;margin-bottom:12px;padding:12px 16px;border:1px solid #bfdbfe;border-radius:10px;background:#eff6ff}.material-numbering-settings>div{min-width:0;flex:1}.material-numbering-settings strong{color:#0f172a;font-size:12px}.material-numbering-settings p{margin:3px 0 0;color:#475569;line-height:1.5}.material-numbering-settings :deep(.el-input-number){width:150px}.category-layout{display:grid;grid-template-columns:minmax(280px,35%) 1fr;gap:18px;min-height:520px}.category-tree-panel,.category-editor{padding:18px;border:1px solid #e2e8f0;border-radius:14px;background:#f8fafc}.category-actions{display:flex;gap:8px;margin-bottom:14px}.category-node{display:flex;align-items:center;justify-content:space-between;gap:12px;width:100%;padding-right:8px}.category-empty{display:grid;min-height:420px;place-items:center;color:#94a3b8}.category-switches{display:flex;flex-wrap:wrap;gap:24px;margin:2px 0 14px}.form-grid{display:grid;grid-template-columns:repeat(2,minmax(0,1fr));column-gap:18px}.field-help{width:100%;margin:6px 0 0;color:#64748b;font-size:11px;line-height:1.5}.weight-unit{width:76px;margin-left:8px}.preview-meta{display:grid;gap:6px;margin-bottom:12px;color:#64748b;font-size:12px;word-break:break-all}.payload-preview{max-height:480px;overflow:auto;padding:18px;border-radius:10px;background:#0f172a;color:#dbeafe;font:12px/1.6 Consolas,monospace;white-space:pre-wrap;word-break:break-all}.material-tabs :deep(.el-tabs__content),.material-tabs :deep(.el-tabs__content *){font-size:11px}:global(.material-editor-dialog),:global(.material-editor-dialog *){font-size:11px}:global(.material-editor-dialog .el-dialog__title){font-size:11px!important}@media(max-width:1000px){.material-page{padding:5px 18px 18px}.material-toolbar__actions :deep(.el-button){width:52px;min-width:52px;flex-basis:52px}.material-brand-filter{width:70px;min-width:70px;flex-basis:70px}.material-toolbar .el-input{min-width:70px;flex-basis:70px}.material-numbering-settings{align-items:stretch;flex-direction:column}.category-layout{grid-template-columns:1fr}.form-grid{grid-template-columns:1fr}}
.material-duplicate-settings{padding:14px 16px;border:1px solid #e2e8f0;border-radius:10px;background:#fff}.material-duplicate-settings>header{display:flex;align-items:flex-start;justify-content:space-between;gap:12px;margin-bottom:10px}.material-duplicate-settings>header p{margin:3px 0 0;color:#64748b;line-height:1.5}.material-duplicate-rule-list{display:grid;gap:6px}.material-duplicate-rule-row{display:grid;grid-template-columns:minmax(180px,240px) 1fr;align-items:center;gap:12px;padding:8px 10px;border-radius:6px;background:#f8fafc}.material-duplicate-rule-row>span{font-weight:600}.material-duplicate-rule-row :deep(.el-checkbox){margin-right:18px}@media(max-width:800px){.material-duplicate-rule-row{grid-template-columns:1fr}}
.material-page{height:100%;min-height:0;display:flex;flex-direction:column}.material-tabs{min-height:0;flex:1 1 auto;display:flex;flex-direction:column}.material-tabs :deep(.el-tabs__header .el-tabs__item){font-size:13px;font-weight:600}.material-tabs :deep(.el-tabs__content){min-height:0;flex:1 1 auto}.material-tabs :deep(.el-tab-pane){height:100%;min-height:0}.material-master-layout{height:100%;min-height:0}.material-master-content{display:flex;flex-direction:column}.material-toolbar{flex:0 0 auto;margin-bottom:5px}.material-table-shell{min-height:0;flex:1 1 auto}.material-table{height:100%}.material-pagination{flex:0 0 auto;justify-content:flex-end;margin-top:5px}.material-pagination :deep(.el-pagination__total),.material-pagination :deep(.el-select__selected-item),.material-pagination :deep(button),.material-pagination :deep(.number){font-size:11px}
.material-tab-label{display:inline-flex;align-items:center;gap:5px}.material-tab-label em{min-width:18px;height:18px;padding:0 5px;border-radius:9px;background:var(--pdm-blue);color:#fff;font-size:10px;font-style:normal;font-weight:600;line-height:18px;text-align:center}
.material-table :deep(.el-table__body tr.el-table__row){height:30px}.material-table :deep(.el-table__body td.el-table__cell){height:30px;padding:0}.material-table :deep(.el-table__body .el-tag){height:20px;padding-top:0;padding-bottom:0;line-height:18px}
.material-code-approval-note{margin-bottom:8px;padding:8px 10px;border:1px solid #dbeafe;border-radius:6px;background:#eff6ff;color:#475569;font-size:11px}
.material-code-approval-workflow{height:100%;min-height:0;overflow:auto;padding-right:2px}.material-code-approval-subtabs{min-height:0}.material-code-approval-subtabs :deep(.el-tabs__content),.material-code-approval-subtabs :deep(.el-tab-pane){height:auto;min-height:0;overflow:visible}.material-code-approval-subtabs :deep(.el-tabs__header){margin:0 0 8px}.material-code-approval-subtabs :deep(.el-tabs__item){height:30px;font-size:11px;font-weight:600}.material-code-approval-subtab-label{display:inline-flex;align-items:center;gap:5px}.material-code-approval-subtab-label em{min-width:18px;height:18px;padding:0 5px;border-radius:9px;background:#e0f2fe;color:#0369a1;font-size:10px;font-style:normal;line-height:18px;text-align:center}
.material-step-feedback{position:sticky;top:0;z-index:4;min-height:82px;max-height:132px;margin-bottom:8px;overflow:auto;padding:7px 9px;border:1px solid #cbd5e1;border-radius:6px;background:#f8fafc;color:#475569;font-size:11px}.material-step-feedback__status,.material-step-feedback__result header{display:flex;align-items:flex-start;gap:8px}.material-step-feedback__status{padding-bottom:5px;border-bottom:1px solid #e2e8f0}.material-step-feedback__status>strong,.material-step-feedback__result header>strong{flex:0 0 auto;color:#0f172a;font-size:11px}.material-step-feedback__status>span,.material-step-feedback__result header>span{min-width:0;font-weight:600;line-height:1.5}.material-step-feedback__status>span.is-running{color:#1d4ed8}.material-step-feedback__result{padding-top:5px}.material-step-feedback__result p{margin:3px 0 0;line-height:1.5}.material-step-feedback__result ul{margin:4px 0 0;padding-left:18px;line-height:1.5}.material-step-feedback.is-success{border-color:#bbf7d0;background:#f0fdf4}.material-step-feedback.is-success .material-step-feedback__result header>span{color:#15803d}.material-step-feedback.is-warning{border-color:#fde68a;background:#fffbeb}.material-step-feedback.is-warning .material-step-feedback__result header>span{color:#b45309}.material-step-feedback.is-error{border-color:#fecaca;background:#fef2f2}.material-step-feedback.is-error .material-step-feedback__result header>span{color:#dc2626}.material-step-feedback.is-empty .material-step-feedback__result header>span,.material-step-feedback.is-empty .material-step-feedback__result p{color:#64748b}
.material-code-workflow-columns{display:grid;grid-template-columns:minmax(0,1fr) minmax(0,1fr);align-items:start;gap:10px;min-width:0}.material-code-workflow-stage{min-width:0;overflow:hidden;padding:10px;border:1px solid #dbe4ef;border-radius:8px;background:#fff}.material-code-workflow-stage__title{display:flex;align-items:center;gap:8px;margin-bottom:8px;color:#334155}.material-code-workflow-stage__title strong{color:#0f766e;font-size:12px;white-space:nowrap}.material-code-workflow-stage__title span{overflow:hidden;color:#64748b;text-overflow:ellipsis;white-space:nowrap}
.material-sync-toolbar{display:flex;align-items:center;gap:8px;margin-bottom:8px}.material-sync-toolbar :deep(.el-button){min-width:110px;height:28px;margin-left:0;font-size:11px}.material-sync-toolbar>span{color:#64748b;font-size:11px}
.material-sync-table{width:100%;min-width:0;max-width:100%}.material-sync-table :deep(.el-table__cell){padding-left:0;padding-right:0;text-align:center}.material-sync-table :deep(.cell){overflow:hidden;padding:0 4px;text-overflow:ellipsis;white-space:nowrap}.material-sync-table :deep(.el-button){margin-left:0;padding:2px 3px;font-size:11px}
.material-code-approval-toolbar{display:flex;align-items:center;justify-content:space-between;gap:12px;margin-bottom:8px}.material-code-approval-toolbar__actions{display:flex;align-items:center;gap:6px}.material-code-approval-toolbar :deep(.el-button){min-width:76px;height:28px;margin-left:0;font-size:11px}.material-code-approval-toolbar>span{color:#64748b;font-size:11px}
.material-code-approval-table-shell{width:100%;min-width:0;max-width:100%;overflow:hidden}.material-code-approval-table{width:100%;min-width:0;max-width:100%}.material-code-approval-table :deep(.el-table__inner-wrapper),.material-code-approval-table :deep(.el-scrollbar),.material-code-approval-table :deep(.el-scrollbar__wrap){max-width:100%}.material-code-approval-table :deep(.el-table__cell){padding-left:0;padding-right:0;text-align:center}.material-code-approval-table :deep(.cell){overflow:hidden;padding:0 4px;text-overflow:ellipsis;white-space:nowrap}.material-code-approval-table :deep(.el-button){margin-left:0;padding:2px 3px;font-size:11px}.material-code-approval-table :deep(.el-tag){max-width:100%;padding:0 5px;font-size:11px}
.material-history-pagination{justify-content:flex-end;margin-top:8px}.material-history-pagination :deep(.el-pagination__total),.material-history-pagination :deep(.el-select__selected-item),.material-history-pagination :deep(button),.material-history-pagination :deep(.number){font-size:11px}
.material-workflow-pagination{justify-content:flex-end;margin-top:8px}.material-workflow-pagination :deep(.el-pagination__total),.material-workflow-pagination :deep(button),.material-workflow-pagination :deep(.number){font-size:11px}
.material-editor-grid{grid-template-columns:repeat(3,minmax(0,200px));gap:0 12px}.material-editor-grid :deep(.el-form-item){margin-bottom:10px}.material-editor-grid__wide{grid-column:span 2}.material-weight-input{display:flex;min-width:0}.material-weight-input :deep(.el-input-number){min-width:0;flex:1}.material-recommend-button{width:100%}.material-attachment-field{display:flex;min-width:0;width:100%;align-items:center;flex-wrap:wrap;gap:4px}.material-attachment-input{display:none}.material-attachment-field>.el-button{width:100%;margin-left:0}.material-upload-progress{color:#64748b;font-size:10px}.material-attachment-list{display:flex;max-height:44px;min-width:0;width:100%;overflow:auto;align-items:flex-start;flex-direction:column}.material-attachment-list :deep(.el-button){display:block;max-width:100%;height:20px;margin-left:0;overflow:hidden;padding:0;text-overflow:ellipsis;white-space:nowrap}
@media(max-width:1000px){.material-master-layout{grid-template-columns:1fr}.material-master-layout.is-category-collapsed{grid-template-columns:34px minmax(0,1fr)}.material-category-nav{max-height:220px}}
@media(max-width:1100px){.material-code-workflow-columns{grid-template-columns:1fr}}
@media(max-width:1000px){.material-editor-grid{grid-template-columns:repeat(2,minmax(0,1fr))}}
@media(max-width:760px){.material-editor-grid{grid-template-columns:1fr}.material-editor-grid__wide{grid-column:auto}}
</style>
