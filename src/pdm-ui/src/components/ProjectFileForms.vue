<script setup lang="ts">
import CompanyNameField from './CompanyNameField.vue'
import { computed, onBeforeUnmount, ref, watch } from 'vue'
import { ElMessageBox } from 'element-plus'
import { ElMessage } from '../statusMessage'
import { PdmApiError, archiveProjectFileForm, downloadProjectAcceptanceExcel, listProjectFileForms, readProjectFileForm, saveProjectFileForm } from '../api'
import { createClientUuid } from '../clientId'
import { acceptanceDefaults, fileFormKinds } from '../projectFileForms'
import type { ProjectFileForm, ProjectFileFormIndex, ProjectFileFormKind, ProjectFileFormSummary, ProjectFormAttachment, ProjectSampleItem } from '../projectFileForms'
import QuantityInputNumber from './QuantityInputNumber.vue'

const props = defineProps<{ projectId: string; token: string; permissions: readonly string[]; onlyKind?: ProjectFileFormKind; showArchiveLink?: boolean }>()
const emit = defineEmits<{ archived: [folderId: string, fileId: string] }>()
const indexes = ref<Partial<Record<ProjectFileFormKind, ProjectFileFormIndex>>>({})
const active = ref<ProjectFileFormKind | null>(null)
const drawer = ref(false)
const draft = ref<ProjectFileForm | null>(null)
const busy = ref(false)
const loading = ref(false)
const attachmentReads = ref(0)
const error = ref('')
const allowedEntries = computed(() => fileFormKinds.filter(entry =>
  (!props.onlyKind || entry.kind === props.onlyKind) && props.permissions.includes('project.file.' + entry.kind + '.view')))
const entries = computed(() => allowedEntries.value.filter(entry => indexes.value[entry.kind]))
const qualityArchive = computed(() => props.onlyKind ? indexes.value[props.onlyKind]?.forms.find(form => form.fileId) : undefined)
const index = computed(() => active.value ? indexes.value[active.value] : undefined)
const canEdit = computed(() => index.value?.canEdit === true)
const canArchive = computed(() => index.value?.canArchive === true)
const canExport = computed(() => index.value?.canExport === true)
const isSample = computed(() => active.value === 'sample-demand')
const isChange = computed(() => active.value === 'customer-change')
const allowsMultiple = computed(() => isSample.value || isChange.value)
const newFormLabel = computed(() => isSample.value ? '新建样件需求' : '新建变更单')
const isAcceptance = computed(() => active.value === 'pre-acceptance' || active.value === 'final-acceptance')
const title = computed(() => fileFormKinds.find(entry => entry.kind === active.value)?.title ?? '项目单据')
const sampleItems = computed(() => draft.value?.data.items ?? [])
const units = ['件', '个', '套', '台', '组', '支', '张', '盒', '包', '米', '毫米', '千克', '克', '升']
const samplePhases = ['设计', '装配', '调试']
let controller: AbortController | null = null
let generation = 0
let baseline = ''

