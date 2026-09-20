<script setup lang="ts">
import { computed, onMounted, reactive, ref, watch } from 'vue'
import { ElMessage } from '../statusMessage'
import { ElMessageBox } from 'element-plus'
import { ArrowDown, ArrowUp, Blocks, Download, Plus, RefreshCw, Trash2, Upload } from '@lucide/vue'
import FunctionBlockDiagram from './FunctionBlockDiagram.vue'
import { useUserDisplayName } from '../userDisplay'
import { createClientId } from '../clientId'
import {
  createProgramTemplate,
  createProgramTemplateRevision,
  decideProgramTemplateTask,
  deleteProgramTemplateDraft,
  downloadProgramTemplate,
  getProgramTemplate,
  getProgramTemplateOptions,
  listProgramTemplateTasks,
  listProgramTemplates,
  saveProgramTemplateOptions,
  submitProgramTemplateRevision,
  updateProgramTemplateDraft,
  uploadProgramTemplateFile,
} from '../api'
import type {
  ProgramTemplate,
  ProgramTemplateAttachmentKind,
  ProgramTemplateChecklistKind,
  ProgramTemplateDraftInput,
  ProgramTemplateParameter,
  ProgramTemplateParameterDirection,
  ProgramTemplateOptionCatalog,
  ProgramTemplateRevision,
  ProgramTemplateTask,
  ProgramTemplateTypeOption,
  ProgramTemplateVersionBump,
} from '../types'

const displayUserName = useUserDisplayName()

const props = withDefaults(defineProps<{
  token: string
  username: string
  permissions: string[]
  requestedTemplateId?: string
}>(), { requestedTemplateId: '' })
const emit = defineEmits<{ tasksChanged: [] }>()

type DraftParameter = Omit<ProgramTemplateParameter, 'id'> & { id: string }
type DraftForm = Omit<ProgramTemplateDraftInput, 'parameters'> & { parameters: DraftParameter[] }
type ProgramTemplateListRow = { template: ProgramTemplate; revision: ProgramTemplateRevision }

const loading = ref(false)
const saving = ref(false)
const deletingDraft = ref(false)
const uploadingKind = ref<ProgramTemplateAttachmentKind | null>(null)
const uploadProgress = ref(0)
const templates = ref<ProgramTemplate[]>([])
const tasks = ref<ProgramTemplateTask[]>([])
const search = ref('')
const typeFilter = ref('')
const categoryFilter = ref('')
const platformFilter = ref('')
const vendorFilter = ref('')
const detailOpen = ref(false)
const editorOpen = ref(false)
const versionDialogOpen = ref(false)
const versionBump = ref<ProgramTemplateVersionBump>('Minor')
const selected = ref<ProgramTemplate | null>(null)
const activeRevisionId = ref('')
const editingTemplateId = ref('')
const editingRevision = ref<ProgramTemplateRevision | null>(null)
const tagText = ref('')
const decisionComment = ref('')
const checkedItems = ref<string[]>([])
const optionDialogOpen = ref(false)
const optionSaving = ref(false)
const optionCatalog = ref<ProgramTemplateOptionCatalog>({ categories: [], vendors: [], platforms: [], types: [] })
const optionDraft = ref<ProgramTemplateOptionCatalog>({ categories: [], vendors: [], platforms: [], types: [] })
type OptionSection = 'AssetType' | 'Category' | 'Vendor' | 'Platform'
const optionSections: { value: OptionSection; label: string }[] = [
  { value: 'AssetType', label: '模板类型' },
  { value: 'Category', label: '分类' },
  { value: 'Vendor', label: '厂商' },
  { value: 'Platform', label: '平台' },
]
const optionSection = ref<OptionSection>('AssetType')
const optionNewValue = ref('')
const optionEditingIndex = ref(-1)
const optionEditingValue = ref('')
const packageInput = ref<HTMLInputElement | null>(null)
const evidenceInput = ref<HTMLInputElement | null>(null)

const form = reactive<DraftForm>({
  assetType: 'PlcFunctionBlock',
  name: '',
  category: '',
  description: '',
  vendor: '',
  platform: '',
  softwareVersion: '',
  applicableSeries: '',
  tags: [],
  changeNote: '',
  parameters: [],
})

const canSubmit = computed(() => props.permissions.includes('program-template.submit'))
const canManageOptions = computed(() => props.permissions.includes('program-template.manage'))
const canDeleteActiveDraft = computed(() => activeRevision.value?.state === 'Draft'
  && (activeRevision.value.createdBy.toLocaleLowerCase() === props.username.toLocaleLowerCase()
    || props.permissions.includes('program-template.manage')))
const checklistKindLabels: Record<ProgramTemplateChecklistKind, string> = {
  PlcFunctionBlock: '功能块检查清单', PlcProgram: '整包程序检查清单', HmiTemplate: 'HMI检查清单',
}
const stateLabels: Record<ProgramTemplateRevision['state'], string> = {
  Draft: '草稿', PendingReview: '待审核', PendingApproval: '待批准', Rejected: '已退回',
  Published: '已发布', Superseded: '已替代', Archived: '已停用',
}
const directionLabels: Record<ProgramTemplateParameterDirection, string> = { Input: '输入', Output: '输出', InOut: '双向' }
const stateTagType = (state: ProgramTemplateRevision['state']) => state === 'Published' ? 'success' : state === 'Rejected' || state === 'Archived' ? 'danger' : state === 'Draft' || state === 'Superseded' ? 'info' : 'warning'

function revisionSort(left: ProgramTemplateRevision, right: ProgramTemplateRevision) {
  const version = right.version.localeCompare(left.version, undefined, { numeric: true })
  return version || right.attemptNumber - left.attemptNumber
}

function latestRevision(template: ProgramTemplate) { return [...template.revisions].sort(revisionSort)[0] }

function listRevision(template: ProgramTemplate) {
  // 该模板我这边还有在处理中的版本（草稿/在审/被退回）时优先显示，否则显示已发布版本。
  const inFlight = template.revisions
    .filter(item => item.createdBy.toLocaleLowerCase() === props.username.toLocaleLowerCase()
      && ['Draft', 'PendingReview', 'PendingApproval', 'Rejected'].includes(item.state))
    .sort(revisionSort)[0]
  return inFlight ?? template.revisions.find(item => item.id === template.currentPublishedRevisionId) ?? latestRevision(template)
}

