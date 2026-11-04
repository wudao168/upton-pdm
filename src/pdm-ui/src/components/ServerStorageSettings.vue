<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { readServerStorage, readStorageBackupPlan, saveServerStorage } from '../api'
import type { ServerStorageSettings, ServerStorageView, StorageBackupPlan } from '../serverStorage'
import { ElMessage } from '../statusMessage'

const props = defineProps<{ token: string }>()
const view = ref<ServerStorageView>()
const draft = ref<ServerStorageSettings>()
const plan = ref<StorageBackupPlan>()
const busy = ref(false)
const checking = ref(false)
const error = ref('')
const fields = [
  { key: 'projectFileRoot', label: '普通项目文件根目录', note: '合同、说明书、技术资料等新上传文件使用此位置；历史版本按上传时的位置读取。' },
  { key: 'oplAttachmentRoot', label: 'OPL附件根目录', note: '包括网页和钉钉提交附件；新附件记录上传位置，旧附件继续使用保留的历史地址。' },
  { key: 'programTemplateRoot', label: '程序模板根目录', note: '保存程序包及测试证明；目录变更后保留旧位置供历史版本读取。' },
  { key: 'uploadTempRoot', label: '上传暂存根目录', note: '保存分块、上传会话和回执；旧会话继续从原位置读取。' },
  { key: 'browserCadArchiveRoot', label: '网页CAD存档任务目录', note: '保存上传原件、解析任务及待提交结果；已有任务仍保留在原位置。' },
  { key: 'conversionWorkRoot', label: '服务器本机转图工作目录', note: '转换过程使用的临时目录；正式PDF和STEP仍存入项目图档库及生产发包库。' },
  { key: 'clientLogRoot', label: '客户端集中日志目录', note: '服务器接收的诊断日志；保留旧日志位置，不改变各客户端自己的本地日志目录。' },
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
    <header class="pdm-manager-heading"><div><h2>集中存储与备份</h2><p>所有历史文件保留。保存地址只控制后续写入，并保留旧地址供读取；不会搬迁或删除现有文件。</p></div></header>
    <div v-if="error" role="alert">{{ error }}<button type="button" class="pdm-text-action" @click="load">重新加载</button></div>
    <template v-else-if="draft && view">
      <p>图档原件、版本PDF与STEP、审核批注及验收资料随上方项目图档库保存；正式交付文件随生产发包库保存；料品、套件和BOM上传附件随料品资料库保存。标准结构使用下方独立存档目录。</p>
      <p>以上为服务器物理位置，不改变项目里的机械图纸、电气图纸、客户资料等业务目录、文件归属或权限。各客户端的本地工作区和下载目录仍在对应电脑设置。</p>
      <div class="pdm-settings-form">
        <label v-for="field in fields" :key="field.key">{{ field.label }}<input v-model="draft[field.key]" :aria-label="field.label" :disabled="busy"><small>{{ field.note }}</small></label>
        <label>备份目标根目录<input v-model="draft.backupRoot" aria-label="备份目标根目录" :disabled="busy" placeholder="待确认，可填写独立备份盘目录或UNC共享目录"><small>留空表示备份目标待确认，不执行备份。数据库、文件和恢复配置按同一备份批次保存；不能与源目录相同或互相包含。</small></label>
      </div>
      <p>当前数据库备份位置：{{ view.databaseBackupRoot || '待确认' }}；文件备份位置：{{ view.filesBackupRoot || '待确认' }}。</p>
      <details>
        <summary>查看历史位置与待迁移配置</summary>
        <p>旧版OPL附件位置：{{ draft.legacyOplAttachmentRoot }}。历史地址只保留用于读取和备份，不能通过本页面移除。</p>
        <p v-for="(roots, kind) in draft.historicalRoots" :key="kind">历史目录：{{ roots.join('；') }}</p>
        <p>数据库当前目录：{{ view.databaseDataRoot || '当前使用内存数据库' }}；配置与密钥当前目录：{{ view.secretsRoot }}。</p>
        <p>下列地址仅登记迁移目标，保存后仍使用当前目录。正式迁移时间另行确认，不能直接移动正在使用的数据库、加密密钥或远程转图任务。</p>
        <div class="pdm-settings-form">
          <label>数据库数据迁移目标<input v-model="draft.databaseDataTarget" aria-label="数据库数据迁移目标" :disabled="busy" placeholder="待正式迁移时确认"></label>
          <label>配置与密钥迁移目标<input v-model="draft.secretsTarget" aria-label="配置与密钥迁移目标" :disabled="busy" placeholder="待正式迁移时确认"><small>Windows本机加密密钥需验证跨服务器恢复能力。</small></label>
          <label>远程转图工作目录目标<input v-model="draft.previewAgentWorkTarget" aria-label="远程转图工作目录目标" :disabled="busy" placeholder="待转图电脑迁移时确认"><small>转图服务器：{{ view.previewAgentUrl || '未配置远程转图' }}；需在转图代理实际修改后核对。</small></label>
        </div>
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
