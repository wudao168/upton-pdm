<script setup lang="ts">
import { ListChecks } from '@lucide/vue'
import { computed, onBeforeUnmount, ref, watch } from 'vue'
import { readQualityInspections, uploadQualityInspection, readQualityInspectionFile, type QualityInspectionRecord } from '../api'
import PdfDrawingViewer from './PdfDrawingViewer.vue'
import type { ProjectSummary } from '../types'
import { ElMessage } from '../statusMessage'

const props = defineProps<{ kind: 'incoming' | 'assembly' | 'preAcceptance' | 'finalAcceptance'; writableProjectIds?: string[]; refreshVersion?: number; projects: ProjectSummary[]; projectId: string; token: string; canEdit: boolean }>()
const emit = defineEmits<{ saved: [] }>()
const customerAcceptance = computed(() => props.kind === 'preAcceptance' || props.kind === 'finalAcceptance')
const selectedKind = ref<'preAcceptance' | 'finalAcceptance'>('preAcceptance')
const title = computed(() => ({ incoming: '来料检验', assembly: '装配验收', preAcceptance: '预验收', finalAcceptance: '终验收' })[props.kind])
const records = ref<QualityInspectionRecord[]>([])
const loading = ref(false)
const error = ref('')
const dialogOpen = ref(false)
const saving = ref(false)
const targetProjectId = ref(props.projectId)
const recordTitle = ref('')
const remark = ref('')
const files = ref<File[]>([])
const uploadedFiles = ref<File[]>([])
const uploadedCount = computed(() => uploadedFiles.value.length)
const uploadFailed = ref(false)
const stationFilter = ref('')
const stations = computed(() => [...new Set(records.value.map(x => x.station).filter(Boolean))])
const visibleRecords = computed(() => records.value.filter(x => !stationFilter.value || x.station === stationFilter.value))
const writableProjects = computed(() => props.projects.filter(x => x.canReadContent && (props.writableProjectIds ? props.writableProjectIds.includes(x.id) : x.effectiveProjectPermissions?.includes('validation-plan.edit'))))
const projectLabel = (id: string) => props.projects.find(x => x.id === id)?.code ?? id
let loadGeneration = 0
async function load() {
  const generation = ++loadGeneration
  loading.value = true
  error.value = ''
  try {
    const result = await Promise.all(props.projects.filter(x => x.canReadContent).map(x => readQualityInspections(x.id, props.token)))
    if (generation === loadGeneration) records.value = result.flat().filter(x => x.kind === props.kind)
  } catch (e) { if (generation === loadGeneration) error.value = e instanceof Error ? e.message : '检验记录加载失败' }
  finally { if (generation === loadGeneration) loading.value = false }
}
function openUpload() {
  targetProjectId.value = writableProjects.value.some(x => x.id === props.projectId) ? props.projectId : writableProjects.value[0]?.id ?? ''
  selectedKind.value = props.kind === 'finalAcceptance' ? 'finalAcceptance' : 'preAcceptance'
  recordTitle.value = ''; remark.value = ''; files.value = []; uploadedFiles.value = []; uploadFailed.value = false; dialogOpen.value = true
}
function selectFile(event: Event) {
  const input = event.target as HTMLInputElement
  for (const file of Array.from(input.files ?? [])) {
    if (![...files.value, ...uploadedFiles.value].some(existing => existing.name === file.name && existing.size === file.size && existing.lastModified === file.lastModified)) files.value.push(file)
  }
  input.value = ''
}
async function save() {
  if (saving.value || !files.value.length || (!customerAcceptance.value && !recordTitle.value.trim()) || !targetProjectId.value) return
  saving.value = true
  uploadFailed.value = false
  try {
    while (files.value.length) {
      await uploadQualityInspection(targetProjectId.value, customerAcceptance.value ? selectedKind.value : props.kind, '', customerAcceptance.value ? (selectedKind.value === 'preAcceptance' ? '预验收' : '终验收') : recordTitle.value, remark.value, files.value[0]!, props.token)
      uploadedFiles.value.push(files.value[0]!)
      files.value = files.value.slice(1)
    }
    dialogOpen.value = false
    emit('saved')
    ElMessage.success(`已保存 ${uploadedCount.value} 个记录文件`)
  } catch (e) { uploadFailed.value = true; ElMessage.error(e instanceof Error ? e.message : '保存失败') }
  finally { await load(); saving.value = false }
}
const previewOpen = ref(false)
const previewLoading = ref(false)
const previewRecord = ref<QualityInspectionRecord | null>(null)
const previewUrl = ref('')
const previewKind = computed(() => /\.pdf$/i.test(previewRecord.value?.fileName ?? '') ? 'pdf' : /\.(png|jpe?g)$/i.test(previewRecord.value?.fileName ?? '') ? 'image' : 'other')
let previewGeneration = 0
function clearPreview() {
  previewGeneration++
  if (previewUrl.value) URL.revokeObjectURL(previewUrl.value)
  previewUrl.value = ''
  previewRecord.value = null
}
async function preview(record: QualityInspectionRecord) {
  if (previewLoading.value) return
  clearPreview()
  const generation = previewGeneration
  previewLoading.value = true
  try {
    const blob = await readQualityInspectionFile(record.id, props.token)
    if (generation !== previewGeneration) return
    previewRecord.value = record
    const type = /\.pdf$/i.test(record.fileName) ? 'application/pdf' : /\.png$/i.test(record.fileName) ? 'image/png' : /\.jpe?g$/i.test(record.fileName) ? 'image/jpeg' : blob.type
    previewUrl.value = URL.createObjectURL(new Blob([blob], { type }))
    previewOpen.value = true
  } catch (e) { ElMessage.error(e instanceof Error ? e.message : '预览失败') }
  finally { previewLoading.value = false }
}
onBeforeUnmount(clearPreview)