const rows = computed<ProgramTemplateListRow[]>(() => templates.value.map(template => ({ template, revision: listRevision(template) })).filter((row): row is ProgramTemplateListRow => Boolean(row.revision)).filter(row => {
  const text = `${row.template.code} ${row.revision!.name} ${row.revision!.description} ${row.revision!.vendor} ${row.revision!.platform} ${row.revision!.tags.join(' ')}`.toLocaleLowerCase('zh-CN')
  return (!search.value.trim() || text.includes(search.value.trim().toLocaleLowerCase('zh-CN')))
    && (!typeFilter.value || row.template.assetType === typeFilter.value)
    && (!categoryFilter.value || row.revision!.category === categoryFilter.value)
    && (!platformFilter.value || row.revision!.platform === platformFilter.value)
    && (!vendorFilter.value || row.revision!.vendor === vendorFilter.value)
}))
// 选项来源：管理员在“选项维护”里维护的值 + 历史版本已使用的值（保证老数据仍可选）。
function usedValues(pick: (revision: ProgramTemplateRevision) => string) {
  return templates.value.flatMap(template => template.revisions.map(pick)).filter(Boolean)
}
function mergeOptions(maintained: string[], used: string[], current?: string) {
  return [...new Set([...maintained, ...used, (current ?? '').trim()].filter(Boolean))].sort((left, right) => left.localeCompare(right, 'zh-CN'))
}
const categories = computed(() => mergeOptions(optionCatalog.value.categories, usedValues(item => item.category)))
const vendors = computed(() => mergeOptions(optionCatalog.value.vendors, usedValues(item => item.vendor)))
const platforms = computed(() => mergeOptions(optionCatalog.value.platforms, usedValues(item => item.platform)))
const categoryOptions = computed(() => mergeOptions(optionCatalog.value.categories, usedValues(item => item.category), form.category))
const vendorOptions = computed(() => mergeOptions(optionCatalog.value.vendors, usedValues(item => item.vendor), form.vendor))
const platformOptions = computed(() => mergeOptions(optionCatalog.value.platforms, usedValues(item => item.platform), form.platform))
// 服务器尚未升级到带选项维护接口的版本时，退回内置三种模板类型，避免类型下拉为空。
const builtinTypes: ProgramTemplateTypeOption[] = [
  { key: 'PlcFunctionBlock', name: 'PLC功能块', codePrefix: 'PT-FB', checklistKind: 'PlcFunctionBlock' },
  { key: 'PlcProgram', name: 'PLC整包模板', codePrefix: 'PT-PLC', checklistKind: 'PlcProgram' },
  { key: 'HmiTemplate', name: 'HMI模板', codePrefix: 'PT-HMI', checklistKind: 'HmiTemplate' },
]
const maintainedTypes = computed(() => (optionCatalog.value.types?.length ? optionCatalog.value.types : builtinTypes))
const assetTypeOptions = computed(() => {
  const options = [...maintainedTypes.value]
  if (form.assetType && !options.some(item => item.key === form.assetType))
    options.push({ key: form.assetType, name: form.assetType, codePrefix: '', checklistKind: 'PlcProgram' })
  return options
})
const assetTypeLabel = (key: string) => maintainedTypes.value.find(item => item.key === key)?.name ?? key
const maintainedTypeKeys = computed(() => new Set(maintainedTypes.value.map(item => item.key)))
const historicalTypeKeys = computed(() => [...new Set(templates.value.map(template => template.assetType))]
  .filter(key => key && !maintainedTypeKeys.value.has(key)))
const optionPlaceholders: Record<Exclude<OptionSection, 'AssetType'>, string> = {
  Category: '输入分类名称',
  Vendor: '输入厂商名称',
  Platform: '输入平台名称',
}
const optionPlaceholder = computed(() => optionPlaceholders[optionSection.value as Exclude<OptionSection, 'AssetType'>] ?? '输入名称')
const currentOptionValues = computed(() => {
  if (optionSection.value === 'Category') return optionDraft.value.categories
  if (optionSection.value === 'Vendor') return optionDraft.value.vendors
  if (optionSection.value === 'Platform') return optionDraft.value.platforms
  return []
})
const currentUsedValues = computed(() => {
  if (optionSection.value === 'Category') return categories.value
  if (optionSection.value === 'Vendor') return vendors.value
  if (optionSection.value === 'Platform') return platforms.value
  return []
})
const activeRevision = computed(() => selected.value?.revisions.find(item => item.id === activeRevisionId.value)
  ?? selected.value?.revisions.find(item => item.id === selected.value?.currentPublishedRevisionId)
  ?? (selected.value ? latestRevision(selected.value) : undefined))
const activeTask = computed(() => tasks.value.find(item => item.templateId === selected.value?.id && item.revisionId === activeRevision.value?.id))
type ApprovalFlowStep = { key: string; title: string; person: string; result: string; note: string; tone: 'done' | 'active' | 'todo' | 'rejected' }
const approvalFlow = computed<ApprovalFlowStep[]>(() => {
  const revision = activeRevision.value
  if (!revision) return []
  const steps: ApprovalFlowStep[] = [{
    key: 'Submit',
    title: '提交',
    person: displayUserName(revision.createdBy),
    result: dateLabel(revision.submittedAt || revision.createdAt),
    note: '',
    tone: revision.state === 'Draft' ? 'active' : 'done',
  }]
  for (const task of revision.approvalTasks ?? []) {
    const isReview = task.stage === 'Review'
    const decided = Boolean(task.decision)
    const current = !decided
      && ((isReview && revision.state === 'PendingReview') || (!isReview && revision.state === 'PendingApproval'))
    steps.push({
      key: task.stage,
      title: isReview ? '电气组织审核' : '集团标准化批准',
      person: task.decisionBy
        ? displayUserName(task.decisionBy)
        : task.assignee ? displayUserName(task.assignee) : '具“批准程序模板”权限的负责人',
      result: decided
        ? `${task.decision === 'Approved' ? (isReview ? '审核通过' : '批准发布') : '已退回'}${task.decidedAt ? ` · ${dateLabel(task.decidedAt)}` : ''}`
        : '进行中',
      note: task.comment || '',
      tone: task.decision === 'Rejected' ? 'rejected' : decided ? 'done' : current ? 'active' : 'todo',
    })
  }
  return steps
})
const currentReviewer = computed(() => {
  const active = approvalFlow.value.find(step => step.key !== 'Submit' && step.tone === 'active')
  return active ? `${active.title}：${active.person}` : ''
})
const previewParameters = computed<ProgramTemplateParameter[]>(() => form.parameters.map((item, index) => ({ ...item, id: item.id || `draft-${index}`, sortOrder: index })))
const groupedParameters = computed(() => ({
  Input: activeRevision.value?.parameters.filter(item => item.direction === 'Input') ?? [],
  Output: activeRevision.value?.parameters.filter(item => item.direction === 'Output') ?? [],
  InOut: activeRevision.value?.parameters.filter(item => item.direction === 'InOut') ?? [],
}))

async function load() {
  loading.value = true
  try {
    const [published, myTasks, catalog] = await Promise.all([
      listProgramTemplates(props.token),
      listProgramTemplateTasks(props.token),
      getProgramTemplateOptions(props.token).catch(() => optionCatalog.value),
    ])
    // 不再按“标准模板 / 我的提交”分开：已发布模板 + 我提交（含草稿/在审）合并为一个列表。
    const merged = [...published]
    if (canSubmit.value) {
      const mine = await listProgramTemplates(props.token, true)
      const known = new Set(merged.map(template => template.id))
      merged.push(...mine.filter(template => !known.has(template.id)))
    }
    templates.value = merged
    tasks.value = myTasks
    optionCatalog.value = catalog
  } catch (error) {
    ElMessage.error(error instanceof Error ? error.message : '程序模板加载失败')
  } finally {
    loading.value = false
  }
}

async function openOptionMaintenance() {
  optionDraft.value = {
    categories: [...optionCatalog.value.categories],
    vendors: [...optionCatalog.value.vendors],
    platforms: [...optionCatalog.value.platforms],
    types: maintainedTypes.value.map(item => ({ ...item })),
  }
  optionSection.value = 'AssetType'
  optionNewValue.value = ''
  cancelOptionEdit()
  cancelTypeEdit()
  optionDialogOpen.value = true
}

function addOption() {
  const value = optionNewValue.value.trim()
  if (!value || currentOptionValues.value.includes(value)) return
  currentOptionValues.value.push(value)
  optionNewValue.value = ''
}

function removeOption(index: number) {
  currentOptionValues.value.splice(index, 1)
  cancelOptionEdit()
}

function moveOption(index: number, direction: -1 | 1) {
  const values = currentOptionValues.value
  const target = index + direction
  if (target < 0 || target >= values.length) return
  const [moved] = values.splice(index, 1)
  values.splice(target, 0, moved!)
  cancelOptionEdit()
}

function startOptionEdit(index: number) {
  optionEditingIndex.value = index
  optionEditingValue.value = currentOptionValues.value[index] ?? ''
}

function commitOptionEdit() {
  const index = optionEditingIndex.value
  const value = optionEditingValue.value.trim()
  if (index < 0) return
  if (!value) { cancelOptionEdit(); return }
  currentOptionValues.value.splice(index, 1, value)
  cancelOptionEdit()
}

function cancelOptionEdit() {
  optionEditingIndex.value = -1
  optionEditingValue.value = ''
}

