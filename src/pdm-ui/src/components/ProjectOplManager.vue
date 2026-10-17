<script setup lang="ts">
import { computed, defineAsyncComponent, onBeforeUnmount, ref, watch } from 'vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import { Download, Plus, RotateCw as RefreshCw } from '@lucide/vue'
import { completeProjectOpl, getProjectExportMetadata, PdmApiError, readOplAttachment, readProjectOpl, saveProjectOpl } from '../api'
import type { ProjectSummary } from '../types'
import type { OplAttachment, OplInput, OplRow, OplView } from '../projectOpl'
import { useUserDisplayName } from '../userDisplay'
import { downloadOplWorkbook } from '../oplWorkbook'

const PdfDrawingViewer = defineAsyncComponent(() => import('./PdfDrawingViewer.vue'))
const props = defineProps<{ project: ProjectSummary; token: string }>()
const displayUserName = useUserDisplayName()
const view = ref<OplView>({ rows: [], people: [] })
const loading = ref(false), saving = ref(false), error = ref(''), drawer = ref(false), readonly = ref(false)
const completing = ref<string | null>(null), exporting = ref(false)
const notifyUpdates = ref(false)
const query = ref(''), status = ref('未完成'), owner = ref(''), priority = ref<string[]>([]), page = ref(1)
const states = ['待处理', '处理中', '待确认', '已完成', '已作废']
const priorities = ['紧急', '高', '普通', '低']
const editing = ref<OplRow | null>(null), retained = ref<OplAttachment[]>([]), files = ref<File[]>([])
const empty = (): OplInput => ({ description: '', priority: '普通', status: '待处理', owner: '', dueDate: null, solution: '', expectedRowVersion: 0, attachmentIds: [], relatedProjectId: props.project.parentProjectId ? props.project.id : null })
const draft = ref(empty()), baseline = ref(''), formError = ref('')
const thumbnails = ref<Record<string, string>>({}), previewUrl = ref('')
const previewKind = ref<'image' | 'pdf'>('image'), previewName = ref('')
function closePreview() { if (previewKind.value === 'pdf' && previewUrl.value) URL.revokeObjectURL(previewUrl.value); previewUrl.value = '' }
const rootProject = computed(() => view.value.rootProject ?? props.project)
const subprojects = computed(() => view.value.subprojects ?? [])
const scopedRows = computed(() => props.project.parentProjectId ? view.value.rows.filter(row => row.issue.relatedProjectId === props.project.id) : view.value.rows)
const relatedProject = (id: string | null | undefined) => { const project = subprojects.value.find(item => item.id === id); return project ? project.code : id ? '关联子项目' : '项目整体' }
let generation = 0
const snapshot = () => JSON.stringify({ draft: draft.value, attachments: retained.value.map(x => x.id) })
const dirty = computed(() => drawer.value && !readonly.value && (snapshot() !== baseline.value || files.value.length > 0))
const closed = (state: string) => ['已完成', '已作废'].includes(state)
const today = () => { const d = new Date(); return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}` }
const overdue = (row: OplRow) => !closed(row.issue.status) && Boolean(row.issue.dueDate && row.issue.dueDate < today())
const activeCount = computed(() => scopedRows.value.filter(row => !closed(row.issue.status)).length)
const overdueCount = computed(() => scopedRows.value.filter(overdue).length)
const filtered = computed(() => scopedRows.value.filter(({ issue }) =>
  (!query.value.trim() || `${issue.number} ${issue.description} ${issue.solution}`.toLowerCase().includes(query.value.trim().toLowerCase()))
  && (!status.value || (status.value === '未完成' ? !closed(issue.status) : issue.status === status.value))
  && (!owner.value || issue.owner === owner.value) && (!priority.value.length || priority.value.includes(issue.priority))))
const visible = computed(() => filtered.value.slice((page.value - 1) * 20, page.value * 20))
const name = (username: string) => view.value.people.find(x => x.username === username)?.displayName || displayUserName(username, username || '待分派')
const date = (value: string | null) => value ? new Date(value).toLocaleDateString('sv-SE') : '—'
const imageType = (file: OplAttachment) => ({ jpg: 'image/jpeg', jpeg: 'image/jpeg', png: 'image/png', gif: 'image/gif', webp: 'image/webp' }[file.name.split('.').pop()?.toLowerCase() || ''])
function message(cause: unknown, fallback: string) { console.error(cause); return cause instanceof PdmApiError && cause.status < 500 && /[\u3400-\u9fff]/.test(cause.message) ? cause.message : fallback }
function releaseUrls() { closePreview(); Object.values(thumbnails.value).forEach(URL.revokeObjectURL); thumbnails.value = {}; previewUrl.value = '' }
async function load() {
  const current = ++generation
  loading.value = true; error.value = ''
  try { const data = await readProjectOpl(props.project.id, props.token); if (current === generation) { releaseUrls(); view.value = data } }
  catch (cause) { if (current === generation) error.value = message(cause, '开口问题加载失败，请检查网络后重试。') }
  finally { if (current === generation) loading.value = false }
}
async function exportExcel() {
  if (loading.value || exporting.value || error.value || !scopedRows.value.length) return
  const current = generation, projectId = props.project.id, projectCode = props.project.code, token = props.token
  const rows = scopedRows.value.map(({ issue }) => [issue.number, relatedProject(issue.relatedProjectId), issue.priority, issue.status,
    issue.description, issue.attachments.map(file => file.name).join('\n'), issue.solution, issue.dueDate || '—', name(issue.owner),
    date(issue.completedAt), date(issue.createdAt), issue.createdByDisplayName || name(issue.createdBy)])
  const projectScope = props.project.parentProjectId ? '当前子项目' : '当前主项目及子项目'
  exporting.value = true
  try {
    const metadata = await getProjectExportMetadata(projectId, token)
    if (current !== generation || projectId !== props.project.id) return
    downloadOplWorkbook(`${projectCode}_OPL_${metadata.exportedAt.slice(0, 16).replace(/[-:T ]/g, '')}.xlsx`, rows,
      { ...metadata, title: 'OPL开口问题清单', scope: `${projectScope} · 全部${rows.length}条`,
        versionLabel: '数据时间', version: metadata.exportedAt.replace('T', ' ').slice(0, 16), notes: '完整导出，包含全部状态，不受列表筛选与分页影响' })
    ElMessage.success(`已导出${rows.length}条开口问题`)
  } catch (cause) { ElMessage.error(message(cause, '开口问题导出失败，请检查网络后重试。')) }
  finally { exporting.value = false }
}
async function thumbnail(row: OplRow, file: OplAttachment) {
  if (!imageType(file) || thumbnails.value[file.id]) return
  const current = generation
  try {
    const blob = await readOplAttachment(props.project.id, row.issue.id, file.id, props.token)
    if (current === generation) thumbnails.value[file.id] = URL.createObjectURL(new Blob([blob], { type: imageType(file) }))
  } catch { /* 文件名仍可点击并显示明确错误，不阻塞清单。 */ }
}
watch(visible, rows => { rows.forEach(row => row.issue.attachments.slice(0, 2).forEach(file => { void thumbnail(row, file) })) })
watch([query, status, owner, priority], () => { page.value = 1 })
function open(row: OplRow | null = null, read = false) {
  editing.value = row; readonly.value = read; files.value = []; formError.value = ''
  retained.value = row ? [...row.issue.attachments] : []
  draft.value = row ? { description: row.issue.description, priority: row.issue.priority, status: row.issue.status, owner: row.issue.owner, dueDate: row.issue.dueDate, solution: row.issue.solution, expectedRowVersion: row.issue.rowVersion, attachmentIds: row.issue.attachments.map(x => x.id), relatedProjectId: row.issue.relatedProjectId ?? null } : empty()
  baseline.value = snapshot(); drawer.value = true
  if (row) window.dispatchEvent(new CustomEvent('pdm-tab-content-viewed', { detail: { projectId: props.project.id, tab: 'opl' } }))
}
async function close(done?: () => void) {
  if (saving.value) return
  if (dirty.value) {
    try { await ElMessageBox.confirm('当前问题有未保存修改，关闭将放弃这些内容。', '关闭问题抽屉', { confirmButtonText: '放弃修改', cancelButtonText: '继续编辑', type: 'warning' }) }
    catch { return }
  }
  drawer.value = false; done?.()
}
function selectFiles(event: Event) {
  const input = event.target as HTMLInputElement
  const selected = [...files.value, ...Array.from(input.files || [])]
  input.value = ''
  if (selected.length + retained.value.length > 10 || selected.some(file => !file.size || file.size > 10 * 1024 * 1024) || selected.reduce((sum, file) => sum + file.size, 0) > 30 * 1024 * 1024) {
    ElMessage.warning('最多10个附件，单个应为1字节至10MB，单次总大小不超过30MB。'); return
  }
  files.value = selected
}
async function save() {
  if (saving.value) return
  formError.value = ''
  if (!draft.value.description.trim()) { formError.value = '请填写问题描述。'; return }
  if (draft.value.status === '待确认' && !draft.value.solution.trim()) { formError.value = '请填写解决措施后再提交确认。'; return }
  saving.value = true
  try {
    const issue = await saveProjectOpl(props.project.id, editing.value?.issue.id || null, { ...draft.value, dueDate: draft.value.dueDate || null, relatedProjectId: draft.value.relatedProjectId || null, attachmentIds: retained.value.map(x => x.id), notifyGroup: !!editing.value && notifyUpdates.value }, files.value, props.token)
    drawer.value = false; files.value = []; ElMessage.success('开口问题已保存'); showNotification(issue); await load()
  } catch (cause) { formError.value = message(cause, '问题保存失败，请检查网络后重试，填写内容已保留。') }
  finally { saving.value = false }
}
async function complete(row: OplRow) {
  if (loading.value || saving.value || completing.value || !row.canComplete || closed(row.issue.status)) return
  completing.value = row.issue.id
  try {
    const issue = await completeProjectOpl(props.project.id, row.issue.id, row.issue.rowVersion, props.token, notifyUpdates.value)
    view.value.rows = view.value.rows.map(item => item.issue.id === issue.id ? { ...item, issue } : item)
    ElMessage.success('问题已完成，已自动记录完成日期。')
    showNotification(issue)
  } catch (cause) { ElMessage.error(message(cause, '确认完成失败，请检查网络后重试。')) }
  finally { completing.value = null }
}
function showNotification(issue: OplRow['issue']) {
  if (issue.notification) ElMessage({ type: issue.notification.sent && issue.notification.message === '群通知已发送。' ? 'success' : 'warning', message: issue.notification.message, duration: 7000 })
}
async function attachment(row: OplRow, file: OplAttachment) {
  try {
    closePreview(); previewName.value = file.name
    if (imageType(file)) { await thumbnail(row, file); if (thumbnails.value[file.id]) { previewKind.value = 'image'; previewUrl.value = thumbnails.value[file.id]!; return } }
    const blob = await readOplAttachment(props.project.id, row.issue.id, file.id, props.token)
    if (/\.pdf$/i.test(file.name)) { previewKind.value = 'pdf'; previewUrl.value = URL.createObjectURL(new Blob([blob], { type: 'application/pdf' })); return }
    const url = URL.createObjectURL(blob), link = document.createElement('a')
    link.href = url; link.download = file.name; link.click(); setTimeout(() => URL.revokeObjectURL(url), 1000)
  } catch (cause) { ElMessage.error(message(cause, '附件读取失败，请检查网络或联系管理员。')) }
}
function beforeUnload(event: BeforeUnloadEvent) { if (dirty.value || saving.value) { event.preventDefault(); event.returnValue = '' } }
window.addEventListener('beforeunload', beforeUnload)
onBeforeUnmount(() => { generation++; releaseUrls(); window.removeEventListener('beforeunload', beforeUnload) })
void load()
</script>

<template>
  <section class="pdm-panel pdm-manager-panel opl-manager" aria-label="开口问题清单">
    <header class="pdm-panel-header"><div><h2>OPL 开口问题清单</h2><span v-if="project.parentProjectId" class="opl-scope">当前子项目：{{ project.code }} · {{ project.name }}</span><span class="opl-counts">全部 {{ scopedRows.length }} · 未完成 {{ activeCount }} · <span :class="{ 'opl-overdue': overdueCount > 0 }">已逾期 {{ overdueCount }}</span> · 已完成 {{ scopedRows.filter(row => row.issue.status === '已完成').length }}</span></div><div class="opl-actions"><el-button :disabled="loading || !!error || !scopedRows.length" :loading="exporting" title="导出当前项目范围的全部开口问题（不受筛选影响）" @click="exportExcel"><Download v-if="!exporting" :size="14" />导出Excel</el-button><el-button :disabled="loading" @click="load"><RefreshCw :size="14" />刷新</el-button><el-button type="primary" :disabled="loading || !!error" @click="open()"><Plus :size="14" />新增问题</el-button></div></header>
    <div class="opl-filters"><el-input v-model="query" aria-label="搜索开口问题" placeholder="搜索问题描述 / 编号" clearable /><el-select v-model="status" aria-label="筛选实施状态"><el-option label="全部状态" value="" /><el-option label="未完成" value="未完成" /><el-option v-for="item in states" :key="item" :label="item" :value="item" /></el-select><el-select v-model="owner" aria-label="筛选负责人" placeholder="全部负责人" clearable filterable><el-option v-for="person in view.people" :key="person.username" :label="person.displayName" :value="person.username" /></el-select><el-select v-model="priority" aria-label="筛选优先级" placeholder="全部优先级" multiple collapse-tags collapse-tags-tooltip clearable><el-option v-for="item in priorities" :key="item" :label="item" :value="item" /></el-select></div>
    <el-alert v-if="error" :title="error" type="error" :closable="false" />
    <div class="opl-table-wrap" v-loading="loading"><el-table :data="visible" height="100%" stripe border row-key="issue.id" empty-text="暂无符合条件的开口问题" class="opl-table" @row-click="(row: OplRow) => open(row, !row.canEdit)">
      <el-table-column label="序号" width="100" fixed><template #default="{ row }">{{ row.issue.number }}</template></el-table-column>
      <el-table-column label="子项目" width="200" show-overflow-tooltip><template #default="{ row }">{{ relatedProject(row.issue.relatedProjectId) }}</template></el-table-column>
      <el-table-column label="优先级" width="100"><template #default="{ row }"><el-tag :type="['紧急', '高'].includes(row.issue.priority) ? 'danger' : 'info'" size="small">{{ row.issue.priority }}</el-tag></template></el-table-column>
      <el-table-column label="实施状态" width="100"><template #default="{ row }"><el-tag :type="row.issue.status === '已完成' ? 'success' : row.issue.status === '处理中' ? 'warning' : 'info'" size="small">{{ row.issue.status }}</el-tag></template></el-table-column>
      <el-table-column prop="issue.description" label="问题描述" width="300"><template #default="{ row }"><span class="opl-description" :title="row.issue.description">{{ row.issue.description }}</span></template></el-table-column>
      <el-table-column label="附件" width="200"><template #default="{ row }"><div class="opl-attachments"><button v-for="file in row.issue.attachments.slice(0, 2)" :key="file.id" :title="file.name" :aria-label="`查看附件 ${file.name}`" @click.stop="attachment(row, file)"><img v-if="thumbnails[file.id]" :src="thumbnails[file.id]" :alt="file.name"><span v-else>{{ file.name }}</span></button><button v-if="row.issue.attachments.length > 2" @click.stop="open(row, true)">+{{ row.issue.attachments.length - 2 }}</button></div><span v-if="!row.issue.attachments.length">—</span></template></el-table-column>
      <el-table-column prop="issue.solution" label="解决措施" width="300" show-overflow-tooltip />
      <el-table-column label="需求日期" width="200"><template #default="{ row }"><span :class="{ 'opl-overdue': overdue(row) }">{{ row.issue.dueDate || '—' }}<small v-if="overdue(row)"> 逾期</small></span></template></el-table-column>
      <el-table-column label="负责人" width="100"><template #default="{ row }">{{ name(row.issue.owner) }}</template></el-table-column>
      <el-table-column label="完成日期" width="200"><template #default="{ row }"><button v-if="!closed(row.issue.status) && row.canComplete" class="pdm-text-action" :disabled="loading || saving || !!completing" @click.stop="complete(row)">{{ completing === row.issue.id ? '完成中…' : '完成' }}</button><span v-else>{{ date(row.issue.completedAt) }}</span></template></el-table-column>
      <el-table-column label="提交日期" width="200"><template #default="{ row }">{{ date(row.issue.createdAt) }}</template></el-table-column>
      <el-table-column label="提交人" width="100"><template #default="{ row }">{{ row.issue.createdByDisplayName || name(row.issue.createdBy) }}</template></el-table-column>
      <el-table-column label="操作" width="200" fixed="right"><template #default="{ row }"><button class="pdm-text-action" @click.stop="open(row, true)">查看</button><button v-if="row.canEdit" class="pdm-text-action" @click.stop="open(row)">编辑</button></template></el-table-column>
    </el-table></div>
    <footer class="opl-footer"><span>共 {{ filtered.length }} 条</span><el-checkbox v-model="notifyUpdates" :disabled="saving || !!completing">更新或完成后通知钉钉群</el-checkbox><el-pagination v-model:current-page="page" :page-size="20" :total="filtered.length" layout="prev, pager, next" /></footer>
    <el-drawer class="opl-drawer" v-model="drawer" direction="rtl" :title="readonly ? '查看开口问题' : editing ? '编辑开口问题' : '新增开口问题'" size="min(680px, 100vw)" append-to-body destroy-on-close :before-close="close" :close-on-click-modal="false">
      <el-form label-position="top" :disabled="saving || readonly" class="opl-form" @submit.prevent="save">
        <div class="opl-project-fields"><el-form-item label="所属项目"><el-input :model-value="`${rootProject.code} · ${rootProject.name}`" disabled /></el-form-item>
        <el-form-item label="子项目"><el-select v-model="draft.relatedProjectId" aria-label="关联子项目" clearable filterable placeholder="不选择，表示项目整体问题"><el-option v-for="item in subprojects" :key="item.id" :label="`${item.code} · ${item.name}`" :value="item.id" /></el-select></el-form-item></div>
        <el-form-item label="问题描述" required><el-input v-model="draft.description" aria-label="问题描述" type="textarea" :rows="4" maxlength="2000" show-word-limit placeholder="请描述现场问题" /></el-form-item>
        <div class="opl-form-grid"><el-form-item label="优先级"><el-select v-model="draft.priority" aria-label="优先级"><el-option v-for="item in priorities" :key="item" :label="item" :value="item" /></el-select></el-form-item><el-form-item label="实施状态"><el-select v-model="draft.status" aria-label="实施状态"><el-option v-for="item in states" :key="item" :label="item" :value="item" :disabled="item === '已完成' && editing !== null && !editing.canComplete && editing.issue.status !== '已完成'" /></el-select></el-form-item><el-form-item label="负责人"><el-select v-model="draft.owner" aria-label="负责人" clearable filterable placeholder="待分派"><el-option v-for="person in view.people" :key="person.username" :label="person.displayName" :value="person.username" /></el-select></el-form-item><el-form-item label="需求完成日期"><el-date-picker v-model="draft.dueDate" aria-label="需求完成日期" type="date" value-format="YYYY-MM-DD" placeholder="请选择日期" style="width:100%" /></el-form-item></div>
        <el-form-item label="解决措施"><el-input v-model="draft.solution" aria-label="解决措施" type="textarea" :rows="4" maxlength="4000" show-word-limit placeholder="请填写处理过程和解决结果" /></el-form-item>
      </el-form>
      <section class="opl-upload"><strong>图片和附件</strong><label v-if="!readonly" class="opl-file-picker">上传图片 / 附件<input type="file" multiple :disabled="saving" aria-label="上传图片和附件" @change="selectFiles"></label><small v-if="!readonly">最多10个附件，单个不超过10MB，单次总大小不超过30MB。</small><div v-for="file in retained" :key="file.id" class="opl-file"><button class="pdm-text-action" @click="editing && attachment(editing, file)">{{ file.name }}</button><button v-if="!readonly" class="pdm-text-action" :disabled="saving" @click="retained = retained.filter(item => item.id !== file.id)">移除</button></div><div v-for="(file, index) in files" :key="index" class="opl-file"><span>{{ file.name }}</span><button class="pdm-text-action" :disabled="saving" @click="files.splice(index, 1)">移除</button></div><span v-if="readonly && !retained.length">暂无附件</span></section>
      <p v-if="editing" class="opl-meta">提交人：{{ editing.issue.createdByDisplayName || name(editing.issue.createdBy) }} · 提交日期：{{ date(editing.issue.createdAt) }} · 最近更新：{{ editing.issue.updatedBy === editing.issue.createdBy ? (editing.issue.createdByDisplayName || name(editing.issue.updatedBy)) : name(editing.issue.updatedBy) }}</p>
      <el-alert v-if="formError" :title="formError" type="error" :closable="false" />
      <template #footer><el-checkbox v-if="editing && !readonly" v-model="notifyUpdates" :disabled="saving">保存后通知钉钉群</el-checkbox><el-button :disabled="saving" @click="close()">{{ readonly ? '关闭' : '取消' }}</el-button><el-button v-if="!readonly" type="primary" :loading="saving" @click="save">保存问题</el-button></template>
    </el-drawer>
    <el-dialog :model-value="!!previewUrl" :title="previewName" width="min(1000px, 95vw)" append-to-body @close="closePreview"><PdfDrawingViewer v-if="previewUrl && previewKind === 'pdf'" :url="previewUrl" /><img v-else-if="previewUrl" :src="previewUrl" alt="问题附件预览" style="display:block;max-width:100%;max-height:75vh;margin:auto"><template #footer><a :href="previewUrl" :download="previewName">下载附件</a></template></el-dialog>
  </section>
</template>

<style scoped>
.opl-manager{display:flex;flex-direction:column;flex:1;min-width:0;min-height:0;overflow:hidden;gap:var(--pdm-container-gap,10px)}
.opl-manager .pdm-panel-header{display:flex;align-items:center;justify-content:space-between;gap:12px;flex-wrap:wrap;margin:0}.opl-manager h2{margin:0 0 5px;font-size:var(--pdm-font-size-heading)}
.opl-scope{display:block;margin-bottom:5px;color:var(--pdm-blue);font-size:var(--pdm-font-size-body)}
.opl-counts,.opl-meta,.opl-footer,.opl-upload small{color:var(--pdm-muted);font-size:var(--pdm-font-size-body)}.opl-actions{display:flex;align-items:center;gap:5px}.opl-actions .el-button{box-sizing:border-box;width:80px;min-width:80px;max-width:80px;height:30px;min-height:30px;flex:0 0 80px;padding:0;margin:0;font-size:12px}.opl-actions svg{margin-right:4px}
.opl-filters{display:flex;flex-wrap:wrap;gap:8px}.opl-filters>.el-input{flex:1 1 210px;max-width:360px}.opl-filters>.el-select{width:145px}
.opl-table-wrap{flex:1;min-height:160px;min-width:0;overflow:hidden}.opl-description{text-align:left;white-space:pre-wrap;overflow-wrap:anywhere}.opl-overdue{color:var(--pdm-danger,#dc2626)}
.opl-attachments{display:flex;gap:4px;align-items:center}.opl-attachments button{padding:0;border:0;background:transparent;color:var(--pdm-blue);max-width:54px;overflow:hidden;text-overflow:ellipsis;white-space:nowrap;cursor:pointer}.opl-attachments img{width:44px;height:36px;object-fit:cover;border-radius:3px}.opl-footer{display:flex;justify-content:space-between;align-items:center;flex-wrap:wrap}.opl-table .pdm-text-action+.pdm-text-action{margin-left:8px}
.opl-form-grid{display:grid;grid-template-columns:1fr 1fr;gap:0 16px}.opl-upload{display:flex;flex-direction:column;gap:10px}.opl-file-picker{border:1px dashed var(--pdm-border);padding:16px;display:flex;flex-direction:column;gap:10px;border-radius:var(--pdm-radius-panel)}.opl-file{display:flex;align-items:center;justify-content:space-between;gap:12px;overflow-wrap:anywhere}.opl-file>.pdm-text-action:first-child{text-align:left}.opl-meta{margin-top:20px}
@media(max-width:600px){.opl-form-grid{grid-template-columns:1fr}.opl-filters>.el-select{flex:1 1 120px}.opl-filters>.el-input{max-width:none}.opl-counts{line-height:1.8}}
.opl-table :deep(.el-table__row) { height: 30px; cursor: pointer; }
.opl-table :deep(.el-table__body .el-table__cell) { height: 30px; padding: 0; }
.opl-table :deep(.el-table__cell) { text-align: center; }
.opl-table :deep(.cell) { white-space: nowrap; overflow: hidden; text-overflow: ellipsis; line-height: 20px; }
.opl-description { white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
.opl-attachments { justify-content: center; max-height: 28px; overflow: hidden; }
.opl-attachments img { height: 24px; width: 28px; }
:global(.opl-drawer .el-drawer__body) { min-height: 0; overflow: hidden; display: flex; flex-direction: column; gap: 8px; padding: 12px 16px; }
:global(.opl-drawer .el-drawer__header) { margin-bottom: 0; flex: 0 0 auto; }
:global(.opl-drawer .el-drawer__footer) { flex: 0 0 auto; }
.opl-form { min-height: 0; flex: 0 0 auto; display: grid; grid-template-rows: auto auto auto auto; gap: 8px; }
.opl-project-fields { display: grid; grid-template-columns: minmax(0, 1fr) minmax(0, 1fr); gap: 12px; }
.opl-form-grid { grid-template-columns: repeat(4, minmax(0, 1fr)); gap: 8px; }
.opl-form :deep(.el-form-item) { min-width: 0; min-height: 0; margin-bottom: 0; display: flex; flex-direction: column; }
.opl-form :deep(.el-form-item__label) { padding: 0; margin-bottom: 4px; height: auto; line-height: 20px; }
.opl-form :deep(.el-form-item__content) { min-width: 0; min-height: 0; flex: 1; }
.opl-form :deep(.el-textarea) { height: 100px; }
.opl-form :deep(.el-textarea__inner) { height: 100px; min-height: 100px !important; resize: none; }
.opl-form :deep(.el-select), .opl-form :deep(.el-input) { min-width: 0; width: 100%; }
.opl-upload { flex: 0 1 auto; min-height: 0; max-height: min(180px, 25vh); overflow: auto; gap: 6px; }
.opl-file-picker { padding: 8px; gap: 6px; }
.opl-meta { flex: 0 0 auto; margin: 0; font-size: 11px; }
</style>