watch(() => [props.projects.map(x => x.id).join(','), props.token, props.refreshVersion], load, { immediate: true })
</script>

<template>
  <section class="pdm-panel quality-inspection" :aria-label="title">
    <header class="quality-section__toolbar"><div class="quality-section__heading"><h2><ListChecks :size="18" />{{ title }}</h2>
      <p v-if="kind === 'incoming'" class="quality-inspection__hint">独立检验记录 · 三坐标等数据文件；非标BOM质检列表将在接口对接后展示。</p>
      <p v-else class="quality-inspection__hint">按项目、子项目归档，可保存多份{{ title }}记录。</p></div><div class="quality-section__actions"><select v-if="kind === 'assembly' && stations.length" v-model="stationFilter" aria-label="筛选工站"><option value="">全部工站</option><option v-for="item in stations" :key="item">{{ item }}</option></select><button v-if="writableProjects.length" class="pdm-secondary-action" @click="openUpload">上传记录</button></div></header>
    <p v-if="error" role="alert">{{ error }} <button @click="load">重试</button></p>
    <div class="quality-inspection__scroll">
      <p v-if="loading">正在加载…</p>
      <table v-else><thead><tr><th>项目</th><th v-if="kind === 'assembly' && stations.length">工站</th><th>检验内容</th><th>文件</th><th>备注</th><th>上传人</th><th>上传时间</th></tr></thead><tbody><tr v-for="record in visibleRecords" :key="record.id"><td>{{ projectLabel(record.projectId) }}</td><td v-if="kind === 'assembly' && stations.length">{{ record.station }}</td><td>{{ record.title }}</td><td><button class="pdm-text-action"  :disabled="previewLoading" @click="preview(record)">{{ record.fileName }}</button></td><td>{{ record.remark || '—' }}</td><td>{{ record.uploadedBy }}</td><td>{{ new Date(record.uploadedAt).toLocaleString() }}</td></tr><tr v-if="!visibleRecords.length"><td :colspan="kind === 'assembly' && stations.length ? 7 : 6" class="quality-inspection__empty">暂无{{ title }}记录</td></tr></tbody></table>
    </div>
    <footer class="quality-inspection__footer"><span>共 {{ visibleRecords.length }} 条记录</span><span>范围：主项目及子项目</span></footer>
    <el-dialog v-model="previewOpen" :title="`检验文件预览 · ${previewRecord?.fileName ?? ''}`" width="90vw" top="5vh" append-to-body destroy-on-close @closed="clearPreview">
      <div class="quality-inspection__preview-actions"><a v-if="previewUrl" :href="previewUrl" :download="previewRecord?.fileName" class="el-button el-button--primary">下载文件</a></div>
      <PdfDrawingViewer v-if="previewOpen && previewUrl && previewKind === 'pdf'" :url="previewUrl" />
      <img v-else-if="previewOpen && previewUrl && previewKind === 'image'" :src="previewUrl" :alt="previewRecord?.title" class="quality-inspection__preview-image">
      <p v-else>此文件暂不支持在线预览，可下载查看。</p>
    </el-dialog>
    <el-dialog v-model="dialogOpen" :title="`上传${title}记录`" width="540px" append-to-body :close-on-click-modal="!saving" :show-close="!saving" :close-on-press-escape="!saving">
      <form class="quality-inspection__form" @submit.prevent="save">
        <label>项目 / 子项目<select v-model="targetProjectId" :disabled="saving" required><option v-for="project in writableProjects" :key="project.id" :value="project.id">{{ project.code }} · {{ project.name }}</option></select></label>
        <label v-if="customerAcceptance">检验内容<select v-model="selectedKind" :disabled="saving || uploadedCount > 0" required aria-label="检验内容"><option value="preAcceptance">预验收</option><option value="finalAcceptance">终验收</option></select></label>
        <label v-else>检验内容<input v-model="recordTitle" :disabled="saving" maxlength="200" required :placeholder="kind === 'incoming' ? '如：三坐标测量报告' : kind === 'assembly' ? '如：导轨平行度、转台跳动' : '如：客户验收报告、问题确认记录'"></label>
        <label>备注<textarea v-model="remark" :disabled="saving" maxlength="2000" rows="3" /></label>
        <label>记录文件<input type="file" multiple :disabled="saving" accept=".pdf,.png,.jpg,.jpeg,.xlsx,.xls,.csv,.zip,.txt,.docx" @change="selectFile"></label>
        <p v-if="files.length || uploadedCount" class="quality-inspection__upload-progress">已保存 {{ uploadedCount }} 个文件，待上传 {{ files.length }} 个</p>
        <ul v-if="files.length || uploadedFiles.length" class="quality-inspection__upload-list" aria-label="记录文件列表">
          <li v-for="(file, index) in uploadedFiles" :key="`saved-${index}`"><span>{{ file.name }}</span><small>已保存</small></li>
          <li v-for="(file, index) in files" :key="`pending-${index}`"><span>{{ file.name }}</span><small>{{ index === 0 && saving ? '上传中' : index === 0 && uploadFailed ? '上传失败' : '待上传' }}</small></li>
        </ul>
        <footer><button type="button" class="pdm-secondary-action" :disabled="saving" @click="dialogOpen = false">取消</button><button type="submit" class="pdm-primary-action" :disabled="saving || !files.length || !targetProjectId">{{ saving ? '上传中…' : '保存记录' }}</button></footer>
      </form>
    </el-dialog>
  </section>