const optionTypes = computed(() => optionDraft.value.types ?? [])
const optionTypeEditingKey = ref('')
const optionTypeForm = reactive<{ name: string; codePrefix: string; checklistKind: ProgramTemplateChecklistKind }>({
  name: '', codePrefix: '', checklistKind: 'PlcFunctionBlock',
})
const optionTypeChecklistKinds: ProgramTemplateChecklistKind[] = ['PlcFunctionBlock', 'PlcProgram', 'HmiTemplate']

function startAddType() {
  optionTypeEditingKey.value = '__new__'
  Object.assign(optionTypeForm, { name: '', codePrefix: '', checklistKind: 'PlcFunctionBlock' })
}

function startEditType(option: ProgramTemplateTypeOption) {
  optionTypeEditingKey.value = option.key
  Object.assign(optionTypeForm, { name: option.name, codePrefix: option.codePrefix, checklistKind: option.checklistKind })
}

function cancelTypeEdit() {
  optionTypeEditingKey.value = ''
  Object.assign(optionTypeForm, { name: '', codePrefix: '', checklistKind: 'PlcFunctionBlock' })
}

function commitTypeEdit() {
  const name = optionTypeForm.name.trim()
  const codePrefix = optionTypeForm.codePrefix.trim().toUpperCase()
  if (!name || !codePrefix) {
    ElMessage.warning('模板类型名称和编号前缀都不能为空')
    return
  }
  const editing = optionTypeEditingKey.value
  if (editing === '__new__') {
    optionTypes.value.push({ key: '', name, codePrefix, checklistKind: optionTypeForm.checklistKind })
  } else {
    const index = optionTypes.value.findIndex(item => item.key === editing)
    if (index >= 0) optionTypes.value.splice(index, 1, { key: editing, name, codePrefix, checklistKind: optionTypeForm.checklistKind })
  }
  cancelTypeEdit()
}

function removeType(key: string) {
  const index = optionTypes.value.findIndex(item => item.key === key)
  if (index >= 0) optionTypes.value.splice(index, 1)
  cancelTypeEdit()
}

function moveType(index: number, direction: -1 | 1) {
  const target = index + direction
  if (target < 0 || target >= optionTypes.value.length) return
  const [moved] = optionTypes.value.splice(index, 1)
  optionTypes.value.splice(target, 0, moved!)
  cancelTypeEdit()
}

async function saveOptionMaintenance() {
  optionSaving.value = true
  try {
    optionCatalog.value = await saveProgramTemplateOptions(optionDraft.value, props.token)
    optionDialogOpen.value = false
    ElMessage.success('程序模板选项已保存')
  } catch (error) {
    ElMessage.error(error instanceof Error ? error.message : '程序模板选项保存失败')
  } finally {
    optionSaving.value = false
  }
}

async function openDetail(templateId: string, revisionId?: string) {
  try {
    selected.value = await getProgramTemplate(templateId, props.token)
    const task = tasks.value.find(item => item.templateId === templateId)
    activeRevisionId.value = revisionId || task?.revisionId || selected.value.currentPublishedRevisionId || latestRevision(selected.value)?.id || ''
    checkedItems.value = []
    decisionComment.value = ''
    detailOpen.value = true
  } catch (error) {
    ElMessage.error(error instanceof Error ? error.message : '程序模板详情加载失败')
  }
}

function blankParameter(direction: ProgramTemplateParameterDirection): DraftParameter {
  return { id: createClientId(), direction, sortOrder: form.parameters.length, name: '', dataType: 'BOOL', defaultValue: '', unit: '', description: '' }
}

function resetForm() {
  Object.assign(form, {
    assetType: 'PlcFunctionBlock', name: '', category: '', description: '', vendor: '', platform: '', softwareVersion: '',
    applicableSeries: '', tags: [], changeNote: '', parameters: [blankParameter('Input'), blankParameter('Output')],
  })
  tagText.value = ''
  editingTemplateId.value = ''
  editingRevision.value = null
}

function openCreate() {
  resetForm()
  editorOpen.value = true
}

function copyRevisionToForm(template: ProgramTemplate, revision: ProgramTemplateRevision) {
  editingTemplateId.value = template.id
  editingRevision.value = revision
  Object.assign(form, {
    assetType: template.assetType,
    name: revision.name,
    category: revision.category,
    description: revision.description,
    vendor: revision.vendor,
    platform: revision.platform,
    softwareVersion: revision.softwareVersion,
    applicableSeries: revision.applicableSeries,
    tags: [...revision.tags],
    changeNote: revision.changeNote,
    parameters: revision.parameters.map(item => ({ ...item })),
  })
  tagText.value = revision.tags.join('，')
}

function editActiveDraft() {
  if (!selected.value || !activeRevision.value || activeRevision.value.state !== 'Draft') return
  copyRevisionToForm(selected.value, activeRevision.value)
  detailOpen.value = false
  editorOpen.value = true
}

async function deleteActiveDraft() {
  const revision = activeRevision.value
  const templateId = selected.value?.id
  if (!revision || revision.state !== 'Draft' || !templateId) return
  try {
    await ElMessageBox.confirm('删除后草稿及已上传的受控文件将无法恢复，确认删除吗？', '删除程序模板草稿', {
      type: 'warning', confirmButtonText: '确认删除', cancelButtonText: '取消',
    })
  } catch (error) {
    if (error === 'cancel' || error === 'close') return
    throw error
  }
  deletingDraft.value = true
  try {
    await deleteProgramTemplateDraft(revision.id, revision.rowVersion, props.token)
    await load()
    const refreshed = templates.value.find(item => item.id === templateId)
    if (refreshed) {
      selected.value = refreshed
      activeRevisionId.value = refreshed.currentPublishedRevisionId || latestRevision(refreshed)?.id || ''
    } else {
      detailOpen.value = false
      selected.value = null
      activeRevisionId.value = ''
    }
    ElMessage.success('程序模板草稿已删除')
  } catch (error) {
    ElMessage.error(error instanceof Error ? error.message : '程序模板草稿删除失败')
  } finally {
    deletingDraft.value = false
  }
}

function addParameter(direction: ProgramTemplateParameterDirection) {
  form.parameters.push(blankParameter(direction))
}

function removeParameter(index: number) {
  form.parameters.splice(index, 1)
}

function moveParameter(index: number, delta: number) {
  const target = index + delta
  if (target < 0 || target >= form.parameters.length) return
  const [item] = form.parameters.splice(index, 1)
  form.parameters.splice(target, 0, item!)
}

function normalizedInput(): ProgramTemplateDraftInput {
  return {
    ...form,
    tags: tagText.value.split(/[，,]/).map(item => item.trim()).filter(Boolean),
    parameters: form.parameters.map((item, index) => ({
      direction: item.direction, sortOrder: index, name: item.name, dataType: item.dataType,
      defaultValue: item.defaultValue, unit: item.unit, description: item.description,
    })),
  }
}

async function saveDraft(showMessage = true) {
  saving.value = true
  try {
    if (!editingRevision.value) {
      const created = await createProgramTemplate(normalizedInput(), props.token)
      editingTemplateId.value = created.id
      editingRevision.value = latestRevision(created) ?? null
    } else {
      editingRevision.value = await updateProgramTemplateDraft(editingRevision.value.id, normalizedInput(), editingRevision.value.rowVersion, props.token)
    }
    if (showMessage) ElMessage.success('程序模板草稿已保存')
    await load()
    return editingRevision.value
  } catch (error) {
    ElMessage.error(error instanceof Error ? error.message : '程序模板草稿保存失败')
    return null
  } finally {
    saving.value = false
  }
}

async function handleFile(kind: ProgramTemplateAttachmentKind, event: Event) {
  const input = event.target as HTMLInputElement
  const file = input.files?.[0]
  input.value = ''
  if (!file) return
  let revision = editingRevision.value
  if (!revision) revision = await saveDraft(false)
  if (!revision) return
  uploadingKind.value = kind
  uploadProgress.value = 0
  try {
    editingRevision.value = await uploadProgramTemplateFile(revision.id, kind, file, revision.rowVersion, props.token, value => { uploadProgress.value = value })
    ElMessage.success(kind === 'Package' ? '程序包已上传并校验' : '离线测试证据已上传并校验')
  } catch (error) {
    ElMessage.error(error instanceof Error ? error.message : '程序模板文件上传失败')
  } finally {
    uploadingKind.value = null
  }
}

