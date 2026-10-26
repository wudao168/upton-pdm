<script setup lang="ts">
import { computed, watch } from 'vue'
import { rememberedTab } from '../pagePreference'
import AuditLog from './AuditLog.vue'
import ClientLogManagement from './ClientLogManagement.vue'
import LoginSecuritySettings from './LoginSecuritySettings.vue'
import U9IntegrationManagement from './U9IntegrationManagement.vue'
import StorageSettings from './StorageSettings.vue'
import DingTalkSettings from './DingTalkSettings.vue'
import ReleaseNotificationSettings from './ReleaseNotificationSettings.vue'
import StockedMaterialSettings from './StockedMaterialSettings.vue'
import ApprovalWorkflowSettings from './ApprovalWorkflowSettings.vue'
import FolderTemplateSettings from './FolderTemplateSettings.vue'
import UserSettings from './UserSettings.vue'
import ProjectPermissionSettings from './ProjectPermissionSettings.vue'
import ProductionScanSettings from './ProductionScanSettings.vue'
import DocumentOperationRules from './DocumentOperationRules.vue'
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
  platformAdministrator: boolean
  canManageClientLogs?: boolean
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

type AdminTab = 'u9' | 'users' | 'project-permissions' | 'folders' | 'settings' | 'dingtalk' | 'release-notifications' | 'stocked-materials' | 'approval' | 'audit' | 'client-logs' | 'production-scan' | 'document-rules' | 'login-security'
const activeTab = rememberedTab<AdminTab>('system.tab', 'u9', ['u9','users','project-permissions','folders','settings','dingtalk','release-notifications','stocked-materials','approval','audit','client-logs','production-scan','document-rules','login-security'])
const hasPermission = (code: string) => props.permissions.includes(code)
const availableTabs = computed<AdminTab[]>(() => [
  (hasPermission('settings.customer.manage') || hasPermission('settings.storage.manage')) && 'u9',
  (hasPermission('settings.organization.manage') || hasPermission('system.role.view')) && 'users',
  hasPermission('system.project-permission.view') && 'project-permissions',
  hasPermission('settings.folder.manage') && 'folders',
  hasPermission('settings.storage.manage') && 'settings',
  'document-rules',
  hasPermission('settings.storage.manage') && 'dingtalk',
  hasPermission('settings.storage.manage') && 'release-notifications',
  hasPermission('settings.storage.manage') && 'stocked-materials',
  hasPermission('settings.storage.manage') && 'approval',
  hasPermission('production.scan.manage') && 'production-scan',
  hasPermission('audit.view') && 'audit',
  props.canManageClientLogs && 'client-logs',
  (props.platformAdministrator || props.canManageClientLogs) && 'login-security',
].filter((tab): tab is AdminTab => Boolean(tab)))
watch(availableTabs, tabs => { if (!tabs.includes(activeTab.value)) activeTab.value = tabs[0] ?? 'users' }, { immediate: true })
</script>

