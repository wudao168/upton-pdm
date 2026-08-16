<script setup lang="ts">
import { PlugZap, RefreshCw, Search } from '@lucide/vue'
import { computed, reactive, ref, watch } from 'vue'
import { ElMessage } from 'element-plus'
import type { CrmConnectionTestResult, CrmCustomerSyncResult, CrmIntegrationSettings, PdmCustomer, UpdateCrmIntegrationInput } from '../types'

const props = defineProps<{
  customers: PdmCustomer[]
  integrationSettings: CrmIntegrationSettings
  pending: boolean
  onSaveSettings: (input: UpdateCrmIntegrationInput) => Promise<CrmIntegrationSettings>
  onTestConnection: () => Promise<CrmConnectionTestResult>
  onSyncCustomers: () => Promise<CrmCustomerSyncResult>
}>()

const query = ref('')
const draft = reactive<UpdateCrmIntegrationInput>({ baseUrl: '', username: '', password: '', autoSyncEnabled: false, autoSyncIntervalMinutes: 60 })
const filteredCustomers = computed(() => {
  const keyword = query.value.trim().toLocaleLowerCase('zh-CN')
  return keyword ? props.customers.filter(item => `${item.code} ${item.name}`.toLocaleLowerCase('zh-CN').includes(keyword)) : props.customers
})
const lastSyncText = computed(() => props.integrationSettings.lastSyncAt
  ? new Date(props.integrationSettings.lastSyncAt).toLocaleString('zh-CN', { hour12: false })
  : '尚未同步')
const automaticSyncText = computed(() => {
  if (!props.integrationSettings.autoSyncEnabled) return '当前已关闭'
  const activity = [props.integrationSettings.lastSyncAt, props.integrationSettings.lastAutoSyncAttemptAt]
    .filter((value): value is string => Boolean(value))
    .map(value => new Date(value).getTime())
    .filter(value => Number.isFinite(value))
  if (!activity.length) return `已启用，每${props.integrationSettings.autoSyncIntervalMinutes}分钟同步；首次同步将在约1分钟内开始`
  const nextAt = new Date(Math.max(...activity) + props.integrationSettings.autoSyncIntervalMinutes * 60_000)
  return `已启用，每${props.integrationSettings.autoSyncIntervalMinutes}分钟同步；下次预计 ${nextAt.toLocaleString('zh-CN', { hour12: false })}`
})

watch(() => props.integrationSettings, settings => {
  draft.baseUrl = settings.baseUrl
  draft.username = settings.username
  draft.password = ''
  draft.autoSyncEnabled = settings.autoSyncEnabled
  draft.autoSyncIntervalMinutes = settings.autoSyncIntervalMinutes || 60
}, { immediate: true, deep: true })

async function saveSettings() {
  if (!draft.baseUrl.trim() || !draft.username.trim()) {
    ElMessage.warning('请填写CRM服务地址和集成账号')
    return
  }
  if (!props.integrationSettings.passwordConfigured && !draft.password) {
    ElMessage.warning('首次配置需要填写集成账号密码')
    return
  }
  try {
    await props.onSaveSettings({ baseUrl: draft.baseUrl.trim(), username: draft.username.trim(), password: draft.password || undefined, autoSyncEnabled: draft.autoSyncEnabled, autoSyncIntervalMinutes: draft.autoSyncIntervalMinutes })
    draft.password = ''
    ElMessage.success('CRM连接配置已保存')
  } catch (error) {
    ElMessage.error(error instanceof Error ? error.message : 'CRM连接配置保存失败')
  }
}

async function testConnection() {
  try {
    const result = await props.onTestConnection()
    ElMessage.success(`CRM连接成功，可读取${result.customerCount}个客户${result.skippedCount ? `，跳过${result.skippedCount}条无效数据` : ''}`)
  } catch (error) {
    ElMessage.error(error instanceof Error ? error.message : 'CRM连接测试失败')
  }
}

async function syncCustomers() {
  try {
    const result = await props.onSyncCustomers()
    ElMessage.success(`已从CRM同步${result.customerCount}个客户${result.skippedCount ? `，跳过${result.skippedCount}条无效数据` : ''}`)
  } catch (error) {
    ElMessage.error(error instanceof Error ? error.message : 'CRM客户同步失败')
  }
}
</script>