async function submitDraft() {
  if (!editingRevision.value?.packageSha256) {
    ElMessage.warning('请先上传ZIP或RAR程序包')
    return
  }
  if (!editingRevision.value.evidenceSha256) {
    ElMessage.warning('请先上传离线测试证据')
    return
  }
  const revision = await saveDraft(false)
  if (!revision) return
  try {
    editingRevision.value = await submitProgramTemplateRevision(revision.id, revision.rowVersion, props.token)
    ElMessage.success('程序模板已提交电气组织审核')
    editorOpen.value = false
    await load()
    emit('tasksChanged')
  } catch (error) {
    ElMessage.error(error instanceof Error ? error.message : '程序模板提交失败')
  }
}

async function createNextVersion() {
  if (!selected.value) return
  try {
    const revision = await createProgramTemplateRevision(selected.value.id, versionBump.value, props.token)
    copyRevisionToForm(selected.value, revision)
    versionDialogOpen.value = false
    detailOpen.value = false
    editorOpen.value = true
  } catch (error) {
    ElMessage.error(error instanceof Error ? error.message : '新版本创建失败')
  }
}

async function decide(decision: 'Approved' | 'Rejected') {
  const task = activeTask.value
  if (!task) return
  let comment = decisionComment.value.trim()
  if (decision === 'Rejected' && !comment) {
    try {
      const result = await ElMessageBox.prompt('请填写需要修改的内容。', '退回程序模板', { inputValidator: value => value.trim().length > 0 || '退回意见不能为空' })
      comment = result.value.trim()
    } catch (error) {
      if (error === 'cancel' || error === 'close') return
      throw error
    }
  }
  try {
    await decideProgramTemplateTask(task.id, decision, comment, checkedItems.value, task.rowVersion, props.token)
    ElMessage.success(decision === 'Approved' ? (task.stage === 'Review' ? '审核已通过，进入批准' : '程序模板已批准发布') : '程序模板已退回')
    detailOpen.value = false
    await load()
    emit('tasksChanged')
  } catch (error) {
    ElMessage.error(error instanceof Error ? error.message : '程序模板审批失败')
  }
}

async function downloadCurrent() {
  if (!selected.value) return
  const published = selected.value.revisions.find(item => item.id === selected.value?.currentPublishedRevisionId)
  if (!published?.packageFileName) return
  try {
    await downloadProgramTemplate(selected.value.id, published.packageFileName, props.token)
  } catch (error) {
    ElMessage.error(error instanceof Error ? error.message : '程序模板下载失败')
  }
}

function dateLabel(value?: string | null) { return value ? new Date(value).toLocaleString() : '—' }
function fileSize(value?: number | null) { return value ? `${(value / 1024 / 1024).toFixed(value > 10 * 1024 * 1024 ? 1 : 2)} MB` : '—' }
function ioSummary(revision: ProgramTemplateRevision) {
  const counts = { Input: 0, Output: 0, InOut: 0 }
  revision.parameters.forEach(item => { counts[item.direction]++ })
  return `输入 ${counts.Input} · 输出 ${counts.Output} · 双向 ${counts.InOut}`
}
function handleRowClick(row: ProgramTemplateListRow) { void openDetail(row.template.id, row.revision.id) }

function stateLabel(state: ProgramTemplateRevision['state']) { return stateLabels[state] }

watch(() => form.assetType, value => {
  if (value === 'PlcFunctionBlock' && form.parameters.length === 0) form.parameters.push(blankParameter('Input'), blankParameter('Output'))
})
watch(() => props.requestedTemplateId, async value => { if (value) await openDetail(value) })
onMounted(async () => {
  await load()
  if (props.requestedTemplateId) await openDetail(props.requestedTemplateId)
})
</script>

