<script setup lang="ts">
import { onMounted, ref, useId } from 'vue'
import { readServerStorage, readStorageBackupPlan, saveServerStorage } from '../api'
import type { ServerStorageSettings, ServerStorageView, StorageBackupPlan } from '../serverStorage'
import { ElMessage } from '../statusMessage'
import StorageDirectoryList from './StorageDirectoryList.vue'

const props = defineProps<{ token: string }>()
const view = ref<ServerStorageView>()
const draft = ref<ServerStorageSettings>()
const plan = ref<StorageBackupPlan>()
const busy = ref(false)
const checking = ref(false)
const error = ref('')
const storageId = useId()
const fields = [
  { key: 'projectFileRoot', label: '普通项目文件根目录' },
  { key: 'oplAttachmentRoot', label: 'OPL附件根目录' },
  { key: 'programTemplateRoot', label: '程序模板根目录' },
  { key: 'uploadTempRoot', label: '上传暂存根目录' },
  { key: 'browserCadArchiveRoot', label: '网页CAD存档任务目录' },
  { key: 'conversionWorkRoot', label: '服务器本机转图工作目录' },
  { key: 'clientLogRoot', label: '客户端集中日志目录' },
] as const
const directoryFields = [...fields, { key: 'backupRoot', label: '备份目标根目录' }] as const
const migrationFields = [
  { key: 'databaseDataTarget', label: '数据库数据迁移目标' },
  { key: 'secretsTarget', label: '配置与密钥迁移目标' },
  { key: 'previewAgentWorkTarget', label: '远程转图工作目录目标' },
] as const

function accept(value: ServerStorageView) {
  view.value = value
  draft.value = { ...value.settings, historicalRoots: { ...value.settings.historicalRoots } }
}
async function load() {
  busy.value = true
  error.value = ''
  try { accept(await readServerStorage(props.token)) }
  catch { error.value = '集中存储设置加载失败，请重新加载后再配置。' }
  finally { busy.value = false }
}
async function save() {
  if (!draft.value) return
  if (fields.some(field => !draft.value![field.key].trim())) { ElMessage.warning('请填写所有服务器文件目录；备份和待迁移目标可以暂时留空。'); return }
  busy.value = true
  try {
    accept(await saveServerStorage(draft.value, props.token))
    plan.value = undefined
    ElMessage.success('存储配置已保存，历史文件及原地址保留，未执行文件迁移或备份。')
  } catch (cause) { ElMessage.error(cause instanceof Error ? cause.message : '存储配置保存失败，请重新加载后重试。') }
  finally { busy.value = false }
}
async function checkBackup() {
  checking.value = true
  try { plan.value = await readStorageBackupPlan(props.token) }
  catch (cause) { ElMessage.error(cause instanceof Error ? cause.message : '备份覆盖范围读取失败，请检查存储配置后重试。') }
  finally { checking.value = false }
}
onMounted(load)
</script>

<template>
  <section class="pdm-panel pdm-manager-panel" aria-label="集中存储与备份设置">
    <header class="pdm-manager-heading"><h2>集中存储与备份</h2></header>
    <div v-if="error" role="alert">{{ error }}<button type="button" class="pdm-text-action" @click="load">重新加载</button></div>
    <template v-else-if="draft && view">
      <StorageDirectoryList label="集中存储目录">
        <tr v-for="field in directoryFields" :key="field.key">
          <th scope="row"><label :for="`${storageId}-${field.key}`">{{ field.label }}</label></th>
          <td><input :id="`${storageId}-${field.key}`" v-model="draft[field.key]" :aria-label="field.label" :disabled="busy" class="pdm-table-input"></td>
        </tr>
        <tr><th scope="row">当前数据库备份位置</th><td>{{ view.databaseBackupRoot || '待确认' }}</td></tr>
        <tr><th scope="row">文件备份位置</th><td>{{ view.filesBackupRoot || '待确认' }}</td></tr>
      </StorageDirectoryList>
      <details>
        <summary>查看历史位置与待迁移配置</summary>
        <StorageDirectoryList label="历史位置与待迁移目录">
          <tr><th scope="row">旧版OPL附件位置</th><td>{{ draft.legacyOplAttachmentRoot }}</td></tr>
          <template v-for="(roots, kind) in draft.historicalRoots" :key="kind">
            <tr v-for="(root, index) in roots" :key="index"><th scope="row">历史目录</th><td>{{ root }}</td></tr>
          </template>
          <tr><th scope="row">数据库当前目录</th><td>{{ view.databaseDataRoot || '当前使用内存数据库' }}</td></tr>
          <tr><th scope="row">配置与密钥当前目录</th><td>{{ view.secretsRoot }}</td></tr>
          <tr><th scope="row">转图服务器</th><td>{{ view.previewAgentUrl || '未配置远程转图' }}</td></tr>
          <tr v-for="field in migrationFields" :key="field.key">
            <th scope="row"><label :for="`${storageId}-${field.key}`">{{ field.label }}</label></th>
            <td><input :id="`${storageId}-${field.key}`" v-model="draft[field.key]" :aria-label="field.label" :disabled="busy" class="pdm-table-input"></td>
          </tr>
        </StorageDirectoryList>
      </details>
      <div class="pdm-settings-actions"><button type="button" class="pdm-primary-action" :disabled="busy" @click="save">保存集中存储设置</button><button type="button" class="pdm-secondary-action" :disabled="busy || checking" @click="checkBackup">{{ checking ? '正在核对覆盖范围…' : '核对备份覆盖范围' }}</button></div>
      <div v-if="plan" role="status">
        <p>{{ plan.message }}已纳入 {{ plan.referenceCount }} 个历史文件引用、{{ plan.sources.length }} 个存储来源。</p>
        <p v-for="warning in plan.warnings" :key="warning">{{ warning }}</p>
        <details v-if="plan.issues?.length"><summary>查看 {{ plan.issues.length }} 项历史完整性问题</summary><div class="pdm-table-scroll"><table class="pdm-project-table" aria-label="历史完整性问题"><thead><tr><th>业务范围</th><th>原记录位置</th><th>核对结果</th></tr></thead><tbody><tr v-for="issue in plan.issues" :key="issue.path + issue.category"><td>{{ issue.category }}</td><td>{{ issue.path }}</td><td>{{ issue.message }}</td></tr></tbody></table></div></details>
        <details><summary>查看实际备份来源</summary><div class="pdm-table-scroll"><table class="pdm-project-table" aria-label="实际备份来源"><thead><tr><th>业务范围</th><th>实际路径</th><th>完整性要求</th></tr></thead><tbody><tr v-for="source in plan.sources" :key="source.path"><td>{{ source.categories.join('、') }}</td><td>{{ source.path }}</td><td>{{ source.exists === false ? (source.required ? '引用目录缺失，需先核查' : '尚未创建或已停用') : (source.required ? '存在数据库引用，缺失时备份失败' : '包含已存在的文件') }}</td></tr></tbody></table></div></details>
      </div>
    </template>
    <p v-else role="status">正在加载集中存储设置…</p>
  </section>
</template>

<style scoped>
.storage-directory-list + details { margin-top: 8px; }
.pdm-project-table td { white-space: normal; overflow-wrap: anywhere; }
</style>
