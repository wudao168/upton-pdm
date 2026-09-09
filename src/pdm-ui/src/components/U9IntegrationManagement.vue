<script setup lang="ts">
import { ElMessage } from '../statusMessage'
import { ElMessageBox } from 'element-plus'
import { computed, onMounted, reactive, ref } from 'vue'
import { getU9InventorySyncStatus, getU9MaterialFullSyncStatus, getU9MaterialIntegration, getU9ProcurementSyncStatus, startU9InventoryFullSync, startU9MaterialFullSync, startU9ProcurementFullSync, testU9MaterialIntegration, updateU9InventorySyncSettings, updateU9MaterialIntegration, updateU9ProcurementSyncSettings } from '../api'
import type {
  CrmConnectionTestResult,
  CrmCustomerSyncResult,
  CrmIntegrationSettings,
  PdmCustomer,
  U9MaterialFullSyncStatusResponse,
  U9MaterialIntegrationSettings,
  U9InventorySyncStatusResponse,
  U9ProcurementSyncStatusResponse,
  UpdateCrmIntegrationInput,
} from '../types'
import CustomerManagement from './CustomerManagement.vue'
import SquareLoader from './SquareLoader.vue'
import U9BomQuery from './U9BomQuery.vue'

const props = defineProps<{
  token: string
  customers: PdmCustomer[]
  customerSettings: CrmIntegrationSettings
  pending: boolean
  canManageBase: boolean
  canManageCustomers: boolean
  onSaveCustomerSettings: (input: UpdateCrmIntegrationInput) => Promise<CrmIntegrationSettings>
  onTestCustomerConnection: () => Promise<CrmConnectionTestResult>
  onSyncCustomers: () => Promise<CrmCustomerSyncResult>
}>()

type U9Tab = 'base' | 'interfaces' | 'customers' | 'material-sync' | 'bom-query'

const activeTab = ref<U9Tab>('base')
const loading = ref(false)
const savingBase = ref(false)
const savingInterfaces = ref(false)
const testingConnection = ref(false)
const fullSyncLoading = ref(false)
const fullSyncStarting = ref(false)
const fullSyncStatus = ref<U9MaterialFullSyncStatusResponse | null>(null)
const inventorySyncLoading = ref(false)
const inventorySyncSaving = ref(false)
const inventorySyncStarting = ref(false)
const inventorySyncStatus = ref<U9InventorySyncStatusResponse | null>(null)
const inventorySyncForm = reactive({
  autoSyncEnabled: true,
  syncIntervalMinutes: 60,
  queryPath: '/webapi/Invtrans/QueryQohAndAvailable',
})
const procurementSyncLoading = ref(false)
const procurementSyncSaving = ref(false)
const procurementSyncStarting = ref(false)
const procurementSyncStatus = ref<U9ProcurementSyncStatusResponse | null>(null)
const procurementSyncForm = reactive({
  autoSyncEnabled: true,
  syncIntervalMinutes: 15,
  queryPath: '/webapi/QueryCommon/QueryInfoBySql',
})
const integration = reactive<U9MaterialIntegrationSettings & { clientSecret: string }>({
  baseUrl: '', enterpriseCode: '', organizationCode: '', userCode: '', clientId: '', clientSecretConfigured: false,
  clientSecret: '', itemCreatePath: '/webapi/ItemMaster/Create', itemQueryPath: '/webapi/ItemMaster/Query',
  itemModifyPath: '/webapi/ItemMaster/Modify', itemDeletePath: '/webapi/ItemMaster/Delete',
  customerQueryPath: '/webapi/GetCommonReference/Create', bomCreatePath: '/webapi/BOM/Create', bomQueryPath: '/webapi/BOM/Query',
  bomModifyPath: '/webapi/BOM/Modify', bomDeletePath: '/webapi/BOM/Delete', bomBatchUnapprovePath: '/webapi/BOM/BatchUnApprove',
  bomBipQueryPagePath: '/webapi/BOM/BIPQueryPage', unitCodeMappings: {}, writeEnabled: false,
})

const customerConnectionSettings = computed<CrmIntegrationSettings>(() => ({
  ...props.customerSettings,
  baseUrl: integration.baseUrl || props.customerSettings.baseUrl,
  username: integration.userCode || props.customerSettings.username,
  passwordConfigured: props.canManageBase ? integration.clientSecretConfigured : props.customerSettings.passwordConfigured,
}))

function applyIntegrationSettings(settings: U9MaterialIntegrationSettings) {
  Object.assign(integration, settings, { clientSecret: '' })
}