function errorText(cause: unknown, fallback: string) {
  return cause instanceof PdmApiError && /[\u3400-\u9fff]/.test(cause.message) ? cause.message : fallback
}
function status(form: ProjectFileFormSummary) {
  if (!form.archivedRevision) return '待存档'
  if (form.archivedRevision !== form.rowVersion) return '待生成新版本'
  if (isChange.value && (form.archivedLayoutVersion ?? 1) < 2) return '可生成新版式'
  return '已存档 V' + form.fileVersion
}
function entryStatus(kind: ProjectFileFormKind) {
  const forms = indexes.value[kind]?.forms ?? []
  if (!forms.length) return '未编制'
  return forms.some(form => form.archivedRevision !== form.rowVersion) ? '有待存档草稿' : '已存档'
}
function accept(form: ProjectFileForm | null) {
  draft.value = form
  baseline = JSON.stringify(form?.data ?? null)
}
function dirty() { return JSON.stringify(draft.value?.data ?? null) !== baseline }
function emptyItem(): ProjectSampleItem {
  return { materialName: '', quantity: null, unit: '件', requiredOn: null, phase: '', note: '', image: null }
}
function newDraft() {
  const receiving = index.value?.receiving
  const data = isSample.value
    ? { recipient: receiving?.recipient ?? '', contact: receiving?.contact ?? '', address: receiving?.address ?? '', items: [emptyItem()] }
    : isAcceptance.value && active.value
      ? { supplier: index.value?.company ?? '', customer: index.value?.customer ?? '', changedOn: null, acceptance: acceptanceDefaults(active.value) }
      : { supplier: index.value?.company ?? '', customer: index.value?.customer ?? '', changedOn: new Date().toLocaleDateString('sv-SE'),
        reason: '', before: '', after: '', paid: null, note: '', evidence: [] }
  accept({ summary: { id: createClientUuid(), number: '', rowVersion: 0, archivedRevision: 0,
    fileId: null, fileVersion: 0, evidenceFileId: null, updatedAt: '' }, data })
}
async function refreshIndexes() {
  const current = ++generation
  controller?.abort()
  controller = new AbortController()
  indexes.value = {}
  drawer.value = false
  active.value = null
  busy.value = false
  loading.value = false
  accept(null)
  if (!props.projectId || !props.token) return
  const { projectId, token } = props
  await Promise.all(allowedEntries.value.map(async entry => {
    try {
      const result = await listProjectFileForms(projectId, entry.kind, token, controller!.signal)
      if (current === generation) indexes.value[entry.kind] = result
    } catch (cause) {
      if (current === generation && !(cause instanceof PdmApiError && cause.status === 403))
        ElMessage.error(errorText(cause, '项目单据入口加载失败，请刷新后重试。'))
    }
  }))
}
watch([() => props.projectId, () => Boolean(props.token), () => props.onlyKind, () => props.permissions.join('|')], refreshIndexes, { immediate: true })
onBeforeUnmount(() => { generation++; controller?.abort() })