</template>

<style scoped>
.quality-inspection__upload-list{list-style:none;margin:0;padding:0;border:1px solid var(--pdm-border);border-radius:5px;max-height:240px;overflow:auto}.quality-inspection__upload-list li{display:flex;align-items:center;justify-content:space-between;gap:12px;padding:8px 10px;border-bottom:1px solid var(--pdm-border)}.quality-inspection__upload-list li:last-child{border-bottom:0}.quality-inspection__upload-list span{min-width:0;overflow-wrap:anywhere}.quality-inspection__upload-list small{flex-shrink:0;color:var(--pdm-muted)}
.quality-inspection__preview-actions{margin-bottom:12px}.quality-inspection__preview-image{display:block;max-width:100%;max-height:75vh;margin:auto;object-fit:contain}
.quality-inspection{display:flex;flex-direction:column;min-height:0;min-width:0;padding:12px;gap:8px;overflow:hidden}.quality-inspection header,.quality-inspection header>div{display:flex;align-items:center;justify-content:space-between;gap:8px}.quality-inspection h2{font-size:16px;margin:0}.quality-inspection__hint{font-size:12px;color:var(--pdm-muted);margin:0}.quality-inspection__scroll{flex:1;min-height:0;overflow:auto;border:1px solid var(--pdm-border);border-radius:7px}.quality-inspection table{width:100%;border-collapse:collapse;table-layout:fixed;font-size:12px}.quality-inspection th{position:sticky;top:0;z-index:1;padding:9px 6px;border:0;background:#f3f6f9;color:var(--pdm-muted);font-weight:500;text-align:center;vertical-align:middle}.quality-inspection tbody tr{background:var(--pdm-surface)}.quality-inspection tbody tr:hover{background:var(--pdm-theme-accent-soft)}.quality-inspection td{height:38px;padding:0 6px;border:0;border-top:1px solid var(--pdm-border-soft);color:var(--pdm-text);overflow-wrap:anywhere;text-align:center;vertical-align:middle}.quality-inspection__footer{display:flex;justify-content:space-between;gap:8px;flex-shrink:0;color:var(--pdm-muted);font-size:12px}.quality-inspection__empty{text-align:center;color:var(--pdm-muted);padding:12px}.quality-inspection__form{display:grid;gap:14px}.quality-inspection__form label{display:grid;gap:6px}.quality-inspection__form input,.quality-inspection__form select,.quality-inspection__form textarea{padding:8px;border:1px solid var(--pdm-border);border-radius:5px}.quality-inspection__form footer{display:flex;justify-content:flex-end;gap:8px}
</style>
