<script setup lang="ts">
import { ArrowLeft, CalendarDays, CheckCircle2, Download, FileUp, LibraryBig, ListChecks, Paperclip, Plus, RotateCcw, Save, Send, Trash2, XCircle } from '@lucide/vue'
import { ElMessageBox } from 'element-plus'
import { computed, onBeforeUnmount, reactive, ref, watch } from 'vue'
import { confirmValidationPlanExecution, createProjectValidationPlanRevision, decideValidationPlanApproval, deleteValidationCheckCategory, deleteValidationCheckItem, downloadValidationPlanAttachment, exportProjectValidationPlan, readProjectValidationPlan, readValidationCheckCatalog, readValidationPlanExecutionRecords, recognizeValidationPlanAttachment, saveProjectValidationPlan, saveValidationCheckCategory, saveValidationCheckItem, submitProjectValidationPlan, uploadValidationPlanAttachment } from '../api'
import { ElMessage } from '../statusMessage'
import type { ProjectSummary, ProjectValidationPlan, ProjectValidationPlanItem, ValidationCheckCatalog, ValidationCheckCategory, ValidationCheckItem, ValidationPlanAttachment, ValidationPlanExecutionRecord, ValidationPlanRecognitionCandidate, ValidationPlanRecognitionDraft } from '../types'
import { useUserDisplayName } from '../userDisplay'

const props = defineProps<{
  projectId: string
  projectCode: string
  projectName: string
  projects: ProjectSummary[]
  token: string
  currentUsername: string
  currentDisplayName: string
  canEdit: boolean
  canManageCatalog: boolean
  canDecideApproval?: boolean
  requestedProjectId?: string
}>()
const emit = defineEmits<{ requestHandled: [] }>()

const displayUserName = useUserDisplayName()

const informationSources = ['技术协议', '技术方案', '内部评审', '客户评审']
const emptyCatalog = (): ValidationCheckCatalog => ({ categories: [], items: [] })
const loading = ref(false)
const view = ref<'summary' | 'detail'>('summary')
const detailProject = ref<ProjectSummary | null>(null)
const summaryPlans = ref<Record<string, ProjectValidationPlan | null>>({})
const summaryErrors = ref<Record<string, boolean>>({})
const saving = ref(false)
const exporting = ref(false)
const uploading = ref(false)
const submitting = ref(false)
const catalog = ref<ValidationCheckCatalog>(emptyCatalog())
const plan = ref<ProjectValidationPlan | null>(null)
const preparedBy = ref('')
const planDate = ref('')
const rows = ref<ProjectValidationPlanItem[]>([])
const dirty = ref(false)
const hydrating = ref(false)
const planFileInput = ref<HTMLInputElement | null>(null)
const evidenceFileInput = ref<HTMLInputElement | null>(null)
const executionRecords = ref<ValidationPlanExecutionRecord[]>([])
const recognizing = ref(false)
const confirmingRecognition = ref(false)
const recognitionOpen = ref(false)
const recognitionDraft = ref<ValidationPlanRecognitionDraft | null>(null)
type RecognitionReviewRow = ValidationPlanRecognitionCandidate & { selected: boolean; result: string; validationDate: string; responsiblePerson: string; remark: string }
const recognitionRows = ref<RecognitionReviewRow[]>([])
const attachmentDialogOpen = ref(false)
const attachmentDialogProject = ref<ProjectSummary | null>(null)
const attachmentDialogKind = ref<'PlanDocument' | 'Evidence'>('PlanDocument')

const familyProjects = computed(() => {
  const byId = new Map(props.projects.map(item => [item.id, item]))
  let root = byId.get(props.projectId)
  while (root?.parentProjectId && byId.has(root.parentProjectId)) root = byId.get(root.parentProjectId)
  const rootId = root?.id ?? props.projectId
  const included = new Set([rootId])
  let changed = true
  while (changed) {
    changed = false
    for (const project of props.projects) {
      if (project.parentProjectId && included.has(project.parentProjectId) && !included.has(project.id)) {
        included.add(project.id)
        changed = true
      }
    }
  }
  return props.projects.filter(item => included.has(item.id)).sort((left, right) => {
    if (left.id === rootId) return -1
    if (right.id === rootId) return 1
    return left.code.localeCompare(right.code, undefined, { numeric: true })
  })
})
const rootProject = computed(() => familyProjects.value.find(item => !item.parentProjectId) ?? familyProjects.value[0])
const activeDetailProject = computed(() => detailProject.value ?? familyProjects.value.find(item => item.id === props.projectId) ?? null)
const normalizedState = computed(() => {
  const value = plan.value?.state
  return typeof value === 'number' ? ['Draft', 'PendingApproval', 'Effective', 'Rejected', 'Superseded'][value] : value
})
const editable = computed(() => props.canEdit && (!plan.value || normalizedState.value === 'Draft' || normalizedState.value === 'Rejected'))
const currentApprovalTask = computed(() => plan.value?.approvalTasks?.slice().sort((a, b) => a.stepOrder - b.stepOrder).find(item => item.decision == null) ?? null)
const canDecideCurrent = computed(() => Boolean(props.canDecideApproval && currentApprovalTask.value
  && (currentApprovalTask.value.assignee.toLocaleLowerCase() === props.currentUsername.toLocaleLowerCase() || props.currentUsername.toLocaleLowerCase() === 'admin')))
const attachmentDialogFiles = computed(() => {
  const projectId = attachmentDialogProject.value?.id
  if (!projectId) return []
  return (summaryPlans.value[projectId]?.attachments ?? []).filter(file => attachmentKind(file) === attachmentDialogKind.value)
})
const selectedRecognitionCount = computed(() => recognitionRows.value.filter(item => item.selected).length)

const selectionOpen = ref(false)
const selectionQuery = ref('')
const selectionCategoryId = ref('')
const selectedItemIds = ref<string[]>([])
const draggedRowIndex = ref<number | null>(null)
const dragOverRowIndex = ref<number | null>(null)
const dragOverPosition = ref<'before' | 'after' | null>(null)

const catalogOpen = ref(false)
const catalogLoading = ref(false)
const catalogAll = ref<ValidationCheckCatalog>(emptyCatalog())
const catalogCategoryId = ref('')
const catalogQuery = ref('')
const categoryEditorOpen = ref(false)
const itemEditorOpen = ref(false)
const categoryForm = reactive({ id: '', name: '', sortOrder: 10, isActive: true, note: '', rowVersion: 0 })
const itemForm = reactive({ id: '', categoryId: '', content: '', defaultInformationSource: '内部评审', sortOrder: 10, isActive: true, note: '', rowVersion: 0 })

const activeCategories = computed(() => catalog.value.categories.filter(item => item.isActive))
const selectableItems = computed(() => {
  const query = selectionQuery.value.trim().toLocaleLowerCase()
  const existing = new Set(rows.value.map(item => item.catalogItemId).filter(Boolean))
  return catalog.value.items.filter(item => item.categoryId === selectionCategoryId.value && item.isActive && !existing.has(item.id)
    && (!query || item.content.toLocaleLowerCase().includes(query)))
})
const selectedCatalogCategory = computed(() => catalogAll.value.categories.find(item => item.id === catalogCategoryId.value) ?? null)
const allSelectableItemsSelected = computed(() => selectableItems.value.length > 0
  && selectableItems.value.every(item => selectedItemIds.value.includes(item.id)))
const managedItems = computed(() => {
  const query = catalogQuery.value.trim().toLocaleLowerCase()
  return catalogAll.value.items.filter(item => item.categoryId === catalogCategoryId.value
    && (!query || item.content.toLocaleLowerCase().includes(query)))
})

watch([preparedBy, planDate, rows], () => {
  if (!hydrating.value) dirty.value = true
}, { deep: true, flush: 'sync' })

watch([() => props.projectId, () => props.projects.map(item => `${item.id}:${item.parentProjectId ?? ''}`).join('|')], () => {
  view.value = 'summary'
  detailProject.value = null
  void loadSummary()
}, { immediate: true })

watch(() => props.requestedProjectId, async id => {
  if (!id) return
  const target = familyProjects.value.find(item => item.id === id)
  if (target) await openProjectPlan(target)
  emit('requestHandled')
})

