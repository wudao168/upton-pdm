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
  if (!props.integrationSettings.passwordConfigured) {
    ElMessage.warning('请先到“U9C接口 → 基础设置”保存完整的OAuth连接参数')
    return
  }
  try {
    await props.onSaveSettings({ baseUrl: draft.baseUrl, username: draft.username, autoSyncEnabled: draft.autoSyncEnabled, autoSyncIntervalMinutes: draft.autoSyncIntervalMinutes })
    ElMessage.success('U9C客户同步计划已保存')
  } catch (error) {
    ElMessage.error(error instanceof Error ? error.message : 'U9C客户同步计划保存失败')
  }
}

async function testConnection() {
  try {
    const result = await props.onTestConnection()
    ElMessage.success(`U9C连接成功，可读取${result.customerCount}个客户${result.skippedCount ? `，跳过${result.skippedCount}条无效数据` : ''}`)
  } catch (error) {
    ElMessage.error(error instanceof Error ? error.message : 'U9C客户连接测试失败')
  }
}

async function syncCustomers() {
  try {
    const result = await props.onSyncCustomers()
    ElMessage.success(`已从U9C同步${result.customerCount}个客户${result.skippedCount ? `，跳过${result.skippedCount}条无效数据` : ''}`)
  } catch (error) {
    ElMessage.error(error instanceof Error ? error.message : 'U9C客户同步失败')
  }
}
</script>

<template>
  <section class="pdm-project-manager pdm-crm-customer-page" aria-label="U9C客户同步">
    <header class="pdm-pagebar">
      <div><div class="pdm-breadcrumb">系统管理 <span>/</span> U9C接口 <span>/</span> 客户查询</div><h1>客户查询</h1><p>客户编码和客户名称由U9C客户参照接口提供，PDM不再手工新建或修改客户。</p></div>
      <button type="button" class="pdm-primary-action" :disabled="pending || !integrationSettings.passwordConfigured" @click="syncCustomers"><RefreshCw :size="16" />从U9C同步</button>
    </header>

    <section class="pdm-panel pdm-crm-connection">
      <header class="pdm-manager-heading">
        <div><h2>U9C客户同步</h2><p>复用“U9C接口 → 基础设置”的OAuth连接，只读调用 <code>GetCommonReference/Create</code> 获取客户编码和名称。</p></div>
        <span :class="integrationSettings.passwordConfigured ? 'pdm-status is-ok' : 'pdm-status is-warn'">{{ integrationSettings.passwordConfigured ? 'OAuth已配置' : 'OAuth未配置' }}</span>
      </header>
      <div class="pdm-settings-form pdm-crm-settings-form">
        <label>U9C服务地址<input :value="integrationSettings.baseUrl" name="u9CustomerBaseUrl" readonly><small>连接地址在“U9C接口 → 基础设置”中统一维护。</small></label>
        <label>U9C用户<input :value="integrationSettings.username" name="u9CustomerUser" readonly><small>OAuth Token仅用于当次查询，不落库保存。</small></label>
      </div>
      <div class="pdm-crm-schedule" aria-label="U9C定时同步设置">
        <div class="pdm-crm-schedule__switch">
          <label><input v-model="draft.autoSyncEnabled" name="u9CustomerAutoSyncEnabled" type="checkbox"><span>启用定时同步</span></label>
          <small>由PDM服务端后台执行，关闭客户端或网页后仍会按计划同步。</small>
        </div>
        <label class="pdm-crm-schedule__interval">同步间隔
          <select v-model.number="draft.autoSyncIntervalMinutes" name="u9CustomerAutoSyncIntervalMinutes" :disabled="!draft.autoSyncEnabled">
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
        <div class="pdm-manager-actions"><button type="button" class="pdm-secondary-action" :disabled="pending || !integrationSettings.passwordConfigured" @click="testConnection"><PlugZap :size="15" />测试连接</button><button type="button" class="pdm-primary-action" :disabled="pending" @click="saveSettings">保存同步计划</button></div>
      </div>
    </section>

    <section class="pdm-panel pdm-project-list">
      <header class="pdm-panel-heading"><div><h2>U9C客户列表</h2><small>仅显示U9C已同步的客户编码和名称；同步不会删除历史项目已使用的客户。</small></div><label class="pdm-inline-search"><Search :size="15" /><input v-model="query" placeholder="搜索客户编码或名称"></label></header>
      <div v-if="filteredCustomers.length" class="pdm-table-scroll"><table class="pdm-project-table"><thead><tr><th>客户编码</th><th>客户名称</th><th>状态</th><th>来源</th></tr></thead><tbody><tr v-for="customer in filteredCustomers" :key="customer.id"><td><strong>{{ customer.code }}</strong></td><td>{{ customer.name }}</td><td><span :class="customer.isActive ? 'pdm-status is-ok' : 'pdm-status is-warn'">{{ customer.isActive ? '当前可见' : '历史保留' }}</span></td><td><span class="pdm-status is-ok">U9C</span></td></tr></tbody></table></div>
      <div v-else class="pdm-project-empty"><RefreshCw :size="38" /><h2>暂无U9C客户数据</h2><p>请先到“U9C接口 → 基础设置”维护连接，再测试客户查询并执行“从U9C同步”。</p></div>
    </section>
  </section>
</template>