async function loadIntegration() {
  if (!props.canManageBase) {
    integration.baseUrl = props.customerSettings.baseUrl
    integration.userCode = props.customerSettings.username
    integration.clientSecretConfigured = props.customerSettings.passwordConfigured
    return
  }
  loading.value = true
  try {
    applyIntegrationSettings(await getU9MaterialIntegration(props.token))
  } catch (error) {
    ElMessage.error(error instanceof Error ? error.message : 'U9C基础设置加载失败')
  } finally {
    loading.value = false
  }
}

async function loadFullSyncStatus() {
  if (!props.canManageBase) return
  fullSyncLoading.value = true
  try {
    fullSyncStatus.value = await getU9MaterialFullSyncStatus(props.token)
  } catch (error) {
    ElMessage.error(error instanceof Error ? error.message : 'U9C料品自动同步状态加载失败')
  } finally {
    fullSyncLoading.value = false
  }
}

async function runFullSyncNow() {
  if (!props.canManageBase || fullSyncStarting.value) return
  try {
    await ElMessageBox.confirm(
      '立即从U9C只读获取当前分类范围内的全部料品并写入PLM料品主档？该操作不会修改U9C。',
      '确认全量同步',
      { confirmButtonText: '开始同步', cancelButtonText: '取消' },
    )
  } catch { return }
  fullSyncStarting.value = true
  try {
    const result = await startU9MaterialFullSync(props.token)
    ElMessage.success(result.message)
    await loadFullSyncStatus()
  } catch (error) {
    ElMessage.error(error instanceof Error ? error.message : 'U9C料品全量同步启动失败')
  } finally {
    fullSyncStarting.value = false
  }
}

async function loadInventorySyncStatus() {
  if (!props.canManageBase) return
  inventorySyncLoading.value = true
  try {
    inventorySyncStatus.value = await getU9InventorySyncStatus(props.token)
    Object.assign(inventorySyncForm, inventorySyncStatus.value.settings)
  } catch (error) {
    ElMessage.error(error instanceof Error ? error.message : 'U9C库存同步状态加载失败')
  } finally {
    inventorySyncLoading.value = false
  }
}

async function saveInventorySyncSettings() {
  if (!props.canManageBase || inventorySyncSaving.value) return
  inventorySyncSaving.value = true
  try {
    const saved = await updateU9InventorySyncSettings(inventorySyncForm, props.token)
    Object.assign(inventorySyncForm, saved)
    if (inventorySyncStatus.value) inventorySyncStatus.value.settings = saved
    ElMessage.success('库存自动同步设置已保存')
  } catch (error) {
    ElMessage.error(error instanceof Error ? error.message : '库存自动同步设置保存失败')
  } finally {
    inventorySyncSaving.value = false
  }
}

async function runInventorySyncNow() {
  if (!props.canManageBase || inventorySyncStarting.value) return
  try {
    await ElMessageBox.confirm(
      '立即从U9C只读获取当前组织的完整库存，并在全部成功后切换PLM库存快照？该操作不会修改U9C。',
      '确认库存全量刷新',
      { confirmButtonText: '开始刷新', cancelButtonText: '取消' },
    )
  } catch { return }
  inventorySyncStarting.value = true
  try {
    const result = await startU9InventoryFullSync(props.token)
    ElMessage.success(result.message)
    window.setTimeout(() => void loadInventorySyncStatus(), 800)
  } catch (error) {
    ElMessage.error(error instanceof Error ? error.message : 'U9C库存全量刷新启动失败')
  } finally {
    inventorySyncStarting.value = false
  }
}

async function loadProcurementSyncStatus() {
  if (!props.canManageBase) return
  procurementSyncLoading.value = true
  try {
    procurementSyncStatus.value = await getU9ProcurementSyncStatus(props.token)
    Object.assign(procurementSyncForm, procurementSyncStatus.value.settings)
  } catch (error) {
    ElMessage.error(error instanceof Error ? error.message : 'U9C采购跟踪同步状态加载失败')
  } finally {
    procurementSyncLoading.value = false
  }
}

async function saveProcurementSyncSettings() {
  if (!props.canManageBase || procurementSyncSaving.value) return
  procurementSyncSaving.value = true
  try {
    const saved = await updateU9ProcurementSyncSettings(procurementSyncForm, props.token)
    Object.assign(procurementSyncForm, saved)
    if (procurementSyncStatus.value) procurementSyncStatus.value.settings = saved
    ElMessage.success('采购跟踪自动同步设置已保存')
  } catch (error) {
    ElMessage.error(error instanceof Error ? error.message : '采购跟踪自动同步设置保存失败')
  } finally {
    procurementSyncSaving.value = false
  }
}