async function loadSummary() {
  loading.value = true
  const plans: Record<string, ProjectValidationPlan | null> = {}
  const errors: Record<string, boolean> = {}
  await Promise.all(familyProjects.value.map(async project => {
    if (!project.canReadContent) {
      plans[project.id] = null
      return
    }
    try {
      plans[project.id] = await readProjectValidationPlan(project.id, props.token)
    } catch {
      plans[project.id] = null
      errors[project.id] = true
    }
  }))
  summaryPlans.value = plans
  summaryErrors.value = errors
  loading.value = false
}

async function openProjectPlan(project: ProjectSummary) {
  if (!project.canReadContent) return
  detailProject.value = project
  view.value = 'detail'
  await loadDetail(project.id)
}

async function backToSummary() {
  if (dirty.value) {
    try {
      await ElMessageBox.confirm('当前验证计划有未保存修改，返回列表将放弃这些修改。', '返回验证计划列表', { type: 'warning', confirmButtonText: '放弃并返回', cancelButtonText: '继续编辑' })
    } catch { return }
  }
  view.value = 'summary'
  detailProject.value = null
  dirty.value = false
  await loadSummary()
}

async function loadDetail(projectId: string) {
  loading.value = true
  hydrating.value = true
  try {
    const [nextCatalog, nextPlan] = await Promise.all([
      readValidationCheckCatalog(props.token),
      readProjectValidationPlan(projectId, props.token),
    ])
    catalog.value = nextCatalog
    hydratePlan(nextPlan)
    executionRecords.value = nextPlan ? (await readValidationPlanExecutionRecords(nextPlan.id, props.token) ?? []) : []
    dirty.value = false
  } catch (error) {
    ElMessage.error(errorMessage(error))
  } finally {
    hydrating.value = false
    loading.value = false
  }
}

function hydratePlan(nextPlan: ProjectValidationPlan | null) {
  plan.value = nextPlan
  const preparedByUsername = nextPlan?.preparedBy || props.currentUsername
  preparedBy.value = props.currentUsername.localeCompare(preparedByUsername, undefined, { sensitivity: 'accent' }) === 0
    ? (props.currentDisplayName || displayUserName(props.currentUsername, props.currentUsername))
    : displayUserName(preparedByUsername, '')
  planDate.value = nextPlan?.effectiveAt?.slice(0, 10) || nextPlan?.validationDate || currentShanghaiDate()
  rows.value = (nextPlan?.items ?? []).map(item => ({ ...item })).sort((left, right) => left.sortOrder - right.sortOrder)
}

function currentShanghaiDate() {
  return new Date().toLocaleDateString('sv-SE', { timeZone: 'Asia/Shanghai' })
}

async function openSelection() {
  try {
    catalog.value = await readValidationCheckCatalog(props.token)
    selectionCategoryId.value = activeCategories.value.some(item => item.id === selectionCategoryId.value)
      ? selectionCategoryId.value
      : activeCategories.value[0]?.id ?? ''
    selectedItemIds.value = []
    selectionQuery.value = ''
    selectionOpen.value = true
  } catch (error) {
    ElMessage.error(errorMessage(error))
  }
}

function appendSelectedItems() {
  const byId = new Map(catalog.value.items.map(item => [item.id, item]))
  const categoryById = new Map(catalog.value.categories.map(item => [item.id, item]))
  for (const id of selectedItemIds.value) {
    const item = byId.get(id)
    const category = item ? categoryById.get(item.categoryId) : undefined
    if (!item || !category || rows.value.some(row => row.catalogItemId === item.id)) continue
    rows.value.push({
      id: crypto.randomUUID(),
      catalogCategoryId: category.id,
      catalogItemId: item.id,
      categoryName: category.name,
      validationContent: item.content,
      informationSource: item.defaultInformationSource,
      validationDate: null,
      result: null,
      responsiblePerson: null,
      remark: null,
      sortOrder: rows.value.length + 1,
    })
  }
  normalizeRowOrder()
  selectionOpen.value = false
}

function addManualRow(afterIndex: number) {
  rows.value.splice(afterIndex + 1, 0, {
    id: crypto.randomUUID(), catalogCategoryId: null, catalogItemId: null, categoryName: '人工项', validationContent: '',
    informationSource: '内部评审', validationDate: null, result: null, responsiblePerson: null, remark: null, sortOrder: rows.value.length + 1,
  })
  normalizeRowOrder()
}

function openDatePicker(event: MouseEvent) {
  const input = (event.currentTarget as HTMLElement).parentElement?.querySelector<HTMLInputElement>('input[type="date"]')
  input?.showPicker?.()
}

let activeDragPointerId: number | null = null

function resetRowDrag() {
  document.removeEventListener('pointermove', moveRowPointerDrag)
  document.removeEventListener('pointerup', finishRowPointerDrag)
  document.removeEventListener('pointercancel', cancelRowPointerDrag)
  activeDragPointerId = null
  draggedRowIndex.value = null
  dragOverRowIndex.value = null
  dragOverPosition.value = null
}

function startRowPointerDrag(index: number, event: PointerEvent) {
  if (!editable.value || event.button !== 0) return
  event.preventDefault()
  activeDragPointerId = event.pointerId
  draggedRowIndex.value = index
  document.addEventListener('pointermove', moveRowPointerDrag, { passive: false })
  document.addEventListener('pointerup', finishRowPointerDrag)
  document.addEventListener('pointercancel', cancelRowPointerDrag)
}

function moveRowPointerDrag(event: PointerEvent) {
  if (activeDragPointerId !== event.pointerId || draggedRowIndex.value === null) return
  event.preventDefault()
  const target = document.elementFromPoint(event.clientX, event.clientY)?.closest<HTMLTableRowElement>('tr[data-row-index]')
  if (!target) return
  const index = Number(target.dataset.rowIndex)
  if (!Number.isInteger(index)) return
  const bounds = target.getBoundingClientRect()
  dragOverRowIndex.value = index
  dragOverPosition.value = event.clientY < bounds.top + bounds.height / 2 ? 'before' : 'after'
}

function applyRowDrop() {
  const source = draggedRowIndex.value
  const index = dragOverRowIndex.value
  const position = dragOverPosition.value
  resetRowDrag()
  if (source === null || index === null || position === null || source < 0 || source >= rows.value.length) return
  let target = index + (position === 'after' ? 1 : 0)
  if (source < target) target -= 1
  if (source === target) return
  const [row] = rows.value.splice(source, 1)
  rows.value.splice(target, 0, row)
  normalizeRowOrder()
}

function finishRowPointerDrag(event: PointerEvent) {
  if (activeDragPointerId === event.pointerId) applyRowDrop()
}

function cancelRowPointerDrag(event: PointerEvent) {
  if (activeDragPointerId === event.pointerId) resetRowDrag()
}

onBeforeUnmount(resetRowDrag)

function toggleSelectableItems(checked: boolean) {
  const visibleIds = new Set(selectableItems.value.map(item => item.id))
  const next = new Set(selectedItemIds.value)
  for (const id of visibleIds) checked ? next.add(id) : next.delete(id)
  selectedItemIds.value = [...next]
}

async function removeRow(index: number) {
  try {
    await ElMessageBox.confirm('从当前项目验证计划中移除此检查项？全局检查项库不会受影响。', '移除检查项', { type: 'warning', confirmButtonText: '移除', cancelButtonText: '取消' })
    rows.value.splice(index, 1)
    normalizeRowOrder()
  } catch { /* 用户取消。 */ }
}

function normalizeRowOrder() {
  rows.value.forEach((item, index) => { item.sortOrder = index + 1 })
}

async function savePlan() {
  if (!activeDetailProject.value) return
  saving.value = true
  try {
    const saved = await saveProjectValidationPlan(activeDetailProject.value.id, {
      preparedBy: preparedBy.value || null,
      validationDate: planDate.value || null,
      expectedRowVersion: plan.value?.rowVersion ?? null,
      items: rows.value.map((item, index) => ({
        catalogItemId: item.catalogItemId,
        validationContent: item.catalogItemId ? null : item.validationContent,
        informationSource: item.informationSource || null,
        validationDate: item.validationDate || null,
        result: item.result || null,
        responsiblePerson: item.responsiblePerson || null,
        remark: item.remark || null,
        sortOrder: index + 1,
      })),
    }, props.token)
    hydrating.value = true
    hydratePlan(saved)
    dirty.value = false
    ElMessage.success('验证计划已保存')
  } catch (error) {
    ElMessage.error(errorMessage(error))
  } finally {
    hydrating.value = false
    saving.value = false
  }
}

