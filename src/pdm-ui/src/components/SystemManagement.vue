<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import AuditLog from './AuditLog.vue'
import U9IntegrationManagement from './U9IntegrationManagement.vue'
import StorageSettings from './StorageSettings.vue'
import FolderTemplateSettings from './FolderTemplateSettings.vue'
import UserSettings from './UserSettings.vue'
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
  permissions: string[]
  currentUsername: string
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
defineEmits<{ refreshAudit: [] }>()

type AdminTab = 'u9' | 'users' | 'folders' | 'settings' | 'audit'
const activeTab = ref<AdminTab>('u9')
const hasPermission = (code: string) => props.permissions.includes(code)
const availableTabs = computed<AdminTab[]>(() => [
  (hasPermission('settings.customer.manage') || hasPermission('settings.storage.manage')) && 'u9',
  (hasPermission('settings.organization.manage') || hasPermission('system.role.view')) && 'users',
  hasPermission('settings.folder.manage') && 'folders',
  hasPermission('settings.storage.manage') && 'settings',
  hasPermission('audit.view') && 'audit',
].filter((tab): tab is AdminTab => Boolean(tab)))
watch(availableTabs, tabs => { if (!tabs.includes(activeTab.value)) activeTab.value = tabs[0] ?? 'users' }, { immediate: true })
</script>

<template>
  <section class="pdm-admin-workspace" aria-label="系统管理">
    <nav class="pdm-admin-tabs" aria-label="系统管理功能">
      <button v-if="availableTabs.includes('u9')" type="button" :class="{ 'is-active': activeTab === 'u9' }" @click="activeTab='u9'">U9C接口</button>
      <button v-if="availableTabs.includes('users')" type="button" :class="{ 'is-active': activeTab === 'users' }" @click="activeTab='users'">用户设置</button>
      <button v-if="availableTabs.includes('folders')" type="button" :class="{ 'is-active': activeTab === 'folders' }" @click="activeTab='folders'">文件夹模板</button>
      <button v-if="availableTabs.includes('settings')" type="button" :class="{ 'is-active': activeTab === 'settings' }" @click="activeTab='settings'">编号与存储</button>
      <button v-if="availableTabs.includes('audit')" type="button" :class="{ 'is-active': activeTab === 'audit' }" @click="activeTab='audit'">全局审计</button>
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
    <UserSettings v-else-if="activeTab === 'users'" :directory="organizationDirectory" :role-directory="rolePermissionDirectory" :permissions="permissions" :current-username="currentUsername" :pending="pending" :on-save-user="onSaveUser" :on-reset-password="onResetUserPassword" :on-save-role-permissions="onUpdateRolePermissions" :on-create-role="onCreateRole" :on-delete-role="onDeleteRole" :on-save-organization="onSaveOrganization" :on-save-unit="onSaveUnit" :on-update-memberships="onUpdateMemberships" :on-update-managers="onUpdateManagers" />
    <FolderTemplateSettings v-else-if="activeTab === 'folders'" :nodes="folderTemplate" :users="organizationDirectory.users" :pending="pending" :on-save="onSaveFolderTemplate" />
    <StorageSettings v-else-if="activeTab === 'settings'" :settings="settings" :equipment-types="equipmentTypes" :numbering-options="numberingOptions" :pending="pending" :on-save-settings="onSaveSettings" :on-save-equipment-type="onSaveEquipmentType" :on-update-counters="onUpdateCounters" />
    <section v-else-if="activeTab === 'audit'" class="pdm-project-manager"><AuditLog :entries="auditEntries" title="全局操作记录" description="查看所有项目的存档、审批、发布和系统管理操作。" @refresh="$emit('refreshAudit')" /></section>
  </section>
</template>