async function runProcurementSyncNow() {
  if (!props.canManageBase || procurementSyncStarting.value) return
  try {
    await ElMessageBox.confirm(
      '立即从U9C只读获取PLM有效项目范围内的请购、标准采购状态，并在全部成功后切换快照？不会查询价格、税额或财务信息，也不会修改U9C。',
      '确认采购跟踪全量刷新',
      { confirmButtonText: '开始刷新', cancelButtonText: '取消' },
    )
  } catch { return }
  procurementSyncStarting.value = true
  try {
    const response = await startU9ProcurementFullSync(props.token)
    ElMessage.success(response.message)
    window.setTimeout(() => void loadProcurementSyncStatus(), 1200)
  } catch (error) {
    ElMessage.error(error instanceof Error ? error.message : 'U9C采购跟踪全量刷新启动失败')
  } finally {
    procurementSyncStarting.value = false
  }
}

function buildUpdateInput(clientSecret: string | null) {
  return {
    baseUrl: integration.baseUrl.trim(),
    enterpriseCode: integration.enterpriseCode.trim(),
    organizationCode: integration.organizationCode.trim(),
    userCode: integration.userCode.trim(),
    clientId: integration.clientId.trim(),
    clientSecret,
    itemCreatePath: integration.itemCreatePath.trim(),
    itemQueryPath: integration.itemQueryPath.trim(),
    itemModifyPath: integration.itemModifyPath.trim(),
    itemDeletePath: integration.itemDeletePath.trim(),
    customerQueryPath: integration.customerQueryPath.trim(),
    bomCreatePath: integration.bomCreatePath.trim(),
    bomQueryPath: integration.bomQueryPath.trim(),
    bomModifyPath: integration.bomModifyPath.trim(),
    bomDeletePath: integration.bomDeletePath.trim(),
    bomBatchUnapprovePath: integration.bomBatchUnapprovePath.trim(),
    bomBipQueryPagePath: integration.bomBipQueryPagePath.trim(),
    unitCodeMappings: {},
    writeEnabled: integration.writeEnabled,
  }
}

async function saveBaseSettings() {
  if (![integration.baseUrl, integration.enterpriseCode, integration.organizationCode, integration.userCode, integration.clientId].every(value => value.trim())) {
    ElMessage.warning('U9C地址、企业编码、组织编码、用户编码和应用ID不能为空')
    return
  }
  if (!integration.clientSecretConfigured && !integration.clientSecret.trim()) {
    ElMessage.warning('首次配置必须填写应用密钥')
    return
  }
  savingBase.value = true
  try {
    const saved = await updateU9MaterialIntegration(buildUpdateInput(integration.clientSecret.trim() || null), props.token)
    applyIntegrationSettings(saved)
    ElMessage.success('U9C基础设置已加密保存')
  } catch (error) {
    ElMessage.error(error instanceof Error ? error.message : 'U9C基础设置保存失败')
  } finally {
    savingBase.value = false
  }
}

async function testConnection() {
  testingConnection.value = true
  try {
    const result = await testU9MaterialIntegration(props.token)
    ElMessage.success(`U9C认证成功：企业 ${result.enterpriseCode} / 组织 ${result.organizationCode}`)
  } catch (error) {
    ElMessage.error(error instanceof Error ? error.message : 'U9C认证测试失败')
  } finally {
    testingConnection.value = false
  }
}

async function saveInterfaceSettings() {
  if (!inventorySyncStatus.value || !procurementSyncStatus.value) {
    ElMessage.warning('库存或采购跟踪接口设置尚未加载完成，请刷新后重试')
    return
  }
  const inventoryQueryPath = inventorySyncForm.queryPath.trim()
  const procurementQueryPath = procurementSyncForm.queryPath.trim()
  if (!inventoryQueryPath.startsWith('/')) {
    ElMessage.warning('库存查询接口路径必须以/开头')
    return
  }
  if (!procurementQueryPath.startsWith('/webapi/')) {
    ElMessage.warning('采购跟踪查询接口路径必须以/webapi/开头')
    return
  }
  if (integration.writeEnabled) {
    try {
      await ElMessageBox.confirm(
        '开启后，获授权人员可从同步任务页手工向 U9C 创建料品。系统仍会先按料号幂等查询，且不会自动批量写入。',
        '确认开启 U9C 真实写入',
        { type: 'warning', confirmButtonText: '确认开启', cancelButtonText: '保持关闭' },
      )
    } catch (error) {
      if (error === 'cancel' || error === 'close') return
      throw error
    }
  }
  savingInterfaces.value = true
  try {
    const [saved, savedInventorySettings, savedProcurementSettings] = await Promise.all([
      updateU9MaterialIntegration(buildUpdateInput(null), props.token),
      updateU9InventorySyncSettings({
        autoSyncEnabled: inventorySyncForm.autoSyncEnabled,
        syncIntervalMinutes: inventorySyncForm.syncIntervalMinutes,
        queryPath: inventoryQueryPath,
      }, props.token),
      updateU9ProcurementSyncSettings({
        autoSyncEnabled: procurementSyncForm.autoSyncEnabled,
        syncIntervalMinutes: procurementSyncForm.syncIntervalMinutes,
        queryPath: procurementQueryPath,
      }, props.token),
    ])
    applyIntegrationSettings(saved)
    Object.assign(inventorySyncForm, savedInventorySettings)
    inventorySyncStatus.value.settings = savedInventorySettings
    Object.assign(procurementSyncForm, savedProcurementSettings)
    procurementSyncStatus.value.settings = savedProcurementSettings
    ElMessage.success(`U9C接口设置已保存，真实写入已${saved.writeEnabled ? '开启' : '关闭'}`)
  } catch (error) {
    ElMessage.error(error instanceof Error ? error.message : 'U9C接口设置保存失败')
  } finally {
    savingInterfaces.value = false
  }
}