async function exportPlan() {
  if (!activeDetailProject.value) return
  if (dirty.value) {
    ElMessage.warning('请先保存当前修改，再导出Excel。')
    return
  }
  exporting.value = true
  try {
    await exportProjectValidationPlan(activeDetailProject.value.id, activeDetailProject.value.code, props.token)
    ElMessage.success('验证计划Excel已导出')
  } catch (error) {
    ElMessage.error(errorMessage(error))
  } finally {
    exporting.value = false
  }
}

async function createRevision() {
  if (!activeDetailProject.value || !plan.value) return
  try {
    const saved = await createProjectValidationPlanRevision(activeDetailProject.value.id, plan.value.rowVersion, props.token)
    hydrating.value = true
    hydratePlan(saved)
    dirty.value = false
    ElMessage.success(`已创建验证计划R${saved.revisionNumber}草稿`)
  } catch (error) {
    ElMessage.error(errorMessage(error))
  } finally {
    hydrating.value = false
  }
}

async function submitForApproval() {
  if (!activeDetailProject.value || !plan.value) return
  if (dirty.value) return ElMessage.warning('请先保存当前修改，再提交审批。')
  try {
    await ElMessageBox.confirm(`提交验证计划R${plan.value.revisionNumber}后，本版本在审批完成前不可修改。`, '提交验证计划审批', { type: 'warning', confirmButtonText: '提交审批', cancelButtonText: '取消' })
    submitting.value = true
    hydratePlan(await submitProjectValidationPlan(activeDetailProject.value.id, plan.value.rowVersion, props.token))
    dirty.value = false
    ElMessage.success('验证计划已提交审批')
  } catch (error) {
    if (error === 'cancel' || error === 'close') return
    ElMessage.error(errorMessage(error))
  } finally {
    submitting.value = false
  }
}

async function decideCurrent(decision: 'Approved' | 'Rejected') {
  const task = currentApprovalTask.value
  if (!task) return
  try {
    const { value } = await ElMessageBox.prompt(decision === 'Approved' ? '可填写审批意见。' : '请填写驳回原因。', decision === 'Approved' ? '批准验证计划' : '驳回验证计划', {
      inputValidator: value => decision === 'Approved' || value.trim().length > 0 || '请填写驳回原因',
      confirmButtonText: decision === 'Approved' ? '批准' : '驳回', cancelButtonText: '取消', type: decision === 'Approved' ? 'success' : 'warning',
    })
    submitting.value = true
    hydratePlan(await decideValidationPlanApproval(task.id, decision, value, props.token))
    dirty.value = false
    ElMessage.success(decision === 'Approved' ? '审批已流转' : '验证计划已驳回')
  } catch (error) {
    if (error === 'cancel' || error === 'close') return
    ElMessage.error(errorMessage(error))
  } finally {
    submitting.value = false
  }
}

async function uploadFile(kind: 'PlanDocument' | 'Evidence', event: Event) {
  const input = event.target as HTMLInputElement
  const file = input.files?.[0]
  input.value = ''
  if (!file || !plan.value || !activeDetailProject.value) return
  uploading.value = true
  try {
    const attachment = await uploadValidationPlanAttachment(plan.value.id, kind, file, props.token)
    await loadDetail(activeDetailProject.value.id)
    ElMessage.success(kind === 'PlanDocument' ? '验证计划文件已归档' : '佐证附件已归档')
    if (kind === 'PlanDocument' && isRecognizableAttachment(attachment)) await recognizeAttachment(attachment)
  } catch (error) {
    ElMessage.error(errorMessage(error))
  } finally {
    uploading.value = false
  }
}

function isRecognizableAttachment(file: ValidationPlanAttachment) {
  return attachmentKind(file) === 'PlanDocument' && /\.(pdf|png|jpe?g)$/i.test(file.originalFileName)
}

async function recognizeAttachment(file: ValidationPlanAttachment) {
  recognizing.value = true
  try {
    const draft = await recognizeValidationPlanAttachment(file.id, props.token)
    recognitionDraft.value = draft
    recognitionRows.value = draft.candidates.map(item => ({
      ...item,
      selected: item.matchStatus !== 'Unmatched',
      result: item.recognizedResult ?? '',
      validationDate: item.recognizedValidationDate ?? '',
      responsiblePerson: item.recognizedResponsiblePerson ?? '',
      remark: item.recognizedRemark ?? '',
    }))
    recognitionOpen.value = true
  } catch (error) {
    ElMessage.error(errorMessage(error))
  } finally {
    recognizing.value = false
  }
}

async function confirmRecognition() {
  if (!plan.value || !recognitionDraft.value) return
  const selected = recognitionRows.value.filter(item => item.selected)
  if (!selected.length) return ElMessage.warning('请至少选择一项识别结果。')
  confirmingRecognition.value = true
  try {
    await confirmValidationPlanExecution(plan.value.id, {
      sourceAttachmentId: recognitionDraft.value.attachmentId,
      ocrText: recognitionDraft.value.ocrText,
      items: selected.map(item => ({
        planItemId: item.planItemId,
        matchConfidence: item.matchConfidence,
        sourceText: item.sourceText,
        recognizedResult: item.recognizedResult ?? null,
        recognizedValidationDate: item.recognizedValidationDate ?? null,
        recognizedResponsiblePerson: item.recognizedResponsiblePerson ?? null,
        recognizedRemark: item.recognizedRemark ?? null,
        result: item.result || null,
        validationDate: item.validationDate || null,
        responsiblePerson: item.responsiblePerson || null,
        remark: item.remark || null,
      })),
    }, props.token)
    executionRecords.value = await readValidationPlanExecutionRecords(plan.value.id, props.token)
    recognitionOpen.value = false
    ElMessage.success('识别结果已确认为验证执行记录')
  } catch (error) {
    ElMessage.error(errorMessage(error))
  } finally {
    confirmingRecognition.value = false
  }
}

function recognitionStatusLabel(status: ValidationPlanRecognitionCandidate['matchStatus']) {
  return ({ Matched: '已匹配', Review: '待复核', Unmatched: '未匹配' } as const)[status]
}

function planItemLabel(planItemId: string) {
  return plan.value?.items.find(item => item.id === planItemId)?.validationContent ?? '检查项'
}

async function downloadAttachment(id: string, fileName: string) {
  try { await downloadValidationPlanAttachment(id, fileName, props.token) }
  catch (error) { ElMessage.error(errorMessage(error)) }
}

async function openCatalogManager() {
  catalogOpen.value = true
  await reloadCatalogManager()
}

async function reloadCatalogManager(preferredCategoryId = catalogCategoryId.value) {
  catalogLoading.value = true
  try {
    catalogAll.value = await readValidationCheckCatalog(props.token, true)
    catalogCategoryId.value = catalogAll.value.categories.some(item => item.id === preferredCategoryId)
      ? preferredCategoryId
      : catalogAll.value.categories[0]?.id ?? ''
  } catch (error) {
    ElMessage.error(errorMessage(error))
  } finally {
    catalogLoading.value = false
  }
}

function editCategory(category?: ValidationCheckCategory) {
  Object.assign(categoryForm, category ? {
    id: category.id, name: category.name, sortOrder: category.sortOrder, isActive: category.isActive,
    note: category.note ?? '', rowVersion: category.rowVersion,
  } : {
    id: '', name: '', sortOrder: (catalogAll.value.categories.at(-1)?.sortOrder ?? 0) + 10,
    isActive: true, note: '', rowVersion: 0,
  })
  categoryEditorOpen.value = true
}

async function submitCategory() {
  if (!categoryForm.name.trim()) return ElMessage.warning('请输入分类名称。')
  try {
    const saved = await saveValidationCheckCategory(categoryForm.id || null, {
      name: categoryForm.name,
      sortOrder: categoryForm.sortOrder,
      isActive: categoryForm.isActive,
      note: categoryForm.note || null,
      expectedRowVersion: categoryForm.id ? categoryForm.rowVersion : null,
    }, props.token)
    categoryEditorOpen.value = false
    await reloadCatalogManager(saved.id)
    catalog.value = await readValidationCheckCatalog(props.token)
    ElMessage.success(categoryForm.id ? '验证分类已更新' : '验证分类已新增')
  } catch (error) {
    ElMessage.error(errorMessage(error))
  }
}