<template>
  <section class="pdm-admin-workspace" aria-label="系统管理">
    <nav class="pdm-admin-tabs pdm-tabs" aria-label="系统管理功能">
      <button v-if="availableTabs.includes('u9')" type="button" :class="{ 'is-active': activeTab === 'u9' }" @click="activeTab='u9'">U9C接口</button>
      <button v-if="availableTabs.includes('users')" type="button" :class="{ 'is-active': activeTab === 'users' }" @click="activeTab='users'">用户设置</button>
      <button v-if="availableTabs.includes('project-permissions')" type="button" :class="{ 'is-active': activeTab === 'project-permissions' }" @click="activeTab='project-permissions'">项目权限</button>
      <button v-if="availableTabs.includes('folders')" type="button" :class="{ 'is-active': activeTab === 'folders' }" @click="activeTab='folders'">文件夹模板</button>
      <button v-if="availableTabs.includes('settings')" type="button" :class="{ 'is-active': activeTab === 'settings' }" @click="activeTab='settings'">编号与存储</button>
      <button v-if="availableTabs.includes('document-rules')" type="button" :class="{ 'is-active': activeTab === 'document-rules' }" @click="activeTab='document-rules'">图档操作规则</button>
      <button v-if="availableTabs.includes('dingtalk')" type="button" :class="{ 'is-active': activeTab === 'dingtalk' }" @click="activeTab='dingtalk'">钉钉应用</button>
      <button v-if="availableTabs.includes('release-notifications')" type="button" :class="{ 'is-active': activeTab === 'release-notifications' }" @click="activeTab='release-notifications'">发布通知</button>
      <button v-if="availableTabs.includes('stocked-materials')" type="button" :class="{ 'is-active': activeTab === 'stocked-materials' }" @click="activeTab='stocked-materials'">常备件设置</button>
      <button v-if="availableTabs.includes('approval')" type="button" :class="{ 'is-active': activeTab === 'approval' }" @click="activeTab='approval'">审批流程</button>
      <button v-if="availableTabs.includes('production-scan')" type="button" :class="{ 'is-active': activeTab === 'production-scan' }" @click="activeTab='production-scan'">生产扫码设置</button>
      <button v-if="availableTabs.includes('audit')" type="button" :class="{ 'is-active': activeTab === 'audit' }" @click="activeTab='audit'">全局审计</button>
      <button v-if="availableTabs.includes('client-logs')" type="button" :class="{ 'is-active': activeTab === 'client-logs' }" @click="activeTab='client-logs'">客户端日志</button>
      <button v-if="availableTabs.includes('login-security')" type="button" :class="{ 'is-active': activeTab === 'login-security' }" @click="activeTab='login-security'">登录安全</button>
    </nav>
    <U9IntegrationManagement
      v-if="activeTab === 'u9'"
      :token="token"
      :customers="customers"
      :customer-settings="crmIntegrationSettings"
      :pending="pending"
      :can-manage-base="hasPermission('settings.storage.manage')"
      :can-manage-customers="hasPermission('settings.customer.manage')"
      :on-save-customer-settings="onSaveCrmIntegrationSettings"
      :on-test-customer-connection="onTestCrmIntegration"
      :on-sync-customers="onSyncCrmCustomers"
    />
    <UserSettings v-else-if="activeTab === 'users'" :directory="organizationDirectory" :role-directory="rolePermissionDirectory" :active-organization-id="activeOrganizationId" :permissions="permissions" :current-username="currentUsername" :platform-administrator="platformAdministrator" :pending="pending" :on-save-user="onSaveUser" :on-reset-password="onResetUserPassword" :on-save-role-permissions="onUpdateRolePermissions" :on-create-role="onCreateRole" :on-delete-role="onDeleteRole" :on-save-organization="onSaveOrganization" :on-save-unit="onSaveUnit" :on-update-memberships="onUpdateMemberships" :on-update-managers="onUpdateManagers" @update-active-organization-id="$emit('updateActiveOrganizationId', $event)" />
    <ProjectPermissionSettings v-else-if="activeTab === 'project-permissions'" :token="token" :can-edit="hasPermission('system.project-permission.edit')" />
    <ProductionScanSettings v-else-if="activeTab === 'production-scan'" :key="activeOrganizationId" :token="token" />
    <FolderTemplateSettings v-else-if="activeTab === 'folders'" :nodes="folderTemplate" :users="organizationDirectory.users" :roles="rolePermissionDirectory.roles" :pending="pending" :on-save="onSaveFolderTemplate" />
    <StorageSettings v-else-if="activeTab === 'settings'" :settings="settings" :token="token" :equipment-types="equipmentTypes" :numbering-options="numberingOptions" :pending="pending" :on-save-settings="onSaveSettings" :on-save-equipment-type="onSaveEquipmentType" :on-update-counters="onUpdateCounters" />
    <DocumentOperationRules v-else-if="activeTab === 'document-rules'" :key="activeOrganizationId" :token="token" :can-edit="hasPermission('settings.storage.manage')" />
    <DingTalkSettings v-else-if="activeTab === 'dingtalk'" :key="activeOrganizationId" :token="token" />
    <ReleaseNotificationSettings v-else-if="activeTab === 'release-notifications'" :key="activeOrganizationId" :token="token" />
    <StockedMaterialSettings v-else-if="activeTab === 'stocked-materials'" :key="activeOrganizationId" :token="token" />
    <ClientLogManagement v-else-if="activeTab === 'client-logs'" :key="activeOrganizationId" :token="token" />
    <LoginSecuritySettings v-else-if="activeTab === 'login-security'" :key="activeOrganizationId" :token="token" />
    <ApprovalWorkflowSettings v-else-if="activeTab === 'approval'" :token="token" :company-id="activeOrganizationId" :settings="settings" :directory="organizationDirectory" :roles="rolePermissionDirectory.roles" :pending="pending" :on-save="onSaveSettings" />
    <section v-else-if="activeTab === 'audit'" class="pdm-project-manager"><AuditLog :entries="auditEntries" title="全局操作记录" description="查看所有项目的存档、审批、发布和系统管理操作。" @refresh="$emit('refreshAudit')" /></section>
  </section>
</template>