async function choose(id: string) {
  if (!active.value) return
  const current = generation, kind = active.value
  loading.value = true
  error.value = ''
  try {
    const result = await readProjectFileForm(props.projectId, kind, id, props.token)
    if (current === generation && active.value === kind) accept(result)
  } catch (cause) {
    if (current === generation) error.value = errorText(cause, '单据读取失败，请重新打开后重试。')
  } finally { if (current === generation) loading.value = false }
}
async function open(kind: ProjectFileFormKind) {
  active.value = kind
  error.value = ''
  drawer.value = true
  accept(null)
  const first = index.value?.forms[0]
  if (allowsMultiple.value && canEdit.value) newDraft()
  else if (first) await choose(first.id)
  else if (canEdit.value) newDraft()
}
async function discardAllowed() {
  if (busy.value || loading.value || attachmentReads.value) return false
  if (!dirty()) return true
  try {
    await ElMessageBox.confirm('当前单据有未保存修改。', '关闭或切换单据', {
      confirmButtonText: '放弃修改', cancelButtonText: '继续编辑', type: 'warning',
    })
    return true
  } catch { return false }
}
async function close(done?: () => void) {
  if (await discardAllowed()) { drawer.value = false; done?.() }
}
async function changeSelected(event: Event) {
  const select = event.target as HTMLSelectElement
  const nextId = select.value
  select.value = draft.value?.summary.id ?? ''
  if (nextId && await discardAllowed()) await choose(nextId)
}
async function addChange() {
  if (canEdit.value && await discardAllowed()) { error.value = ''; newDraft() }
}
function updateIndex(kind: ProjectFileFormKind, saved: ProjectFileFormSummary) {
  const current = indexes.value[kind]
  if (!current) return
  const existing = current.forms.findIndex(form => form.id === saved.id)
  if (existing < 0) current.forms.unshift(saved)
  else current.forms.splice(existing, 1, saved)
}
async function persist(mode: 'save' | 'archive' | 'export') {
  const folderId = index.value?.folderId
  if (!active.value || !draft.value || !folderId || busy.value || attachmentReads.value) return
  if (mode === 'export' ? !canExport.value : !canEdit.value || mode === 'archive' && !canArchive.value) return
  const current = generation, kind = active.value, form = draft.value, projectId = props.projectId
  busy.value = true
  error.value = ''
  try {
    if (!form.summary.rowVersion || dirty()) {
      if (!canEdit.value) return
      const data = { ...form.data, changedOn: form.data.changedOn || null,
        items: form.data.items?.map(item => ({ ...item, requiredOn: item.requiredOn || null })) }
      const saved = await saveProjectFileForm(projectId, kind, form.summary.id, {
        expectedRowVersion: form.summary.rowVersion, data,
      }, props.token)
      if (current !== generation) return
      accept(saved)
      updateIndex(kind, saved.summary)
    }
    if (mode === 'save') { ElMessage.success('草稿已保存'); return }
    if (mode === 'export') {
      await downloadProjectAcceptanceExcel(projectId, kind, form.summary.id, `${draft.value!.summary.number}_${title.value}.xlsx`, props.token)
      return
    }
    const saved = await archiveProjectFileForm(projectId, kind, form.summary.id, draft.value!.summary.rowVersion, props.token)
    if (current !== generation) return
    updateIndex(kind, saved)
    if (draft.value) draft.value.summary = saved
    drawer.value = false
    ElMessage.success('已生成并存档到项目文件 · V' + saved.fileVersion)
    if (saved.fileId) emit('archived', folderId, saved.fileId)
  } catch (cause) {
    if (current === generation) error.value = errorText(cause, mode === 'archive' ? '生成存档失败，请核对必填内容及项目文件目录后重试。'
      : mode === 'export' ? '验收单下载失败，请核对填写内容后重试。' : '草稿保存失败，请稍后重试。')
  } finally { if (current === generation) busy.value = false }
}
async function attachment(file: File): Promise<ProjectFormAttachment> {
  if (file.size <= 0 || file.size > 2 * 1024 * 1024) throw new Error('单个图片或附件须大于0且不超过2MB。')
  const content = await new Promise<string>((resolve, reject) => {
    const reader = new FileReader()
    reader.onload = () => resolve(String(reader.result).split(',')[1] ?? '')
    reader.onerror = () => reject(new Error('文件读取失败，请重新选择。'))
    reader.readAsDataURL(file)
  })
  return { id: createClientUuid(), fileName: file.name, content }
}
async function chooseImage(item: ProjectSampleItem, event: Event) {
  const input = event.target as HTMLInputElement, file = input.files?.[0]
  input.value = ''
  if (!file || !canEdit.value) return
  const form = draft.value
  attachmentReads.value++
  try {
    const selected = await attachment(file)
    if (draft.value === form && drawer.value) item.image = selected
  } catch (cause) { error.value = cause instanceof Error ? cause.message : '图片读取失败。' }
  finally { attachmentReads.value-- }
}
async function chooseEvidence(event: Event) {
  const input = event.target as HTMLInputElement, chosen = Array.from(input.files ?? [])
  input.value = ''
  const form = draft.value
  if (!form || !canEdit.value || !chosen.length) return
  const previous = form.data.evidence ?? []
  if (previous.length + chosen.length > 8) { error.value = '每张变更单最多添加8个确认附件。'; return }
  attachmentReads.value++
  try {
    const added = await Promise.all(chosen.map(attachment))
    if (draft.value === form && drawer.value) form.data.evidence = [...previous, ...added]
  } catch (cause) { error.value = cause instanceof Error ? cause.message : '附件读取失败。' }
  finally { attachmentReads.value-- }
}
function imageUrl(file: ProjectFormAttachment) {
  return 'data:image/' + (file.fileName.toLowerCase().endsWith('.png') ? 'png' : 'jpeg') + ';base64,' + file.content
}
function locateQualityArchive() {
  if (!props.onlyKind || !qualityArchive.value?.fileId) return
  const folderId = indexes.value[props.onlyKind]?.folderId
  if (folderId) emit('archived', folderId, qualityArchive.value.fileId)
}
</script>

