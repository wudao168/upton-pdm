<script setup lang="ts">
import { ElMessage } from '../statusMessage'
import { ElMessageBox } from 'element-plus'
import { computed, reactive, ref, watch } from 'vue'
import type { ProjectContentResetReadiness, ProjectContentResetSnapshotSummary, ProjectCopyOptionsInput, ProjectCopyPreview, ProjectCopyResult, ProjectSummary } from '../types'
import { getProjectContentResetReadiness, resetProjectContent, restoreProjectContent } from '../api'

const props = defineProps<{
  modelValue: boolean
  project: ProjectSummary
  projects: ProjectSummary[]
  token?: string
  canCopyContent: boolean
  canResetContent?: boolean
  pending: boolean
  onPreviewProjectCopy?: (projectId: string, input: ProjectCopyOptionsInput) => Promise<ProjectCopyPreview>
  onCopyProjectContent?: (projectId: string, input: ProjectCopyOptionsInput) => Promise<ProjectCopyResult>
  onContentResetComplete?: () => Promise<unknown>
}>()

const emit = defineEmits<{ 'update:modelValue': [value: boolean] }>()
const copyPreview = ref<ProjectCopyPreview | null>(null)
const copyPreviewPending = ref(false)
const copyInput = reactive<ProjectCopyOptionsInput>({
  sourceProjectId: '', copyModels: true, copyDrawings: true, copyBom: true, copyValidationItems: true, folderIds: null,
})
const resetReadiness = ref<ProjectContentResetReadiness | null>(null)
const resetPending = ref(false)
const includeChildren = ref(false)
const resetReason = ref('')
const resetConfirmation = ref('')
type SettingsTab = 'copy' | 'reset'
const activeSettingsTab = ref<SettingsTab>(props.canCopyContent ? 'copy' : 'reset')

const copySourceProjects = computed(() => props.projects
  .filter(item => item.id !== props.project.id && item.canReadContent)
  .sort((left, right) => right.code.localeCompare(left.code, 'zh-CN', { numeric: true, sensitivity: 'base' })))

function resetCopy() {
  copyPreview.value = null
  copyPreviewPending.value = false
  copyInput.sourceProjectId = ''
  copyInput.copyModels = true
  copyInput.copyDrawings = true
  copyInput.copyBom = true
  copyInput.copyValidationItems = true
  copyInput.folderIds = null
}

function resetDangerForm() {
  resetReadiness.value = null
  resetPending.value = false
  includeChildren.value = false
  resetReason.value = ''
  resetConfirmation.value = ''
}

function resetSettingsTab() {
  activeSettingsTab.value = props.canCopyContent ? 'copy' : 'reset'
}

function selectSettingsTab(tab: SettingsTab) {
  activeSettingsTab.value = tab
  if (tab === 'reset' && props.canResetContent && !resetReadiness.value) void refreshResetReadiness()
}

watch(() => props.modelValue, open => { if (open) { resetCopy(); resetDangerForm(); resetSettingsTab(); if (activeSettingsTab.value === 'reset' && props.canResetContent) void refreshResetReadiness() } }, { immediate: true })
watch(() => props.project.id, () => { resetCopy(); resetDangerForm(); resetSettingsTab(); if (props.modelValue && activeSettingsTab.value === 'reset' && props.canResetContent) void refreshResetReadiness() })

function closeSettings() {
  emit('update:modelValue', false)
}

function formatCopyBytes(value: number) {
  if (value < 1024) return `${value} B`
  if (value < 1024 * 1024) return `${(value / 1024).toFixed(1)} KB`
  if (value < 1024 * 1024 * 1024) return `${(value / 1024 / 1024).toFixed(1)} MB`
  return `${(value / 1024 / 1024 / 1024).toFixed(2)} GB`
}

async function refreshCopyPreview(resetFolders = false) {
  if (!copyInput.sourceProjectId || !props.onPreviewProjectCopy) {
    copyPreview.value = null
    return
  }
  copyPreviewPending.value = true
  try {
    const preview = await props.onPreviewProjectCopy(props.project.id, {
      ...copyInput,
      folderIds: resetFolders ? null : [...(copyInput.folderIds ?? [])],
    })
    copyPreview.value = preview
    if (resetFolders) copyInput.folderIds = preview.folders.filter(folder => folder.defaultSelected).map(folder => folder.id)
  } catch (error) {
    copyPreview.value = null
    ElMessage.error(error instanceof Error ? error.message : '复制范围预检失败')
  } finally {
    copyPreviewPending.value = false
  }
}