const fullSyncStatusText = computed(() => {
  switch (fullSyncStatus.value?.latestRun?.status) {
    case 'Running': return '同步中'
    case 'Succeeded': return '已完成'
    case 'PartiallySucceeded': return '部分完成'
    case 'Failed': return '失败'
    default: return '尚未运行'
  }
})

const fullSyncTagType = computed(() => {
  switch (fullSyncStatus.value?.latestRun?.status) {
    case 'Running': return 'primary'
    case 'Succeeded': return 'success'
    case 'PartiallySucceeded': return 'warning'
    case 'Failed': return 'danger'
    default: return 'info'
  }
})

const fullSyncInactivatedCount = computed(() =>
  (fullSyncStatus.value?.latestRun?.categoryResults ?? []).reduce((total, item) => total + (item.inactivatedCount ?? 0), 0))
const fullSyncConflictCount = computed(() =>
  (fullSyncStatus.value?.latestRun?.categoryResults ?? []).reduce((total, item) => total + (item.conflictCount ?? 0), 0))
const inventorySyncStatusText = computed(() => {
  const status = inventorySyncStatus.value?.latestRun?.status
  if (status === 'Running') return '刷新中'
  if (status === 'Succeeded') return '最近刷新成功'
  if (status === 'Failed') return '最近刷新失败'
  return '尚未刷新'
})
const inventorySyncTagType = computed(() => inventorySyncStatus.value?.latestRun?.status === 'Succeeded' ? 'success'
  : inventorySyncStatus.value?.latestRun?.status === 'Failed' ? 'danger'
    : inventorySyncStatus.value?.latestRun?.status === 'Running' ? 'warning' : 'info')
const procurementSyncStatusText = computed(() => {
  const status = procurementSyncStatus.value?.latestRun?.status
  if (status === 'Running') return '刷新中'
  if (status === 'Succeeded') return '最近刷新成功'
  if (status === 'Failed') return '最近刷新失败'
  return '尚未刷新'
})
const procurementSyncTagType = computed(() => procurementSyncStatus.value?.latestRun?.status === 'Succeeded' ? 'success'
  : procurementSyncStatus.value?.latestRun?.status === 'Failed' ? 'danger'
    : procurementSyncStatus.value?.latestRun?.status === 'Running' ? 'warning' : 'info')

function formatSyncTime(value?: string | null) {
  return value ? new Date(value).toLocaleString('zh-CN', { hour12: false }) : '—'
}

onMounted(() => {
  void loadIntegration()
  void loadFullSyncStatus()
  void loadInventorySyncStatus()
  void loadProcurementSyncStatus()
})
</script>