<template>
  <div v-if="entries.length" class="project-file-form-actions">
    <button v-for="entry in entries" :key="entry.kind" type="button" class="pdm-secondary-action project-form-entry"
      :class="{ 'project-form-entry--pending': entryStatus(entry.kind) !== '已存档', 'project-form-entry--long': entry.kind === 'customer-change' }"
      :title="entry.title + ' · ' + entryStatus(entry.kind)" @click="open(entry.kind)">{{ entry.button }}</button>
    <button v-if="showArchiveLink && qualityArchive?.fileId" type="button" class="pdm-secondary-action"
      :title="qualityArchive.number + ' · V' + qualityArchive.fileVersion" @click="locateQualityArchive">查看归档</button>
  </div>
  <el-drawer v-model="drawer" :title="title" size="min(1000px, 100vw)" append-to-body destroy-on-close :before-close="close">
    <div class="project-form-editor" v-loading="loading || busy">
      <p v-if="error" class="pdm-inline-error" role="alert">{{ error }}</p>
      <template v-if="index">
        <div class="project-form-context"><strong>{{ index.projectCode }} · {{ index.projectName }}</strong><span v-if="isAcceptance">请在质量页签的预/终验收中上传记录，原文件会自动归入验收资料。</span><span v-else>归档目录 · 项目文件</span></div>
        <div v-if="allowsMultiple" class="project-form-switch">
          <label>选择历史单据
            <select aria-label="选择历史单据" :value="draft?.summary.id ?? ''" :disabled="loading || busy" @change="changeSelected">
              <option value="" disabled>选择已保存单据</option>
              <option v-if="draft && !draft.summary.rowVersion" :value="draft.summary.id">{{ newFormLabel }}</option>
              <option v-for="form in index.forms" :key="form.id" :value="form.id">{{ form.number }} · {{ status(form) }}</option>
            </select>
          </label>
          <button v-if="canEdit" type="button" class="pdm-secondary-action" :disabled="busy || loading" @click="addChange">{{ newFormLabel }}</button>
        </div>
        <p v-if="draft?.summary.rowVersion" class="project-form-meta">{{ draft.summary.number }} · {{ status(draft.summary) }}</p>
        <p v-if="!draft && !loading" class="project-form-empty">当前项目尚未编制单据。</p>
        <fieldset v-if="draft" :disabled="!canEdit || busy || loading || attachmentReads > 0">
          <template v-if="isSample">
            <div class="project-form-fields">
              <label>公司名称<CompanyNameField :name="index.company"><input :value="index.company" disabled /></CompanyNameField></label>
              <label>收货人<input v-model="draft.data.recipient" maxlength="80" /></label>
              <label>联系方式<input v-model="draft.data.contact" maxlength="100" /></label>
              <label class="project-form-wide">收货地址<input v-model="draft.data.address" maxlength="300" /></label>
            </div>
            <div class="project-form-table">
              <table class="pdm-fit-columns" data-pdm-min-width="830" aria-label="样件需求明细">
                <colgroup>
                  <col class="project-form-sequence"><col><col class="project-form-quantity"><col class="project-form-unit">
                  <col class="project-form-image"><col class="project-form-date"><col class="project-form-phase"><col>
                  <col v-if="canEdit" class="project-form-action">
                </colgroup>
                <thead><tr><th scope="col">序号</th><th scope="col">物料名称</th><th scope="col">数量</th><th scope="col">单位</th><th scope="col">图片</th><th scope="col">需求日期</th><th scope="col">适用阶段</th><th scope="col">备注</th><th v-if="canEdit" scope="col">操作</th></tr></thead>
                <tbody><tr v-for="(item, row) in sampleItems" :key="row">
                  <td>{{ row + 1 }}</td>
                  <td><input v-model="item.materialName" maxlength="200" :aria-label="'物料名称 ' + (row + 1)" /></td>
                  <td><QuantityInputNumber v-model="item.quantity" :max="1000000" :controls="false" :disabled="!canEdit || busy || loading" :aria-label="'数量 ' + (row + 1)" /></td>
                  <td><select v-model="item.unit" :aria-label="'单位 ' + (row + 1)"><option v-for="unit in units" :key="unit">{{ unit }}</option></select></td>
                  <td>
                    <img v-if="item.image?.content" class="project-form-thumb" :src="imageUrl(item.image)" :alt="item.image.fileName" />
                    <label v-if="canEdit && !item.image" class="project-form-upload">选择文件<input type="file" accept=".png,.jpg,.jpeg" :aria-label="'选择样件图片 ' + (row + 1)" @change="chooseImage(item, $event)" /></label>
                    <button v-if="canEdit && item.image" type="button" class="pdm-text-action" @click="item.image = null">移除图片</button>
                    <span v-if="!item.image && !canEdit">—</span>
                  </td>
                  <td><input v-model="item.requiredOn" type="date" :aria-label="'需求日期 ' + (row + 1)" /></td>
                  <td><select v-model="item.phase" :aria-label="'适用阶段 ' + (row + 1)">
                    <option value="">请选择</option><option v-if="item.phase && !samplePhases.includes(item.phase)" :value="item.phase">{{ item.phase }}</option>
                    <option v-for="phase in samplePhases" :key="phase">{{ phase }}</option>
                  </select></td>
                  <td><input v-model="item.note" maxlength="500" :aria-label="'备注 ' + (row + 1)" /></td>
                  <td v-if="canEdit"><button type="button" class="pdm-text-action" :aria-label="'删除第' + (row + 1) + '行'" @click="sampleItems.splice(row, 1)">删除</button></td>
                </tr></tbody>
                <tbody v-if="!sampleItems.length"><tr><td :colspan="canEdit ? 9 : 8">暂无样件需求明细</td></tr></tbody>
              </table>
            </div>
            <button v-if="canEdit" type="button" class="pdm-secondary-action" :disabled="sampleItems.length >= 100" @click="sampleItems.push(emptyItem())">添加明细</button>
          </template>
          <template v-else-if="isAcceptance && draft.data.acceptance">
            <div class="project-form-fields">
              <label>供方<input v-model="draft.data.supplier" maxlength="200" /></label>
              <label>需方<input v-model="draft.data.customer" maxlength="200" /></label>
              <label>合同编号<input v-model="draft.data.acceptance.contractNumber" maxlength="120" /></label>
              <label>验收时间<input v-model="draft.data.changedOn" type="date" /></label>
              <label>验收地点<input v-model="draft.data.acceptance.location" maxlength="300" /></label>
              <label>验收人员<input v-model="draft.data.acceptance.attendees" maxlength="300" /></label>
              <label>验收结果<select v-model="draft.data.acceptance.passed"><option :value="null">待填写</option><option :value="true">验收合格</option><option :value="false">验收不合格</option></select></label>
              <label class="project-form-wide">验收标准<textarea v-model="draft.data.acceptance.standard" rows="2" maxlength="2000" /></label>
              <label class="project-form-wide">验收说明<textarea v-model="draft.data.acceptance.explanation" rows="2" maxlength="2000" /></label>
              <label v-if="active === 'pre-acceptance'" class="project-form-wide">整改要求<textarea v-model="draft.data.acceptance.corrections" rows="3" maxlength="2000" /></label>
              <label class="project-form-wide">验收人意见<textarea v-model="draft.data.acceptance.comments" rows="3" maxlength="3000" /></label>
              <label class="project-form-wide">验收约定<textarea v-model="draft.data.acceptance.terms" rows="3" maxlength="3000" /></label>
              <small>双方签字、盖章和验收日期在 Excel 中填写。签署后在质量页上传记录，原件归入验收资料。</small>
            </div>
          </template>
          <template v-else-if="isChange">
            <div class="project-form-fields">
              <label>供方<input v-model="draft.data.supplier" maxlength="200" /></label>
              <label>需方<input v-model="draft.data.customer" maxlength="200" /></label>
              <label>变更日期<input v-model="draft.data.changedOn" type="date" /></label>
              <label>是否付费<select v-model="draft.data.paid" aria-label="是否付费"><option :value="null">请选择</option><option :value="true">是</option><option :value="false">否</option></select></label>
              <label class="project-form-wide">变更原因<textarea v-model="draft.data.reason" rows="3" maxlength="2000" /></label>
              <label>变更前内容<textarea v-model="draft.data.before" rows="5" maxlength="3000" /></label>
              <label>变更后内容<textarea v-model="draft.data.after" rows="5" maxlength="3000" /></label>
              <label class="project-form-wide">备注<textarea v-model="draft.data.note" rows="2" maxlength="2000" /></label>
            </div>
            <section class="project-form-evidence" aria-label="客户确认附件">
              <strong>客户确认附件</strong>
              <label v-if="canEdit" class="project-form-upload">选择文件<input type="file" multiple accept=".png,.jpg,.jpeg,.pdf,.eml" aria-label="选择客户确认附件" @change="chooseEvidence" /></label>
              <small>支持截图、PDF 和 EML 邮件，单个不超过2MB，合计不超过8MB。</small>
              <ul v-if="draft.data.evidence?.length">
                <li v-for="(file, i) in draft.data.evidence" :key="file.id"><span>{{ file.fileName }}</span><button v-if="canEdit" type="button" class="pdm-text-action" @click="draft.data.evidence!.splice(i, 1)">移除</button></li>
              </ul>
              <small>Excel 保留双方签字和日期栏。确认附件随变更单存档。</small>
            </section>
          </template>
        </fieldset>
      </template>
    </div>
    <template #footer>
      <el-button :disabled="busy || loading || attachmentReads > 0" @click="close()">关闭</el-button>
      <el-button v-if="canEdit && draft" :disabled="busy || loading || attachmentReads > 0" @click="persist('save')">保存草稿</el-button>
      <el-button v-if="isAcceptance && canExport && draft" type="primary" :disabled="busy || loading" @click="persist('export')">下载 Excel</el-button>
      <el-button v-if="!isAcceptance && canArchive && draft" type="primary" :disabled="busy || loading || attachmentReads > 0" @click="persist('archive')">生成并存档</el-button>
    </template>
  </el-drawer>
