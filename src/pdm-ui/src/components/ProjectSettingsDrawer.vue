<script setup lang="ts">
import { ElMessage } from '../statusMessage'
import { ElMessageBox } from 'element-plus'
import { computed, reactive, ref, watch } from 'vue'
import type { ProjectCopyOptionsInput, ProjectCopyPreview, ProjectCopyResult, ProjectSummary } from '../types'

const props = defineProps<{
  modelValue: boolean
  project: ProjectSummary
  projects: ProjectSummary[]
  canCopyContent: boolean
  pending: boolean
  onPreviewProjectCopy: (projectId: string, input: ProjectCopyOptionsInput) => Promise<ProjectCopyPreview>
  onCopyProjectContent: (projectId: string, input: ProjectCopyOptionsInput) => Promise<ProjectCopyResult>
}>()

const emit = defineEmits<{ 'update:modelValue': [value: boolean] }>()
const copyPreview = ref<ProjectCopyPreview | null>(null)
const copyPreviewPending = ref(false)
const copyInput = reactive<ProjectCopyOptionsInput>({
  sourceProjectId: '', copyModels: true, copyDrawings: true, copyBom: true, copyValidationItems: true, folderIds: null,
})

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

watch(() => props.modelValue, open => { if (open) resetCopy() })
watch(() => props.project.id, resetCopy)

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
  if (!copyInput.sourceProjectId) {
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
  if (!copyPreview.value?.canExecute || !copyInput.sourceProjectId) return
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
</script>

<template>
  <el-drawer :model-value="modelValue" :title="`项目设置 · ${project.code}`" size="720px" :close-on-click-modal="false" @update:model-value="emit('update:modelValue', $event)">
    <section class="pdm-project-settings" aria-label="项目设置内容">
      <section v-if="canCopyContent" class="pdm-project-settings__section" aria-labelledby="pdm-project-copy-settings-title">
        <header>
          <h3 id="pdm-project-copy-settings-title">项目内容复制</h3>
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
      <p v-else class="pdm-project-settings__empty">当前账号没有可操作的项目设置。</p>
    </section>
    <template #footer>
      <button type="button" class="pdm-secondary-action" :disabled="pending" @click="closeSettings">取消</button>
      <button v-if="canCopyContent" type="button" class="pdm-primary-action" :disabled="pending || copyPreviewPending || !copyPreview?.canExecute" @click="executeProjectCopy">{{ pending ? '正在复制…' : '确认复制' }}</button>
    </template>
  </el-drawer>
</template>