async function executeProjectCopy() {
  if (!copyPreview.value?.canExecute || !copyInput.sourceProjectId || !props.onCopyProjectContent) return
  const source = props.projects.find(item => item.id === copyInput.sourceProjectId)
  try {
    await ElMessageBox.confirm(
      `确认将“${source?.code ?? '源项目'}”的所选最新内容复制到已创建项目“${props.project.code}”吗？\n\n新图档将获得新的受控身份并从 W1 开始；不会覆盖目标已有内容。验证计划只复制检查项目，不复制附件、审批、执行记录和结果。`,
      '确认复制项目内容',
      { type: 'warning', confirmButtonText: '确认复制', cancelButtonText: '取消' },
    )
    const result = await props.onCopyProjectContent(props.project.id, { ...copyInput, folderIds: [...(copyInput.folderIds ?? [])] })
    closeSettings()
    ElMessage.success(`复制完成：图档 ${result.documentCount}，BOM ${result.bomItemCount}，验证检查项 ${result.validationItemCount}，项目文件 ${result.projectFileCount}`)
  } catch (error) {
    if (error === 'cancel' || error === 'close') return
    ElMessage.error(error instanceof Error ? error.message : '项目内容复制失败')
  }
}

async function refreshResetReadiness() {
  resetPending.value = true
  try {
    resetReadiness.value = await getProjectContentResetReadiness(props.project.id, includeChildren.value, props.token ?? '')
  } catch (error) {
    resetReadiness.value = null
    ElMessage.error(error instanceof Error ? error.message : '项目内容检查失败')
  } finally {
    resetPending.value = false
  }
}

function formatResetDate(value: string) {
  return new Date(value).toLocaleString('zh-CN', { hour12: false })
}

async function executeProjectReset() {
  if (!resetReadiness.value?.canReset || !resetReason.value.trim() || resetConfirmation.value.trim().toLocaleLowerCase() !== props.project.code.toLocaleLowerCase()) return
  try {
    await ElMessageBox.confirm(
      `确认重置“${props.project.code}”的项目内容？\n\n系统将保留项目基本信息和人员分配，清空所列业务内容，并创建可恢复30天的整项快照。该操作不能通过刷新页面撤销。`,
      '确认重置项目内容',
      { type: 'error', confirmButtonText: '确认重置', cancelButtonText: '取消' },
    )
    resetPending.value = true
    await resetProjectContent(props.project.id, includeChildren.value, resetReason.value.trim(), resetConfirmation.value.trim(), props.token ?? '')
    await props.onContentResetComplete?.()
    resetReason.value = ''
    resetConfirmation.value = ''
    await refreshResetReadiness()
    ElMessage.success('项目内容已重置，整项快照将在30天内保留')
  } catch (error) {
    if (error !== 'cancel' && error !== 'close') ElMessage.error(error instanceof Error ? error.message : '项目内容重置失败')
  } finally {
    resetPending.value = false
  }
}

async function executeSnapshotRestore(snapshot: ProjectContentResetSnapshotSummary) {
  try {
    const { value } = await ElMessageBox.prompt(`输入项目号 ${snapshot.projectCode} 以恢复此快照。恢复仅能在项目尚未产生新内容时执行。`, '恢复项目内容快照', {
      type: 'warning', confirmButtonText: '确认恢复', cancelButtonText: '取消', inputPattern: new RegExp(`^${snapshot.projectCode.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')}$`, 'i'), inputErrorMessage: '项目号不匹配',
    })
    resetPending.value = true
    await restoreProjectContent(props.project.id, snapshot.id, value, props.token ?? '')
    await props.onContentResetComplete?.()
    await refreshResetReadiness()
    ElMessage.success('项目内容快照已恢复')
  } catch (error) {
    if (error !== 'cancel' && error !== 'close') ElMessage.error(error instanceof Error ? error.message : '快照恢复失败')
  } finally {
    resetPending.value = false
  }
}
</script>