async function removeCategory(category: ValidationCheckCategory) {
  if (category.itemCount || category.referenceCount) return ElMessage.warning('该分类包含检查项或已有项目引用，只能停用。')
  try {
    await ElMessageBox.confirm(`确定删除分类“${category.name}”？`, '删除验证分类', { type: 'warning', confirmButtonText: '删除', cancelButtonText: '取消' })
    await deleteValidationCheckCategory(category.id, category.rowVersion, props.token)
    await reloadCatalogManager()
    ElMessage.success('验证分类已删除')
  } catch (error) {
    if (error instanceof Error) ElMessage.error(errorMessage(error))
  }
}

function editItem(item?: ValidationCheckItem) {
  const categoryId = item?.categoryId ?? catalogCategoryId.value
  Object.assign(itemForm, item ? {
    id: item.id, categoryId: item.categoryId, content: item.content,
    defaultInformationSource: item.defaultInformationSource, sortOrder: item.sortOrder,
    isActive: item.isActive, note: item.note ?? '', rowVersion: item.rowVersion,
  } : {
    id: '', categoryId, content: '', defaultInformationSource: '内部评审',
    sortOrder: (managedItems.value.at(-1)?.sortOrder ?? 0) + 10, isActive: true, note: '', rowVersion: 0,
  })
  itemEditorOpen.value = true
}

async function submitItem() {
  if (!itemForm.categoryId || !itemForm.content.trim()) return ElMessage.warning('请选择分类并填写检查项内容。')
  try {
    const saved = await saveValidationCheckItem(itemForm.id || null, {
      categoryId: itemForm.categoryId,
      content: itemForm.content,
      defaultInformationSource: itemForm.defaultInformationSource,
      sortOrder: itemForm.sortOrder,
      isActive: itemForm.isActive,
      note: itemForm.note || null,
      expectedRowVersion: itemForm.id ? itemForm.rowVersion : null,
    }, props.token)
    itemEditorOpen.value = false
    await reloadCatalogManager(saved.categoryId)
    catalog.value = await readValidationCheckCatalog(props.token)
    ElMessage.success(itemForm.id ? '检查项已更新' : '检查项已新增')
  } catch (error) {
    ElMessage.error(errorMessage(error))
  }
}

async function removeCatalogItem(item: ValidationCheckItem) {
  if (item.referenceCount) return ElMessage.warning('该检查项已有项目引用，只能停用。')
  try {
    await ElMessageBox.confirm('确定从全局库删除该检查项？', '删除检查项', { type: 'warning', confirmButtonText: '删除', cancelButtonText: '取消' })
    await deleteValidationCheckItem(item.id, item.rowVersion, props.token)
    await reloadCatalogManager(item.categoryId)
    catalog.value = await readValidationCheckCatalog(props.token)
    ElMessage.success('检查项已删除')
  } catch (error) {
    if (error instanceof Error) ElMessage.error(errorMessage(error))
  }
}

function errorMessage(error: unknown) {
  return error instanceof Error ? error.message : '操作失败，请稍后重试。'
}

function summaryStatus(project: ProjectSummary) {
  if (!project.canReadContent) return '无访问权限'
  if (summaryErrors.value[project.id]) return '加载失败'
  const projectPlan = summaryPlans.value[project.id]
  if (!projectPlan) return '未建立'
  const state = typeof projectPlan.state === 'number' ? ['Draft', 'PendingApproval', 'Effective', 'Rejected', 'Superseded'][projectPlan.state] : projectPlan.state
  return ({ Draft: '草稿', PendingApproval: '审批中', Effective: '已生效', Rejected: '已驳回', Superseded: '已替代' } as Record<string, string>)[state] ?? '草稿'
}

function attachmentKind(file: ValidationPlanAttachment): 'PlanDocument' | 'Evidence' {
  return file.kind === 'PlanDocument' || file.kind === 0 ? 'PlanDocument' : 'Evidence'
}

function summaryAttachmentCount(projectId: string, kind: 'PlanDocument' | 'Evidence') {
  return (summaryPlans.value[projectId]?.attachments ?? []).filter(file => attachmentKind(file) === kind).length
}

function openSummaryAttachments(project: ProjectSummary, kind: 'PlanDocument' | 'Evidence') {
  if (!project.canReadContent) return
  attachmentDialogProject.value = project
  attachmentDialogKind.value = kind
  attachmentDialogOpen.value = true
}

function stateLabel() {
  return ({ Draft: '草稿', PendingApproval: '审批中', Effective: '已生效', Rejected: '已驳回', Superseded: '已替代' } as Record<string, string>)[normalizedState.value ?? ''] ?? '未建立'
}

function fileSize(value: number) {
  if (value < 1024) return `${value} B`
  if (value < 1024 * 1024) return `${(value / 1024).toFixed(1)} KB`
  return `${(value / 1024 / 1024).toFixed(1)} MB`
}

function formatDateTime(value?: string | null) {
  return value ? new Date(value).toLocaleString() : '—'
}
</script>

