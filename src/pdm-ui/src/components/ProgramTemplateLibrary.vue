<script setup lang="ts">
import { computed, onMounted, reactive, ref, watch } from 'vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import { ArrowDown, ArrowUp, Blocks, Download, Plus, RefreshCw, Trash2, Upload } from '@lucide/vue'
import FunctionBlockDiagram from './FunctionBlockDiagram.vue'
import { useUserDisplayName } from '../userDisplay'
import {
  createProgramTemplate,
  createProgramTemplateRevision,
  decideProgramTemplateTask,
  downloadProgramTemplate,
  getProgramTemplate,
  listProgramTemplateTasks,
  listProgramTemplates,
  submitProgramTemplateRevision,
  updateProgramTemplateDraft,
  uploadProgramTemplateFile,
} from '../api'
import type {
  ProgramTemplate,
  ProgramTemplateAssetType,
  ProgramTemplateAttachmentKind,
  ProgramTemplateDraftInput,
  ProgramTemplateParameter,
  ProgramTemplateParameterDirection,
  ProgramTemplateRevision,
  ProgramTemplateTask,
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

type ListMode = 'published' | 'mine'
type DraftParameter = Omit<ProgramTemplateParameter, 'id'> & { id: string }
type DraftForm = Omit<ProgramTemplateDraftInput, 'parameters'> & { parameters: DraftParameter[] }
type ProgramTemplateListRow = { template: ProgramTemplate; revision: ProgramTemplateRevision }

const mode = ref<ListMode>('published')
const loading = ref(false)
const saving = ref(false)
const uploadingKind = ref<ProgramTemplateAttachmentKind | null>(null)
const uploadProgress = ref(0)
const templates = ref<ProgramTemplate[]>([])
const tasks = ref<ProgramTemplateTask[]>([])
const search = ref('')
const typeFilter = ref<ProgramTemplateAssetType | ''>('')
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
const assetLabels: Record<ProgramTemplateAssetType, string> = {
  PlcFunctionBlock: 'PLC功能块', PlcProgram: 'PLC整包模板', HmiTemplate: 'HMI模板',
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
  if (mode.value === 'published') return template.revisions.find(item => item.id === template.currentPublishedRevisionId) ?? latestRevision(template)
  return template.revisions
    .filter(item => item.createdBy.toLocaleLowerCase() === props.username.toLocaleLowerCase())
    .sort(revisionSort)[0] ?? latestRevision(template)
}

const rows = computed<ProgramTemplateListRow[]>(() => templates.value.map(template => ({ template, revision: listRevision(template) })).filter((row): row is ProgramTemplateListRow => Boolean(row.revision)).filter(row => {
  const text = `${row.template.code} ${row.revision!.name} ${row.revision!.description} ${row.revision!.vendor} ${row.revision!.platform} ${row.revision!.tags.join(' ')}`.toLocaleLowerCase('zh-CN')
  return (!search.value.trim() || text.includes(search.value.trim().toLocaleLowerCase('zh-CN')))
    && (!typeFilter.value || row.template.assetType === typeFilter.value)
    && (!vendorFilter.value || row.revision!.vendor === vendorFilter.value)
}))
const vendors = computed(() => [...new Set(templates.value.flatMap(template => template.revisions.map(item => item.vendor)).filter(Boolean))].sort())
const activeRevision = computed(() => selected.value?.revisions.find(item => item.id === activeRevisionId.value)
  ?? selected.value?.revisions.find(item => item.id === selected.value?.currentPublishedRevisionId)
  ?? (selected.value ? latestRevision(selected.value) : undefined))
const activeTask = computed(() => tasks.value.find(item => item.templateId === selected.value?.id && item.revisionId === activeRevision.value?.id))
const previewParameters = computed<ProgramTemplateParameter[]>(() => form.parameters.map((item, index) => ({ ...item, id: item.id || `draft-${index}`, sortOrder: index })))
const groupedParameters = computed(() => ({
  Input: activeRevision.value?.parameters.filter(item => item.direction === 'Input') ?? [],
  Output: activeRevision.value?.parameters.filter(item => item.direction === 'Output') ?? [],
  InOut: activeRevision.value?.parameters.filter(item => item.direction === 'InOut') ?? [],
}))

async function load() {
  loading.value = true
  try {
    [templates.value, tasks.value] = await Promise.all([listProgramTemplates(props.token, mode.value === 'mine'), listProgramTemplateTasks(props.token)])
  } catch (error) {
    ElMessage.error(error instanceof Error ? error.message : '程序模板加载失败')
  } finally {
    loading.value = false
  }
}

async function switchMode(value: ListMode) {
  mode.value = value
  search.value = ''
  typeFilter.value = ''
  vendorFilter.value = ''
  await load()
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
  return { id: crypto.randomUUID(), direction, sortOrder: form.parameters.length, name: '', dataType: 'BOOL', defaultValue: '', unit: '', description: '' }
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
    ElMessage.success(kind === 'Package' ? 'ZIP程序包已上传并校验' : '离线测试证据已上传并校验')
  } catch (error) {
    ElMessage.error(error instanceof Error ? error.message : '程序模板文件上传失败')
  } finally {
    uploadingKind.value = null
  }
}

