<script setup lang="ts">
import { computed, watch } from 'vue'
import { rememberedTab } from '../pagePreference'
import { systemManagementTabs, type SystemTab } from '../navigationAccess'
import AuditLog from './AuditLog.vue'
import ClientLogManagement from './ClientLogManagement.vue'
import LoginSecuritySettings from './LoginSecuritySettings.vue'
import U9IntegrationManagement from './U9IntegrationManagement.vue'
import StorageSettings from './StorageSettings.vue'
import AiProviderSettings from './AiProviderSettings.vue'
import DingTalkSettings from './DingTalkSettings.vue'
import StockedMaterialSettings from './StockedMaterialSettings.vue'
import ApprovalWorkflowSettings from './ApprovalWorkflowSettings.vue'
import FolderTemplateSettings from './FolderTemplateSettings.vue'
import UserSettings from './UserSettings.vue'
import PermissionCenter from './PermissionCenter.vue'
import ProductionScanSettings from './ProductionScanSettings.vue'
import type { AuditEntry, CreateRoleInput, CrmConnectionTestResult, CrmCustomerSyncResult, CrmIntegrationSettings, EquipmentTypeDefinition, OrganizationDirectory, OrganizationUnit, PdmCustomer, PdmSystemSettings, PdmUser, ProjectFolderTemplateNode, ProjectNumberingOptions, ProjectOrganization, RolePermissionDirectory, SaveOrganizationUnitInput, SavePdmUserInput, SaveProjectOrganizationInput, UpdateCrmIntegrationInput } from '../types'

const props = defineProps<{
  token: string
  customers: PdmCustomer[]
  crmIntegrationSettings: CrmIntegrationSettings
  settings: PdmSystemSettings
  equipmentTypes: EquipmentTypeDefinition[]
  numberingOptions: ProjectNumberingOptions
  organizationDirectory: OrganizationDirectory
  rolePermissionDirectory: RolePermissionDirectory
  activeOrganizationId: string
  permissions: string[]
  currentUsername: string
  projectId?: string
  administrator?: boolean
  platformAdministrator: boolean
  auditEntries: AuditEntry[]
  folderTemplate: ProjectFolderTemplateNode[]
  pending: boolean
  onSaveCrmIntegrationSettings: (input: UpdateCrmIntegrationInput) => Promise<CrmIntegrationSettings>
  onTestCrmIntegration: () => Promise<CrmConnectionTestResult>
  onSyncCrmCustomers: () => Promise<CrmCustomerSyncResult>
  onSaveSettings: (settings: PdmSystemSettings) => Promise<PdmSystemSettings>
  onSaveEquipmentType: (input: EquipmentTypeDefinition) => Promise<EquipmentTypeDefinition>
  onUpdateCounters: (organizationId: string, currentProjectSequence: number, currentSerialSequence: number) => Promise<ProjectNumberingOptions>
  onSaveOrganization: (input: SaveProjectOrganizationInput) => Promise<ProjectOrganization>
  onSaveUnit: (input: SaveOrganizationUnitInput) => Promise<OrganizationUnit>
  onUpdateMemberships: (username: string, unitIds: string[], primaryUnitId: string) => Promise<OrganizationDirectory>
  onUpdateManagers: (unitId: string, primaryManager: string, collaborativeManagers: string[]) => Promise<OrganizationDirectory>
  onSaveUser: (input: SavePdmUserInput, creating: boolean) => Promise<PdmUser>
  onResetUserPassword: (username: string) => Promise<PdmUser>
  onSaveFolderTemplate: (nodes: ProjectFolderTemplateNode[]) => Promise<ProjectFolderTemplateNode[]>
  onUpdateRolePermissions: (role: string, permissions: string[]) => Promise<RolePermissionDirectory>
  onCreateRole: (input: CreateRoleInput) => Promise<RolePermissionDirectory>
  onDeleteRole: (role: string) => Promise<RolePermissionDirectory>
}>()
defineEmits<{ refreshAudit: []; updateActiveOrganizationId: [organizationId: string] }>()

const activeTab = rememberedTab<SystemTab>('system.tab', 'u9', systemManagementTabs(() => true))
const hasPermission = (code: string) => props.permissions.includes(code)
const availableTabs = computed(() => systemManagementTabs(hasPermission))
watch(availableTabs, tabs => { if (!tabs.includes(activeTab.value)) activeTab.value = tabs[0] ?? 'users' }, { immediate: true })
</script>