<template>
  <section class="program-template-library" aria-label="程序模板库">
    <section class="pdm-panel program-template-panel">
      <div class="program-template-toolbar">
        <el-select v-model="typeFilter" clearable filterable placeholder="全部类型" aria-label="模板类型"><el-option v-for="item in maintainedTypes" :key="item.key" :label="item.name" :value="item.key" /></el-select>
        <el-select v-model="categoryFilter" clearable filterable placeholder="全部分类" aria-label="模板分类"><el-option v-for="item in categories" :key="item" :label="item" :value="item" /></el-select>
        <el-select v-model="platformFilter" clearable filterable placeholder="全部平台" aria-label="模板平台"><el-option v-for="item in platforms" :key="item" :label="item" :value="item" /></el-select>
        <el-select v-model="vendorFilter" clearable filterable placeholder="全部厂商" aria-label="模板厂商"><el-option v-for="vendor in vendors" :key="vendor" :label="vendor" :value="vendor" /></el-select>
        <el-input v-model="search" clearable placeholder="搜索编号、名称、平台或标签" aria-label="搜索程序模板" />
        <el-button v-if="canSubmit" class="program-template-upload" type="primary" :icon="Plus" @click="openCreate">上传程序模板</el-button>
        <el-button v-if="canManageOptions" @click="openOptionMaintenance">选项维护</el-button>
        <el-button :icon="RefreshCw" :loading="loading" @click="load">刷新</el-button>
        <span>共 {{ rows.length }} 项</span>
      </div>

      <el-table v-loading="loading" :data="rows" stripe height="100%" row-key="template.id" @row-click="handleRowClick">
        <el-table-column label="编号" width="108"><template #default="{ row }"><code>{{ row.template.code }}</code></template></el-table-column>
        <el-table-column label="程序名称" min-width="140"><template #default="{ row }"><strong class="program-template-name">{{ row.revision.name }}</strong></template></el-table-column>
        <el-table-column label="功能说明" min-width="220"><template #default="{ row }"><span class="program-template-description">{{ row.revision.description || '—' }}</span></template></el-table-column>
        <el-table-column label="类型" width="100"><template #default="{ row }"><el-tag effect="plain">{{ assetTypeLabel(row.template.assetType) }}</el-tag></template></el-table-column>
        <el-table-column label="厂商 / 平台" min-width="150"><template #default="{ row }"><span class="program-template-vendor"><strong>{{ row.revision.vendor }}</strong><span class="program-template-cell-note">{{ row.revision.platform }} · {{ row.revision.softwareVersion }}</span></span></template></el-table-column>
        <el-table-column label="接口 / 适用范围" min-width="170"><template #default="{ row }"><span>{{ row.revision.parameters.length ? ioSummary(row.revision) : row.revision.applicableSeries || '—' }}</span></template></el-table-column>
        <el-table-column label="版本" width="80"><template #default="{ row }"><code>{{ row.revision.version }}</code></template></el-table-column>
        <el-table-column label="提交人" width="90"><template #default="{ row }">{{ displayUserName(row.revision.createdBy) }}</template></el-table-column>
        <el-table-column label="状态" width="75"><template #default="{ row }"><el-tag :type="stateTagType(row.revision.state)">{{ stateLabel(row.revision.state) }}</el-tag></template></el-table-column>
        <el-table-column label="更新时间" width="140"><template #default="{ row }">{{ dateLabel(row.revision.publishedAt || row.revision.submittedAt || row.revision.createdAt) }}</template></el-table-column>
        <el-table-column label="操作" width="64" fixed="right"><template #default="{ row }"><el-button link type="primary" @click.stop="openDetail(row.template.id, row.revision.id)">查看</el-button></template></el-table-column>
      </el-table>
    </section>

    <el-drawer v-model="detailOpen" class="program-template-detail" size="min(1120px, 94vw)" destroy-on-close>
      <template #header>
        <div v-if="selected && activeRevision" class="program-template-detail-title"><Blocks :size="28" /><div><h2>{{ activeRevision.name }}</h2><p>{{ selected.code }} · {{ assetTypeLabel(selected.assetType) }} · {{ activeRevision.version }}<template v-if="currentReviewer"> · 当前 {{ currentReviewer }}</template></p></div><el-tag :type="stateTagType(activeRevision.state)">{{ stateLabels[activeRevision.state] }}</el-tag></div>
      </template>
      <template v-if="selected && activeRevision">
        <section class="program-template-section is-description"><h3>功能说明</h3><p>{{ activeRevision.description }}</p></section>

        <section v-if="activeRevision.parameters.length" class="program-template-section is-interface">
          <h3>输入 / 输出接口</h3>
          <div class="program-parameter-groups">
            <article v-for="direction in (['Input', 'Output', 'InOut'] as const)" :key="direction" :class="`is-${direction.toLowerCase()}`">
              <header>{{ directionLabels[direction] }}参数（{{ groupedParameters[direction].length }}）</header>
              <div v-for="parameter in groupedParameters[direction]" :key="parameter.id" :title="parameter.description || ''"><strong>{{ parameter.name }}</strong><code>{{ parameter.dataType }}</code></div>
              <p v-if="!groupedParameters[direction].length">无</p>
            </article>
          </div>
        </section>

        <section v-if="activeRevision.parameters.length" class="program-template-section is-diagram"><h3>功能块图形化表示</h3><div class="program-template-diagram-scroll"><FunctionBlockDiagram :code="selected.code" :name="activeRevision.name" :version="activeRevision.version" :parameters="activeRevision.parameters" /></div></section>

        <section class="program-template-section is-properties"><h3>属性与受控文件</h3><dl class="program-template-properties"><div><dt>厂商</dt><dd>{{ activeRevision.vendor }}</dd></div><div><dt>平台</dt><dd>{{ activeRevision.platform }}</dd></div><div><dt>软件版本</dt><dd>{{ activeRevision.softwareVersion }}</dd></div><div><dt>适用系列</dt><dd>{{ activeRevision.applicableSeries || '—' }}</dd></div><div><dt>来源公司</dt><dd>{{ selected.originCompanyName || '集团共享' }}</dd></div><div><dt>上传人</dt><dd>{{ displayUserName(activeRevision.createdBy) }}</dd></div><div><dt>ZIP/RAR程序包</dt><dd>{{ activeRevision.packageFileName || '未上传' }} · {{ fileSize(activeRevision.packageFileLength) }}</dd></div><div><dt>离线测试证据</dt><dd>{{ activeRevision.evidenceFileName || '未上传' }}</dd></div><div class="is-wide"><dt>程序包 SHA-256</dt><dd><code>{{ activeRevision.packageSha256 || '—' }}</code></dd></div><div class="is-wide"><dt>版本说明</dt><dd>{{ activeRevision.changeNote }}</dd></div></dl></section>

        <section v-if="activeTask" class="program-template-section program-template-decision is-decision"><h3>{{ activeTask.stage === 'Review' ? '电气组织审核' : '集团标准化批准' }}</h3><el-checkbox-group v-if="activeTask.requiredChecklist.length" v-model="checkedItems"><el-checkbox v-for="item in activeTask.requiredChecklist" :key="item" :label="item">{{ item }}</el-checkbox></el-checkbox-group><el-input v-model="decisionComment" type="textarea" :rows="3" placeholder="审批意见（退回时必填）" /></section>

        <section class="program-template-section is-flow"><h3>审核流程</h3><ol class="program-template-flow"><li v-for="step in approvalFlow" :key="step.key" :class="`is-${step.tone}`"><strong>{{ step.title }}</strong><span>{{ step.person }}</span><small>{{ step.result }}</small><em v-if="step.note">{{ step.note }}</em></li></ol></section>

        <section class="program-template-section is-history"><h3>版本历史</h3><div class="program-template-history"><button v-for="revision in selected.revisions" :key="revision.id" type="button" :class="{ 'is-active': revision.id === activeRevision.id }" @click="activeRevisionId = revision.id"><code>{{ revision.version }}</code><span>第{{ revision.attemptNumber }}次提交</span><el-tag size="small" :type="stateTagType(revision.state)">{{ stateLabels[revision.state] }}</el-tag><small>{{ dateLabel(revision.publishedAt || revision.submittedAt || revision.createdAt) }}</small></button></div></section>
      </template>
      <template #footer>
        <div class="program-template-detail-actions">
          <el-button v-if="canDeleteActiveDraft" type="danger" plain :loading="deletingDraft" @click="deleteActiveDraft">删除草稿</el-button>
          <el-button v-if="activeRevision?.state === 'Draft' && activeRevision.createdBy === username" @click="editActiveDraft">编辑草稿</el-button>
          <el-button v-if="activeRevision?.state === 'Rejected' && activeRevision.createdBy === username && canSubmit && !selected?.revisions.some(item => ['Draft','PendingReview','PendingApproval'].includes(item.state))" @click="createNextVersion">根据退回意见修改</el-button>
          <el-button v-if="selected?.currentPublishedRevisionId && canSubmit && !selected.revisions.some(item => ['Draft','PendingReview','PendingApproval'].includes(item.state))" @click="versionDialogOpen = true">创建新版本</el-button>
          <el-button v-if="activeTask" type="danger" plain @click="decide('Rejected')">退回</el-button>
          <el-button v-if="activeTask" type="primary" @click="decide('Approved')">{{ activeTask.stage === 'Review' ? '审核通过' : '批准发布' }}</el-button>
          <el-button v-if="selected?.currentPublishedRevisionId && !selected.isArchived" type="primary" :icon="Download" @click="downloadCurrent">下载批准版本</el-button>
        </div>
      </template>
    </el-drawer>

    <el-dialog v-model="optionDialogOpen" title="程序模板选项维护" width="min(720px, calc(100vw - 32px))">
      <p class="program-template-option-hint">模板类型、分类、厂商、平台在这里维护好后，上传与编辑草稿时从这些值中选择，不再允许临时输入。</p>
      <el-radio-group v-model="optionSection" class="program-template-option-tabs">
        <el-radio-button v-for="section in optionSections" :key="section.value" :label="section.value">{{ section.label }}</el-radio-button>
      </el-radio-group>
      <div class="program-template-option-list">
        <template v-if="optionSection === 'AssetType'">
          <div v-for="(option, index) in optionTypes" :key="option.key || index" class="program-template-option-row" :class="{ 'is-type-edit': optionTypeEditingKey === option.key }">
            <template v-if="optionTypeEditingKey === option.key">
              <el-input v-model="optionTypeForm.name" size="small" placeholder="类型名称" />
              <el-input v-model="optionTypeForm.codePrefix" size="small" placeholder="编号前缀，如 PT-FB" />
              <el-select v-model="optionTypeForm.checklistKind" size="small"><el-option v-for="value in optionTypeChecklistKinds" :key="value" :label="checklistKindLabels[value]" :value="value" /></el-select>
              <div class="program-template-option-actions"><el-button link type="primary" @click="commitTypeEdit">确定</el-button><el-button link @click="cancelTypeEdit">取消</el-button></div>
            </template>
            <template v-else>
              <strong>{{ option.name }}</strong>
              <small class="program-template-option-used">编号前缀 {{ option.codePrefix }} · {{ checklistKindLabels[option.checklistKind] }} · 存储标识 {{ option.key || '保存时自动生成' }}</small>
              <div class="program-template-option-actions">
                <el-button link :disabled="index === 0" @click="moveType(index, -1)">上移</el-button>
                <el-button link :disabled="index === optionTypes.length - 1" @click="moveType(index, 1)">下移</el-button>
                <el-button link @click="startEditType(option)">编辑</el-button>
                <el-button link type="danger" @click="removeType(option.key)">删除</el-button>
              </div>
            </template>
          </div>
          <div v-if="optionTypeEditingKey === '__new__'" class="program-template-option-row is-new is-type-edit">
            <el-input v-model="optionTypeForm.name" size="small" placeholder="类型名称" />
            <el-input v-model="optionTypeForm.codePrefix" size="small" placeholder="编号前缀，如 PT-SCADA" />
            <el-select v-model="optionTypeForm.checklistKind" size="small"><el-option v-for="value in optionTypeChecklistKinds" :key="value" :label="checklistKindLabels[value]" :value="value" /></el-select>
            <div class="program-template-option-actions"><el-button type="primary" size="small" @click="commitTypeEdit">确认新增</el-button><el-button size="small" @click="cancelTypeEdit">取消</el-button></div>
          </div>
          <div v-else class="program-template-option-row is-new"><el-button type="primary" size="small" @click="startAddType">新增模板类型</el-button></div>
          <small class="program-template-option-used">编号前缀决定新模板编号（如 PT-FB-0001）；检查清单组决定该类模板审核时需要勾选的检查项。历史模板类型未维护：{{ historicalTypeKeys.join('、') || '—' }}</small>
        </template>
        <template v-else>
          <div v-if="!currentOptionValues.length" class="program-template-option-empty">暂无维护项，请在下方新增。</div>
          <div v-for="(item, index) in currentOptionValues" :key="item" class="program-template-option-row">
            <template v-if="optionEditingIndex === index">
              <el-input v-model="optionEditingValue" size="small" :placeholder="optionPlaceholder" @keyup.enter="commitOptionEdit" />
              <div class="program-template-option-actions"><el-button link type="primary" @click="commitOptionEdit">确定</el-button><el-button link @click="cancelOptionEdit">取消</el-button></div>
            </template>
            <template v-else>
              <strong>{{ item }}</strong>
              <div class="program-template-option-actions">
                <el-button link :disabled="index === 0" @click="moveOption(index, -1)">上移</el-button>
                <el-button link :disabled="index === currentOptionValues.length - 1" @click="moveOption(index, 1)">下移</el-button>
                <el-button link @click="startOptionEdit(index)">编辑</el-button>
                <el-button link type="danger" @click="removeOption(index)">删除</el-button>
              </div>
            </template>
          </div>
          <div class="program-template-option-row is-new">
            <el-input v-model="optionNewValue" size="small" :placeholder="optionPlaceholder" @keyup.enter="addOption" />
            <div class="program-template-option-actions"><el-button type="primary" size="small" :disabled="!optionNewValue.trim()" @click="addOption">新增</el-button></div>
          </div>
          <small class="program-template-option-used">历史版本已使用：{{ currentUsedValues.join('、') || '—' }}</small>
        </template>
      </div>
      <template #footer><el-button @click="optionDialogOpen = false">取消</el-button><el-button type="primary" :loading="optionSaving" @click="saveOptionMaintenance">保存</el-button></template>
    </el-dialog>
    <el-drawer v-model="editorOpen" class="program-template-editor" :title="editingRevision ? `编辑 ${editingRevision.version} 草稿` : '上传程序模板'" size="min(1040px, 96vw)" destroy-on-close>
      <el-form label-position="top" class="program-template-form">
        <section><h3>1. 基本信息</h3><div class="program-template-form-grid"><el-form-item label="模板类型"><el-select v-model="form.assetType" :disabled="!!editingRevision"><el-option v-for="item in assetTypeOptions" :key="item.key" :label="item.name" :value="item.key" /></el-select></el-form-item><el-form-item label="模板名称"><el-input v-model="form.name" /></el-form-item><el-form-item label="分类"><el-select v-model="form.category" filterable clearable placeholder="从维护好的分类中选择"><el-option v-for="item in categoryOptions" :key="item" :label="item" :value="item" /></el-select></el-form-item><el-form-item label="厂商"><el-select v-model="form.vendor" filterable clearable placeholder="从维护好的厂商中选择"><el-option v-for="item in vendorOptions" :key="item" :label="item" :value="item" /></el-select></el-form-item><el-form-item label="平台"><el-select v-model="form.platform" filterable clearable placeholder="从维护好的平台中选择"><el-option v-for="item in platformOptions" :key="item" :label="item" :value="item" /></el-select></el-form-item><el-form-item label="软件版本"><el-input v-model="form.softwareVersion" /></el-form-item><el-form-item label="适用系列"><el-input v-model="form.applicableSeries" /></el-form-item><el-form-item label="标签"><el-input v-model="tagText" placeholder="多个标签使用逗号分隔" /></el-form-item><el-form-item class="is-half" label="功能说明"><el-input v-model="form.description" type="textarea" :rows="2" /></el-form-item><el-form-item class="is-half" label="版本说明"><el-input v-model="form.changeNote" type="textarea" :rows="2" /></el-form-item></div></section>

        <section><h3>2. 受控文件</h3><div class="program-template-upload-grid"><article><input ref="packageInput" type="file" accept=".zip,.rar" hidden @change="handleFile('Package', $event)" /><Upload :size="22" /><strong>ZIP/RAR程序包</strong><span>{{ editingRevision?.packageFileName || '支持 ZIP、RAR' }}</span><el-button :loading="uploadingKind === 'Package'" :disabled="uploadingKind !== null" @click="packageInput?.click()">{{ editingRevision?.packageFileName ? '重新上传' : '选择程序包' }}</el-button></article><article><input ref="evidenceInput" type="file" accept=".pdf,.doc,.docx,.xls,.xlsx,.png,.jpg,.jpeg" hidden @change="handleFile('TestEvidence', $event)" /><Upload :size="22" /><strong>离线测试证据</strong><span>{{ editingRevision?.evidenceFileName || 'PDF、Word、Excel、PNG或JPG' }}</span><el-button :loading="uploadingKind === 'TestEvidence'" :disabled="uploadingKind !== null" @click="evidenceInput?.click()">{{ editingRevision?.evidenceFileName ? '重新上传' : '选择测试证据' }}</el-button></article></div><el-progress v-if="uploadingKind" :percentage="uploadProgress" /></section>

        <section><div class="program-template-interface-heading"><div><h3>3. 接口定义</h3><p>PLC功能块为必填；其他模板可按需维护，详情图由这些数据自动生成。</p></div><div><el-button @click="addParameter('Input')">+ 输入</el-button><el-button @click="addParameter('Output')">+ 输出</el-button><el-button @click="addParameter('InOut')">+ 双向</el-button></div></div><div class="program-template-interface-layout"><div class="program-template-parameter-editor"><div v-for="(parameter, index) in form.parameters" :key="parameter.id" class="program-template-parameter-row"><el-select v-model="parameter.direction" aria-label="接口方向"><el-option v-for="(label, value) in directionLabels" :key="value" :label="label" :value="value" /></el-select><el-input v-model="parameter.name" placeholder="参数名" /><el-input v-model="parameter.dataType" placeholder="数据类型" /><el-input v-model="parameter.defaultValue" placeholder="默认值" /><el-input v-model="parameter.description" placeholder="说明" /><el-button-group><el-button :icon="ArrowUp" :disabled="index === 0" aria-label="上移参数" @click="moveParameter(index, -1)" /><el-button :icon="ArrowDown" :disabled="index === form.parameters.length - 1" aria-label="下移参数" @click="moveParameter(index, 1)" /><el-button type="danger" plain :icon="Trash2" aria-label="删除参数" @click="removeParameter(index)" /></el-button-group></div><el-empty v-if="!form.parameters.length" description="当前没有接口定义" :image-size="56" /></div><div v-if="previewParameters.length" class="program-template-live-preview"><FunctionBlockDiagram code="预览" :name="form.name || '未命名功能块'" :version="editingRevision?.version || 'v1.0.0'" :parameters="previewParameters" /></div></div></section>
      </el-form>
      <template #footer><el-button @click="editorOpen = false">取消</el-button><el-button :loading="saving" @click="saveDraft()">保存草稿</el-button><el-button type="primary" :loading="saving" @click="submitDraft">提交审核</el-button></template>
    </el-drawer>

    <el-dialog v-model="versionDialogOpen" title="创建新版本" width="430px"><el-form label-position="top"><el-form-item label="变更级别"><el-radio-group v-model="versionBump"><el-radio-button label="Major">重大变更</el-radio-button><el-radio-button label="Minor">功能变更</el-radio-button><el-radio-button label="Patch">修正变更</el-radio-button></el-radio-group></el-form-item><p class="program-template-version-hint">系统将根据当前批准版本自动生成新的语义版本号。</p></el-form><template #footer><el-button @click="versionDialogOpen = false">取消</el-button><el-button type="primary" @click="createNextVersion">创建草稿</el-button></template></el-dialog>
  </section>