<template>
  <el-drawer :model-value="modelValue" :title="`项目设置 · ${project.code}`" size="720px" :close-on-click-modal="false" @update:model-value="emit('update:modelValue', $event)">
    <section class="pdm-project-settings" aria-label="项目设置内容">
      <nav v-if="canCopyContent || canResetContent" class="pdm-project-settings__tabs" role="tablist" aria-label="项目设置选项卡">
        <button v-if="canCopyContent" type="button" role="tab" :aria-selected="activeSettingsTab === 'copy'" @click="selectSettingsTab('copy')">项目复制</button>
        <button v-if="canResetContent" type="button" role="tab" :aria-selected="activeSettingsTab === 'reset'" @click="selectSettingsTab('reset')">项目重置</button>
      </nav>
      <section v-if="canCopyContent" v-show="activeSettingsTab === 'copy'" class="pdm-project-settings__section" aria-labelledby="pdm-project-copy-settings-title">
        <header>
          <h3 id="pdm-project-copy-settings-title">项目复制</h3>
          <p>从已有项目选择需要复用的最新设计资料，不会覆盖目标项目已有内容。</p>
        </header>
        <div class="pdm-project-staffing-form" aria-label="复制项目内容设置">
          <label class="pdm-dialog-field">目标项目<el-input :model-value="`${project.code} · ${project.name}`" disabled /></label>
          <label class="pdm-dialog-field">源项目<el-select v-model="copyInput.sourceProjectId" filterable placeholder="选择已有项目" style="width:100%" @change="refreshCopyPreview(true)"><el-option v-for="item in copySourceProjects" :key="item.id" :label="`${item.code} · ${item.name}`" :value="item.id" /></el-select></label>
          <div class="pdm-dialog-field">
            <span>默认复制内容</span>
            <el-checkbox v-model="copyInput.copyModels" @change="refreshCopyPreview()">最新 3D 图档</el-checkbox>
            <el-checkbox v-model="copyInput.copyDrawings" @change="refreshCopyPreview()">最新 2D 图档</el-checkbox>
            <el-checkbox v-model="copyInput.copyBom" @change="refreshCopyPreview()">BOM</el-checkbox>
            <el-checkbox v-model="copyInput.copyValidationItems" @change="refreshCopyPreview()">验证计划检查项目</el-checkbox>
          </div>
          <div v-if="copyPreview" class="pdm-dialog-field">
            <span>其他项目文件夹（仅“气路时序”默认勾选）</span>
            <el-checkbox-group v-model="copyInput.folderIds" @change="refreshCopyPreview()">
              <div v-for="folder in copyPreview.folders" :key="folder.id"><el-checkbox :value="folder.id">{{ folder.path }}（{{ folder.fileCount }} 个文件）</el-checkbox></div>
            </el-checkbox-group>
          </div>
          <p v-if="copyPreviewPending" class="pdm-counter-note">正在检查源文件、目标空白状态和权限…</p>
          <template v-else-if="copyPreview">
            <p class="pdm-counter-note">预检范围：3D {{ copyPreview.modelCount }}，2D {{ copyPreview.drawingCount }}，BOM {{ copyPreview.bomItemCount }}，验证检查项 {{ copyPreview.validationItemCount }}，项目文件 {{ copyPreview.projectFileCount }}；共 {{ formatCopyBytes(copyPreview.totalBytes) }}。</p>
            <p v-for="reason in copyPreview.blockingReasons" :key="reason" class="pdm-counter-note is-warning">不能复制：{{ reason }}</p>
            <p v-for="warning in copyPreview.warnings" :key="warning" class="pdm-counter-note">提示：{{ warning }}</p>
          </template>
          <p v-else class="pdm-counter-note">目标项目必须先创建。选择源项目后，系统会检查目标是否已有同类内容。</p>
        </div>
      </section>
      <section v-if="canResetContent" v-show="activeSettingsTab === 'reset'" class="pdm-project-settings__section pdm-project-settings__danger" aria-labelledby="pdm-project-reset-settings-title">
        <header><h3 id="pdm-project-reset-settings-title">项目重置</h3><p>仅系统管理员可执行。保留项目基本信息和人员分配，业务内容进入可恢复30天的整项快照。</p></header>
        <el-alert title="危险操作" type="error" :closable="false" description="如已发布BOM，或存在进行中的审批、发布、CAD属性写回、外部同步及已签出图档，服务端将拒绝重置。项目计划和验证计划会随项目内容一并进入快照。" />
        <div class="pdm-reset-scope">
          <el-checkbox v-model="includeChildren" :disabled="resetPending" @change="refreshResetReadiness">同时重置下属子项目</el-checkbox>
          <button type="button" class="pdm-secondary-action" :disabled="resetPending" @click="refreshResetReadiness">{{ resetPending ? '正在检查…' : '重新检查范围' }}</button>
        </div>
        <template v-if="resetReadiness">
          <p class="pdm-counter-note">重置范围：{{ resetReadiness.includedProjects.map(item => item.code).join('、') }}</p>
          <div class="pdm-reset-counts"><span v-for="(count, label) in resetReadiness.counts" :key="label"><strong>{{ count }}</strong>{{ label }}</span></div>
          <el-alert v-if="resetReadiness.blockers.length" title="当前不能重置" type="error" :closable="false"><ul><li v-for="blocker in resetReadiness.blockers" :key="blocker">{{ blocker }}</li></ul></el-alert>
          <div v-if="resetReadiness.restorableSnapshots.length" class="pdm-reset-snapshots"><strong>30天内可恢复的快照</strong><div v-for="snapshot in resetReadiness.restorableSnapshots" :key="snapshot.id"><span>{{ formatResetDate(snapshot.createdAt) }} · {{ snapshot.createdBy }} · {{ snapshot.reason }}</span><button type="button" class="pdm-secondary-action" :disabled="resetPending" @click="executeSnapshotRestore(snapshot)">恢复</button></div></div>
          <template v-if="resetReadiness.canReset && Object.values(resetReadiness.counts).some(count => count > 0)">
            <label class="pdm-dialog-field">重置原因 <b>*</b><el-input v-model="resetReason" type="textarea" :rows="3" maxlength="500" show-word-limit placeholder="请填写可审计的重置原因" /></label>
            <label class="pdm-dialog-field">输入项目号确认 <b>*</b><el-input v-model="resetConfirmation" :placeholder="`请输入 ${project.code}`" /></label>
            <button type="button" class="pdm-danger-action" :disabled="resetPending || !resetReason.trim() || resetConfirmation.trim().toLocaleLowerCase() !== project.code.toLocaleLowerCase()" @click="executeProjectReset">{{ resetPending ? '正在重置…' : '重置项目内容' }}</button>
          </template>
          <p v-else-if="resetReadiness.canReset" class="pdm-counter-note">当前项目没有可重置的业务内容。</p>
        </template>
      </section>
      <p v-if="!canCopyContent && !canResetContent" class="pdm-project-settings__empty">当前账号没有可操作的项目设置。</p>
    </section>
    <template #footer>
      <button type="button" class="pdm-secondary-action" :disabled="pending" @click="closeSettings">取消</button>
      <button v-if="canCopyContent && activeSettingsTab === 'copy'" type="button" class="pdm-primary-action" :disabled="pending || copyPreviewPending || !copyPreview?.canExecute" @click="executeProjectCopy">{{ pending ? '正在复制…' : '确认复制' }}</button>
    </template>
  </el-drawer>