<template>
  <section class="pdm-admin-workspace" aria-label="系统管理">
    <header class="pdm-admin-header">
    <nav class="pdm-admin-tabs pdm-tabs" aria-label="系统管理功能">
      <button v-if="availableTabs.includes('u9')" type="button" :class="{ 'is-active': activeTab === 'u9' }" @click="activeTab='u9'">系统接口</button>
      <button v-if="availableTabs.includes('ai')" type="button" :class="{ 'is-active': activeTab === 'ai' }" @click="activeTab='ai'">AI服务</button>
      <button v-if="availableTabs.includes('users')" type="button" :class="{ 'is-active': activeTab === 'users' }" @click="activeTab='users'">用户设置</button>
      <button v-if="availableTabs.includes('settings')" type="button" :class="{ 'is-active': activeTab === 'settings' }" @click="activeTab='settings'">编号与存储</button>
      <button v-if="availableTabs.includes('approval')" type="button" :class="{ 'is-active': activeTab === 'approval' }" @click="activeTab='approval'">审批流程</button>
      <button v-if="availableTabs.includes('production-scan')" type="button" :class="{ 'is-active': activeTab === 'production-scan' }" @click="activeTab='production-scan'">生产扫码设置</button>
      <button v-if="availableTabs.includes('audit')" type="button" :class="{ 'is-active': activeTab === 'audit' }" @click="activeTab='audit'">全局审计</button>
      <button v-if="availableTabs.includes('client-logs')" type="button" :class="{ 'is-active': activeTab === 'client-logs' }" @click="activeTab='client-logs'">客户端日志</button>
      <button v-if="availableTabs.includes('login-security')" type="button" :class="{ 'is-active': activeTab === 'login-security' }" @click="activeTab='login-security'">登录安全</button>
    </nav>
      <slot name="version" />
    </header>
    <U9IntegrationManagement
      v-if="activeTab === 'u9'"
      :token="token"
      :customers="customers"
      :customer-settings="crmIntegrationSettings"
      :pending="pending"
      :can-view-u9="hasPermission('settings.storage.manage') || hasPermission('settings.customer.manage')" :can-manage-base="hasPermission('settings.storage.manage')"
      :can-manage-customers="hasPermission('settings.customer.manage')"
      :on-save-customer-settings="onSaveCrmIntegrationSettings"
      :on-test-customer-connection="onTestCrmIntegration"
      :on-sync-customers="onSyncCrmCustomers"
    >
      <template v-if="hasPermission('stock.settings.manage')" #stocked-materials>
        <StockedMaterialSettings :key="activeOrganizationId" :token="token" :can-synchronize="hasPermission('stock.synchronize')" />
      </template>
      <template #dingtalk>
        <DingTalkSettings :key="activeOrganizationId" :token="token" />
      </template>
    </U9IntegrationManagement>
    <UserSettings v-else-if="activeTab === 'users'" :directory="organizationDirectory" :role-directory="rolePermissionDirectory" :active-organization-id="activeOrganizationId" :token="token" :permissions="permissions" :current-username="currentUsername" :platform-administrator="platformAdministrator" :pending="pending" :on-save-user="onSaveUser" :on-reset-password="onResetUserPassword" :on-save-role-permissions="onUpdateRolePermissions" :on-create-role="onCreateRole" :on-delete-role="onDeleteRole" :on-save-organization="onSaveOrganization" :on-save-unit="onSaveUnit" :on-update-memberships="onUpdateMemberships" :on-update-managers="onUpdateManagers" @update-active-organization-id="$emit('updateActiveOrganizationId', $event)">
      <template #permissions>
        <PermissionCenter :token="token" :company-id="activeOrganizationId" :roles="rolePermissionDirectory.roles" />
      </template>
    </UserSettings>
    <AiProviderSettings v-else-if="activeTab === 'ai'" :key="activeOrganizationId" :token="token" />
    <ProductionScanSettings v-else-if="activeTab === 'production-scan'" :key="activeOrganizationId" :token="token" />
    <StorageSettings :administrator="administrator || platformAdministrator" v-else-if="activeTab === 'settings'" :settings="settings" :token="token" :equipment-types="equipmentTypes" :numbering-options="numberingOptions" :pending="pending" :on-save-settings="onSaveSettings" :on-save-equipment-type="onSaveEquipmentType" :on-update-counters="onUpdateCounters" :can-manage-storage="hasPermission('settings.storage.manage')" :can-manage-folders="hasPermission('settings.folder.manage')">
      <template #folders>
        <FolderTemplateSettings :nodes="folderTemplate" :users="organizationDirectory.users" :roles="rolePermissionDirectory.roles" :pending="pending" :on-save="onSaveFolderTemplate" />
      </template>
    </StorageSettings>
    <ClientLogManagement v-else-if="activeTab === 'client-logs'" :key="activeOrganizationId" :token="token" />
    <LoginSecuritySettings v-else-if="activeTab === 'login-security'" :key="activeOrganizationId" :token="token" />
    <ApprovalWorkflowSettings v-else-if="activeTab === 'approval'" :token="token" :company-id="activeOrganizationId" :settings="settings" :directory="organizationDirectory" :roles="rolePermissionDirectory.roles" :pending="pending" :on-save="onSaveSettings" />
    <section v-else-if="activeTab === 'audit'" class="pdm-project-manager"><AuditLog :entries="auditEntries" title="全局操作记录" description="查看所有项目的存档、审批、发布和系统管理操作。" @refresh="$emit('refreshAudit')" /></section>
  </section>
</template>

<style scoped>
.pdm-admin-header {
  display: flex;
  align-items: flex-start;
  gap: 8px;
  flex: 0 0 auto;
  margin-bottom: var(--pdm-container-gap);
}
.pdm-admin-header > .pdm-admin-tabs {
  flex: 1 1 auto;
  min-width: 0;
  margin-block-end: 0 !important;
}
</style>