</template>

<style scoped>
.project-file-form-actions,.project-form-switch,.project-form-context{display:flex;align-items:center;gap:8px;flex-wrap:wrap}
:root body .project-file-form-actions .project-form-entry.project-form-entry--long{width:112px!important;min-width:112px!important;max-width:112px!important}
:root body .project-file-form-actions .project-form-entry.project-form-entry--pending{color:#9a4b00!important;border-color:#e2a14f!important;background:#fff4df!important}
:root body .project-file-form-actions .project-form-entry.project-form-entry--pending:hover{background:#ffe8bb!important}
.project-form-editor{display:grid;gap:12px;min-width:0}
.project-form-context{justify-content:space-between;padding:10px 12px;background:var(--pdm-surface-soft);border-radius:10px}
.project-form-context span,.project-form-meta,.project-form-evidence small{color:var(--pdm-muted)}
.project-form-switch{align-items:flex-end}.project-form-switch label{min-width:min(280px,100%)}
.project-form-editor fieldset{display:grid;gap:12px;min-width:0;margin:0;padding:0;border:0}
.project-form-fields{display:grid;grid-template-columns:repeat(2,minmax(0,1fr));gap:12px}
.project-form-wide{grid-column:1/-1}
.project-form-editor label:not(.project-form-upload){display:grid;gap:5px}
.project-form-editor :is(input:not([type=file]),select,textarea){box-sizing:border-box;width:100%;min-width:0;color:var(--pdm-text);background:var(--pdm-surface);border:1px solid var(--pdm-border);padding:4px 9px;font:inherit}
.project-form-editor :is(input:not([type=file]),select){height:28px;border-radius:999px}
.project-form-editor textarea{resize:vertical;border-radius:16px}
.project-form-editor :is(input,select,textarea):focus-visible{outline:2px solid var(--pdm-theme-accent);outline-offset:1px}
.project-form-table{overflow:auto}.project-form-table table{width:100%;min-width:830px;table-layout:fixed;border-collapse:collapse}
.project-form-table :is(th,td){text-align:center;vertical-align:middle;border:1px solid var(--pdm-border);padding:5px;min-width:54px}
.project-form-table th{background:var(--pdm-surface-soft)}
.project-form-sequence{width:42px}
.project-form-quantity{width:86px}
.project-form-unit,.project-form-phase{width:90px}
.project-form-image{width:96px}
.project-form-date{width:148px}
.project-form-action{width:58px}
.project-form-table td :is(input:not([type=file]),select){height:24px;text-align:center}
.project-form-table td :deep(.el-input-number){width:72px}
.project-form-table td :deep(.el-input-number .el-input__inner){border:0!important;box-shadow:none!important;background:transparent!important;padding:0!important}
.project-form-thumb{display:block;width:66px;height:43px;object-fit:contain;margin:0 auto 3px}
.project-form-upload{display:inline-flex;position:relative;align-items:center;justify-content:center;width:80px;height:28px;flex:none;border-radius:999px;background:#0e9d6a;color:white;cursor:pointer}
.project-form-upload:focus-within{outline:2px solid var(--pdm-theme-accent);outline-offset:2px}
.project-form-upload input[type=file]{position:absolute;inset:0;opacity:0;width:100%;cursor:pointer}
.project-form-empty{text-align:center}.project-form-meta{margin:0}
.project-form-evidence{display:flex;gap:8px;align-items:center;flex-wrap:wrap}
.project-form-evidence ul{width:100%;list-style:none;padding:0;margin:0}
.project-form-evidence li{display:flex;justify-content:center;align-items:center;gap:8px;padding:5px;border-bottom:1px solid var(--pdm-border);overflow-wrap:anywhere}
.project-form-evidence small:last-child{flex-basis:100%}
@media(max-width:650px){.project-form-fields{grid-template-columns:minmax(0,1fr)}}
</style>