</template>

<style scoped>
.pdm-project-settings{display:flex;flex-direction:column;gap:18px}.pdm-project-settings__tabs{display:flex;gap:4px;padding:3px;border:1px solid var(--pdm-border);border-radius:7px;background:var(--pdm-surface-muted)}.pdm-project-settings__tabs button{flex:1;min-height:34px;border:0;border-radius:5px;background:transparent;color:var(--pdm-muted);font-weight:600;cursor:pointer}.pdm-project-settings__tabs button[aria-selected="true"]{background:#fff;color:var(--pdm-blue);box-shadow:0 1px 4px rgba(15,23,42,.1)}.pdm-project-settings__section{display:flex;flex-direction:column;gap:14px;padding-bottom:18px;border-bottom:1px solid var(--pdm-border)}.pdm-project-settings__section header h3{margin:0 0 5px}.pdm-project-settings__section header p{margin:0;color:var(--pdm-muted)}.pdm-project-settings__danger{border:1px solid #f2c5c0;border-radius:8px;padding:16px;background:#fffafa}.pdm-reset-scope{display:flex;align-items:center;justify-content:space-between;gap:12px}.pdm-reset-counts{display:grid;grid-template-columns:repeat(2,minmax(0,1fr));gap:8px}.pdm-reset-counts span{display:flex;align-items:baseline;gap:7px;padding:9px 11px;border:1px solid #e2e8f0;border-radius:6px;background:#fff;color:var(--pdm-muted)}.pdm-reset-counts strong{font-size:18px;color:var(--pdm-text)}.pdm-reset-snapshots{display:flex;flex-direction:column;gap:8px}.pdm-reset-snapshots>div{display:flex;align-items:center;justify-content:space-between;gap:12px;padding:8px 10px;border:1px solid #e2e8f0;border-radius:6px;background:#fff}.pdm-reset-snapshots span{min-width:0;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}.pdm-project-settings__danger ul{margin:8px 0 0;padding-left:20px}.pdm-dialog-field b{color:var(--pdm-danger)}.pdm-danger-action{align-self:flex-start;min-height:34px;border:1px solid #dc2626;border-radius:5px;padding:0 14px;background:#dc2626;color:#fff;cursor:pointer}.pdm-danger-action:disabled{cursor:not-allowed;opacity:.45}
</style>