<template>
  <section class="pdm-panel pdm-manager-panel validation-plan" aria-label="项目验证计划">
    <header v-if="view === 'summary'" class="validation-plan__toolbar">
      <div>
        <h2><ListChecks :size="18" />验证计划</h2>
        <p>{{ rootProject?.code || projectCode }} · 主项目及子项目验证计划</p>
      </div>
      <div class="validation-plan__actions">
        <button v-if="canManageCatalog" type="button" class="pdm-secondary-action" @click="openCatalogManager"><LibraryBig :size="14" />模板管理</button>
      </div>
    </header>

    <template v-if="view === 'summary'">
      <div v-if="loading" class="validation-plan__empty">正在加载项目验证计划列表…</div>
      <div v-else class="validation-plan__table-scroll">
        <table class="validation-plan-summary__table">
          <thead><tr><th>项目代号</th><th>项目名称</th><th>项目类型</th><th>验证项数</th><th>编制人</th><th>编制日期</th><th>最近保存</th><th>状态</th><th>验证计划</th><th>附件</th></tr></thead>
          <tbody>
            <tr v-for="project in familyProjects" :key="project.id" :class="{ 'is-disabled': !project.canReadContent }" :tabindex="project.canReadContent ? 0 : -1" @click="openProjectPlan(project)" @keydown.enter.self.prevent="openProjectPlan(project)" @keydown.space.self.prevent="openProjectPlan(project)">
              <td><strong>{{ project.code }}</strong></td>
              <td>{{ project.name }}</td>
              <td>{{ project.parentProjectId ? '子项目' : '主项目' }}</td>
              <td>{{ summaryPlans[project.id]?.items.length ?? 0 }}</td>
              <td>{{ summaryPlans[project.id]?.preparedBy ? displayUserName(summaryPlans[project.id]?.preparedBy, summaryPlans[project.id]?.preparedBy ?? undefined) : '—' }}</td>
              <td>{{ summaryPlans[project.id]?.validationDate || '—' }}</td>
              <td>{{ formatDateTime(summaryPlans[project.id]?.updatedAt) }}</td>
              <td><span class="validation-plan-summary__status" :class="`is-${summaryStatus(project)}`">{{ summaryStatus(project) }}</span></td>
              <td><button type="button" class="validation-plan-summary__file-link" :aria-label="`查看 ${project.code} 的验证计划文件`" @click.stop="openSummaryAttachments(project, 'PlanDocument')">{{ summaryAttachmentCount(project.id, 'PlanDocument') }}</button></td>
              <td><button type="button" class="validation-plan-summary__file-link" :aria-label="`查看 ${project.code} 的附件`" @click.stop="openSummaryAttachments(project, 'Evidence')">{{ summaryAttachmentCount(project.id, 'Evidence') }}</button></td>
            </tr>
          </tbody>
        </table>
      </div>
      <footer class="validation-plan__footer"><span>共 {{ familyProjects.length }} 个项目</span><span>范围：{{ rootProject?.code || projectCode }} 主项目及其子项目</span></footer>
    </template>

    <el-dialog v-model="attachmentDialogOpen" :title="`${attachmentDialogProject?.code || ''} · ${attachmentDialogKind === 'PlanDocument' ? '验证计划文件' : '附件明细'}`" width="960px" append-to-body>
      <div class="validation-plan-attachment-list">
        <table v-if="attachmentDialogFiles.length">
          <thead><tr><th>原文件名</th><th>版本</th><th>上传人</th><th>上传时间</th><th>大小</th><th>SHA-256</th><th>操作</th></tr></thead>
          <tbody><tr v-for="file in attachmentDialogFiles" :key="file.id"><td :title="file.originalFileName">{{ file.originalFileName }}</td><td>V{{ file.fileVersion }}</td><td>{{ displayUserName(file.uploadedBy, file.uploadedBy) }}</td><td>{{ formatDateTime(file.uploadedAt) }}</td><td>{{ fileSize(file.fileLength) }}</td><td class="is-sha" :title="file.sha256">{{ file.sha256 }}</td><td><button type="button" class="pdm-text-action" @click="downloadAttachment(file.id, file.originalFileName)">下载</button></td></tr></tbody>
        </table>
        <div v-else class="validation-plan-attachment-list__empty">暂无{{ attachmentDialogKind === 'PlanDocument' ? '验证计划文件' : '附件' }}</div>
      </div>
      <template #footer><button type="button" class="pdm-primary-action" @click="attachmentDialogOpen = false">关闭</button></template>
    </el-dialog>

    <template v-if="view !== 'summary'">
    <header class="validation-plan__toolbar">
      <div>
        <h2><button type="button" class="validation-plan__back" title="返回验证计划列表" @click="backToSummary"><ArrowLeft :size="17" /></button><ListChecks :size="18" />验证计划</h2>
        <p>{{ activeDetailProject?.code }} · {{ activeDetailProject?.name }} · 从全局检查项库选取后形成项目快照</p>
      </div>
      <div class="validation-plan__actions">
        <button v-if="canManageCatalog" type="button" class="pdm-secondary-action" @click="openCatalogManager"><LibraryBig :size="14" />模板管理</button>
        <input ref="planFileInput" class="validation-plan__file-input" type="file" accept=".pdf,.png,.jpg,.jpeg,.xlsx,.xls" @change="uploadFile('PlanDocument', $event)">
        <input ref="evidenceFileInput" class="validation-plan__file-input" type="file" accept=".pdf,.png,.jpg,.jpeg,.xlsx,.xls,.csv,.zip" @change="uploadFile('Evidence', $event)">
        <button type="button" class="pdm-secondary-action validation-plan__upload-action" :disabled="!plan || normalizedState !== 'Effective' || uploading" title="验证计划审批完成并生效后可上传" @click="planFileInput?.click()"><FileUp :size="14" />上传计划</button>
        <button type="button" class="pdm-secondary-action validation-plan__upload-action" :disabled="!plan || normalizedState !== 'Effective' || uploading" title="验证计划审批完成并生效后可上传" @click="evidenceFileInput?.click()"><Paperclip :size="14" />上传附件</button>
        <button type="button" class="pdm-secondary-action" :disabled="!editable" @click="openSelection"><Plus :size="14" />选取内容</button>
        <button type="button" class="pdm-secondary-action" :disabled="!plan || dirty || exporting" @click="exportPlan"><Download :size="14" />{{ exporting ? '导出中…' : '导出Excel' }}</button>
        <button v-if="normalizedState === 'Effective'" type="button" class="pdm-secondary-action" :disabled="!canEdit" @click="createRevision"><RotateCcw :size="14" />创建新版本</button>
        <button v-if="editable" type="button" class="pdm-primary-action" :disabled="saving || loading" @click="savePlan"><Save :size="14" />{{ saving ? '保存中…' : '保存' }}</button>
        <button v-if="plan && editable" type="button" class="pdm-primary-action" :disabled="dirty || submitting" @click="submitForApproval"><Send :size="14" />提交审批</button>
        <button v-if="canDecideCurrent" type="button" class="pdm-secondary-action" :disabled="submitting" @click="decideCurrent('Rejected')"><XCircle :size="14" />驳回</button>
        <button v-if="canDecideCurrent" type="button" class="pdm-primary-action" :disabled="submitting" @click="decideCurrent('Approved')"><CheckCircle2 :size="14" />批准</button>
      </div>
    </header>

    <div class="validation-plan__meta">
      <label>项目代号<input :value="activeDetailProject?.code" readonly></label>
      <label>项目名称<input :value="activeDetailProject?.name" readonly></label>
      <label>编制人<input :value="preparedBy" readonly></label>
      <label>编制日期<input :value="planDate" readonly type="date"></label>
      <span v-if="plan" class="validation-plan__state">R{{ plan.revisionNumber }} · {{ stateLabel() }}</span>
      <span v-if="dirty" class="validation-plan__dirty">有未保存修改</span>
    </div>

    <div v-if="loading" class="validation-plan__empty">正在加载验证计划…</div>
    <div v-else-if="rows.length" class="validation-plan__table-scroll">
      <table class="validation-plan__table">
        <thead><tr><th>序号</th><th>分类</th><th>验证内容</th><th>信息来源</th><th>验证日期</th><th>责任人</th><th>结果</th><th>备注</th><th>操作</th></tr></thead>
        <tbody>
          <tr v-for="(row, index) in rows" :key="row.id" :data-row-index="index" :class="{ 'is-row-dragging': draggedRowIndex === index, 'is-drag-over-before': dragOverRowIndex === index && dragOverPosition === 'before', 'is-drag-over-after': dragOverRowIndex === index && dragOverPosition === 'after' }">
            <td class="is-sequence">{{ index + 1 }}</td>
            <td><span class="validation-plan__category">{{ row.categoryName }}</span></td>
            <td class="is-content"><input v-if="!row.catalogItemId" v-model="row.validationContent" :disabled="!editable" maxlength="1500" placeholder="请输入验证内容"><span v-else :title="row.validationContent">{{ row.validationContent }}</span></td>
            <td><select v-model="row.informationSource" :disabled="!editable"><option value="">—</option><option v-for="source in informationSources" :key="source" :value="source">{{ source }}</option></select></td>
            <td class="validation-plan__date-cell"><input v-model="row.validationDate" :disabled="!editable" type="date" tabindex="-1" aria-hidden="true"><button type="button" :disabled="!editable" title="选择验证日期" aria-label="选择验证日期" @click="openDatePicker($event)"><CalendarDays :size="14" /></button></td>
            <td><input v-model="row.responsiblePerson" :disabled="!editable" maxlength="100" placeholder="责任人"></td>
            <td><input v-model="row.result" :disabled="!editable" maxlength="1500" placeholder="填写结果"></td>
            <td><input v-model="row.remark" :disabled="!editable" maxlength="1000" placeholder="备注"></td>
            <td><div class="validation-plan__row-actions"><span v-if="editable" class="validation-plan__drag-handle" role="button" tabindex="0" :aria-label="`拖动第 ${index + 1} 行排序`" :title="`按住拖动第 ${index + 1} 行排序`" @pointerdown="startRowPointerDrag(index, $event)">⠿</span><button type="button" :disabled="!editable" :title="`在第 ${index + 1} 行下方添加人工项`" :aria-label="`在第 ${index + 1} 行下方添加人工项`" @click="addManualRow(index)"><Plus :size="14" /></button><button type="button" :disabled="!editable" title="移除" @click="removeRow(index)"><Trash2 :size="14" /></button></div></td>
          </tr>
        </tbody>
      </table>
    </div>
    <div v-else class="validation-plan__empty"><ListChecks :size="42" /><h3>当前项目还没有验证检查项</h3><p>按分类从全局检查项库选取，或直接添加人工项。</p><div v-if="editable" class="validation-plan__actions"><button type="button" class="pdm-primary-action" @click="openSelection">选择检查项</button><button type="button" class="pdm-secondary-action" @click="addManualRow(-1)"><Plus :size="14" />添加人工项</button></div></div>
    <section v-if="plan?.approvalTasks?.length || plan?.attachments?.length || executionRecords.length" class="validation-plan__records">
      <div v-if="plan?.approvalTasks?.length"><strong>审批记录</strong><span v-for="task in plan.approvalTasks" :key="task.id">{{ task.stepOrder }}. {{ task.stepName }} · {{ displayUserName(task.assignee, task.assignee) }} · {{ task.decision == null ? '待处理' : (task.decision === 'Approved' || task.decision === 0 ? '已批准' : '已驳回') }}</span></div>
      <div v-if="plan?.attachments?.length"><strong>归档文件（验收资料 / 验证计划）</strong><div v-for="file in plan.attachments" :key="file.id" class="validation-plan__attachment-row"><button type="button" class="validation-plan__attachment" :title="`SHA-256 ${file.sha256}`" @click="downloadAttachment(file.id, file.originalFileName)">{{ file.kind === 'PlanDocument' || file.kind === 0 ? '验证计划' : '佐证附件' }} · {{ file.originalFileName }} · V{{ file.fileVersion }} · {{ fileSize(file.fileLength) }} · {{ displayUserName(file.uploadedBy, file.uploadedBy) }}</button><button v-if="normalizedState === 'Effective' && isRecognizableAttachment(file)" type="button" class="pdm-text-action" :disabled="recognizing" @click="recognizeAttachment(file)">{{ recognizing ? '识别中…' : '识别结果' }}</button></div></div>
      <div v-if="executionRecords.length"><strong>验证执行记录</strong><article v-for="record in executionRecords" :key="record.id" class="validation-plan__execution-record"><span>{{ record.sourceFileName }} · {{ displayUserName(record.confirmedBy, record.confirmedBy) }} · {{ formatDateTime(record.confirmedAt) }}</span><small v-for="item in record.items" :key="item.id" :title="item.sourceText">{{ planItemLabel(item.planItemId) }}：{{ item.result || '未填写结果' }}<template v-if="item.responsiblePerson"> · {{ item.responsiblePerson }}</template><template v-if="item.validationDate"> · {{ item.validationDate }}</template></small></article></div>
    </section>
    <footer class="validation-plan__footer"><span>共 {{ rows.length }} 项</span><span v-if="plan">最近保存：{{ displayUserName(plan.updatedBy) }} · {{ new Date(plan.updatedAt).toLocaleString() }}</span><span v-else>尚未保存</span></footer>
    </template>

    <el-dialog v-model="recognitionOpen" class="validation-plan-recognition-dialog" :title="`识别并确认 · ${recognitionDraft?.originalFileName || ''}`" width="min(1220px, 96vw)" append-to-body destroy-on-close>
      <div class="validation-recognition__notice">OCR结果不会修改已审批的验证计划。请核对后选择需要写入“验证执行记录”的项目；低置信度内容必须人工确认。</div>
      <div class="validation-recognition__table-scroll">
        <table class="validation-recognition__table">
          <thead><tr><th>采用</th><th>状态</th><th>置信度</th><th>验证内容</th><th>结果</th><th>验证日期</th><th>责任人</th><th>备注</th></tr></thead>
          <tbody><tr v-for="row in recognitionRows" :key="row.planItemId" :class="`is-${row.matchStatus.toLowerCase()}`"><td><input v-model="row.selected" type="checkbox" :aria-label="`采用 ${row.validationContent} 的识别结果`"></td><td><span>{{ recognitionStatusLabel(row.matchStatus) }}</span></td><td>{{ Math.round(row.matchConfidence * 100) }}%</td><td class="is-content" :title="row.sourceText">{{ row.validationContent }}</td><td><input v-model="row.result" maxlength="1500" placeholder="人工确认结果"></td><td><input v-model="row.validationDate" type="date"></td><td><input v-model="row.responsiblePerson" maxlength="100" placeholder="责任人"></td><td><input v-model="row.remark" maxlength="1000" placeholder="备注"></td></tr></tbody>
        </table>
      </div>
      <template #footer><span>已选择 {{ selectedRecognitionCount }} 项</span><button type="button" class="pdm-secondary-action" @click="recognitionOpen = false">取消</button><button type="button" class="pdm-primary-action" :disabled="!selectedRecognitionCount || confirmingRecognition" @click="confirmRecognition">{{ confirmingRecognition ? '确认中…' : '确认写入执行记录' }}</button></template>
    </el-dialog>

    <el-dialog v-model="selectionOpen" class="validation-plan-selector-dialog" title="选择验证检查项" width="920px" append-to-body destroy-on-close>
      <div class="validation-selector">
        <aside><strong>检查分类</strong><button v-for="category in activeCategories" :key="category.id" type="button" :class="{ 'is-active': selectionCategoryId === category.id }" @click="selectionCategoryId = category.id; selectedItemIds = []"><span>{{ category.name }}</span><small>{{ catalog.items.filter(item => item.categoryId === category.id && item.isActive).length }}</small></button></aside>
        <section><div class="validation-selector__filter"><input v-model="selectionQuery" type="search" placeholder="搜索检查项内容" aria-label="搜索验证检查项"><label class="validation-selector__select-all"><input type="checkbox" :checked="allSelectableItemsSelected" :disabled="!selectableItems.length" @change="toggleSelectableItems(($event.target as HTMLInputElement).checked)">全选当前列表</label></div><div class="validation-selector__items"><label v-for="item in selectableItems" :key="item.id"><input v-model="selectedItemIds" type="checkbox" :value="item.id"><span>{{ item.content }}</span><small>{{ item.defaultInformationSource }}</small></label><p v-if="!selectableItems.length">当前分类没有可加入的检查项。</p></div></section>
      </div>
      <template #footer><span>已选 {{ selectedItemIds.length }} 项</span><button type="button" class="pdm-secondary-action" @click="selectionOpen = false">取消</button><button type="button" class="pdm-primary-action" :disabled="!selectedItemIds.length" @click="appendSelectedItems">加入计划</button></template>
    </el-dialog>

    <el-dialog v-model="catalogOpen" title="全局验证检查项库" width="1100px" append-to-body destroy-on-close>
      <div v-loading="catalogLoading" class="validation-catalog">
        <aside><header><strong>分类</strong><button type="button" class="pdm-text-action" @click="editCategory()">+ 新增</button></header><button v-for="category in catalogAll.categories" :key="category.id" type="button" :class="{ 'is-active': catalogCategoryId === category.id, 'is-disabled': !category.isActive }" @click="catalogCategoryId = category.id"><span>{{ category.name }}</span><small>{{ category.itemCount }}项</small></button></aside>
        <section>
          <header><div><strong>{{ selectedCatalogCategory?.name || '请选择分类' }}</strong><small v-if="selectedCatalogCategory">{{ selectedCatalogCategory.isActive ? '启用中' : '已停用' }} · 引用 {{ selectedCatalogCategory.referenceCount }} 次</small></div><div><button v-if="selectedCatalogCategory" type="button" class="pdm-secondary-action" @click="editCategory(selectedCatalogCategory)">编辑分类</button><button v-if="selectedCatalogCategory" type="button" class="pdm-secondary-action" :disabled="Boolean(selectedCatalogCategory.itemCount || selectedCatalogCategory.referenceCount)" @click="removeCategory(selectedCatalogCategory)">删除分类</button><button type="button" class="pdm-primary-action" :disabled="!selectedCatalogCategory" @click="editItem()">新增检查项</button></div></header>
          <input v-model="catalogQuery" type="search" placeholder="搜索当前分类检查项" aria-label="搜索全局检查项">
          <div class="validation-catalog__items"><article v-for="item in managedItems" :key="item.id" :class="{ 'is-disabled': !item.isActive }"><div><span>{{ item.content }}</span><small>{{ item.defaultInformationSource }} · 排序 {{ item.sortOrder }} · {{ item.isActive ? '启用' : '停用' }}<template v-if="item.referenceCount"> · 已引用 {{ item.referenceCount }} 次</template></small></div><button type="button" class="pdm-text-action" @click="editItem(item)">编辑</button><button type="button" class="pdm-text-action is-danger" :disabled="Boolean(item.referenceCount)" @click="removeCatalogItem(item)">删除</button></article><p v-if="!managedItems.length">当前分类没有检查项。</p></div>
        </section>
      </div>
      <template #footer><small>已引用的检查项和分类不可删除，可在编辑中停用；已保存项目仍保留原内容快照。</small><button type="button" class="pdm-primary-action" @click="catalogOpen = false">完成</button></template>
    </el-dialog>

    <el-dialog v-model="categoryEditorOpen" :title="categoryForm.id ? '编辑验证分类' : '新增验证分类'" width="520px" append-to-body>
      <div class="validation-editor"><label>分类名称<input v-model="categoryForm.name" maxlength="150"></label><label>排序<input v-model.number="categoryForm.sortOrder" type="number" min="0"></label><label class="is-check"><input v-model="categoryForm.isActive" type="checkbox">启用该分类</label><label>备注<textarea v-model="categoryForm.note" maxlength="500" rows="3"></textarea></label></div>
      <template #footer><button type="button" class="pdm-secondary-action" @click="categoryEditorOpen = false">取消</button><button type="button" class="pdm-primary-action" @click="submitCategory">保存</button></template>
    </el-dialog>

    <el-dialog v-model="itemEditorOpen" :title="itemForm.id ? '编辑验证检查项' : '新增验证检查项'" width="640px" append-to-body>
      <div class="validation-editor"><label>所属分类<select v-model="itemForm.categoryId"><option v-for="category in catalogAll.categories" :key="category.id" :value="category.id">{{ category.name }}{{ category.isActive ? '' : '（已停用）' }}</option></select></label><label>检查项内容<textarea v-model="itemForm.content" maxlength="1500" rows="5"></textarea></label><label>默认信息来源<select v-model="itemForm.defaultInformationSource"><option v-for="source in informationSources" :key="source" :value="source">{{ source }}</option></select></label><label>排序<input v-model.number="itemForm.sortOrder" type="number" min="0"></label><label class="is-check"><input v-model="itemForm.isActive" type="checkbox">启用该检查项</label><label>备注<textarea v-model="itemForm.note" maxlength="500" rows="2"></textarea></label></div>
      <template #footer><button type="button" class="pdm-secondary-action" @click="itemEditorOpen = false">取消</button><button type="button" class="pdm-primary-action" @click="submitItem">保存</button></template>
    </el-dialog>
  </section>