<template>
  <section class="pdm-project-manager pdm-crm-customer-page" aria-label="CRM客户同步">
    <header class="pdm-pagebar">
      <div><div class="pdm-breadcrumb">系统管理 <span>/</span> CRM客户</div><h1>CRM客户</h1><p>客户编码和客户名称由CRM开放接口提供，PDM不再手工新建或修改客户。</p></div>
      <button type="button" class="pdm-primary-action" :disabled="pending || !integrationSettings.passwordConfigured" @click="syncCustomers"><RefreshCw :size="16" />从CRM同步</button>
    </header>

    <section class="pdm-panel pdm-crm-connection">
      <header class="pdm-manager-heading">
        <div><h2>CRM连接设置</h2><p>服务端使用集成账号登录CRM并读取 <code>GET /api/open/v1/customers</code>，密码加密保存且不会回显。</p></div>
        <span :class="integrationSettings.passwordConfigured ? 'pdm-status is-ok' : 'pdm-status is-warn'">{{ integrationSettings.passwordConfigured ? '已配置' : '未配置' }}</span>
      </header>
      <div class="pdm-settings-form pdm-crm-settings-form">
        <label>CRM服务地址<input v-model="draft.baseUrl" name="crmBaseUrl" maxlength="500" placeholder="例如 http://127.0.0.1:8080"><small>填写CRM服务器根地址，不需要追加 /api。</small></label>
        <label>集成账号<input v-model="draft.username" name="crmUsername" maxlength="100" autocomplete="username" placeholder="具备客户查看权限的CRM账号"><small>读取范围由该账号在CRM中的客户查看权限决定。</small></label>
        <label>集成密码<input v-model="draft.password" name="crmPassword" type="password" maxlength="500" autocomplete="new-password" :placeholder="integrationSettings.passwordConfigured ? '已保存；留空表示不修改' : '请输入CRM账号密码'"><small>Token仅用于当次测试或同步，不落库保存。</small></label>
      </div>
      <div class="pdm-crm-schedule" aria-label="CRM定时同步设置">
        <div class="pdm-crm-schedule__switch">
          <label><input v-model="draft.autoSyncEnabled" name="crmAutoSyncEnabled" type="checkbox"><span>启用定时同步</span></label>
          <small>由PDM服务端后台执行，关闭客户端或网页后仍会按计划同步。</small>
        </div>
        <label class="pdm-crm-schedule__interval">同步间隔
          <select v-model.number="draft.autoSyncIntervalMinutes" name="crmAutoSyncIntervalMinutes" :disabled="!draft.autoSyncEnabled">
            <option :value="5">5分钟</option><option :value="15">15分钟</option><option :value="30">30分钟</option><option :value="60">1小时</option><option :value="180">3小时</option><option :value="360">6小时</option><option :value="720">12小时</option><option :value="1440">1天</option><option :value="10080">7天</option>
          </select>
        </label>
        <div class="pdm-crm-schedule__status">
          <span>{{ automaticSyncText }}</span>
          <small v-if="integrationSettings.lastAutoSyncError" class="is-error">上次自动同步失败：{{ integrationSettings.lastAutoSyncError }}</small>
        </div>
      </div>
      <div class="pdm-crm-connection-footer">
        <small>最近同步：{{ lastSyncText }}<template v-if="integrationSettings.lastSyncAt">，{{ integrationSettings.lastSyncCount }}个客户</template></small>
        <div class="pdm-manager-actions"><button type="button" class="pdm-secondary-action" :disabled="pending || !integrationSettings.passwordConfigured" @click="testConnection"><PlugZap :size="15" />测试连接</button><button type="button" class="pdm-primary-action" :disabled="pending" @click="saveSettings">保存配置</button></div>
      </div>
    </section>

    <section class="pdm-panel pdm-project-list">
      <header class="pdm-panel-heading"><div><h2>CRM客户列表</h2><small>仅显示CRM已同步的客户编码和名称；当前同步不会删除历史项目已使用的客户。</small></div><label class="pdm-inline-search"><Search :size="15" /><input v-model="query" placeholder="搜索客户编码或名称"></label></header>
      <div v-if="filteredCustomers.length" class="pdm-table-scroll"><table class="pdm-project-table"><thead><tr><th>客户编码</th><th>客户名称</th><th>状态</th><th>来源</th></tr></thead><tbody><tr v-for="customer in filteredCustomers" :key="customer.id"><td><strong>{{ customer.code }}</strong></td><td>{{ customer.name }}</td><td><span :class="customer.isActive ? 'pdm-status is-ok' : 'pdm-status is-warn'">{{ customer.isActive ? '当前可见' : '历史保留' }}</span></td><td><span class="pdm-status is-ok">CRM</span></td></tr></tbody></table></div>
      <div v-else class="pdm-project-empty"><RefreshCw :size="38" /><h2>暂无CRM客户数据</h2><p>请先保存连接配置，再测试连接并执行“从CRM同步”。</p></div>
    </section>
  </section>
</template>