</template>

<style scoped>
.program-template-library { min-height: 0; height: 100%; display: flex; flex-direction: column; gap: 12px; }
.program-template-panel { min-height: 0; flex: 1; display: flex; flex-direction: column; overflow: hidden; padding: 0; }.program-template-toolbar { min-height: 50px; display: grid; grid-template-columns: 128px 128px 128px 128px minmax(160px,1fr) 150px auto auto auto; align-items: center; gap: 9px; padding: 8px 12px; border-bottom: 1px solid var(--pdm-border); }.program-template-toolbar > span { color: var(--pdm-muted); font-size: 10px; white-space: nowrap; }.program-template-toolbar .program-template-upload { width: 150px; }
.program-template-panel :deep(.el-table__body),.program-template-panel :deep(.el-table__body .cell),.program-template-panel :deep(.el-table__body code),.program-template-panel :deep(.el-table__body .el-tag),.program-template-panel :deep(.el-table__body .el-button) { font-size: 12px; }.program-template-name,.program-template-description,.program-template-cell-note { min-width: 0; display: block; overflow: hidden; font-size: 12px; line-height: 20px; text-overflow: ellipsis; white-space: nowrap; }.program-template-description,.program-template-cell-note { color: var(--pdm-muted); }.program-template-vendor { min-width: 0; display: flex; align-items: center; gap: 5px; overflow: hidden; white-space: nowrap; }.program-template-vendor strong { flex: 0 0 auto; font-size: 12px; }.program-template-vendor .program-template-cell-note { display: inline; }
.program-template-detail-title { min-width: 0; display: flex; align-items: center; gap: 10px; }.program-template-detail-title div { min-width: 0; flex: 1; }.program-template-detail-title h2 { margin: 0; overflow: hidden; font-size: 16px; text-overflow: ellipsis; white-space: nowrap; }.program-template-detail-title p { margin: 3px 0 0; color: var(--pdm-muted); font-size: 10px; }
.program-template-section { padding: 2px 0 18px; }.program-template-section + .program-template-section { border-top: 1px solid var(--pdm-border-soft); padding-top: 16px; }.program-template-section h3 { display: flex; align-items: center; gap: 8px; margin: 0 0 10px; color: var(--pdm-muted); font-size: 11px; font-weight: 600; }.program-template-section h3::after { height: 1px; flex: 1; background: var(--pdm-border-soft); content: ''; }.program-template-section > p { margin: 0; color: var(--pdm-text-soft); line-height: 1.4; }
.program-parameter-groups { display: grid; grid-template-columns: repeat(3,minmax(0,1fr)); gap: 10px; }.program-parameter-groups article { overflow: hidden; border: 1px solid var(--pdm-border); border-radius: 8px; }.program-parameter-groups article header { padding: 9px 11px; background: #eef5ff; color: var(--pdm-blue); font-size: 10px; font-weight: 600; }.program-parameter-groups article.is-input header { background: #e8f8f5; color: var(--pdm-green); }.program-parameter-groups article.is-output header { background: #fff4e5; color: var(--pdm-orange); }.program-parameter-groups article.is-inout header { background: #f1edff; color: var(--pdm-purple); }.program-parameter-groups article > div { min-height: 34px; display: flex; align-items: center; justify-content: space-between; gap: 8px; padding: 6px 10px; border-top: 1px solid var(--pdm-border-soft); }.program-parameter-groups article strong { overflow: hidden; font-size: 10px; text-overflow: ellipsis; white-space: nowrap; }.program-parameter-groups article code { color: var(--pdm-muted); font-size: 10px; }.program-parameter-groups article > p { margin: 0; padding: 12px; color: var(--pdm-muted); text-align: center; }
.program-template-diagram-scroll { overflow-x: auto; border: 1px solid var(--pdm-border); border-radius: 9px; background: #fbfdff; padding: 8px 12px; }
.program-template-properties { display: grid; grid-template-columns: repeat(2,minmax(0,1fr)); gap: 0 20px; margin: 0; }.program-template-properties > div { min-width: 0; display: grid; grid-template-columns: 90px minmax(0,1fr); padding: 7px 0; border-bottom: 1px solid var(--pdm-border-soft); }.program-template-properties .is-wide { grid-column: 1/-1; }.program-template-properties dt { color: var(--pdm-muted); }.program-template-properties dd { min-width: 0; margin: 0; overflow-wrap: anywhere; }.program-template-properties code { font-size: 10px; }
.program-template-decision :deep(.el-checkbox-group) { display: grid; gap: 8px; margin-bottom: 12px; }.program-template-decision :deep(.el-checkbox) { height: auto; white-space: normal; }
.program-template-history { display: grid; gap: 6px; }.program-template-history button { display: grid; grid-template-columns: 75px 90px 90px 1fr; align-items: center; gap: 8px; min-height: 38px; border: 1px solid var(--pdm-border); border-radius: 6px; padding: 5px 9px; background: white; text-align: left; cursor: pointer; }.program-template-history button.is-active,.program-template-history button:hover { border-color: var(--pdm-blue); background: var(--pdm-blue-soft); }.program-template-history small { color: var(--pdm-muted); text-align: right; }
.program-template-detail-actions { width: 100%; display: flex; justify-content: flex-end; gap: 8px; }
.program-template-option-hint { margin: 0 0 10px; color: var(--pdm-muted); font-size: 12px; }
.program-template-option-used { display: block; margin-top: 4px; color: var(--pdm-muted); font-size: 11px; }
.program-template-option-tabs { margin-bottom: 10px; }
.program-template-option-list { display: grid; gap: 6px; max-height: 52vh; overflow: auto; }
.program-template-option-row { min-height: 34px; display: grid; grid-template-columns: minmax(0,1fr) auto; align-items: center; gap: 8px; border: 1px solid var(--pdm-border); border-radius: 6px; padding: 5px 9px; }
.program-template-option-row strong { min-width: 0; overflow: hidden; font-size: 12px; text-overflow: ellipsis; white-space: nowrap; }
.program-template-option-row.is-new { border-style: dashed; }
.program-template-option-row .program-template-option-used { grid-column: 1; margin: 0; }
.program-template-option-actions { display: flex; align-items: center; gap: 4px; }
.program-template-option-empty { padding: 12px; color: var(--pdm-muted); font-size: 12px; text-align: center; }
.program-template-option-row.is-type-edit { grid-template-columns: minmax(0,1.2fr) minmax(0,1fr) minmax(0,1fr) auto; }
.program-template-flow { display: grid; grid-auto-flow: column; grid-auto-columns: minmax(0,1fr); gap: 8px; margin: 0; padding: 0; list-style: none; }
.program-template-flow li { min-width: 0; display: grid; gap: 2px; border: 1px solid var(--pdm-border); border-left: 3px solid var(--pdm-border); border-radius: 6px; padding: 6px 9px; }
.program-template-flow li.is-done { border-left-color: var(--pdm-green); }
.program-template-flow li.is-active { border-left-color: var(--pdm-blue); background: var(--pdm-blue-soft); }
.program-template-flow li.is-rejected { border-left-color: var(--pdm-orange); }
.program-template-flow strong,.program-template-flow span,.program-template-flow small,.program-template-flow em { min-width: 0; overflow: hidden; font-size: 12px; text-overflow: ellipsis; white-space: nowrap; }
.program-template-flow span,.program-template-flow small { color: var(--pdm-muted); }
.program-template-flow em { color: var(--pdm-orange); font-style: normal; }
:global(.program-template-detail) { font-size: 12px; }
:global(.program-template-detail .el-drawer__header) { min-height: 52px; margin-bottom: 0; padding: 0 14px; }
:global(.program-template-detail .el-drawer__body) { min-height: 0; display: grid; grid-template-columns: minmax(0,.86fr) minmax(0,1.14fr); grid-template-rows: auto auto auto auto; align-content: start; gap: 8px 14px; overflow: hidden; padding: 8px 12px; }
:global(.program-template-detail .el-drawer__footer) { padding: 7px 12px; }
:global(.program-template-detail .program-template-section),:global(.program-template-detail .program-template-section + .program-template-section) { min-width: 0; min-height: 0; overflow: hidden; border-top: 0; padding: 0; }
:global(.program-template-detail .program-template-section h3) { margin-bottom: 6px; font-size: 12px; }
:global(.program-template-detail .program-template-section > p) { font-size: 12px; line-height: 1.4; }
:global(.program-template-detail .is-description) { grid-column: 1; grid-row: 1; }
:global(.program-template-detail .is-properties) { grid-column: 2; grid-row: 1; }
:global(.program-template-detail .is-interface) { grid-column: 1; grid-row: 2; }
:global(.program-template-detail .is-diagram) { grid-column: 2; grid-row: 2; align-self: start; }
:global(.program-template-detail .is-flow) { grid-column: 1/-1; grid-row: 3; }
:global(.program-template-detail .is-history) { grid-column: 1/-1; grid-row: 4; }
:global(.program-template-detail .is-decision) { grid-column: 1/-1; grid-row: 5; }
:global(.program-template-detail .program-parameter-groups) { gap: 6px; }
:global(.program-template-detail .program-parameter-groups article header) { padding: 6px 8px; font-size: 12px; }
:global(.program-template-detail .program-parameter-groups article > div) { min-height: 28px; padding: 3px 8px; }
:global(.program-template-detail .program-parameter-groups article strong),:global(.program-template-detail .program-parameter-groups article code) { font-size: 12px; }
:global(.program-template-detail .program-template-diagram-scroll) { min-height: 0; overflow: hidden; padding: 6px; }
:global(.program-template-detail .program-template-diagram-scroll .program-block-diagram) { min-width: 0; width: 100%; height: auto; }
:global(.program-template-detail .program-template-properties) { gap: 0 12px; }
:global(.program-template-detail .program-template-properties > div) { grid-template-columns: 78px minmax(0,1fr); min-height: 29px; align-items: center; padding: 3px 0; }
:global(.program-template-detail .program-template-properties code) { font-size: 12px; }
:global(.program-template-detail .program-template-history) { gap: 4px; }
:global(.program-template-detail .program-template-history button) { grid-template-columns: 65px 75px 72px 1fr; min-height: 30px; padding: 3px 7px; font-size: 12px; }
.program-template-editor { font-size: 12px; }.program-template-editor :deep(.el-drawer__header) { min-height: 44px; margin-bottom: 0; padding: 0 16px; }.program-template-editor :deep(.el-drawer__body) { padding: 8px 12px; }.program-template-editor :deep(.el-drawer__footer) { padding: 7px 12px; }.program-template-editor :deep(.el-input__inner),.program-template-editor :deep(.el-select__selected-item),.program-template-editor :deep(.el-button) { font-size: 12px; }
.program-template-form { display: grid; gap: 8px; }.program-template-form > section { padding: 9px 11px; border: 1px solid var(--pdm-border); border-radius: 8px; }.program-template-form h3 { margin: 0 0 7px; font-size: 12px; }.program-template-form :deep(.el-form-item) { margin-bottom: 7px; }.program-template-form :deep(.el-form-item__label) { height: 19px; padding-bottom: 2px; font-size: 12px; line-height: 17px; }.program-template-form-grid { display: grid; grid-template-columns: repeat(4,minmax(0,1fr)); gap: 0 8px; }.program-template-form-grid > * { grid-column: span 1; }.program-template-form-grid .is-half { grid-column: span 2; }.program-template-form-grid .is-wide { grid-column: 1/-1; }
.program-template-form :deep(.el-input__inner),.program-template-form :deep(.el-select__selected-item),.program-template-form :deep(.el-button) { font-size: 12px; }
.program-template-upload-grid { display: grid; grid-template-columns: repeat(2,minmax(0,1fr)); gap: 8px; }.program-template-upload-grid article { min-height: 70px; display: grid; grid-template-columns: auto 1fr auto; grid-template-rows: auto auto; align-items: center; gap: 3px 8px; padding: 7px 10px; border: 1px dashed #adc2dc; border-radius: 8px; background: #f8fbff; }.program-template-upload-grid article svg { grid-row: 1/3; color: var(--pdm-blue); }.program-template-upload-grid article span { color: var(--pdm-muted); font-size: 12px; }.program-template-upload-grid article .el-button { grid-column: 3; grid-row: 1/3; }
.program-template-interface-heading { display: flex; align-items: center; justify-content: space-between; gap: 10px; }.program-template-interface-heading h3 { margin-bottom: 2px; }.program-template-interface-heading p { margin: 0; color: var(--pdm-muted); font-size: 12px; }.program-template-interface-layout { display: grid; grid-template-columns: minmax(570px,1.15fr) minmax(360px,.85fr); gap: 10px; margin-top: 7px; }.program-template-parameter-editor { min-width: 0; display: grid; align-content: start; gap: 5px; }.program-template-parameter-row { display: grid; grid-template-columns: 76px minmax(90px,1fr) 90px 76px minmax(110px,1fr) 86px; grid-template-areas: "direction name type default description actions"; gap: 4px; padding: 4px; border: 1px solid var(--pdm-border-soft); border-radius: 7px; background: #fbfdff; }.program-template-parameter-row>*:nth-child(1){grid-area:direction}.program-template-parameter-row>*:nth-child(2){grid-area:name}.program-template-parameter-row>*:nth-child(3){grid-area:type}.program-template-parameter-row>*:nth-child(4){grid-area:default}.program-template-parameter-row>*:nth-child(5){grid-area:description}.program-template-parameter-row>*:nth-child(6){grid-area:actions;align-self:center}.program-template-parameter-row :deep(.el-button-group){display:flex;flex-wrap:nowrap}.program-template-parameter-row :deep(.el-button-group .el-button){width:28px;float:none;margin-left:0;padding:0}.program-template-live-preview { align-self: start; overflow: hidden; border: 1px solid var(--pdm-border); border-radius: 8px; background: #fbfdff; padding: 4px; }.program-template-live-preview :deep(.program-block-diagram){min-width:0}.program-template-version-hint { margin: 0; color: var(--pdm-muted); font-size: 10px; }
@media(max-width:1000px){.program-template-toolbar{grid-template-columns:repeat(2,minmax(0,1fr))}.program-template-toolbar .el-select{width:100%}.program-template-toolbar .el-input{grid-column:1/-1}.program-template-interface-layout{grid-template-columns:1fr}.program-template-form-grid{grid-template-columns:repeat(4,minmax(0,1fr))}.program-template-form-grid>*{grid-column:span 2}.program-template-form-grid .is-half{grid-column:span 2}.program-template-form-grid .is-wide{grid-column:1/-1}}
@media(max-width:680px){.program-parameter-groups,.program-template-upload-grid,.program-template-form-grid{grid-template-columns:1fr}.program-template-form-grid>*,.program-template-form-grid .is-half,.program-template-form-grid .is-wide{grid-column:auto}.program-template-properties{grid-template-columns:1fr}.program-template-properties .is-wide{grid-column:auto}.program-template-parameter-row{grid-template-columns:84px minmax(120px,1fr) 90px auto;grid-template-areas:"direction name type actions" "default description description actions"}.program-template-history button{grid-template-columns:65px 75px 80px}.program-template-history small{display:none}}
</style>
