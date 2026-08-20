<script setup lang="ts">
import { ElMessage, ElMessageBox } from 'element-plus'
import { computed, onMounted, reactive, ref } from 'vue'
import { getU9MaterialIntegration, importU9MaterialSample, previewU9MaterialSample, testU9MaterialIntegration, updateU9MaterialIntegration } from '../api'
import type {
  CrmConnectionTestResult,
  CrmCustomerSyncResult,
  CrmIntegrationSettings,
  PdmCustomer,
  U9MaterialIntegrationSettings,
  U9MaterialSamplePreview,
  UpdateCrmIntegrationInput,
} from '../types'
import CustomerManagement from './CustomerManagement.vue'

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

type U9Tab = 'base' | 'customers' | 'material-sync' | 'materials'

const activeTab = ref<U9Tab>('base')
const loading = ref(false)
const savingBase = ref(false)
const savingMaterial = ref(false)
const testingConnection = ref(false)
const previewingSample = ref(false)
const importingSample = ref(false)
const sampleCategoryCodes = ref(['0101', '0102', '0204'])
const sampleLimitPerCategory = ref(10)
const samplePreview = ref<U9MaterialSamplePreview | null>(null)
const sampleCategoryOptions = [
  { code: '0101', name: '电气外购件' },
  { code: '0102', name: '机械外购件' },
  { code: '0204', name: '非标机加件' },
]
const integration = reactive<U9MaterialIntegrationSettings & { clientSecret: string }>({
  baseUrl: '', enterpriseCode: '', organizationCode: '', userCode: '', clientId: '', clientSecretConfigured: false,
  clientSecret: '', itemCreatePath: '', itemQueryPath: '', itemModifyPath: '', itemDeletePath: '', unitCodeMappings: {}, writeEnabled: false,
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

async function saveMaterialSettings() {
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
  savingMaterial.value = true
  try {
    const saved = await updateU9MaterialIntegration(buildUpdateInput(null), props.token)
    applyIntegrationSettings(saved)
    ElMessage.success(`U9C料品接口设置已保存，真实写入已${saved.writeEnabled ? '开启' : '关闭'}`)
  } catch (error) {
    ElMessage.error(error instanceof Error ? error.message : 'U9C料品接口设置保存失败')
  } finally {
    savingMaterial.value = false
  }
}

async function previewMaterialSample() {
  if (!sampleCategoryCodes.value.length) {
    ElMessage.warning('请至少选择一个分类')
    return
  }
  previewingSample.value = true
  try {
    samplePreview.value = await previewU9MaterialSample(sampleCategoryCodes.value, sampleLimitPerCategory.value, props.token)
    ElMessage.success(`只读预览完成，共核对 ${samplePreview.value.items.length} 个U9C料品`)
  } catch (error) {
    samplePreview.value = null
    ElMessage.error(error instanceof Error ? error.message : 'U9C料品样本预览失败')
  } finally {
    previewingSample.value = false
  }
}

async function importMaterialSample() {
  if (!samplePreview.value?.items.some(item => item.canImport)) {
    ElMessage.warning('当前预览没有可导入料品')
    return
  }
  try {
    await ElMessageBox.confirm(
      `将重新只读核对U9C，并向PDM导入所选分类每类最多 ${sampleLimitPerCategory.value} 条；不会向U9C写入。`,
      '确认导入PDM',
      { type: 'warning', confirmButtonText: '确认导入', cancelButtonText: '取消' },
    )
  } catch (error) {
    if (error === 'cancel' || error === 'close') return
    throw error
  }
  importingSample.value = true
  try {
    const result = await importU9MaterialSample(sampleCategoryCodes.value, sampleLimitPerCategory.value, props.token)
    samplePreview.value = result.preview
    ElMessage.success(`导入完成：新建 ${result.createdCount}，刷新 ${result.refreshedCount}，跳过 ${result.skippedCount}`)
  } catch (error) {
    ElMessage.error(error instanceof Error ? error.message : 'U9C料品样本导入失败')
  } finally {
    importingSample.value = false
  }
}

onMounted(loadIntegration)
</script>

<template>
  <section class="pdm-project-manager u9-integration-page" aria-label="U9C接口管理" v-loading="loading">
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

      <el-tab-pane v-if="canManageCustomers" label="客户查询" name="customers">
        <CustomerManagement
          :customers="customers"
          :integration-settings="customerConnectionSettings"
          :pending="pending"
          :on-save-settings="onSaveCustomerSettings"
          :on-test-connection="onTestCustomerConnection"
          :on-sync-customers="onSyncCustomers"
        />
      </el-tab-pane>

      <el-tab-pane v-if="canManageBase" label="料品同步" name="material-sync">
        <section class="pdm-panel u9-settings-card" aria-label="U9C料品样本同步">
          <header class="u9-card-heading"><div><h2>料品样本同步</h2><p>从U9C只读获取料品，在PDM端形成可搜索、可引用、可受控变更的主档。</p></div><span class="pdm-status is-warn">非全量</span></header>
          <el-alert title="本功能硬限制为 0101、0102、0204，每类最多10条。预览和导入只调用U9C查询接口，不会创建、修改或删除U9C料品。" type="warning" :closable="false" show-icon />
          <div class="sample-sync-controls">
            <el-checkbox-group v-model="sampleCategoryCodes" aria-label="样本同步分类">
              <el-checkbox v-for="category in sampleCategoryOptions" :key="category.code" :value="category.code">{{ category.code }} {{ category.name }}</el-checkbox>
            </el-checkbox-group>
            <el-form-item label="每类上限"><el-input-number v-model="sampleLimitPerCategory" :min="1" :max="10" :step="1" /></el-form-item>
            <div class="u9-actions">
              <el-button :loading="previewingSample" @click="previewMaterialSample">只读预览</el-button>
              <el-button type="primary" :loading="importingSample" :disabled="!samplePreview" @click="importMaterialSample">确认导入PDM</el-button>
            </div>
          </div>
          <el-table v-if="samplePreview" :data="samplePreview.items" row-key="materialCode" class="sample-sync-table" empty-text="所选分类未返回可核对料品">
            <el-table-column prop="materialCode" label="U9C料号" min-width="130" />
            <el-table-column prop="name" label="名称" min-width="150" show-overflow-tooltip />
            <el-table-column prop="categoryCode" label="分类" width="80" />
            <el-table-column prop="unitCode" label="PDM单位" width="90" />
            <el-table-column prop="specification" label="规格" min-width="170" show-overflow-tooltip />
            <el-table-column label="处理" width="170"><template #default="{ row }"><el-tag :type="row.canImport ? row.existsInPdm ? 'warning' : 'success' : 'info'">{{ row.decision }}</el-tag></template></el-table-column>
          </el-table>
        </section>
      </el-tab-pane>

      <el-tab-pane v-if="canManageBase" label="料品接口" name="materials">
        <section class="pdm-panel u9-settings-card" aria-label="U9C料品接口设置">
          <header class="u9-card-heading"><div><h2>料品接口</h2><p>维护料品接口合同和真实写入开关；PDM直接使用U9C计量单位编码，不再维护单位映射。OAuth参数来自“基础设置”。</p></div></header>
          <el-form label-position="top" class="u9-settings-form">
            <div class="u9-form-grid">
              <el-form-item label="料品创建接口路径"><el-input v-model="integration.itemCreatePath" name="u9ItemCreatePath" /></el-form-item>
              <el-form-item label="料品查询接口路径"><el-input v-model="integration.itemQueryPath" name="u9ItemQueryPath" /></el-form-item>
              <el-form-item label="料品修改接口路径"><el-input v-model="integration.itemModifyPath" name="u9ItemModifyPath" /></el-form-item>
              <el-form-item label="料品删除接口路径"><el-input v-model="integration.itemDeletePath" name="u9ItemDeletePath" /></el-form-item>
            </div>
            <el-checkbox v-model="integration.writeEnabled">启用人工确认后的真实写入</el-checkbox>
            <p class="u9-write-note">开启后仍不会自动写入；每个任务必须人工确认，系统先按料号查询，再仅创建不存在的料品。</p>
            <div class="u9-actions"><el-button type="primary" :loading="savingMaterial" @click="saveMaterialSettings">保存料品接口</el-button></div>
          </el-form>
        </section>
      </el-tab-pane>
    </el-tabs>
  </section>
</template>

<style scoped>
.u9-integration-page{min-width:0}.u9-interface-tabs{margin-top:4px}.u9-settings-card{padding:24px}.u9-card-heading{display:flex;align-items:flex-start;justify-content:space-between;gap:18px;margin-bottom:20px}.u9-card-heading h2{margin:0 0 6px;font-size:20px}.u9-card-heading p{margin:0;color:#64748b;line-height:1.6}.u9-settings-form{margin-top:18px}.u9-form-grid{display:grid;grid-template-columns:repeat(2,minmax(0,1fr));gap:0 18px}.u9-actions{display:flex;gap:10px;margin-top:4px}.sample-sync-controls{display:grid;grid-template-columns:minmax(320px,1fr) 150px auto;align-items:end;gap:18px;margin:20px 0}.sample-sync-controls :deep(.el-form-item){margin-bottom:0}.sample-sync-table{width:100%}.u9-write-note{margin:6px 0 18px;color:#9a3412;font-size:13px}@media(max-width:900px){.u9-form-grid{grid-template-columns:1fr}.sample-sync-controls{grid-template-columns:1fr}.u9-settings-card{padding:18px}}
</style>