async function submitDraft() {
  if (!editingRevision.value?.packageSha256) {
    ElMessage.warning('请先上传ZIP程序包')
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
    mode.value = 'mine'
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
function assetLabel(assetType: ProgramTemplateAssetType) { return assetLabels[assetType] }
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
        <div class="program-template-tabs" role="tablist" aria-label="程序模板视图">
          <button type="button" role="tab" :aria-selected="mode === 'published'" @click="switchMode('published')">标准模板</button>
          <button v-if="canSubmit" type="button" role="tab" :aria-selected="mode === 'mine'" @click="switchMode('mine')">我的提交</button>
        </div>
        <el-select v-model="typeFilter" clearable placeholder="全部类型" aria-label="模板类型"><el-option v-for="(label, value) in assetLabels" :key="value" :label="label" :value="value" /></el-select>
        <el-select v-model="vendorFilter" clearable filterable placeholder="全部厂商" aria-label="模板厂商"><el-option v-for="vendor in vendors" :key="vendor" :label="vendor" :value="vendor" /></el-select>
        <el-input v-model="search" clearable placeholder="搜索编号、名称、平台或标签" aria-label="搜索程序模板" />
        <el-button v-if="canSubmit" type="primary" :icon="Plus" @click="openCreate">上传程序模板</el-button>
        <el-button :icon="RefreshCw" :loading="loading" @click="load">刷新</el-button>
        <span>共 {{ rows.length }} 项</span>
      </div>

      <el-table v-loading="loading" :data="rows" stripe height="100%" row-key="template.id" @row-click="handleRowClick">
        <el-table-column label="编号" width="108"><template #default="{ row }"><code>{{ row.template.code }}</code></template></el-table-column>
        <el-table-column label="程序名称" min-width="140"><template #default="{ row }"><strong class="program-template-name">{{ row.revision.name }}</strong></template></el-table-column>
        <el-table-column label="功能说明" min-width="220"><template #default="{ row }"><span class="program-template-description">{{ row.revision.description || '—' }}</span></template></el-table-column>
        <el-table-column label="类型" width="100"><template #default="{ row }"><el-tag effect="plain">{{ assetLabel(row.template.assetType) }}</el-tag></template></el-table-column>
        <el-table-column label="厂商 / 平台" min-width="150"><template #default="{ row }"><span class="program-template-vendor"><strong>{{ row.revision.vendor }}</strong><span class="program-template-cell-note">{{ row.revision.platform }} · {{ row.revision.softwareVersion }}</span></span></template></el-table-column>
        <el-table-column label="接口 / 适用范围" min-width="170"><template #default="{ row }"><span>{{ row.revision.parameters.length ? ioSummary(row.revision) : row.revision.applicableSeries || '—' }}</span></template></el-table-column>
        <el-table-column label="版本" width="80"><template #default="{ row }"><code>{{ row.revision.version }}</code></template></el-table-column>
        <el-table-column label="状态" width="75"><template #default="{ row }"><el-tag :type="stateTagType(row.revision.state)">{{ stateLabel(row.revision.state) }}</el-tag></template></el-table-column>
        <el-table-column label="更新时间" width="140"><template #default="{ row }">{{ dateLabel(row.revision.publishedAt || row.revision.submittedAt || row.revision.createdAt) }}</template></el-table-column>
        <el-table-column label="操作" width="64" fixed="right"><template #default="{ row }"><el-button link type="primary" @click.stop="openDetail(row.template.id, row.revision.id)">查看</el-button></template></el-table-column>
      </el-table>
    </section>

    <el-drawer v-model="detailOpen" class="program-template-detail" size="min(1120px, 94vw)" destroy-on-close>
      <template #header>
        <div v-if="selected && activeRevision" class="program-template-detail-title"><Blocks :size="28" /><div><h2>{{ activeRevision.name }}</h2><p>{{ selected.code }} · {{ assetLabels[selected.assetType] }} · {{ activeRevision.version }}</p></div><el-tag :type="stateTagType(activeRevision.state)">{{ stateLabels[activeRevision.state] }}</el-tag></div>
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

        <section class="program-template-section is-properties"><h3>属性与受控文件</h3><dl class="program-template-properties"><div><dt>厂商</dt><dd>{{ activeRevision.vendor }}</dd></div><div><dt>平台</dt><dd>{{ activeRevision.platform }}</dd></div><div><dt>软件版本</dt><dd>{{ activeRevision.softwareVersion }}</dd></div><div><dt>适用系列</dt><dd>{{ activeRevision.applicableSeries || '—' }}</dd></div><div><dt>来源公司</dt><dd>{{ selected.originCompanyName || '集团共享' }}</dd></div><div><dt>上传人</dt><dd>{{ displayUserName(activeRevision.createdBy) }}</dd></div><div><dt>ZIP程序包</dt><dd>{{ activeRevision.packageFileName || '未上传' }} · {{ fileSize(activeRevision.packageFileLength) }}</dd></div><div><dt>离线测试证据</dt><dd>{{ activeRevision.evidenceFileName || '未上传' }}</dd></div><div class="is-wide"><dt>程序包 SHA-256</dt><dd><code>{{ activeRevision.packageSha256 || '—' }}</code></dd></div><div class="is-wide"><dt>版本说明</dt><dd>{{ activeRevision.changeNote }}</dd></div></dl></section>

        <section v-if="activeTask" class="program-template-section program-template-decision is-decision"><h3>{{ activeTask.stage === 'Review' ? '电气组织审核' : '集团标准化批准' }}</h3><el-checkbox-group v-if="activeTask.requiredChecklist.length" v-model="checkedItems"><el-checkbox v-for="item in activeTask.requiredChecklist" :key="item" :label="item">{{ item }}</el-checkbox></el-checkbox-group><el-input v-model="decisionComment" type="textarea" :rows="3" placeholder="审批意见（退回时必填）" /></section>

        <section class="program-template-section is-history"><h3>版本历史</h3><div class="program-template-history"><button v-for="revision in selected.revisions" :key="revision.id" type="button" :class="{ 'is-active': revision.id === activeRevision.id }" @click="activeRevisionId = revision.id"><code>{{ revision.version }}</code><span>第{{ revision.attemptNumber }}次提交</span><el-tag size="small" :type="stateTagType(revision.state)">{{ stateLabels[revision.state] }}</el-tag><small>{{ dateLabel(revision.publishedAt || revision.submittedAt || revision.createdAt) }}</small></button></div></section>
      </template>
      <template #footer>
        <div class="program-template-detail-actions">
          <el-button v-if="activeRevision?.state === 'Draft' && activeRevision.createdBy === username" @click="editActiveDraft">编辑草稿</el-button>
          <el-button v-if="activeRevision?.state === 'Rejected' && activeRevision.createdBy === username && canSubmit && !selected?.revisions.some(item => ['Draft','PendingReview','PendingApproval'].includes(item.state))" @click="createNextVersion">根据退回意见修改</el-button>
          <el-button v-if="selected?.currentPublishedRevisionId && canSubmit && !selected.revisions.some(item => ['Draft','PendingReview','PendingApproval'].includes(item.state))" @click="versionDialogOpen = true">创建新版本</el-button>
          <el-button v-if="activeTask" type="danger" plain @click="decide('Rejected')">退回</el-button>
          <el-button v-if="activeTask" type="primary" @click="decide('Approved')">{{ activeTask.stage === 'Review' ? '审核通过' : '批准发布' }}</el-button>
          <el-button v-if="selected?.currentPublishedRevisionId && !selected.isArchived" type="primary" :icon="Download" @click="downloadCurrent">下载批准版本</el-button>
        </div>
      </template>
    </el-drawer>

    <el-drawer v-model="editorOpen" class="program-template-editor" :title="editingRevision ? `编辑 ${editingRevision.version} 草稿` : '上传程序模板'" size="min(1040px, 96vw)" destroy-on-close>
      <el-form label-position="top" class="program-template-form">
        <section><h3>1. 基本信息</h3><div class="program-template-form-grid"><el-form-item label="模板类型"><el-select v-model="form.assetType" :disabled="!!editingRevision"><el-option v-for="(label, value) in assetLabels" :key="value" :label="label" :value="value" /></el-select></el-form-item><el-form-item label="模板名称"><el-input v-model="form.name" /></el-form-item><el-form-item label="分类"><el-input v-model="form.category" placeholder="逻辑运算、运动控制、报警等" /></el-form-item><el-form-item label="厂商"><el-input v-model="form.vendor" placeholder="Siemens、Mitsubishi等" /></el-form-item><el-form-item label="平台"><el-input v-model="form.platform" placeholder="TIA Portal、GX Works等" /></el-form-item><el-form-item label="软件版本"><el-input v-model="form.softwareVersion" /></el-form-item><el-form-item label="适用系列"><el-input v-model="form.applicableSeries" /></el-form-item><el-form-item label="标签"><el-input v-model="tagText" placeholder="多个标签使用逗号分隔" /></el-form-item><el-form-item class="is-half" label="功能说明"><el-input v-model="form.description" type="textarea" :rows="2" /></el-form-item><el-form-item class="is-half" label="版本说明"><el-input v-model="form.changeNote" type="textarea" :rows="2" /></el-form-item></div></section>

        <section><h3>2. 受控文件</h3><div class="program-template-upload-grid"><article><input ref="packageInput" type="file" accept=".zip" hidden @change="handleFile('Package', $event)" /><Upload :size="22" /><strong>ZIP程序包</strong><span>{{ editingRevision?.packageFileName || '仅支持未加密 ZIP' }}</span><el-button :loading="uploadingKind === 'Package'" :disabled="uploadingKind !== null" @click="packageInput?.click()">{{ editingRevision?.packageFileName ? '重新上传' : '选择程序包' }}</el-button></article><article><input ref="evidenceInput" type="file" accept=".pdf,.xlsx,.png,.jpg,.jpeg" hidden @change="handleFile('TestEvidence', $event)" /><Upload :size="22" /><strong>离线测试证据</strong><span>{{ editingRevision?.evidenceFileName || 'PDF、XLSX、PNG或JPG' }}</span><el-button :loading="uploadingKind === 'TestEvidence'" :disabled="uploadingKind !== null" @click="evidenceInput?.click()">{{ editingRevision?.evidenceFileName ? '重新上传' : '选择测试证据' }}</el-button></article></div><el-progress v-if="uploadingKind" :percentage="uploadProgress" /></section>

        <section><div class="program-template-interface-heading"><div><h3>3. 接口定义</h3><p>PLC功能块为必填；其他模板可按需维护，详情图由这些数据自动生成。</p></div><div><el-button @click="addParameter('Input')">+ 输入</el-button><el-button @click="addParameter('Output')">+ 输出</el-button><el-button @click="addParameter('InOut')">+ 双向</el-button></div></div><div class="program-template-interface-layout"><div class="program-template-parameter-editor"><div v-for="(parameter, index) in form.parameters" :key="parameter.id" class="program-template-parameter-row"><el-select v-model="parameter.direction" aria-label="接口方向"><el-option v-for="(label, value) in directionLabels" :key="value" :label="label" :value="value" /></el-select><el-input v-model="parameter.name" placeholder="参数名" /><el-input v-model="parameter.dataType" placeholder="数据类型" /><el-input v-model="parameter.defaultValue" placeholder="默认值" /><el-input v-model="parameter.description" placeholder="说明" /><el-button-group><el-button :icon="ArrowUp" :disabled="index === 0" aria-label="上移参数" @click="moveParameter(index, -1)" /><el-button :icon="ArrowDown" :disabled="index === form.parameters.length - 1" aria-label="下移参数" @click="moveParameter(index, 1)" /><el-button type="danger" plain :icon="Trash2" aria-label="删除参数" @click="removeParameter(index)" /></el-button-group></div><el-empty v-if="!form.parameters.length" description="当前没有接口定义" :image-size="56" /></div><div v-if="previewParameters.length" class="program-template-live-preview"><FunctionBlockDiagram code="预览" :name="form.name || '未命名功能块'" :version="editingRevision?.version || 'v1.0.0'" :parameters="previewParameters" /></div></div></section>
      </el-form>
      <template #footer><el-button @click="editorOpen = false">取消</el-button><el-button :loading="saving" @click="saveDraft()">保存草稿</el-button><el-button type="primary" :loading="saving" @click="submitDraft">提交审核</el-button></template>
    </el-drawer>

    <el-dialog v-model="versionDialogOpen" title="创建新版本" width="430px"><el-form label-position="top"><el-form-item label="变更级别"><el-radio-group v-model="versionBump"><el-radio-button label="Major">重大变更</el-radio-button><el-radio-button label="Minor">功能变更</el-radio-button><el-radio-button label="Patch">修正变更</el-radio-button></el-radio-group></el-form-item><p class="program-template-version-hint">系统将根据当前批准版本自动生成新的语义版本号。</p></el-form><template #footer><el-button @click="versionDialogOpen = false">取消</el-button><el-button type="primary" @click="createNextVersion">创建草稿</el-button></template></el-dialog>
  </section>
</template>

<style scoped>
.program-template-library { min-height: 0; height: 100%; display: flex; flex-direction: column; gap: 12px; }
.program-template-panel { min-height: 0; flex: 1; display: flex; flex-direction: column; overflow: hidden; padding: 0; }.program-template-toolbar { min-height: 50px; display: grid; grid-template-columns: auto 140px 150px minmax(220px,1fr) auto auto auto; align-items: center; gap: 9px; padding: 8px 12px; border-bottom: 1px solid var(--pdm-border); }.program-template-toolbar > span { color: var(--pdm-muted); font-size: 10px; white-space: nowrap; }.program-template-tabs { display: inline-flex; padding: 3px; border: 1px solid var(--pdm-border); border-radius: 7px; background: var(--pdm-surface-muted); }.program-template-tabs button { min-height: 28px; border: 0; border-radius: 5px; padding: 0 12px; background: transparent; color: var(--pdm-muted); cursor: pointer; }.program-template-tabs button[aria-selected="true"] { background: white; color: var(--pdm-blue); box-shadow: 0 1px 4px rgba(15,23,42,.1); }
.program-template-panel :deep(.el-table__body),.program-template-panel :deep(.el-table__body .cell),.program-template-panel :deep(.el-table__body code),.program-template-panel :deep(.el-table__body .el-tag),.program-template-panel :deep(.el-table__body .el-button) { font-size: 12px; }.program-template-name,.program-template-description,.program-template-cell-note { min-width: 0; display: block; overflow: hidden; font-size: 12px; line-height: 20px; text-overflow: ellipsis; white-space: nowrap; }.program-template-description,.program-template-cell-note { color: var(--pdm-muted); }.program-template-vendor { min-width: 0; display: flex; align-items: center; gap: 5px; overflow: hidden; white-space: nowrap; }.program-template-vendor strong { flex: 0 0 auto; font-size: 12px; }.program-template-vendor .program-template-cell-note { display: inline; }
.program-template-detail-title { min-width: 0; display: flex; align-items: center; gap: 10px; }.program-template-detail-title div { min-width: 0; flex: 1; }.program-template-detail-title h2 { margin: 0; overflow: hidden; font-size: 17px; text-overflow: ellipsis; white-space: nowrap; }.program-template-detail-title p { margin: 3px 0 0; color: var(--pdm-muted); font-size: 10px; }
.program-template-section { padding: 2px 0 18px; }.program-template-section + .program-template-section { border-top: 1px solid var(--pdm-border-soft); padding-top: 16px; }.program-template-section h3 { display: flex; align-items: center; gap: 8px; margin: 0 0 10px; color: #7789a2; font-size: 11px; font-weight: 600; }.program-template-section h3::after { height: 1px; flex: 1; background: var(--pdm-border-soft); content: ''; }.program-template-section > p { margin: 0; color: #41536b; line-height: 1.8; }
.program-parameter-groups { display: grid; grid-template-columns: repeat(3,minmax(0,1fr)); gap: 10px; }.program-parameter-groups article { overflow: hidden; border: 1px solid var(--pdm-border); border-radius: 8px; }.program-parameter-groups article header { padding: 9px 11px; background: #eef5ff; color: #285f9f; font-size: 10px; font-weight: 700; }.program-parameter-groups article.is-input header { background: #e8f8f5; color: #087f76; }.program-parameter-groups article.is-output header { background: #fff4e5; color: #b76400; }.program-parameter-groups article.is-inout header { background: #f1edff; color: #6845bd; }.program-parameter-groups article > div { min-height: 34px; display: flex; align-items: center; justify-content: space-between; gap: 8px; padding: 6px 10px; border-top: 1px solid var(--pdm-border-soft); }.program-parameter-groups article strong { overflow: hidden; font-size: 10px; text-overflow: ellipsis; white-space: nowrap; }.program-parameter-groups article code { color: var(--pdm-muted); font-size: 9px; }.program-parameter-groups article > p { margin: 0; padding: 12px; color: var(--pdm-muted); text-align: center; }
.program-template-diagram-scroll { overflow-x: auto; border: 1px solid var(--pdm-border); border-radius: 9px; background: #fbfdff; padding: 8px 12px; }
.program-template-properties { display: grid; grid-template-columns: repeat(2,minmax(0,1fr)); gap: 0 20px; margin: 0; }.program-template-properties > div { min-width: 0; display: grid; grid-template-columns: 90px minmax(0,1fr); padding: 7px 0; border-bottom: 1px solid var(--pdm-border-soft); }.program-template-properties .is-wide { grid-column: 1/-1; }.program-template-properties dt { color: var(--pdm-muted); }.program-template-properties dd { min-width: 0; margin: 0; overflow-wrap: anywhere; }.program-template-properties code { font-size: 9px; }
.program-template-decision :deep(.el-checkbox-group) { display: grid; gap: 8px; margin-bottom: 12px; }.program-template-decision :deep(.el-checkbox) { height: auto; white-space: normal; }
.program-template-history { display: grid; gap: 6px; }.program-template-history button { display: grid; grid-template-columns: 75px 90px 90px 1fr; align-items: center; gap: 8px; min-height: 38px; border: 1px solid var(--pdm-border); border-radius: 6px; padding: 5px 9px; background: white; text-align: left; cursor: pointer; }.program-template-history button.is-active,.program-template-history button:hover { border-color: var(--pdm-blue); background: var(--pdm-blue-soft); }.program-template-history small { color: var(--pdm-muted); text-align: right; }
.program-template-detail-actions { width: 100%; display: flex; justify-content: flex-end; gap: 8px; }
:global(.program-template-detail) { font-size: 12px; }
:global(.program-template-detail .el-drawer__header) { min-height: 52px; margin-bottom: 0; padding: 0 14px; }
:global(.program-template-detail .el-drawer__body) { min-height: 0; display: grid; grid-template-columns: minmax(0,.86fr) minmax(0,1.14fr); grid-template-rows: auto auto auto auto; align-content: start; gap: 8px 14px; overflow: hidden; padding: 8px 12px; }
:global(.program-template-detail .el-drawer__footer) { padding: 7px 12px; }
:global(.program-template-detail .program-template-section),:global(.program-template-detail .program-template-section + .program-template-section) { min-width: 0; min-height: 0; overflow: hidden; border-top: 0; padding: 0; }
:global(.program-template-detail .program-template-section h3) { margin-bottom: 6px; font-size: 12px; }
:global(.program-template-detail .program-template-section > p) { font-size: 12px; line-height: 1.5; }
:global(.program-template-detail .is-description) { grid-column: 1; grid-row: 1; }
:global(.program-template-detail .is-properties) { grid-column: 2; grid-row: 1; }
:global(.program-template-detail .is-interface) { grid-column: 1; grid-row: 2; }
:global(.program-template-detail .is-diagram) { grid-column: 2; grid-row: 2; align-self: start; }
:global(.program-template-detail .is-history) { grid-column: 1/-1; grid-row: 3; }
:global(.program-template-detail .is-decision) { grid-column: 1/-1; grid-row: 4; }
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
@media(max-width:1000px){.program-template-toolbar{grid-template-columns:repeat(2,minmax(0,1fr))}.program-template-tabs,.program-template-toolbar .el-input{grid-column:1/-1}.program-template-interface-layout{grid-template-columns:1fr}.program-template-form-grid{grid-template-columns:repeat(4,minmax(0,1fr))}.program-template-form-grid>*{grid-column:span 2}.program-template-form-grid .is-half{grid-column:span 2}.program-template-form-grid .is-wide{grid-column:1/-1}}
@media(max-width:680px){.program-parameter-groups,.program-template-upload-grid,.program-template-form-grid{grid-template-columns:1fr}.program-template-form-grid>*,.program-template-form-grid .is-half,.program-template-form-grid .is-wide{grid-column:auto}.program-template-properties{grid-template-columns:1fr}.program-template-properties .is-wide{grid-column:auto}.program-template-parameter-row{grid-template-columns:84px minmax(120px,1fr) 90px auto;grid-template-areas:"direction name type actions" "default description description actions"}.program-template-history button{grid-template-columns:65px 75px 80px}.program-template-history small{display:none}}
</style>
