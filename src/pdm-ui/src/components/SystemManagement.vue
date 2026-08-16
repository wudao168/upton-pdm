<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import AuditLog from './AuditLog.vue'
import CustomerManagement from './CustomerManagement.vue'
import StorageSettings from './StorageSettings.vue'
import OrganizationSettings from './OrganizationSettings.vue'
import FolderTemplateSettings from './FolderTemplateSettings.vue'
import RolePermissionSettings from './RolePermissionSettings.vue'
import type { AuditEntry, CrmConnectionTestResult, CrmCustomerSyncResult, CrmIntegrationSettings, EquipmentTypeDefinition, OrganizationDirectory, OrganizationUnit, PdmCustomer, PdmSystemSettings, ProjectFolderTemplateNode, ProjectNumberingOptions, ProjectOrganization, RolePermissionDirectory, SaveOrganizationUnitInput, SaveProjectOrganizationInput, UpdateCrmIntegrationInput } from '../types'

const props = defineProps<{
  customers: PdmCustomer[]
  crmIntegrationSettings: CrmIntegrationSettings
  settings: PdmSystemSettings
  equipmentTypes: EquipmentTypeDefinition[]
  numberingOptions: ProjectNumberingOptions
  organizationDirectory: OrganizationDirectory
  rolePermissionDirectory: RolePermissionDirectory
  permissions: string[]
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
  onSaveFolderTemplate: (nodes: ProjectFolderTemplateNode[]) => Promise<ProjectFolderTemplateNode[]>
  onUpdateRolePermissions: (role: string, permissions: string[]) => Promise<RolePermissionDirectory>
}>()
defineEmits<{ refreshAudit: [] }>()

type AdminTab = 'customers' | 'organization' | 'roles' | 'folders' | 'settings' | 'audit'
const activeTab = ref<AdminTab>('customers')
const hasPermission = (code: string) => props.permissions.includes(code)
const availableTabs = computed<AdminTab[]>(() => [
  hasPermission('settings.customer.manage') && 'customers',
  hasPermission('settings.organization.manage') && 'organization',
  hasPermission('system.role.view') && 'roles',
  hasPermission('settings.folder.manage') && 'folders',
  hasPermission('settings.storage.manage') && 'settings',
  hasPermission('audit.view') && 'audit',
].filter((tab): tab is AdminTab => Boolean(tab)))
watch(availableTabs, tabs => { if (!tabs.includes(activeTab.value)) activeTab.value = tabs[0] ?? 'roles' }, { immediate: true })
</script>

<template>
  <section class="pdm-admin-workspace" aria-label="系统管理">
    <nav class="pdm-admin-tabs" aria-label="系统管理功能">
      <button v-if="availableTabs.includes('customers')" type="button" :class="{ 'is-active': activeTab === 'customers' }" @click="activeTab='customers'">CRM客户</button>
      <button v-if="availableTabs.includes('organization')" type="button" :class="{ 'is-active': activeTab === 'organization' }" @click="activeTab='organization'">组织结构</button>
      <button v-if="availableTabs.includes('roles')" type="button" :class="{ 'is-active': activeTab === 'roles' }" @click="activeTab='roles'">角色权限</button>
      <button v-if="availableTabs.includes('folders')" type="button" :class="{ 'is-active': activeTab === 'folders' }" @click="activeTab='folders'">文件夹模板</button>
      <button v-if="availableTabs.includes('settings')" type="button" :class="{ 'is-active': activeTab === 'settings' }" @click="activeTab='settings'">编号与存储</button>
      <button v-if="availableTabs.includes('audit')" type="button" :class="{ 'is-active': activeTab === 'audit' }" @click="activeTab='audit'">全局审计</button>
    </nav>
    <CustomerManagement v-if="activeTab === 'customers'" :customers="customers" :integration-settings="crmIntegrationSettings" :pending="pending" :on-save-settings="onSaveCrmIntegrationSettings" :on-test-connection="onTestCrmIntegration" :on-sync-customers="onSyncCrmCustomers" />
    <OrganizationSettings v-else-if="activeTab === 'organization'" :directory="organizationDirectory" :pending="pending" :on-save-organization="onSaveOrganization" :on-save-unit="onSaveUnit" :on-update-memberships="onUpdateMemberships" :on-update-managers="onUpdateManagers" />
    <RolePermissionSettings v-else-if="activeTab === 'roles'" :directory="rolePermissionDirectory" :can-edit="hasPermission('system.role.edit')" :pending="pending" :on-save="onUpdateRolePermissions" />
    <FolderTemplateSettings v-else-if="activeTab === 'folders'" :nodes="folderTemplate" :users="organizationDirectory.users" :pending="pending" :on-save="onSaveFolderTemplate" />
    <StorageSettings v-else-if="activeTab === 'settings'" :settings="settings" :equipment-types="equipmentTypes" :numbering-options="numberingOptions" :pending="pending" :on-save-settings="onSaveSettings" :on-save-equipment-type="onSaveEquipmentType" :on-update-counters="onUpdateCounters" />
    <section v-else-if="activeTab === 'audit'" class="pdm-project-manager"><header class="pdm-pagebar"><div><div class="pdm-breadcrumb">系统管理 <span>/</span> 全局审计</div><h1>全局审计</h1><p>查看全系统关键操作记录。</p></div></header><AuditLog :entries="auditEntries" title="全局操作记录" description="查看所有项目的存档、审批、发布和系统管理操作。" @refresh="$emit('refreshAudit')" /></section>
  </section>
</template>
