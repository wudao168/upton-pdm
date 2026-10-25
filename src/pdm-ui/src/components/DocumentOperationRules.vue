<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue'
import { ElInput, ElPagination, ElSwitch, ElMessage } from 'element-plus'
import { getDocumentOperationRuleSettings, setDocumentOperationRule, type DocumentOperationRuleSettings } from '../api'
import { documentOperationRules, documentRuleCategories, matchesDocumentOperationRule, type DocumentRuleCategory } from '../documentOperationRules'

const query = ref('')
const props = withDefaults(defineProps<{ token?: string; canEdit?: boolean }>(), { token: '', canEdit: false })
const settings = ref<DocumentOperationRuleSettings>({ revision: 0, disabledRuleIds: [] })
const loaded = ref(false)
const loading = ref(false)
const savingId = ref('')
const settingsError = ref('')
async function loadSettings() {
  if (!props.token) return
  loading.value = true
  settingsError.value = ''
  try { settings.value = await getDocumentOperationRuleSettings(props.token); loaded.value = true }
  catch { loaded.value = false; settingsError.value = '无法读取规则状态，请检查连接后刷新；当前显示默认值。' }
  finally { loading.value = false }
}
async function changeRule(ruleId: string, enabled: boolean) {
  if (!loaded.value || !props.canEdit || savingId.value) return
  savingId.value = ruleId
  try {
    settings.value = await setDocumentOperationRule(ruleId, enabled, settings.value.revision, props.token)
    ElMessage.success(`${ruleId} ${enabled ? '已启用' : '已停用拦截'}，自动处理和诊断提示保留`)
  } catch {
    ElMessage.error('规则状态未保存，已保留原状态。请刷新后重试。')
  } finally { savingId.value = '' }
}
onMounted(loadSettings)
const category = ref<DocumentRuleCategory | ''>('')
const page = ref(1)
const pageSize = ref(200)
const categories = computed(() => [...new Set(documentOperationRules.map(rule => rule.category))])
const nonBlockingCount = documentOperationRules.filter(rule => !rule.canBlock).length
const filtered = computed(() => {
  return documentOperationRules.filter(rule => (!category.value || rule.category === category.value)
    && matchesDocumentOperationRule(rule, query.value))
})
const rows = computed(() => filtered.value.slice((page.value - 1) * pageSize.value, page.value * pageSize.value))
watch([query, category, pageSize], () => { page.value = 1 })
</script>

<template>
  <section class="pdm-document-rules" aria-label="图档操作规则">
    <aside class="pdm-panel pdm-document-rules__categories" aria-label="规则分类">
      <h2>分类</h2>
      <nav aria-label="图档规则分类">
        <button type="button" :class="{ 'is-active': category === '' }" :aria-pressed="category === ''" @click="category = ''">全部分类</button>
        <button v-for="key in categories" :key="key" type="button" :class="{ 'is-active': category === key }" :aria-pressed="category === key" @click="category = key">{{ documentRuleCategories[key] }}</button>
      </nav>
    </aside>
    <section class="pdm-panel pdm-manager-panel pdm-document-rules__content">
    <header class="pdm-manager-heading"><h2>图档操作规则</h2></header>
    <div class="pdm-document-rules__toolbar">
      <el-input v-model="query" clearable placeholder="搜索编号、规则或粘贴报错语句" aria-label="搜索图档规则" />
    </div>
    <p class="pdm-document-rules__hint">{{ nonBlockingCount }}项前置拦截已取消，自动处理和诊断提示保留。其余开关仅控制可选拦截；实际权限、文件保存、上传及打开结果仍须正确。结构或工程图待补全时可先保存已有有效内容，完成后再用于正式发布。{文件名}、{路径}为动态内容，可粘贴完整报错定位。</p>
    <p v-if="settingsError" role="alert" class="pdm-document-rules__hint">{{ settingsError }} <button type="button" @click="loadSettings">刷新状态</button></p>
    <div class="pdm-table-scroll">
      <table class="pdm-edit-table pdm-fit-columns" aria-label="图档操作规则列表">
        <colgroup><col style="width:6%" /><col style="width:8%" /><col style="width:10%" /><col style="width:12%" /><col style="width:30%" /><col style="width:34%" /></colgroup>
        <thead><tr><th>编号</th><th>拦截</th><th>分类</th><th>规则</th><th>处理方式</th><th>报错／提示语句</th></tr></thead>
        <tbody>
          <tr v-for="rule in rows" :key="rule.id"><td><span class="pdm-document-rules__cell">{{ rule.id }}</span></td><td><span class="pdm-document-rules__cell"><span v-if="!rule.canBlock">仅提示</span><el-switch v-else :model-value="!settings.disabledRuleIds.includes(rule.id)" :aria-label="`${rule.id} ${rule.name}规则启用`" :disabled="!loaded || !canEdit || loading || !!savingId" :loading="savingId === rule.id" @change="value => changeRule(rule.id, Boolean(value))" /><small v-if="rule.canBlock && !loaded">未读取</small></span></td><td><span class="pdm-document-rules__cell">{{ documentRuleCategories[rule.category] }}</span></td><td><span class="pdm-document-rules__cell">{{ rule.name }}</span></td><td><span class="pdm-document-rules__cell">{{ rule.behavior }}</span></td>
            <td><span class="pdm-document-rules__cell"><span v-for="message in rule.messages" :key="message.text" class="pdm-document-rules__message"><span class="pdm-document-rules__message-kind">{{ message.kind }}：</span>{{ message.text }}</span><span v-if="rule.messageNote" class="pdm-document-rules__hint">{{ rule.messageNote }}</span></span></td>
          </tr>
          <tr v-if="!rows.length"><td colspan="6">没有符合条件的规则</td></tr>
        </tbody>
      </table>
    </div>
    <el-pagination v-model:current-page="page" v-model:page-size="pageSize" :page-sizes="[50, 100, 200]" :total="filtered.length" layout="total, sizes, prev, pager, next" />
    </section>
  </section>