</template>

<style scoped>
.validation-plan { gap: 12px; padding: 12px; }
.validation-plan__toolbar,.validation-plan__meta,.validation-plan__footer { display:flex; align-items:center; justify-content:space-between; gap:12px; }
.validation-plan__toolbar h2 { display:flex; align-items:center; gap:7px; margin:0; font-size:17px; }
.validation-plan__toolbar p { margin:4px 0 0; color:var(--pdm-muted); font-size:12px; }
.validation-plan__actions { display:flex; align-items:center; gap:8px; flex-wrap:wrap; }
.validation-plan__actions button,.validation-plan__toolbar button { display:inline-flex; align-items:center; gap:5px; }.validation-plan__toolbar .validation-plan__actions>button { box-sizing:border-box; justify-content:center; width:100px; height:32px; min-height:32px; padding:0; white-space:nowrap; }
.validation-plan__upload-action:disabled { border-color:#cbd2dc; background:#eef1f4; color:#98a2b3; cursor:not-allowed; opacity:1; }
.validation-plan__back { display:grid!important; place-items:center; width:26px; height:26px; padding:0; border:1px solid var(--pdm-border); border-radius:5px; background:var(--pdm-surface); color:var(--pdm-text); }
.validation-plan__meta { justify-content:flex-start; padding:10px 12px; border:1px solid var(--pdm-border); border-radius:8px; background:var(--pdm-soft); }
.validation-plan__meta label { display:grid; gap:4px; color:var(--pdm-muted); font-size:11px; }
.validation-plan__meta input { width:190px; height:30px; padding:0 9px; border:1px solid var(--pdm-border); border-radius:5px; background:var(--pdm-surface); color:var(--pdm-text); }
.validation-plan__dirty { margin-left:auto; color:#b56a00; font-size:12px; }
.validation-plan__table-scroll { min-height:0; flex:1 1 auto; overflow:auto; border:1px solid var(--pdm-border); border-radius:7px; }
.validation-plan__table { width:100%; min-width:1320px; border-collapse:collapse; table-layout:fixed; font-size:12px; }
.validation-plan-summary__table { width:100%; min-width:1240px; border-collapse:collapse; table-layout:fixed; font-size:12px; }
.validation-plan-summary__table th { position:sticky; top:0; z-index:1; padding:9px 6px; border:0; background:#f3f6f9; color:var(--pdm-muted); font-weight:500; text-align:center; vertical-align:middle; }
.validation-plan-summary__table tbody tr { background:var(--pdm-surface); cursor:pointer; }
.validation-plan-summary__table tbody tr:hover,.validation-plan-summary__table tbody tr:focus { background:var(--pdm-theme-accent-soft); outline:none; }
.validation-plan-summary__table tbody tr.is-disabled { cursor:not-allowed; opacity:.55; }
.validation-plan-summary__table td { height:38px; padding:0 6px; border:0; border-top:1px solid var(--pdm-border-soft); color:var(--pdm-text); text-align:center; vertical-align:middle; }
.validation-plan-summary__file-link { min-width:34px; padding:3px 8px; border:1px solid var(--pdm-theme-accent-border); border-radius:999px; background:var(--pdm-theme-accent-soft); color:var(--pdm-primary); font:inherit; cursor:pointer; }
.validation-plan-summary__file-link:hover,.validation-plan-summary__file-link:focus-visible { border-color:var(--pdm-primary); outline:2px solid var(--pdm-theme-accent-focus); }
.validation-plan-summary__status { display:inline-block; min-width:54px; padding:3px 7px; border-radius:999px; background:var(--pdm-soft); color:var(--pdm-muted); }
.validation-plan-summary__status.is-已生效 { background:#e8f7f3; color:#087f72; }
.validation-plan-summary__status.is-审批中 { background:#fff5db; color:#9a6500; }
.validation-plan-summary__status.is-已驳回 { background:#fff1f1; color:#b94040; }
.validation-plan-summary__status.is-加载失败 { background:#fff1f1; color:#b94040; }
.validation-plan__table th { position:sticky; top:0; z-index:1; padding:9px 3px; border:0; background:#f3f6f9; color:var(--pdm-muted); font-weight:500; text-align:center; vertical-align:middle; }
.validation-plan__table tbody tr { background:var(--pdm-surface); }
.validation-plan__table td { padding:0 3px; border:0; border-top:1px solid var(--pdm-border-soft); color:var(--pdm-text); text-align:center; vertical-align:middle; }
.validation-plan__table th:nth-child(1){width:50px}.validation-plan__table th:nth-child(2){width:105px}.validation-plan__table th:nth-child(3){width:320px}.validation-plan__table th:nth-child(4){width:110px}.validation-plan__table th:nth-child(5){width:125px}.validation-plan__table th:nth-child(6){width:145px}.validation-plan__table th:nth-child(7){width:110px}.validation-plan__table th:nth-child(8){width:145px}.validation-plan__table th:nth-child(9){width:94px}
.validation-plan__table input,.validation-plan__table select { box-sizing:border-box; width:100%; min-width:0; height:24px; padding:3px 2px; border:1px solid transparent; border-radius:4px; background:transparent; color:var(--pdm-text); font:inherit; text-align:center; }
.validation-plan__table input:focus,.validation-plan__table select:focus { border-color:var(--pdm-blue); background:var(--pdm-surface); outline:2px solid var(--pdm-theme-accent-focus); }
.validation-plan__table input:disabled,.validation-plan__table select:disabled { border-color:transparent; background:transparent; color:inherit; opacity:1; }
.validation-plan__date-cell { position:relative; }.validation-plan__date-cell input[type="date"] { position:absolute; width:1px; height:1px; opacity:0; pointer-events:none; }.validation-plan__date-cell button { display:inline-grid; place-items:center; width:24px; height:24px; padding:0; border:1px solid var(--pdm-theme-accent-border); border-radius:4px; background:var(--pdm-theme-accent-soft); color:var(--pdm-primary); }.validation-plan__date-cell button:disabled { opacity:.55; }.validation-plan__table .is-sequence { color:var(--pdm-muted); white-space:nowrap; }.validation-plan__drag-handle { display:inline-block; margin-right:4px; color:var(--pdm-primary); cursor:grab; touch-action:none; user-select:none; }.validation-plan__drag-handle:focus-visible { outline:2px solid var(--pdm-theme-accent-focus); outline-offset:2px; border-radius:2px; }.validation-plan__table tbody tr.is-row-dragging { opacity:.5; }.validation-plan__table tbody tr.is-drag-over-before { box-shadow:inset 0 2px 0 var(--pdm-primary); }.validation-plan__table tbody tr.is-drag-over-after { box-shadow:inset 0 -2px 0 var(--pdm-primary); }.validation-plan__table th:nth-child(3),.validation-plan__table .is-content { text-align:left; }.validation-plan__table .is-content { line-height:1.55; white-space:normal; }.validation-plan__table .is-content input { text-align:left; }
.validation-plan__category { display:inline-block; padding:3px 7px; border-radius:999px; background:#e8f7f3; color:#087f72; }
.validation-plan__row-actions { display:flex; justify-content:center; gap:5px; }.validation-plan__row-actions button { display:grid; place-items:center; width:18px; height:18px; padding:0; border:1px solid var(--pdm-theme-accent-border); border-radius:3px; background:var(--pdm-theme-accent-soft); color:var(--pdm-primary); }.validation-plan__row-actions button:last-child { border-color:#f3b3b3; background:#fff1f1; color:#b94040; }
.validation-plan__empty { min-height:0; flex:1 1 auto; display:flex; flex-direction:column; align-items:center; justify-content:center; gap:8px; color:var(--pdm-muted); }.validation-plan__empty h3,.validation-plan__empty p { margin:0; }
.validation-plan__footer { color:var(--pdm-muted); font-size:11px; }
.validation-plan__file-input { display:none; }
.validation-plan__state { align-self:flex-end; padding:4px 9px; border-radius:999px; background:var(--pdm-theme-accent-soft); color:var(--pdm-primary); font-size:12px; white-space:nowrap; }
.validation-plan__records { display:grid; grid-template-columns:repeat(2,minmax(0,1fr)); gap:10px; }
.validation-plan__records>div { display:flex; flex-direction:column; gap:6px; min-width:0; padding:9px 11px; border:1px solid var(--pdm-border-soft); border-radius:7px; background:var(--pdm-soft); font-size:11px; }
.validation-plan__records>div>strong { color:var(--pdm-text); }
.validation-plan__attachment { overflow:hidden; padding:0; border:0; background:transparent; color:var(--pdm-primary); text-align:left; text-overflow:ellipsis; white-space:nowrap; cursor:pointer; }
.validation-plan__attachment-row { display:flex; align-items:center; gap:8px; min-width:0; }.validation-plan__attachment-row .validation-plan__attachment { min-width:0; flex:1 1 auto; }.validation-plan__attachment-row .pdm-text-action { flex:0 0 auto; }
.validation-plan__execution-record { display:grid; gap:4px; padding:7px 0; border-top:1px solid var(--pdm-border-soft); }.validation-plan__execution-record:first-of-type { border-top:0; }.validation-plan__execution-record small { overflow:hidden; color:var(--pdm-muted); text-overflow:ellipsis; white-space:nowrap; }
.validation-recognition__notice { margin-bottom:10px; padding:9px 11px; border:1px solid #f1cf83; border-radius:6px; background:#fff8e6; color:#805900; font-size:12px; }
.validation-recognition__table-scroll { max-height:min(62vh,650px); overflow:auto; border:1px solid var(--pdm-border); border-radius:7px; }
.validation-recognition__table { width:100%; min-width:1120px; border-collapse:collapse; table-layout:fixed; font-size:12px; }.validation-recognition__table th,.validation-recognition__table td { padding:7px 6px; border-bottom:1px solid var(--pdm-border-soft); text-align:center; vertical-align:middle; }.validation-recognition__table th { position:sticky; top:0; z-index:1; background:#f3f6f9; color:var(--pdm-muted); font-weight:500; }.validation-recognition__table th:nth-child(1){width:46px}.validation-recognition__table th:nth-child(2){width:66px}.validation-recognition__table th:nth-child(3){width:62px}.validation-recognition__table th:nth-child(4){width:300px}.validation-recognition__table th:nth-child(5){width:120px}.validation-recognition__table th:nth-child(6){width:125px}.validation-recognition__table th:nth-child(7){width:100px}.validation-recognition__table th:nth-child(8){width:150px}.validation-recognition__table td.is-content { text-align:left; line-height:1.45; }.validation-recognition__table input:not([type="checkbox"]) { box-sizing:border-box; width:100%; height:29px; padding:4px 6px; border:1px solid var(--pdm-border); border-radius:4px; background:var(--pdm-surface); color:var(--pdm-text); }.validation-recognition__table tr.is-review td:nth-child(2),.validation-recognition__table tr.is-unmatched td:nth-child(2) { color:#a26600; }.validation-recognition__table tr.is-unmatched { background:#fff8f0; }
.validation-plan-attachment-list { max-height:min(62vh,620px); overflow:auto; border:1px solid var(--pdm-border); border-radius:7px; }
.validation-plan-attachment-list table { width:100%; min-width:850px; border-collapse:collapse; table-layout:fixed; font-size:12px; }
.validation-plan-attachment-list th,.validation-plan-attachment-list td { padding:9px 8px; border-bottom:1px solid var(--pdm-border-soft); text-align:center; vertical-align:middle; }
.validation-plan-attachment-list th { position:sticky; top:0; z-index:1; background:#f3f6f9; color:var(--pdm-muted); font-weight:500; }
.validation-plan-attachment-list th:first-child,.validation-plan-attachment-list td:first-child { width:190px; text-align:left; overflow:hidden; text-overflow:ellipsis; white-space:nowrap; }
.validation-plan-attachment-list th:nth-child(2) { width:55px; }.validation-plan-attachment-list th:nth-child(3) { width:85px; }.validation-plan-attachment-list th:nth-child(4) { width:145px; }.validation-plan-attachment-list th:nth-child(5) { width:75px; }.validation-plan-attachment-list th:last-child { width:55px; }
.validation-plan-attachment-list td.is-sha { overflow:hidden; font-family:Consolas,monospace; text-align:left; text-overflow:ellipsis; white-space:nowrap; }
.validation-plan-attachment-list__empty { display:grid; place-items:center; min-height:180px; color:var(--pdm-muted); }
.validation-selector { display:grid; grid-template-columns:220px 1fr; height:100%; min-height:0; border:1px solid var(--pdm-border); border-radius:7px; overflow:hidden; }
.validation-selector aside,.validation-catalog aside { display:flex; flex-direction:column; min-height:0; overflow:auto; padding:10px; background:var(--pdm-soft); border-right:1px solid var(--pdm-border); }
.validation-selector aside>strong { padding:5px 7px 10px; }.validation-selector aside>button,.validation-catalog aside>button { display:flex; justify-content:space-between; gap:8px; width:100%; padding:8px; border:0; border-radius:5px; background:transparent; color:var(--pdm-text); text-align:left; }.validation-selector aside>button.is-active,.validation-catalog aside>button.is-active { background:#dff4ef; color:#087f72; }
.validation-selector section { display:flex; flex-direction:column; min-height:0; padding:12px; }.validation-selector__filter { display:flex; flex:0 0 auto; align-items:center; gap:12px; }.validation-selector__filter>input,.validation-catalog section>input { height:34px; padding:0 10px; border:1px solid var(--pdm-border); border-radius:5px; }.validation-selector__filter>input { min-width:0; flex:1 1 auto; }
.validation-selector__select-all { display:flex; flex:0 0 auto; align-items:center; gap:6px; color:var(--pdm-text); font-size:12px; white-space:nowrap; }.validation-selector__items { min-height:0; flex:1 1 auto; margin-top:10px; overflow:auto; }.validation-selector__items label { display:grid; grid-template-columns:22px 1fr 90px; align-items:start; gap:6px; padding:9px 6px; border-bottom:1px solid var(--pdm-border); }.validation-selector__items small { color:var(--pdm-muted); text-align:right; }
.validation-catalog { display:grid; grid-template-columns:240px 1fr; height:600px; min-height:0; border:1px solid var(--pdm-border); border-radius:7px; overflow:hidden; }.validation-catalog aside header,.validation-catalog section>header { display:flex; align-items:center; justify-content:space-between; gap:8px; padding:4px 4px 10px; }.validation-catalog aside>button.is-disabled,.validation-catalog__items article.is-disabled { opacity:.55; }
.validation-catalog section { display:flex; flex-direction:column; min-width:0; min-height:0; padding:12px; }.validation-catalog section>header>div { display:flex; align-items:center; gap:8px; }.validation-catalog section>header small { color:var(--pdm-muted); }.validation-catalog__items { min-height:0; margin-top:10px; overflow:auto; }.validation-catalog__items article { display:grid; grid-template-columns:1fr auto auto; align-items:center; gap:10px; padding:9px 6px; border-bottom:1px solid var(--pdm-border); }.validation-catalog__items article>div { display:grid; gap:4px; }.validation-catalog__items article small { color:var(--pdm-muted); }
.validation-editor { display:grid; gap:13px; }.validation-editor label { display:grid; gap:5px; color:var(--pdm-muted); font-size:12px; }.validation-editor input,.validation-editor select,.validation-editor textarea { width:100%; padding:8px 9px; border:1px solid var(--pdm-border); border-radius:5px; background:var(--pdm-surface); color:var(--pdm-text); box-sizing:border-box; }.validation-editor label.is-check { display:flex; flex-direction:row; align-items:center; color:var(--pdm-text); }.validation-editor label.is-check input { width:auto; }
.pdm-text-action.is-danger { color:#b94040; }
@media (max-width: 1100px) { .validation-plan__toolbar { align-items:flex-start; }.validation-plan__meta { flex-wrap:wrap; }.validation-plan__meta input { width:150px; } }
</style>