<template>
  <section class="pdm-project-manager u9-integration-page pdm-loading-host" aria-label="U9C接口管理">
    <SquareLoader v-if="loading" overlay label="正在加载U9C接口设置" />
    <el-tabs v-model="activeTab" class="u9-interface-tabs">
      <el-tab-pane label="基础设置" name="base">
        <section class="pdm-panel u9-settings-card" aria-label="U9C基础设置">
          <header class="u9-card-heading">
            <div><h2>基础设置</h2><p>所有U9C接口共用服务地址和OAuth2参数；应用密钥只加密保存，页面不会回显。</p></div>
            <span :class="integration.clientSecretConfigured ? 'pdm-status is-ok' : 'pdm-status is-warn'">{{ integration.clientSecretConfigured ? 'OAuth已配置' : 'OAuth未配置' }}</span>
          </header>
          <el-alert v-if="!canManageBase" title="当前账号没有U9C基础设置维护权限，仅可查看客户同步所使用的地址和用户。" type="info" :closable="false" show-icon />
          <el-form label-position="top" class="u9-settings-form">
            <div class="u9-form-grid">
              <el-form-item label="U9C地址" required><el-input v-model="integration.baseUrl" name="u9BaseUrl" :disabled="!canManageBase" placeholder="http://服务器/U9" /></el-form-item>
              <el-form-item label="应用ID" required><el-input v-model="integration.clientId" name="u9ClientId" :disabled="!canManageBase" /></el-form-item>
              <el-form-item label="企业编码" required><el-input v-model="integration.enterpriseCode" name="u9EnterpriseCode" :disabled="!canManageBase" /></el-form-item>
              <el-form-item label="组织编码" required><el-input v-model="integration.organizationCode" name="u9OrganizationCode" :disabled="!canManageBase" /></el-form-item>
              <el-form-item label="用户编码" required><el-input v-model="integration.userCode" name="u9UserCode" :disabled="!canManageBase" /></el-form-item>
              <el-form-item :label="integration.clientSecretConfigured ? '应用密钥（已配置，留空不修改）' : '应用密钥'" required>
                <el-input v-model="integration.clientSecret" name="u9ClientSecret" type="password" show-password autocomplete="new-password" :disabled="!canManageBase" />
              </el-form-item>
            </div>
            <div v-if="canManageBase" class="u9-actions">
              <el-button type="primary" :loading="savingBase" @click="saveBaseSettings">保存基础设置</el-button>
              <el-button :loading="testingConnection" :disabled="!integration.clientSecretConfigured" @click="testConnection">测试已保存连接</el-button>
            </div>
          </el-form>
        </section>
      </el-tab-pane>

      <el-tab-pane v-if="canManageBase" label="接口设置" name="interfaces">
        <section class="pdm-panel u9-settings-card" aria-label="U9C接口设置">
          <header class="u9-card-heading">
            <div><h2>接口设置</h2><p>客户、料品、BOM及后续新增的U9C接口统一在此维护；OAuth参数来自“基础设置”。</p></div>
          </header>
          <el-alert title="接口路径按当前U9C OpenAPI合同校验。BOM删除已核准版本时，还会使用弃审和记录定位接口。" type="info" :closable="false" show-icon />
          <el-form label-position="top" class="u9-settings-form">
            <section class="u9-interface-group" aria-label="客户接口">
              <h3>客户接口</h3>
              <div class="u9-form-grid">
                <el-form-item label="客户查询接口路径"><el-input v-model="integration.customerQueryPath" name="u9CustomerQueryPath" /></el-form-item>
              </div>
            </section>
            <section class="u9-interface-group" aria-label="料品接口">
              <h3>料品接口</h3>
              <div class="u9-form-grid">
                <el-form-item label="料品创建接口路径"><el-input v-model="integration.itemCreatePath" name="u9ItemCreatePath" /></el-form-item>
                <el-form-item label="料品查询接口路径"><el-input v-model="integration.itemQueryPath" name="u9ItemQueryPath" /></el-form-item>
                <el-form-item label="料品修改接口路径"><el-input v-model="integration.itemModifyPath" name="u9ItemModifyPath" /></el-form-item>
                <el-form-item label="料品删除接口路径"><el-input v-model="integration.itemDeletePath" name="u9ItemDeletePath" /></el-form-item>
              </div>
            </section>
            <section class="u9-interface-group" aria-label="库存接口">
              <h3>库存与采购接口</h3>
              <div class="u9-form-grid">
                <el-form-item label="库存查询接口路径"><el-input v-model.trim="inventorySyncForm.queryPath" name="u9InventoryQueryPath" /></el-form-item>
                <el-form-item label="采购跟踪查询接口路径"><el-input v-model.trim="procurementSyncForm.queryPath" name="u9ProcurementQueryPath" /></el-form-item>
              </div>
            </section>
            <section class="u9-interface-group" aria-label="BOM接口">
              <h3>BOM接口</h3>
              <div class="u9-form-grid">
                <el-form-item label="BOM创建接口路径"><el-input v-model="integration.bomCreatePath" name="u9BomCreatePath" /></el-form-item>
                <el-form-item label="BOM查询接口路径"><el-input v-model="integration.bomQueryPath" name="u9BomQueryPath" /></el-form-item>
                <el-form-item label="BOM修改接口路径"><el-input v-model="integration.bomModifyPath" name="u9BomModifyPath" /></el-form-item>
                <el-form-item label="BOM删除接口路径"><el-input v-model="integration.bomDeletePath" name="u9BomDeletePath" /></el-form-item>
                <el-form-item label="BOM弃审接口路径"><el-input v-model="integration.bomBatchUnapprovePath" name="u9BomBatchUnapprovePath" /></el-form-item>
                <el-form-item label="BOM记录定位接口路径"><el-input v-model="integration.bomBipQueryPagePath" name="u9BomBipQueryPagePath" /></el-form-item>
              </div>
            </section>
            <el-checkbox v-model="integration.writeEnabled">启用人工确认后的真实写入</el-checkbox>
            <p class="u9-write-note">开启后仍不会自动写入；料品和BOM写入均须先生成预览并由获授权人员确认。</p>
            <div class="u9-actions"><el-button type="primary" :loading="savingInterfaces" @click="saveInterfaceSettings">保存接口设置</el-button></div>
          </el-form>
        </section>
      </el-tab-pane>

      <el-tab-pane v-if="canManageCustomers" label="客户查询" name="customers">
        <CustomerManagement
          :customers="customers"
          :integration-settings="customerConnectionSettings"
          :customer-query-path="integration.customerQueryPath"
          :pending="pending"
          :on-save-settings="onSaveCustomerSettings"
          :on-test-connection="onTestCustomerConnection"
          :on-sync-customers="onSyncCustomers"
        />
      </el-tab-pane>

      <el-tab-pane v-if="canManageBase" label="料品同步" name="material-sync">
        <section class="pdm-panel u9-settings-card pdm-loading-host" aria-label="U9C料品自动全量同步">
          <SquareLoader v-if="fullSyncLoading" overlay label="正在加载自动同步状态" />
          <header class="u9-card-heading">
            <div><h2>U9C料品自动全量同步</h2><p>每天只读获取完整料品资料写入PLM料品主档，供快速查询和BOM引用；PLM取号独立使用本地全局基线。</p></div>
            <div class="u9-actions"><el-tag :type="fullSyncTagType">{{ fullSyncStatusText }}</el-tag><el-button type="primary" :loading="fullSyncStarting" @click="runFullSyncNow">立即全量同步</el-button><el-button :loading="fullSyncLoading" @click="loadFullSyncStatus">刷新状态</el-button></div>
          </header>
          <el-alert title="同步范围动态取自“分类维护”中允许创建、启用且可见的分类；U9C不再返回的料品会在PLM标记停用并禁止新BOM引用，历史BOM保留；同编码PLM自有主档不覆盖并计入冲突。" type="info" :closable="false" show-icon />
          <dl class="full-sync-summary">
            <div><dt>执行计划</dt><dd>每日 {{ fullSyncStatus?.scheduleTime ?? '02:00' }}，每 {{ fullSyncStatus?.checkIntervalMinutes ?? 30 }} 分钟检查</dd></div>
            <div><dt>最近开始</dt><dd>{{ formatSyncTime(fullSyncStatus?.latestRun?.startedAt) }}</dd></div>
            <div><dt>最近完成</dt><dd>{{ formatSyncTime(fullSyncStatus?.latestRun?.completedAt) }}</dd></div>
            <div><dt>分类进度</dt><dd>{{ fullSyncStatus?.latestRun?.completedCategoryCount ?? 0 }} / {{ fullSyncStatus?.latestRun?.categoryCount ?? fullSyncStatus?.categories.length ?? 0 }}</dd></div>
            <div><dt>发现料品</dt><dd>{{ fullSyncStatus?.latestRun?.discoveredCount ?? 0 }}</dd></div>
            <div><dt>PLM处理</dt><dd>新建 {{ fullSyncStatus?.latestRun?.createdCount ?? 0 }} · 刷新 {{ fullSyncStatus?.latestRun?.refreshedCount ?? 0 }} · 停用 {{ fullSyncInactivatedCount }} · 冲突 {{ fullSyncConflictCount }} · 跳过 {{ fullSyncStatus?.latestRun?.skippedCount ?? 0 }}</dd></div>
          </dl>
          <div class="full-sync-categories" aria-label="自动同步分类">
            <strong>当前同步范围</strong>
            <el-tag v-for="category in fullSyncStatus?.categories ?? []" :key="category.code" effect="plain">{{ category.code }} {{ category.name }}</el-tag>
            <span v-if="!fullSyncStatus?.categories.length" class="pdm-muted">暂无允许创建的分类</span>
          </div>
          <el-alert v-if="fullSyncStatus?.latestRun?.lastError" :title="fullSyncStatus.latestRun.lastError" type="error" :closable="false" show-icon />
          <el-table :data="fullSyncStatus?.latestRun?.categoryResults ?? []" row-key="categoryCode" class="full-sync-table" empty-text="尚无分类同步结果">
            <el-table-column prop="categoryCode" label="分类" width="90" />
            <el-table-column prop="categoryName" label="分类名称" min-width="150" />
            <el-table-column prop="maximumSequence" label="最大流水" width="110" />
            <el-table-column prop="discoveredCount" label="发现" width="80" />
            <el-table-column prop="createdCount" label="新建" width="80" />
            <el-table-column prop="refreshedCount" label="刷新" width="80" />
            <el-table-column prop="inactivatedCount" label="停用" width="80" />
            <el-table-column prop="conflictCount" label="冲突" width="80" />
            <el-table-column prop="skippedCount" label="跳过" width="80" />
            <el-table-column label="状态" width="100"><template #default="{ row }"><el-tag :type="row.succeeded ? 'success' : 'danger'">{{ row.succeeded ? '完成' : '失败' }}</el-tag></template></el-table-column>
            <el-table-column prop="error" label="说明" min-width="220" show-overflow-tooltip />
          </el-table>
        </section>
        <section class="pdm-panel u9-settings-card u9-inventory-sync-card pdm-loading-host" aria-label="U9C库存自动全量刷新">
          <SquareLoader v-if="inventorySyncLoading" overlay label="正在加载库存同步状态" />
          <header class="u9-card-heading">
            <div><h2>U9C库存自动全量刷新</h2><p>按设置周期只读刷新“料品库存”快照；只有整批成功才切换新快照，失败时继续使用上一次完整数据。</p></div>
            <div class="u9-actions"><el-tag :type="inventorySyncTagType">{{ inventorySyncStatusText }}</el-tag><el-button type="primary" :loading="inventorySyncStarting" @click="runInventorySyncNow">立即全量刷新</el-button><el-button :loading="inventorySyncLoading" @click="loadInventorySyncStatus">刷新状态</el-button></div>
          </header>
          <el-form label-position="top" class="u9-settings-form">
            <div class="u9-form-grid">
              <el-form-item label="自动同步"><el-switch v-model="inventorySyncForm.autoSyncEnabled" active-text="启用" inactive-text="关闭" /></el-form-item>
              <el-form-item label="同步间隔（分钟）"><el-input-number v-model="inventorySyncForm.syncIntervalMinutes" :min="15" :max="1440" :step="15" controls-position="right" /></el-form-item>
            </div>
            <div class="u9-actions"><el-button type="primary" :loading="inventorySyncSaving" @click="saveInventorySyncSettings">保存库存同步设置</el-button></div>
          </el-form>
          <dl class="full-sync-summary inventory-sync-summary">
            <div><dt>执行计划</dt><dd>{{ inventorySyncForm.autoSyncEnabled ? `每 ${inventorySyncForm.syncIntervalMinutes} 分钟` : '已关闭' }}</dd></div>
            <div><dt>最近开始</dt><dd>{{ formatSyncTime(inventorySyncStatus?.latestRun?.startedAt) }}</dd></div>
            <div><dt>最近完成</dt><dd>{{ formatSyncTime(inventorySyncStatus?.latestRun?.completedAt) }}</dd></div>
            <div><dt>U9C源明细</dt><dd>{{ inventorySyncStatus?.latestRun?.sourceRowCount ?? 0 }}</dd></div>
            <div><dt>库存快照明细</dt><dd>{{ inventorySyncStatus?.latestRun?.storedRowCount ?? 0 }}</dd></div>
            <div><dt>覆盖料号</dt><dd>{{ inventorySyncStatus?.latestRun?.materialCount ?? 0 }}</dd></div>
          </dl>
          <el-alert v-if="inventorySyncStatus?.latestRun?.lastError" :title="inventorySyncStatus.latestRun.lastError" type="error" :closable="false" show-icon />
        </section>
        <section class="pdm-panel u9-settings-card u9-inventory-sync-card pdm-loading-host" aria-label="U9C采购跟踪自动全量刷新">
          <SquareLoader v-if="procurementSyncLoading" overlay label="正在加载采购跟踪同步状态" />
          <header class="u9-card-heading">
            <div><h2>U9C采购跟踪自动全量刷新</h2><p>每15分钟只读同步PLM有效项目范围内的请购与标准采购行状态；不读取价格、税额、币种及财务信息，失败时继续使用上一次完整快照。</p></div>
            <div class="u9-actions"><el-tag :type="procurementSyncTagType">{{ procurementSyncStatusText }}</el-tag><el-button type="primary" :loading="procurementSyncStarting" @click="runProcurementSyncNow">立即全量刷新</el-button><el-button :loading="procurementSyncLoading" @click="loadProcurementSyncStatus">刷新状态</el-button></div>
          </header>
          <el-form label-position="top" class="u9-settings-form">
            <div class="u9-form-grid">
              <el-form-item label="自动同步"><el-switch v-model="procurementSyncForm.autoSyncEnabled" active-text="启用" inactive-text="关闭" /></el-form-item>
              <el-form-item label="同步间隔（分钟）"><el-input-number v-model="procurementSyncForm.syncIntervalMinutes" :min="15" :max="1440" :step="15" controls-position="right" /></el-form-item>
            </div>
            <div class="u9-actions"><el-button type="primary" :loading="procurementSyncSaving" @click="saveProcurementSyncSettings">保存采购跟踪同步设置</el-button></div>
          </el-form>
          <dl class="full-sync-summary inventory-sync-summary">
            <div><dt>执行计划</dt><dd>{{ procurementSyncForm.autoSyncEnabled ? `每 ${procurementSyncForm.syncIntervalMinutes} 分钟` : '已关闭' }}</dd></div>
            <div><dt>最近开始</dt><dd>{{ formatSyncTime(procurementSyncStatus?.latestRun?.startedAt) }}</dd></div>
            <div><dt>最近完成</dt><dd>{{ formatSyncTime(procurementSyncStatus?.latestRun?.completedAt) }}</dd></div>
            <div><dt>U9C源明细</dt><dd>{{ procurementSyncStatus?.latestRun?.sourceRowCount ?? 0 }}</dd></div>
            <div><dt>快照明细</dt><dd>{{ procurementSyncStatus?.latestRun?.storedRowCount ?? 0 }}</dd></div>
            <div><dt>项目范围</dt><dd>{{ procurementSyncStatus?.latestRun?.projectCount ?? 0 }}</dd></div>
          </dl>
          <el-alert v-if="procurementSyncStatus?.latestRun?.lastError" :title="procurementSyncStatus.latestRun.lastError" type="error" :closable="false" show-icon />
        </section>
      </el-tab-pane>

      <el-tab-pane v-if="canManageBase" label="BOM维护" name="bom-query">
        <U9BomQuery :token="token" :organization-code="integration.organizationCode" :write-enabled="integration.writeEnabled" />
      </el-tab-pane>
    </el-tabs>
  </section>