</template>

<style scoped>
.pdm-document-rules { --pdm-detail-row-height:50px; display:grid; grid-template-columns:200px minmax(0,1fr); flex:1; gap:var(--pdm-container-gap); min-height:0; min-width:0; overflow:hidden; }
.pdm-document-rules__categories { display:flex; flex-direction:column; min-height:0; padding:var(--pdm-container-gap); box-sizing:border-box; overflow:hidden; }
.pdm-document-rules__categories h2 { margin:5px 8px 10px; font-size:14px; }
.pdm-document-rules__categories nav { display:flex; flex-direction:column; gap:5px; min-height:0; overflow:auto; }
.pdm-document-rules__categories button { flex:none; min-height:36px; padding:8px; border:0; border-radius:var(--pdm-radius-control); background:transparent; color:var(--pdm-text); font:inherit; text-align:left; cursor:pointer; }
.pdm-document-rules__categories button:hover,.pdm-document-rules__categories button.is-active { background:var(--pdm-theme-accent-soft); color:var(--pdm-theme-accent); }
.pdm-document-rules__categories button.is-active { font-weight:700; }
.pdm-document-rules__content { min-width:0; gap:var(--pdm-container-gap); }
.pdm-manager-heading h2 { margin:0; }
.pdm-document-rules__toolbar { display:flex; flex-wrap:wrap; gap:10px; }
.pdm-document-rules__toolbar .el-input { width:320px; max-width:100%; }
.pdm-document-rules__hint { margin:0; color:var(--el-text-color-secondary); font-size:12px; line-height:1.6; }
.pdm-document-rules__message { display:block; margin:0; line-height:20px; }
.pdm-document-rules__message-kind { color:var(--el-text-color-secondary); }
.pdm-table-scroll { flex:1; min-height:0; overflow:auto; }
.pdm-edit-table { width:100%; min-width:1000px; }
.pdm-edit-table th { position:sticky; top:0; z-index:1; }
.pdm-edit-table th,.pdm-edit-table td { height:50px; box-sizing:border-box; padding:4px 8px; vertical-align:middle; }
.pdm-edit-table td { white-space:normal; overflow-wrap:anywhere; }
.pdm-document-rules__cell { display:block; max-height:40px; overflow:auto; line-height:20px; }
.pdm-document-rules__cell:has(>.el-switch) { display:flex; align-items:center; gap:4px; }
.pdm-document-rules :deep(.el-pagination) { justify-content:flex-end; flex-wrap:wrap; }
</style>