</template>

<style scoped>
.u9-integration-page{min-width:0;min-height:0}.u9-interface-tabs{min-height:0;flex:1 1 auto;display:flex;flex-direction:column;overflow:hidden;margin-top:4px}.u9-interface-tabs :deep(.el-tabs__content){min-height:0;flex:1 1 auto;overflow:hidden}.u9-interface-tabs :deep(.el-tab-pane){min-height:0;height:100%;overflow:auto}.u9-settings-card{padding:24px}.u9-inventory-sync-card{margin-top:18px}.u9-card-heading{display:flex;align-items:flex-start;justify-content:space-between;gap:18px;margin-bottom:20px}.u9-card-heading h2{margin:0 0 6px;font-size:20px}.u9-card-heading p{margin:0;color:#64748b;line-height:1.6}.u9-settings-form{margin-top:18px}.u9-form-grid{display:grid;grid-template-columns:repeat(2,minmax(0,1fr));gap:0 18px}.u9-interface-group{margin:0 0 16px;padding:16px 18px 4px;border:1px solid #e2e8f0;border-radius:8px;background:#f8fafc}.u9-interface-group h3{margin:0 0 14px;font-size:15px;color:#1e293b}.u9-actions{display:flex;align-items:center;gap:10px;margin-top:4px}.full-sync-summary{display:grid;grid-template-columns:repeat(6,minmax(0,1fr));gap:12px;margin:20px 0}.full-sync-summary div{padding:14px 16px;border:1px solid #e2e8f0;border-radius:8px;background:#f8fafc}.full-sync-summary dt{margin-bottom:6px;color:#64748b;font-size:12px}.full-sync-summary dd{margin:0;color:#0f172a;font-weight:600}.full-sync-categories{display:flex;align-items:center;flex-wrap:wrap;gap:8px;margin:0 0 16px}.full-sync-categories strong{margin-right:4px}.full-sync-table{width:100%}.u9-write-note{margin:6px 0 18px;color:#9a3412;font-size:13px}@media(max-width:900px){.u9-form-grid,.full-sync-summary{grid-template-columns:1fr}.u9-settings-card{padding:18px}}
</style>
