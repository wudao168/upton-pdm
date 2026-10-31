import approvedAppIcon from '../src/assets/plm-app-icon.svg'
import { flushPromises as flushVuePromises, mount } from '@vue/test-utils'
import ElementPlus, { ElMessageBox } from 'element-plus'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import App from '../src/App.vue'
import { ElMessage, globalStatus } from '../src/statusMessage'

async function flushPromises() {
  await flushVuePromises()
  if (!vi.isFakeTimers()) await vi.dynamicImportSettled()
  await flushVuePromises()
}

const projectId = '11111111-1111-1111-1111-111111111111'
let materialRequestsUnauthorized = false
let resumeRequestsUnauthorized = false
let passwordResetRequestsForbidden = false
let drawingReviewsResponse: Array<Record<string, unknown>> = []
let nonStandardBomResponse: Array<Record<string, unknown>> = []

function json(value: unknown, status = 200) {
  return new Response(JSON.stringify(value), { status, headers: { 'Content-Type': 'application/json' } })
}

function installApiMock(projectsBeforeDefault: Array<Record<string, unknown>> = [], loginRoleCodes?: string[], loginPermissions?: string[]) {
  const projects: Array<Record<string, unknown>> = [...projectsBeforeDefault, { id: projectId, code: 'PRJ-REAL-001', name: '真实装配项目', owner: 'engineer', primaryProjectManager: 'engineer', collaborativeProjectManagers: [], responsibleUsers: ['engineer'], vaultLocation: 'D:\\PDM\\PRJ-REAL-001', releaseLocation: 'D:\\Release\\PRJ-REAL-001', isActive: true, quantity: 1, serialNumbers: [], effectiveProjectPermissions: ['project.plan.edit', 'project.edit', 'project.child.create', 'bom.edit', 'bom.mechanical.edit', 'bom.electrical.edit', 'validation-plan.edit', 'release.manage'] }]
  const customers = [{ id: 'customer-1', code: 'C00465', name: '中山比亚迪电子有限公司', isActive: true }]
  let crmSettings = { baseUrl: 'http://10.7.7.188/U9', username: 'pdm', passwordConfigured: true, autoSyncEnabled: false, autoSyncIntervalMinutes: 60, lastSyncAt: null as string | null, lastSyncCount: 0, lastAutoSyncAttemptAt: null as string | null, lastAutoSyncError: null as string | null }
  let u9Settings = { baseUrl: 'http://10.7.7.188/U9', enterpriseCode: '01', organizationCode: '7', userCode: 'pdm', clientId: 'PDM', clientSecretConfigured: true, itemCreatePath: '/webapi/ItemMaster/Create', itemQueryPath: '/webapi/ItemMaster/Query', itemModifyPath: '/webapi/ItemMaster/Modify', itemDeletePath: '/webapi/ItemMaster/Delete', unitCodeMappings: {}, writeEnabled: false }
  const validationRules = { standard: ['drawingNumber', 'name', 'unit', 'specification', 'quantity', 'revision'], nonStandard: ['drawingNumber', 'name', 'unit', 'material', 'quantity', 'revision'], electrical: ['drawingNumber', 'name', 'unit', 'quantity', 'revision'] }
  const editLocks = [{ documentId: 'doc-lock', projectId, projectCode: 'PRJ-REAL-001', projectName: '真实装配项目', drawingNumber: 'LOCK-001', documentName: '长期编辑图档', fileName: 'LOCK-001.SLDPRT', checkedOutBy: 'designer', checkedOutAt: '2026-08-14T00:00:00Z', checkoutMachine: 'DESIGN-WS', lastHeartbeatAt: '2026-08-14T00:03:00Z', leaseExpiresAt: '2026-08-14T00:18:00Z', connectionState: 'Active', attentionLevel: 'Reminder', releaseRequestedBy: null, releaseRequestedAt: null, releaseRequestReason: null, ownedByCurrentUser: false, canRequestRelease: true, canForceRelease: false }]
  const engineerPermissions = ['project.view', 'project.create', 'project.child.create', 'project.plan.edit', 'project.staffing.manage', 'project.designer.assign', 'project.content.view', 'document.edit', 'bom.edit', 'bom.mechanical.edit', 'bom.electrical.edit', 'material.view', 'material.apply', 'release.manage', 'standard-library.view']
  const adminPermissions = [...engineerPermissions, 'material.manage', 'project.delete', 'project.execution.assign', 'approval.decide', 'settings.customer.manage', 'settings.organization.manage', 'settings.folder.manage', 'settings.storage.manage', 'system.role.view', 'system.role.edit', 'audit.view']
  const roleDirectory = {
    permissions: [
      { code: 'project.view', name: '查看负责项目', module: '项目管理', sensitive: false },
      { code: 'project.designer.assign', name: '分配子项目设计人员', module: '项目分工', sensitive: false },
      { code: 'document.edit', name: '登记、签出和存档图档', module: '项目内容', sensitive: true },
      { code: 'material.apply', name: '申请新增料品', module: '料品管理', sensitive: false },
      { code: 'system.role.view', name: '查看角色权限', module: '角色权限', sensitive: false },
      { code: 'system.role.edit', name: '修改角色权限', module: '角色权限', sensitive: true },
    ],
    roles: [
      { role: 'Engineer', name: '工程师', description: '承担设计与图档工作。', baseRole: 'Engineer', isSystem: true, isSystemAdministrator: false, permissions: engineerPermissions, userCount: 1 },
      { role: 'Administrator', name: '系统管理员', description: '固定全部权限。', baseRole: 'Administrator', isSystem: true, isSystemAdministrator: true, permissions: adminPermissions, userCount: 1 },
    ],
  }
  vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = String(input).replace(/\?summary=true$/, '')
    if (url.endsWith('/client-bootstrap.json')) return json({ ConfigurationVersion: '2026.09.12.1234-version-display' })
    if (url.endsWith('/health')) return json({ status: 'ok' })
    if (url.endsWith('/api/approval-tasks/mine')) return json([])
    if (url.endsWith('/api/standard-structures')) return json({ catalog: { companyId: 'company', categories: [], structures: [], storageRoot: '', rowVersion: 0 }, canManage: false, canArchive: false, canConfigureStorage: false })
    if (url.endsWith('/api/edit-locks')) return json(editLocks)
    if (url.endsWith('/api/audit?take=200')) return json([])
    if (url.endsWith('/api/auth/login')) {
      const credentials = JSON.parse(String(init?.body)) as { username: string; password: string }
      return credentials.password === 'correct-password'
        ? json({ accessToken: 'test-token', expiresAt: '2099-01-01T00:00:00Z', resumeToken: 'test-resume-token', username: credentials.username, displayName: credentials.username === 'admin' ? '系统管理员' : '真实工程师', role: credentials.username === 'admin' ? 'Administrator' : 'Engineer', roles: loginRoleCodes, permissions: loginPermissions ?? (credentials.username === 'admin' ? adminPermissions : engineerPermissions) })
        : json({ title: 'Unauthorized' }, 401)
    }
    if (url.endsWith('/api/auth/resume')) return resumeRequestsUnauthorized
      ? json({ title: 'Unauthorized' }, 401)
      : json({ accessToken: 'renewed-token', expiresAt: '2099-01-01T00:00:00Z', resumeToken: 'renewed-resume-token', username: 'engineer', displayName: '真实工程师', role: 'Engineer', permissions: engineerPermissions })
    if (url.endsWith('/api/auth/me')) return json({ username: 'admin', displayName: '系统管理员', nickname: null, gender: 'unspecified', landline: null, mobilePhone: null, email: null })
    if (url.endsWith('/api/document-operation-rules')) return json({ revision: 0, disabledRuleIds: [] })
    if (url.endsWith('/api/password-reset-requests')) return passwordResetRequestsForbidden ? json({ title: 'Forbidden' }, 403) : json([])
    if (url.includes('/api/materials/page')) {
      if (materialRequestsUnauthorized) { materialRequestsUnauthorized = false; return json({ title: 'Unauthorized' }, 401) }
      return json({ items: [], total: 0, page: 1, pageSize: 50 })
    }
    if (url.endsWith('/api/materials') || url.includes('/api/materials?')) {
      if (materialRequestsUnauthorized) { materialRequestsUnauthorized = false; return json({ title: 'Unauthorized' }, 401) }
      return json([])
    }
    if (url.includes('/api/engineering-kits')) return json([])
    if (url.includes('/api/material-code/applications')) return json([])
    if (url.endsWith('/api/material-category-rules')) return json([])
    if (url.endsWith('/api/materials/notification-permission')) return json({ enabled: loginRoleCodes?.some(role => ['Approver', 'ProcessReviewer', 'StandardizationAssistant'].includes(role)) || loginPermissions?.includes('material.notify') || false })
    if (url.includes('/api/material-sync-tasks')) return json([])
    if (url.endsWith('/api/role-permissions')) return json(roleDirectory)
    if (url.includes('/api/role-permissions/') && init?.method === 'PUT') {
      const role = decodeURIComponent(url.split('/').at(-1)!)
      const request = JSON.parse(String(init.body)) as { permissions: string[] }
      const target = roleDirectory.roles.find(item => item.role === role)
      if (target && !target.isSystemAdministrator) target.permissions = [...new Set(request.permissions)]
      return json(roleDirectory)
    }
    if (url.endsWith('/api/customers')) {
      return json(customers)
    }
    if (url.endsWith('/api/crm-integration/test') && init?.method === 'POST') return json({ customerCount: 2, skippedCount: 1, testedAt: '2026-08-14T12:00:00Z' })
    if (url.endsWith('/api/crm-integration/sync') && init?.method === 'POST') {
      if (!customers.some(customer => customer.code === 'C00888')) customers.push({ id: 'customer-u9-2', code: 'C00888', name: 'U9C同步客户', isActive: true })
      crmSettings = { ...crmSettings, lastSyncAt: '2026-08-14T12:01:00Z', lastSyncCount: customers.length }
      return json({ customerCount: customers.length, skippedCount: 1, syncedAt: crmSettings.lastSyncAt, settings: crmSettings, customers })
    }
    if (url.endsWith('/api/crm-integration')) {
      if (init?.method === 'PUT') {
        const request = JSON.parse(String(init.body)) as { baseUrl: string; username: string; password?: string; autoSyncEnabled: boolean; autoSyncIntervalMinutes: number }
        crmSettings = { ...crmSettings, baseUrl: request.baseUrl, username: request.username, passwordConfigured: Boolean(request.password) || crmSettings.passwordConfigured, autoSyncEnabled: request.autoSyncEnabled, autoSyncIntervalMinutes: request.autoSyncIntervalMinutes }
      }
      return json(crmSettings)
    }
    if (url.endsWith('/api/u9-material-integration')) {
      if (init?.method === 'PUT') u9Settings = { ...u9Settings, ...JSON.parse(String(init.body)), clientSecretConfigured: true }
      return json(u9Settings)
    }
    if (url.endsWith('/api/u9-material-integration/test') && init?.method === 'POST') return json({ ...u9Settings, testedAt: '2026-08-20T00:00:00Z' })
    if (url.endsWith('/api/users')) return json([{ username: 'admin', displayName: '系统管理员', role: 'Administrator', isActive: true }, { username: 'engineer', displayName: '真实工程师', role: 'Engineer', isActive: true }])
    if (url.endsWith('/api/organization-directory')) return json({
      organizations: [{ id: '70000000-0000-0000-0000-000000000001', name: '昆山阿普顿自动化系统有限公司', projectCompanyCode: '7', modelCompanyCode: 'AK', crmCompanyName: '昆山阿普顿自动化系统有限公司', isActive: true, currentProjectSequence: 0, currentSerialSequence: 0 }],
      units: [], memberships: [], managers: [],
      users: [{ username: 'admin', displayName: '系统管理员', role: 'Administrator', isActive: true }, { username: 'engineer', displayName: '真实工程师', role: 'Engineer', isActive: true }],
    })
    if (url.endsWith('/api/bom-validation-rules')) return json(validationRules)
    if (url.endsWith('/api/system-settings')) return json(init?.method === 'PUT' ? JSON.parse(String(init.body)) : { vaultRoot: 'D:\\PDM\\Vault', releaseRoot: 'D:\\PDM\\Release', checkoutHeartbeatSeconds: 180, checkoutLeaseMinutes: 15, checkoutOfflineGraceMinutes: 60, checkoutReminderHours: 4, checkoutStrongReminderHours: 8, checkoutOverdueHours: 24, checkoutForceReleaseHours: 48, validationRules })
    if (url.endsWith('/api/system-settings/equipment-types')) return json([{ code: 0, name: '标准设备', isActive: true }, { code: 2, name: '测试设备', isActive: true }])
    if (url.includes('/api/system-settings/equipment-types/') && init?.method === 'PUT') return json({ code: Number(url.split('/').at(-1)), ...JSON.parse(String(init.body)) })
    if (url.endsWith('/api/folder-template')) return json([])
    if (url.endsWith('/api/project-numbering/options')) return json({
      organizations: [{ id: '70000000-0000-0000-0000-000000000001', name: '昆山阿普顿自动化系统有限公司', projectCompanyCode: '7', modelCompanyCode: 'AK', crmCompanyName: '昆山阿普顿自动化系统有限公司' }],
      projectTypes: [{ code: 'P', name: '标准项目' }, { code: 'W', name: '外发项目' }],
      equipmentTypes: [{ code: 0, name: '类型00' }, { code: 2, name: '类型02' }],
    })
    if (url.endsWith('/api/projects')) {
      if (init?.method === 'POST') {
        const request = JSON.parse(String(init.body)) as Record<string, unknown>
        const created = { id: 'project-created', ...request, code: 'P700001', projectAlias: request.projectAlias, organizationName: '昆山阿普顿自动化系统有限公司', customerCode: 'C00465', customerName: '中山比亚迪电子有限公司', customerProjectSequence: 1, deviceModel: 'AK-2-C00465-001-00', owner: 'engineer', responsibleUsers: ['engineer'], vaultLocation: 'D:\\PDM\\Vault\\P700001', releaseLocation: 'D:\\PDM\\Release\\P700001', serialNumbers: ['70000001', '70000002'], isActive: true }
        projects.push(created)
        return json(created, 201)
      }
      return json(projects)
    }
    if (url.endsWith('/api/projects/project-created/children') && init?.method === 'POST') {
      const request = JSON.parse(String(init.body)) as Record<string, unknown>
      const child = { id: 'project-child', ...request, code: 'P700001-1', parentProjectId: 'project-created', childSequence: 1, owner: 'engineer', responsibleUsers: ['engineer'], vaultLocation: 'D:\\PDM\\Vault\\P700001-1', releaseLocation: 'D:\\PDM\\Release\\P700001-1', customerCode: 'C00465', customerName: '中山比亚迪电子有限公司', deviceModel: 'AK-2-C00465-001-01', serialNumbers: ['70000003', '70000004'], isActive: true }
      projects.push(child)
      return json(child, 201)
    }
    if (url.includes('/api/projects/') && url.endsWith('/procurement-tracking')) {
      const requestedProjectId = url.split('/api/projects/')[1]?.split('/')[0] ?? ''
      const requestedProject = projects.find(project => project.id === requestedProjectId)
      const rootProject = requestedProject?.parentProjectId
        ? projects.find(project => project.id === requestedProject.parentProjectId)
        : requestedProject
      return json({
        projectId: requestedProjectId,
        projectCode: rootProject?.code ?? '',
        subprojectCode: requestedProject?.parentProjectId ? requestedProject.code : null,
        items: [],
        lastSuccessfulRefreshAt: null,
        lastRefreshError: null,
        hasPublishedBom: false,
      })
    }
    if (url.endsWith('/api/projects/project-child')) return json(projects.find(project => project.id === 'project-child'))
    if (url.includes('/api/projects/project-child/')) {
      if (url.endsWith('/reference-tree')) return json({ title: 'Not Found' }, 404)
      return json([])
    }
    if (url.endsWith('/api/projects/project-created')) return json(projects.find(project => project.id === 'project-created'))
    if (url.includes('/api/projects/project-created/')) {
      if (url.endsWith('/reference-tree')) return json({ title: 'Not Found' }, 404)
      return json([])
    }
    if (url.endsWith(`/api/projects/${projectId}/versions`)) return json([
      { id: 'version-w2', documentId: 'doc-root', drawingNumber: 'REAL-ASM-001', documentName: '真实总装配', fileName: 'REAL-ASM-001.SLDASM', revision: { display: 'W2' }, status: 'Work', createdBy: 'engineer', createdAt: '2026-08-11T02:00:00Z', changeNote: '调整材料' },
      { id: 'version-w1', documentId: 'doc-root', drawingNumber: 'REAL-ASM-001', documentName: '真实总装配', fileName: 'REAL-ASM-001.SLDASM', revision: { display: 'W1' }, status: 'Work', createdBy: 'engineer', createdAt: '2026-08-10T02:00:00Z', changeNote: '首次存档' },
    ])
    if (url.endsWith(`/api/projects/${projectId}/audit?take=200`)) return json([{ id: 'audit-1', occurredAt: '2026-08-11T02:00:00Z', actor: 'engineer', action: 'document.checkin', entityType: 'DocumentVersion', entityId: 'version-w2', detail: 'W2' }])
    if (url.split('?')[0].endsWith(`/api/projects/${projectId}/files`)) return json([])
    if (url.endsWith(`/api/projects/${projectId}/folders`)) return json([])
    if (url.endsWith(`/api/projects/${projectId}/drawing-reviews`) && (!init?.method || init.method === 'GET')) return json(drawingReviewsResponse)
    if (url.endsWith(`/api/projects/${projectId}/drawing-review-candidates`)) return json([
      { modelDocumentId: 'doc-root', drawingDocumentId: 'doc-drawing', drawingNumber: 'REAL-ASM-001', name: '真实总装配', configuration: '默认', bomKinds: ['NonStandard'], modelRevision: 'W2', drawingRevision: 'W2', state: 'Ready', selectable: true },
    ])
    if (url.endsWith(`/api/projects/${projectId}`)) return json(projects.find(item => item.id === projectId) ?? projects[0])
    if (url.endsWith('/document-relations')) return json([
      { modelDocumentId: 'doc-root', drawingDocumentId: 'doc-drawing' },
      { modelDocumentId: 'doc-root', drawingDocumentId: 'doc-drawing-missing' },
    ])
    if (url.endsWith('/documents') || url.endsWith('/folder-documents')) {
      return json([
        { id: 'doc-root', projectId, drawingNumber: 'REAL-ASM-001', name: '真实总装配', fileName: 'REAL-ASM-001.SLDASM', kind: 0, revision: { display: 'W2' }, storedVersionCount: 2, checkedOutBy: 'engineer' },
        { id: 'doc-part', projectId, drawingNumber: 'REAL-PRT-001', name: '真实底板', fileName: 'REAL-PRT-001.SLDPRT', kind: 1, revision: { display: 'A' }, storedVersionCount: 1, checkedOutBy: null },
        { id: 'doc-stale', projectId, drawingNumber: 'STALE-PRT-001', name: '历史版本子件', fileName: 'STALE-PRT-001.SLDPRT', kind: 1, revision: { display: 'W4' }, storedVersionCount: 4, checkedOutBy: null },
        { id: 'doc-drawing', projectId, drawingNumber: 'REAL-ASM-001', name: '真实总装工程图', fileName: 'REAL-ASM-001.SLDDRW', kind: 2, revision: { display: 'W2' }, storedVersionCount: 2, checkedOutBy: null },
        { id: 'doc-drawing-missing', projectId, drawingNumber: 'REAL-PRT-001', name: '遗漏工程图', fileName: 'REAL-PRT-001.SLDDRW', kind: 2, revision: { display: 'W1' }, storedVersionCount: 0, checkedOutBy: null },
      ])
    }
    if (url.endsWith('/reference-tree')) {
      return json({
        nodeId: 'node-root', documentId: 'doc-root', instancePath: 'REAL-ASM-001', fileName: 'REAL-ASM-001.SLDASM', displayName: '真实总装配', kind: 0, configuration: '默认', quantity: 1, status: 0, revision: { display: 'W1' }, checkedOutBy: 'engineer',
        children: [
          { nodeId: 'node-part-1', documentId: 'doc-part', instancePath: 'REAL-ASM-001/REAL-PRT-001-1', fileName: 'REAL-PRT-001.SLDPRT', displayName: '真实底板-1', kind: 1, configuration: '默认', quantity: 1, status: 0, revision: { display: 'A' }, checkedOutBy: null, children: [] },
          { nodeId: 'node-part-2', documentId: 'doc-part', instancePath: 'REAL-ASM-001/REAL-PRT-001-2', fileName: 'REAL-PRT-001.SLDPRT', displayName: '真实底板-2', kind: 1, configuration: '默认', quantity: 1, status: 0, revision: { display: 'A' }, checkedOutBy: null, children: [] },
          { nodeId: 'node-part-recovered', documentId: null, instancePath: 'REAL-ASM-001/REAL-PRT-001-3', fileName: 'REAL-PRT-001.SLDPRT', displayName: '真实底板-3', kind: 1, configuration: '默认', quantity: 1, status: 0, revision: null, checkedOutBy: null, children: [] },
          { nodeId: 'node-stale', documentId: 'doc-stale', instancePath: 'REAL-ASM-001/STALE-PRT-001-1', fileName: 'STALE-PRT-001.SLDPRT', displayName: '历史版本子件', kind: 1, configuration: '默认', quantity: 1, status: 0, revision: { display: 'W3' }, checkedOutBy: null, children: [] },
          { nodeId: 'node-stale-duplicate', documentId: 'doc-part', instancePath: 'REAL-ASM-001/REAL-PRT-001-2', fileName: 'REAL-PRT-001.SLDPRT', displayName: '旧快照重复项', kind: 1, configuration: '默认', quantity: 1, status: 0, revision: null, checkedOutBy: null, children: [] },
          { nodeId: 'node-drawing', documentId: 'doc-drawing', instancePath: 'REAL-ASM-001/REAL-ASM-001.SLDDRW', fileName: 'REAL-ASM-001.SLDDRW', displayName: '真实总装工程图', kind: 2, configuration: '工程图', quantity: 1, status: 0, revision: { display: 'W4' }, checkedOutBy: null, children: [] },
          { nodeId: 'node-unregistered', documentId: null, instancePath: 'REAL-ASM-001/3-1', fileName: '3.SLDPRT', displayName: '3-1', kind: 1, configuration: '默认', quantity: 1, status: 0, revision: null, checkedOutBy: null, children: [] },
        ],
      })
    }
    if (url.endsWith('/boms/Standard')) {
      return json([{ sequence: 1, drawingNumber: 'REAL-PRT-001', name: '真实底板', quantity: 2, unit: '件', material: 'Q235B', specification: '10mm', revision: 'A', isComplete: true }])
    }
    if (url.endsWith('/boms/NonStandard')) return json(nonStandardBomResponse)
    if (url.endsWith('/boms/Unclassified')) return json([])
    if (url.endsWith('/boms/Electrical')) {
      return json([{ sequence: 1, drawingNumber: 'REAL-EL-001', name: '真实传感器', quantity: 1, unit: '件', material: null, specification: 'PNP', revision: 'A', isComplete: false }])
    }
    if (url.endsWith('/bom-source-data')) {
      return json([{ id: 'source-real-part', kind: 'Standard', sequence: 1, drawingNumber: 'REAL-PRT-001', name: '真实底板', quantity: 2, unit: '件', material: 'Q235B', specification: '10mm', revision: 'A', isComplete: true, source: 'Auto', sourceDocumentId: 'doc-part' }])
    }
    if (url.endsWith('/boms/empty-declarations')) return json([])
    if (url.endsWith('/release-packages')) {
      return json([{ id: 'package-1', number: 'RP-REAL-001', state: 2, approvalTasks: [{ stage: 1, assignee: '工艺工程师', decisionBy: '工艺工程师', decision: 0, decidedAt: '2026-08-11T01:00:00Z' }, { stage: 2, assignee: '批准人', decisionBy: null, decision: null, decidedAt: null }], publishedAt: null }])
    }
    if (url.endsWith('/api/documents/doc-root/versions')) {
      return json([
        { id: 'version-w2', documentId: 'doc-root', revision: { display: 'W2' }, status: 'Work', fileLength: 20, sha256: 'B'.repeat(64), createdBy: 'engineer', createdAt: '2026-08-11T02:00:00Z', changeNote: '调整材料' },
        { id: 'version-w1', documentId: 'doc-root', revision: { display: 'W1' }, status: 'Work', fileLength: 10, sha256: 'A'.repeat(64), createdBy: 'engineer', createdAt: '2026-08-10T02:00:00Z', changeNote: '首次存档' },
      ])
    }
    if (url.endsWith('/api/documents/doc-part/versions')) {
      return json([
        { id: 'version-a', documentId: 'doc-part', revision: { display: 'A' }, status: 'Work', fileLength: 30, sha256: 'C'.repeat(64), createdBy: 'engineer', createdAt: '2026-08-12T03:04:05Z', changeNote: '首次存档' },
      ])
    }
    if (url.includes('/api/documents/doc-root/versions/compare')) {
      return json({ documentId: 'doc-root', left: { revision: { display: 'W1' }, status: 0, createdBy: 'engineer', createdAt: '2026-08-10T02:00:00Z', changeNote: '首次存档' }, right: { revision: { display: 'W2' }, status: 0, createdBy: 'engineer', createdAt: '2026-08-11T02:00:00Z', changeNote: '调整材料' }, propertyChanges: [{ kind: 2, name: 'Material', previousValue: 'Q235B', currentValue: '304' }], referenceChanges: [{ kind: 5, instancePath: 'ROOT/P-001', previousValue: '1', currentValue: '2' }], bomChanges: [{ kind: 3, drawingNumber: 'P-001', field: '材料', previousValue: 'Q235B', currentValue: '304' }] })
    }
    return json({ title: `Unexpected URL: ${url}` }, 404)
  }))
}

async function login(wrapper: ReturnType<typeof mount>, openProject = true, username = 'engineer') {
  await openLogin(wrapper)
  await wrapper.get('input[name="username"]').setValue(username)
  await wrapper.get('input[name="password"]').setValue('correct-password')
  await wrapper.get('form[aria-label="登录PLM"]').trigger('submit')
  await flushPromises()
  if (openProject) {
    await projectTabByText(wrapper, '图档').trigger('click')
    await flushPromises()
  } else {
    await navigateTo(wrapper, '项目列表')
    await flushPromises()
  }
}

async function openLogin(wrapper: ReturnType<typeof mount>) {
  await flushPromises()
  if (wrapper.find('form[aria-label="登录PLM"]').exists()) return
  await buttonByText(wrapper, '登录').trigger('click')
  await flushPromises()
}

function buttonByText(wrapper: ReturnType<typeof mount>, label: string) {
  const button = wrapper.findAll('button').find((candidate) => candidate.text().includes(label))
  if (!button) throw new Error(`Button not found: ${label}`)
  return button
}

async function navigateTo(wrapper: ReturnType<typeof mount>, label: string) {
  const projectPages = ['项目总览', '项目日历', '项目列表']
  const standardPages = ['标准物料', '标准结构', '常备件']
  const group = projectPages.includes(label) ? '项目总览' : standardPages.includes(label) ? '标准物料' : label
  const button = wrapper.get('.pdm-sidebar').findAll('button').find(button => button.text().trim() === group)!
  await button.trigger('click')
  await flushPromises()
  if (projectPages.includes(label) || standardPages.includes(label)) {
    const tab = wrapper.get('.pdm-navigation-tabs').findAll('[role="tab"]').find(tab => tab.text() === label)!
    await tab.trigger('click')
    await flushPromises()
  }
}

async function runProjectAction(wrapper: ReturnType<typeof mount>, command: string, projectCode = 'PRJ-REAL-001') {
  if (command === 'open') {
    const projectLink = wrapper.findAll('.pdm-project-code-link').find(candidate => candidate.text().trim() === projectCode)
    if (!projectLink) throw new Error(`Project link not found: ${projectCode}`)
    await projectLink.trigger('click')
    await wrapper.vm.$nextTick()
    return
  }

  const menu = wrapper.findAllComponents({ name: 'ElDropdown' }).find(candidate => candidate.attributes('aria-label') === `操作项目${projectCode}`)
  if (!menu) throw new Error(`Project action menu not found: ${projectCode}`)
  menu.vm.$emit('command', command)
  await wrapper.vm.$nextTick()
}

function projectTabByText(wrapper: ReturnType<typeof mount>, label: string) {
  const button = wrapper.findAll('.pdm-project-tabs button').find(candidate => candidate.text().trim() === label)
  if (!button) throw new Error(`Project tab not found: ${label}`)
  return button
}

async function expandAllDocumentTree(wrapper: ReturnType<typeof mount>) {
  const tree = wrapper.get('[aria-label="项目设计树"]')
  for (let level = 0; level < 8; level += 1) {
    const collapsed = tree.findAll('.pdm-tree-row[aria-expanded="false"]')
    if (collapsed.length === 0) return
    for (const row of collapsed) await row.get('.pdm-tree-row__toggle').trigger('click')
    await wrapper.vm.$nextTick()
  }
}

describe('PLM client workspace', () => {
  beforeEach(() => {
    materialRequestsUnauthorized = false
    resumeRequestsUnauthorized = false
    passwordResetRequestsForbidden = false
    drawingReviewsResponse = []
    nonStandardBomResponse = []
    window.sessionStorage.clear()
    window.localStorage.clear()
    Object.defineProperty(window, 'chrome', { configurable: true, value: undefined })
    installApiMock()
  })

  it('有料品查看权限但没有消息权限时，打开管理页也不产生角标或待办提醒', async () => {
    installApiMock([], ['ProjectManager'])
    const originalFetch = globalThis.fetch
    vi.stubGlobal('fetch', vi.fn((input: RequestInfo | URL, init?: RequestInit) => {
      if (String(input).includes('/api/material-sync-tasks')) return Promise.resolve(json([{ id: 'ordinary', materialId: 'ordinary-material', status: 'Failed' }]))
      return originalFetch(input, init)
    }))
    const wrapper = mount(App, { attachTo: document.body, global: { plugins: [ElementPlus] } })
    try {
      await login(wrapper, false, 'admin')
      const badge = () => wrapper.get('.pdm-sidebar').findAll('.pdm-sidebar__nav button').find(button => button.text().includes('料品管理'))!.find('em')
      expect(badge().exists()).toBe(false)
      await buttonByText(wrapper, '料品管理').trigger('click'); await flushPromises()
      const materials = wrapper.findComponent({ name: 'MaterialManagement' })
      expect(materials.props('canReceiveNotices')).toBe(false)
      materials.vm.$emit('noticeCountsChange', { syncTasks: 99, codeApprovals: 99 }); await flushPromises()
      expect(badge().exists()).toBe(false)
      await buttonByText(wrapper, '我的待办').trigger('click'); await flushPromises()
      expect(wrapper.findComponent({ name: 'MyTasks' }).props('materialCodeTasks')).toEqual([])
    } finally { wrapper.unmount() }
  })

  it.each([false, true])('人工待办排除自动处理的BOM表头申请并保留普通审批（普通审批：%s）', async ordinary => {
    installApiMock([], ['Administrator', 'Approver'])
    const originalFetch = globalThis.fetch
    vi.stubGlobal('fetch', vi.fn((input: RequestInfo | URL, init?: RequestInit) => {
      if (String(input).includes('/api/material-code/applications')) return Promise.resolve(json([
        { id: 'automatic-header', projectId, projectCode: 'P700016-4', applicationName: '项目主BOM', applicationType: 'BomHeader', bomHeaderKind: 'Master', status: 'Pending', canDecide: false, canEmergencyDecide: false, requestedAt: '2026-10-22T02:26:25Z', requestedBy: 'engineer', rowVersion: 1 },
        ...(ordinary ? [{ id: 'ordinary-approval', projectId, projectCode: 'P700016-4', applicationType: 'StandardBomItem', status: 'Pending', canDecide: true, requestedAt: '2026-10-26T00:00:00Z', requestedBy: 'engineer', rowVersion: 1 }] : []),
      ]))
      return originalFetch(input, init)
    }))
    const wrapper = mount(App, { attachTo: document.body, global: { plugins: [ElementPlus] } })
    try {
      await login(wrapper, false, 'admin')
      await buttonByText(wrapper, '我的待办').trigger('click')
      await flushPromises()
      const tasks = wrapper.findComponent({ name: 'MyTasks' })
      expect(tasks.props('materialCodeTasks').map((application: { id: string }) => application.id)).toEqual(ordinary ? ['ordinary-approval'] : [])
      expect(tasks.text()).toContain(`料号审批（${ordinary ? 1 : 0}）`)
      expect(tasks.text()).not.toContain('BOM料号审批')
      await tasks.findAll('button').find(button => button.text() === '刷新')!.trigger('click')
      await flushPromises()
      expect(tasks.props('materialCodeTasks').map((application: { id: string }) => application.id)).toEqual(ordinary ? ['ordinary-approval'] : [])
    } finally { wrapper.unmount() }
  })

  it.each([false, true])('料品角标排除BOM料号任务并在刷新后保持与页面一致（普通任务：%s）', async ordinary => {
    installApiMock([], ['Administrator', 'Approver'])
    const originalFetch = globalThis.fetch
    const calls: string[] = []
    vi.stubGlobal('fetch', vi.fn((input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input)
      if (url.includes('/api/material-sync-tasks')) {
        calls.push(url)
        return Promise.resolve(json([
          { id: 'header', materialId: 'header-material', bomHeaderKind: 'Standard', status: 'Failed' },
          ...(ordinary ? [{ id: 'ordinary', materialId: 'ordinary-material', status: 'Failed' }, { id: 'older', materialId: 'ordinary-material', status: 'Failed' }] : []),
        ]))
      }
      if (url.includes('/api/material-code/applications')) return Promise.resolve(json([{ id: 'header-approval', applicationType: 'BomHeader', status: 'Pending', canDecide: true }]))
      return originalFetch(input, init)
    }))
    const wrapper = mount(App, { attachTo: document.body, global: { plugins: [ElementPlus] } })
    try {
      await login(wrapper, false, 'admin')
      const badge = () => wrapper.get('.pdm-sidebar').findAll('.pdm-sidebar__nav button').find(button => button.text().includes('料品管理'))!.find('em')
      expect(badge().exists()).toBe(ordinary)
      if (ordinary) expect(badge().text()).toBe('1')
      await buttonByText(wrapper, '料品管理').trigger('click')
      await flushPromises()
      expect(badge().exists()).toBe(ordinary)
      if (ordinary) expect(badge().text()).toBe('1')
      await buttonByText(wrapper, '我的待办').trigger('click')
      await flushPromises()
      expect(badge().exists()).toBe(ordinary)
      if (ordinary) expect(badge().text()).toBe('1')
      expect(calls.length).toBeGreaterThan(1)
      expect(calls.every(url => url.includes('ordinaryOnly=true'))).toBe(true)
    } finally { wrapper.unmount() }
  })

  it.each([false, true].flatMap(desktop => [false, true].map(delegated => ({ desktop, delegated }))))('非管理员不显示系统设置且不能恢复或进入页面（$desktop，授权设置权限：$delegated）', async ({ desktop, delegated }) => {
    installApiMock([], ['Engineer'], delegated ? ['project.view', 'settings.storage.manage', 'system.role.view', 'audit.view'] : undefined)
    Object.defineProperty(window, 'chrome', { configurable: true, value: desktop ? { webview: { postMessage: vi.fn(), addEventListener: vi.fn() } } : undefined })
    window.localStorage.setItem('upton-pdm-active-navigation', 'admin')
    window.localStorage.setItem('upton-pdm-active-navigation:owner', JSON.stringify(['engineer', '']))
    const wrapper = mount(App, { attachTo: document.body, global: { plugins: [ElementPlus] } })
    try {
      await login(wrapper, false)
      expect(wrapper.findAll('.pdm-sidebar__footer button').some(button => button.text().includes('系统设置'))).toBe(false)
      expect(wrapper.find('.pdm-admin-workspace').exists()).toBe(false)
      wrapper.getComponent({ name: 'SideNav' }).vm.$emit('navigate', 'admin', '系统设置')
      await flushPromises()
      expect(wrapper.find('.pdm-admin-workspace').exists()).toBe(false)
      expect(wrapper.find('[aria-label="项目列表"]').exists()).toBe(true)
    } finally { wrapper.unmount() }
  })

  it.each(['Administrator', 'platform_admin', 'developer', 'PlatformAdministrator'])('管理员身份保留系统设置入口（%s）', async role => {
    installApiMock([], [role], ['project.view', 'settings.storage.manage', 'system.role.view'])
    const wrapper = mount(App, { attachTo: document.body, global: { plugins: [ElementPlus] } })
    try {
      await login(wrapper, false)
      await buttonByText(wrapper, '系统设置').trigger('click')
      await flushPromises()
      expect(wrapper.find('.pdm-admin-workspace').exists()).toBe(true)
    } finally { wrapper.unmount() }
  })

  it('管理员退出后切换非管理员会卸载系统设置并清除入口', async () => {
    Object.defineProperty(window, 'chrome', { configurable: true, value: { webview: { postMessage: vi.fn(), addEventListener: vi.fn() } } })
    const wrapper = mount(App, { attachTo: document.body, global: { plugins: [ElementPlus] } })
    try {
      await login(wrapper, false, 'admin')
      await buttonByText(wrapper, '系统设置').trigger('click'); await flushPromises()
      expect(wrapper.find('.pdm-admin-workspace').exists()).toBe(true)
      await buttonByText(wrapper, '退出').trigger('click'); await flushPromises()
      await login(wrapper, false)
      expect(wrapper.find('.pdm-admin-workspace').exists()).toBe(false)
      expect(wrapper.findAll('.pdm-sidebar__footer button').some(button => button.text().includes('系统设置'))).toBe(false)
    } finally { wrapper.unmount() }
  })

  it('在系统管理上方显示网页发布版本', async () => {
    const wrapper = mount(App, { attachTo: document.body, global: { plugins: [ElementPlus] } })
    try {
      await login(wrapper, false)
      await flushPromises()
      expect(wrapper.get('.pdm-sidebar__version').text()).toBe('V2026.09.12.1234')
      expect(wrapper.get('.pdm-sidebar__version').attributes('title')).toBe('版本 2026.09.12.1234-version-display，点击查看详情')
    } finally { wrapper.unmount() }
  })

  it('空项目保留设计树、预览区和受权限控制的存档入口，存档后可刷新显示图档', async () => {
    const originalFetch = fetch
    let empty = true
    let enabled = true
    let canArchive = true
    vi.stubGlobal('fetch', vi.fn((input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input)
      if (url.includes('/api/browser-archives/capabilities')) return Promise.resolve(json({ enabled, canArchive, canConfigure: false }))
      if (empty && /\/(documents|folder-documents|document-relations)$/.test(url)) return Promise.resolve(json([]))
      if (empty && url.endsWith('/reference-tree')) return Promise.resolve(json(null))
      return originalFetch(input, init)
    }))
    const wrapper = mount(App, { attachTo: document.body, global: { plugins: [ElementPlus] } })
    try {
      await login(wrapper)
      expect(wrapper.get('.pdm-tree-empty').text()).toContain('当前项目暂无图档')
      expect(wrapper.find('[aria-label="工作区命令栏"]').exists()).toBe(true)
      expect(wrapper.get('[aria-label="图档预览"]').text()).toContain('项目尚未关联CAD图纸')
      expect(wrapper.get('.pdm-tree-legend .browser-cad-entry').text()).toBe('存档')
      await wrapper.get('.browser-cad-entry').trigger('click')
      await flushPromises()
      expect(document.querySelector('[aria-label="图档存档"]')?.textContent).toContain('选择文件')
      canArchive = false
      window.dispatchEvent(new Event('pdm-browser-archive-settings'))
      await flushPromises()
      expect(wrapper.get('.browser-cad-entry').attributes('disabled')).toBeDefined()
      enabled = false
      window.dispatchEvent(new Event('pdm-browser-archive-settings'))
      await flushPromises()
      expect(wrapper.find('.browser-cad-entry').exists()).toBe(false)
      empty = false
      await wrapper.get('[aria-label="刷新设计树"]').trigger('click')
      await flushPromises()
      expect(wrapper.get('[aria-label="项目设计树"]').text()).toContain('REAL-ASM-001')
      expect(wrapper.get('[aria-label="图档预览"]').text()).not.toContain('项目尚未关联CAD图纸')
    } finally { wrapper.unmount() }
  })

  it.each(['Administrator', 'platform_admin', 'developer', 'PlanningManager', 'Engineer'])('项目计划模板已从系统管理移除（角色 %s）', async roleCode => {
    installApiMock([], [String(roleCode)])
    const wrapper = mount(App, { attachTo: document.body, global: { plugins: [ElementPlus] } })
    try {
      await login(wrapper, false, 'admin')
      await buttonByText(wrapper, '系统设置').trigger('click')
      await flushPromises()
      const tabs = wrapper.get('.pdm-admin-tabs').text()
      expect(tabs).not.toContain('项目计划模板')
    } finally { wrapper.unmount() }
  })

  it('密码重置待办被拒绝时仍可加载首页', async () => {
    installApiMock([], ['Administrator'])
    passwordResetRequestsForbidden = true
    const wrapper = mount(App, { attachTo: document.body, global: { plugins: [ElementPlus] } })
    try {
      await login(wrapper, false)
      expect(wrapper.find('.pdm-workspace-state.is-error').exists()).toBe(false)
      expect(wrapper.find('[aria-label="项目列表"]').exists()).toBe(true)
      expect(vi.mocked(fetch).mock.calls.some(([input]) => String(input).endsWith('/api/password-reset-requests'))).toBe(true)
    } finally { wrapper.unmount() }
  })

  it('项目计划维护由独立权限控制，系统管理员保留完整权限', async () => {
    const stub = { props: ['canEdit'], template: '<div data-test="project-plan-permission">{{ String(canEdit) }}</div>' }
    const verify = async (username: string, expected: string, roles: string[] = []) => {
      window.sessionStorage.clear()
      window.localStorage.clear()
      installApiMock([], roles)
      const wrapper = mount(App, { attachTo: document.body, global: { plugins: [ElementPlus], stubs: { ProjectPlanManager: stub } } })
      try {
        await login(wrapper, false, username)
        await runProjectAction(wrapper, 'open')
        await flushPromises()
        await projectTabByText(wrapper, '计划').trigger('click')
        await flushPromises()
        expect(wrapper.get('[data-test="project-plan-permission"]').text()).toBe(expected)
      } finally { wrapper.unmount() }
    }
    await verify('admin', 'true')
    await verify('engineer', 'true')
    await verify('developer', 'true', ['developer'])
  })

  it.each(['a', 'c', 'p', 'o', 'g'])('applies saved theme %s and synchronizes it to the Windows client', async savedTheme => {
    const postMessage = vi.fn()
    window.localStorage.setItem('pdm_theme', savedTheme)
    Object.defineProperty(window, 'chrome', {
      configurable: true,
      value: { webview: { postMessage, addEventListener: vi.fn() } },
    })

    const wrapper = mount(App, { attachTo: document.body, global: { plugins: [ElementPlus] } })
    expect(wrapper.get('.pdm-app-shell').classes()).toContain(`theme-${savedTheme}`)
    expect(document.documentElement.dataset.pdmTheme).toBe(savedTheme)
    expect(postMessage).toHaveBeenCalledWith({ type: 'theme-change', payload: { theme: savedTheme } })

    wrapper.unmount()
    expect(document.documentElement.dataset.pdmTheme).toBeUndefined()
  })

  it('clears operation status when changing project tabs or main pages', async () => {
    const wrapper = mount(App, { attachTo: document.body, global: { plugins: [ElementPlus] } })
    await login(wrapper)
    ElMessage.success('图档旧状态')
    await projectTabByText(wrapper, '概览').trigger('click')
    await flushPromises()
    expect(globalStatus.value).toBeUndefined()
    ElMessage.warning('概览旧状态')
    await navigateTo(wrapper, '项目列表')
    await flushPromises()
    expect(globalStatus.value).toBeUndefined()
    expect(wrapper.find('.pdm-global-status.is-idle').exists()).toBe(true)
    wrapper.unmount()
  })

  it('restores and toggles the collapsed main navigation', async () => {
    window.localStorage.setItem('upton-pdm-sidebar-collapsed', 'true')
    const wrapper = mount(App, { attachTo: document.body, global: { plugins: [ElementPlus] } })
    await login(wrapper, false)

    expect(wrapper.get('.pdm-app-body').classes()).toContain('is-sidebar-collapsed')
    expect(wrapper.get('.pdm-titlebar__sidebar-toggle').attributes('aria-label')).toBe('展开主导航')

    await wrapper.get('.pdm-titlebar__sidebar-toggle').trigger('click')
    expect(wrapper.get('.pdm-app-body').classes()).not.toContain('is-sidebar-collapsed')
    expect(window.localStorage.getItem('upton-pdm-sidebar-collapsed')).toBe('false')

    wrapper.unmount()
  })

  it.each([false, true])('已有审核单的图档页面默认折叠审核栏，切换后仍默认折叠（客户端：%s）', async desktop => {
    const postMessage = vi.fn()
    if (desktop) Object.defineProperty(window, 'chrome', { configurable: true, value: { webview: { postMessage, addEventListener: vi.fn() } } })
    drawingReviewsResponse = [{ id: 'approved-review', projectId, number: 'DR-APPROVED', state: 'Approved', createdBy: 'designer', createdAt: '2026-10-23T00:00:00Z', markups: [],
      items: [{ id: 'approved-item', drawingDocumentId: 'doc-drawing', drawingNumber: 'REAL-ASM-001', drawingState: 'Approved', modelState: 'NotRequired' }] }]
    const wrapper = mount(App, { attachTo: document.body, global: { plugins: [ElementPlus] } })
    try {
      await login(wrapper)
      if (desktop) {
        expect(postMessage).toHaveBeenCalledWith(expect.objectContaining({ type: 'review-overlay-state', payload: expect.objectContaining({ visible: true, collapsed: true }) }))
      } else {
        expect(wrapper.get('[aria-label="图纸审核面板"]').classes()).toContain('is-collapsed')
        await wrapper.get('button[aria-label="展开图纸审核栏"]').trigger('click')
        expect(wrapper.get('[aria-label="图纸审核面板"]').classes()).not.toContain('is-collapsed')
      }
      await projectTabByText(wrapper, '信息').trigger('click')
      await flushPromises()
      await projectTabByText(wrapper, '图档').trigger('click')
      await flushPromises()
      if (desktop) {
        expect(postMessage.mock.calls.filter(([message]) => message.type === 'review-overlay-state').at(-1)?.[0].payload.collapsed).toBe(true)
      } else expect(wrapper.get('[aria-label="图纸审核面板"]').classes()).toContain('is-collapsed')
    } finally { wrapper.unmount() }
  })

  it('keeps new workflow actions visible to the fixed administrator before a stale session permission list is renewed', async () => {
    const wrapper = mount(App, { attachTo: document.body, global: { plugins: [ElementPlus] } })
    await openLogin(wrapper)
    await wrapper.get('input[name="username"]').setValue('admin')
    await wrapper.get('input[name="password"]').setValue('correct-password')
    await wrapper.get('form[aria-label="登录PLM"]').trigger('submit')
    await flushPromises()

    await projectTabByText(wrapper, '图档').trigger('click')
    await flushPromises()

    expect(wrapper.find('[aria-label="图纸审核面板"]').exists()).toBe(true)
    await wrapper.get('button[aria-label="展开图纸审核栏"]').trigger('click')
    expect(wrapper.get('[aria-label="图纸审核面板"]').classes()).not.toContain('is-collapsed')
    await flushPromises()
    expect(buttonByText(wrapper, '选择范围并发起审核').exists()).toBe(true)
    wrapper.unmount()
  })

  it('restores valid remembered login without waiting for health checks or session renewal', async () => {
    window.localStorage.setItem('upton-pdm-session', JSON.stringify({
      accessToken: 'test-token', expiresAt: '2099-01-01T00:00:00Z', resumeToken: 'test-resume-token',
      username: 'engineer', displayName: '真实工程师', role: 'Engineer', permissions: ['project.view'],
    }))
    const originalFetch = vi.mocked(fetch).getMockImplementation()!
    vi.mocked(fetch).mockImplementation((input, init) => {
      const url = String(input)
      if (url.endsWith('/health') || url.endsWith('/api/auth/resume')) return new Promise<Response>(() => {})
      return originalFetch(input, init)
    })
    const wrapper = mount(App, { attachTo: document.body, global: { plugins: [ElementPlus] } })
    await flushPromises()
    expect(wrapper.find('[aria-label="正在恢复登录状态"]').exists()).toBe(false)
    expect(wrapper.find('form[aria-label="登录PLM"]').exists()).toBe(false)
    expect(wrapper.find('.pdm-sidebar__nav').exists()).toBe(true)
    wrapper.unmount()
  })
  it('renews a still-valid restored session on startup so newly granted permissions are visible', async () => {
    window.localStorage.setItem('upton-pdm-session', JSON.stringify({
      accessToken: 'stale-token',
      expiresAt: '2099-01-01T00:00:00Z',
      resumeToken: 'test-resume-token',
      username: 'engineer',
      displayName: '真实工程师',
      role: 'Engineer',
      permissions: ['project.view'],
    }))

    const wrapper = mount(App, { attachTo: document.body, global: { plugins: [ElementPlus] } })
    expect(wrapper.find('form[aria-label="登录PLM"]').exists()).toBe(false)
    expect(wrapper.find('[aria-label="正在恢复登录状态"]').exists()).toBe(true)
    await flushPromises()

    expect(wrapper.find('form[aria-label="登录PLM"]').exists()).toBe(false)
    expect(window.localStorage.getItem('upton-pdm-session')).toContain('renewed-token')
    expect(wrapper.findAll('.pdm-sidebar__nav .pdm-nav-item').some(item => item.text().includes('标准物料'))).toBe(true)
    expect(wrapper.find('[aria-label="正在恢复登录状态"]').exists()).toBe(false)
    expect(vi.mocked(fetch).mock.calls.filter(([input]) => String(input).endsWith('/api/auth/resume'))).toHaveLength(1)
    wrapper.unmount()
  })

  it('opens the independent standard structure workspace with drawing and BOM tabs', async () => {
    const wrapper = mount(App, { attachTo: document.body, global: { plugins: [ElementPlus] } })
    await login(wrapper, false)

    const navigationLabels = wrapper.findAll('.pdm-sidebar__nav .pdm-nav-item').map(item => item.text().trim())
    expect(navigationLabels).toContain('标准物料')
    expect(navigationLabels).not.toContain('标准结构')
    expect(navigationLabels).toContain('料品管理')

    await navigateTo(wrapper, '标准结构')
    await flushPromises()

    expect(wrapper.get('.pdm-sidebar__nav .pdm-nav-item.is-active').text()).toContain('标准物料')
    const standardStructurePage = wrapper.get('[aria-label="标准结构"]')
    expect(standardStructurePage.text()).toContain('分类筛选')
    expect(standardStructurePage.get('[aria-label="标准结构功能"]').findAll('button').map(button => button.text()).filter(Boolean)).toEqual(['图档', 'BOM'])
    expect(standardStructurePage.text()).not.toContain('审核')
    expect(window.localStorage.getItem('upton-pdm-active-navigation')).toBe('standard-structure')
    wrapper.unmount()
  })

  it('loads review drawings without requesting submit-only reviewer options for a user without submit permission', async () => {
    drawingReviewsResponse = [{ id: 'review-1', projectId, number: 'DR-TEST', state: 'WritingProperties', createdBy: 'designer', createdAt: '2026-09-30T00:00:00Z', markups: [], items: [{ id: 'item-1', drawingDocumentId: 'doc-drawing', drawingNumber: 'REAL-ASM-001', drawingState: 'Approved', modelState: 'NotRequired' }] }]
    const originalFetch = vi.mocked(fetch).getMockImplementation()!
    vi.mocked(fetch).mockImplementation((input, init) => String(input).endsWith('/drawing-reviewers')
      ? Promise.resolve(json({ message: '当前角色未配置执行此操作的权限。' }, 403))
      : originalFetch(input, init))
    const wrapper = mount(App, { attachTo: document.body, global: { plugins: [ElementPlus] } })
    await login(wrapper)
    await projectTabByText(wrapper, '图档').trigger('click')
    await flushPromises()
    expect(vi.mocked(fetch).mock.calls.some(([input]) => String(input).endsWith('/drawing-reviewers'))).toBe(false)
    await wrapper.get('button[aria-label="展开图纸审核栏"]').trigger('click')
    expect(wrapper.get('[aria-label="图纸审核状态表"]').text()).toContain('REAL-ASM-001')
    expect(wrapper.text()).not.toContain('当前角色未配置执行此操作的权限。')
    wrapper.unmount()
  })

  it('does not reload the preview when the drawing review panel is opened', async () => {
    const postMessage = vi.fn()
    Object.defineProperty(window, 'chrome', {
      configurable: true,
      value: { webview: { postMessage, addEventListener: vi.fn() } },
    })
    drawingReviewsResponse = [{
      id: 'review-1', projectId, number: 'DR-20260823-0001', state: 'InReview', createdBy: 'submitter', createdAt: '2026-08-23T00:00:00Z', markups: [],
      items: [{
        id: 'review-item-1', packageId: 'review-1', bomItemId: 'bom-1', drawingNumber: 'REAL-ASM-001', name: '真实总装配',
        modelDocumentId: 'doc-root', modelVersionId: 'version-w2', modelRevision: 'W2', modelSha256: 'A'.repeat(64), modelCreatedBy: 'designer',
        drawingDocumentId: 'doc-drawing', drawingVersionId: 'drawing-version-w2', drawingRevision: 'W2', drawingSha256: 'B'.repeat(64), drawingCreatedBy: 'drawing-designer',
        modelState: 'Pending', drawingState: 'Pending', effectiveModelVersionId: 'version-w2', effectiveDrawingVersionId: 'drawing-version-w2',
      }],
    }]
    const wrapper = mount(App, { attachTo: document.body, global: { plugins: [ElementPlus] } })
    await login(wrapper)
    const previewCallsBeforeOpen = postMessage.mock.calls.filter(([message]) => message.type === 'preview-document').length

    await flushPromises()

    expect(wrapper.find('[aria-label="图纸审核面板"]').exists()).toBe(false)
    expect(postMessage).toHaveBeenCalledWith(expect.objectContaining({
      type: 'review-overlay-state',
      payload: expect.objectContaining({ visible: true, selectedDocumentId: 'doc-root' }),
    }))
    expect(postMessage.mock.calls.filter(([message]) => message.type === 'preview-document')).toHaveLength(previewCallsBeforeOpen)
    wrapper.unmount()
  })

  it('separates read-only review opening from the property writeback workflow', async () => {
    const postMessage = vi.fn()
    Object.defineProperty(window, 'chrome', {
      configurable: true,
      value: { webview: { postMessage, addEventListener: vi.fn() } },
    })
    drawingReviewsResponse = [{
      id: 'review-writeback', projectId, number: 'DR-20260824-0001', state: 'WritingProperties', createdBy: 'submitter', createdAt: '2026-08-24T00:00:00Z', markups: [],
      items: [{
        id: 'review-item-writeback', packageId: 'review-writeback', bomItemId: 'bom-1', drawingNumber: 'REAL-ASM-001', name: '真实总装配',
        modelDocumentId: 'doc-root', modelVersionId: 'version-w2', modelRevision: 'W2', modelSha256: 'A'.repeat(64), modelCreatedBy: 'designer',
        drawingDocumentId: 'doc-drawing', drawingVersionId: 'drawing-version-w2', drawingRevision: 'W2', drawingSha256: 'B'.repeat(64), drawingCreatedBy: 'drawing-designer',
        modelState: 'NotRequired', drawingState: 'Approved', effectiveModelVersionId: 'version-w2', effectiveDrawingVersionId: 'drawing-version-w2',
      }],
    }]
    const wrapper = mount(App, { attachTo: document.body, global: { plugins: [ElementPlus] } })
    await login(wrapper)
    window.dispatchEvent(new CustomEvent('pdm-solidworks-capability', { detail: { available: true } }))
    await flushPromises()
    await wrapper.findAll('.pdm-tree-row').find(row => row.text().includes('真实总装工程图'))!.trigger('click')
    await flushPromises()

    await buttonByText(wrapper, '打开审核版').trigger('click')
    expect(postMessage).toHaveBeenCalledWith({
      type: 'open-document',
      payload: expect.objectContaining({
        projectId,
        documentId: 'doc-drawing',
        mode: 'SpecificReadOnly',
        versionId: 'drawing-version-w2',
      }),
    })

    window.dispatchEvent(new CustomEvent('pdm-solidworks-status', { detail: { state: 'ready', message: '请求已发送' } }))
    await flushPromises()
    await buttonByText(wrapper, '回写审核标记').trigger('click')
    expect(postMessage).toHaveBeenCalledWith({
      type: 'open-document',
      payload: expect.objectContaining({
        projectId,
        documentId: 'doc-drawing',
        mode: 'PropertyWriteback',
        versionId: undefined,
      }),
    })

    wrapper.unmount()
  })

  it('places password help on the left and remembered credentials on the right', async () => {
    const wrapper = mount(App, { attachTo: document.body, global: { plugins: [ElementPlus] } })
    await openLogin(wrapper)
    const options = wrapper.get('.pdm-login-options')

    expect(options.element.children[0]?.textContent).toBe('忘记密码？')
    expect(options.element.children[1]?.textContent).toContain('保存账号和密码')

    await options.get('button').trigger('click')
    await flushPromises()
    expect(document.body.textContent).toContain('申请重置密码')
    expect(document.body.textContent).toContain('发送申请')
    wrapper.unmount()
  })

  it('restores independent pages per account and gives this window priority over another window', async () => {
    let wrapper = mount(App, { attachTo: document.body, global: { plugins: [ElementPlus] } })
    await login(wrapper, false)
    await navigateTo(wrapper, '我的待办'); await flushPromises()
    wrapper.unmount()
    window.localStorage.setItem('upton-pdm-active-navigation:' + JSON.stringify(['engineer','']), 'projects')
    wrapper = mount(App, { attachTo: document.body, global: { plugins: [ElementPlus] } })
    await flushPromises()
    expect(wrapper.get('[data-navigation-page="tasks"]').isVisible()).toBe(true)
    expect(wrapper.find('.pdm-project-workspace').exists()).toBe(false)
    await buttonByText(wrapper, '退出').trigger('click'); await flushPromises()
    await wrapper.get('input[name="username"]').setValue('admin')
    await wrapper.get('input[name="password"]').setValue('correct-password')
    await wrapper.get('form[aria-label="登录PLM"]').trigger('submit'); await flushPromises()
    expect(wrapper.get('.pdm-sidebar__nav .pdm-nav-item.is-active').text()).toContain('项目中心')
    wrapper.unmount()
  })
  it('uses the project center as the first home and restores the current page and project tab after refresh', async () => {
    const wrapper = mount(App, { attachTo: document.body, global: { plugins: [ElementPlus] } })
    await openLogin(wrapper)
    await wrapper.get('input[name="username"]').setValue('engineer')
    await wrapper.get('input[name="password"]').setValue('correct-password')
    await wrapper.get('form[aria-label="登录PLM"]').trigger('submit')
    await flushPromises()

    expect(wrapper.find('.pdm-project-workspace').exists()).toBe(true)
    expect(wrapper.findAll('.pdm-sidebar__nav .pdm-nav-item').map(item => item.text().trim())).toEqual(['项目总览', '项目中心', '工时管理', '我的待办', '制造中心', '标准物料', '标准程序', '料品管理'])
    expect(wrapper.get('.pdm-sidebar__nav .pdm-nav-item.is-active').text()).toContain('项目中心')
    await projectTabByText(wrapper, 'BOM').trigger('click')
    await flushPromises()

    wrapper.unmount()
    window.sessionStorage.clear()
    const restoredProjectPage = mount(App, { attachTo: document.body, global: { plugins: [ElementPlus] } })
    await flushPromises()
    expect(restoredProjectPage.find('.pdm-project-workspace').exists()).toBe(true)
    expect(restoredProjectPage.get('.pdm-project-tabs button.is-active').text()).toBe('BOM')

    await navigateTo(restoredProjectPage, '项目列表')
    expect(restoredProjectPage.get('[aria-label="项目中心"]').text()).toContain('PRJ-REAL-001')
    expect(restoredProjectPage.get('.pdm-sidebar__nav .pdm-nav-item.is-active').text()).toContain('项目总览')
    expect(window.localStorage.getItem('upton-pdm-active-navigation')).toBe('projects')

    restoredProjectPage.unmount()
    const restoredHome = mount(App, { attachTo: document.body, global: { plugins: [ElementPlus] } })
    await flushPromises()
    expect(restoredHome.text()).toContain('PRJ-REAL-001')
    expect(restoredHome.get('.pdm-sidebar__nav .pdm-nav-item.is-active').text()).toContain('项目总览')
    expect(restoredHome.find('.pdm-project-workspace').exists()).toBe(false)
    expect(restoredHome.get('[data-navigation-page="projects"]').isVisible()).toBe(true)
    expect(JSON.parse(window.localStorage.getItem('upton-pdm-project-center:' + JSON.stringify(['engineer', ''])) ?? '{}')).toMatchObject({ projectId, tab: 'bom', username: 'engineer' })
    expect(window.localStorage.getItem('upton-pdm-active-navigation')).toBe('projects')
    await navigateTo(restoredHome, '项目中心')
    await flushPromises()
    expect(restoredHome.get('.pdm-project-tabs button.is-active').text()).toBe('BOM')

    restoredHome.unmount()
    const restored = mount(App, { attachTo: document.body, global: { plugins: [ElementPlus] } })
    await flushPromises()
    expect(restored.find('.pdm-project-workspace').exists()).toBe(true)
    expect(restored.get('.pdm-project-tabs button.is-active').text()).toBe('BOM')
    restored.unmount()
  })

  it('keeps the project available when only BOM versions fail to load', async () => {
    const fetchWithProjects = globalThis.fetch
    vi.stubGlobal('fetch', vi.fn((input: RequestInfo | URL, init?: RequestInit) =>
      String(input).replace(/\?summary=true$/, '').endsWith(`/api/projects/${projectId}/bom-versions`)
        ? Promise.resolve(json({ title: 'PLM服务发生内部错误' }, 500))
        : fetchWithProjects(input, init)))
    const wrapper = mount(App, { attachTo: document.body, global: { plugins: [ElementPlus] } })
    await login(wrapper)

    expect(wrapper.find('.pdm-project-workspace').exists()).toBe(true)
    expect(wrapper.find('.pdm-workspace-state.is-error').exists()).toBe(false)
    await projectTabByText(wrapper, 'BOM').trigger('click')
    expect(wrapper.get('.pdm-module-error').text()).toContain('BOM版本：PLM服务发生内部错误')
    expect(wrapper.get('.pdm-module-error button').text()).toBe('重试读取')
    wrapper.unmount()
  })

  it('keeps project navigation available when an auxiliary startup feed fails', async () => {
    const fetchWithProjects = globalThis.fetch
    vi.stubGlobal('fetch', vi.fn((input: RequestInfo | URL, init?: RequestInit) =>
      String(input).endsWith('/api/customers')
        ? Promise.resolve(json({ title: 'PLM服务发生内部错误' }, 500))
        : fetchWithProjects(input, init)))
    const wrapper = mount(App, { attachTo: document.body, global: { plugins: [ElementPlus] } })
    await login(wrapper)

    expect(wrapper.find('.pdm-project-workspace').exists()).toBe(true)
    expect(wrapper.find('.pdm-workspace-state.is-error').exists()).toBe(false)
    expect(wrapper.get('.pdm-module-error').text()).toContain('客户列表')
    wrapper.unmount()
  })

  it('keeps the selected child project when an older focus refresh finishes later', async () => {
    const child = {
      id: 'project-child',
      code: 'PRJ-REAL-001-1',
      name: '泵组单元',
      parentProjectId: projectId,
      childSequence: 1,
      owner: 'engineer',
      responsibleUsers: ['engineer'],
      vaultLocation: 'D:\\PDM\\PRJ-REAL-001-1',
      releaseLocation: 'D:\\Release\\PRJ-REAL-001-1',
      isActive: true,
      quantity: 1,
      serialNumbers: [],
    }
    installApiMock([child])
    const wrapper = mount(App, { attachTo: document.body, global: { plugins: [ElementPlus] } })
    await login(wrapper)

    const originalFetch = vi.mocked(fetch)
    let releaseStaleRequest!: () => void
    const staleRequestGate = new Promise<void>(resolve => { releaseStaleRequest = resolve })
    let blockNextMainTreeRequest = true
    vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      if (blockNextMainTreeRequest && String(input).endsWith(`/api/projects/${projectId}/reference-tree`)) {
        blockNextMainTreeRequest = false
        await staleRequestGate
      }
      return originalFetch(input, init)
    }))

    window.dispatchEvent(new Event('focus'))
    await new Promise(resolve => window.setTimeout(resolve, 0))
    await wrapper.get('[aria-label="选择项目号 PRJ-REAL-001-1"]').trigger('click')
    await flushPromises()
    expect(wrapper.get('.pdm-project-sidebar__summary').text()).toContain('PRJ-REAL-001-1 · 泵组单元')

    releaseStaleRequest()
    await flushPromises()
    expect(wrapper.get('.pdm-project-sidebar__summary').text()).toContain('PRJ-REAL-001-1 · 泵组单元')
    wrapper.unmount()
  })

  it('switches with fresh project data without reloading unrelated global settings', async () => {
    const child = {
      id: 'project-child', code: 'PRJ-REAL-001-1', name: '泵组单元', parentProjectId: projectId,
      childSequence: 1, owner: 'engineer', responsibleUsers: ['engineer'], canReadContent: true,
      vaultLocation: 'D:\\PDM\\child', releaseLocation: 'D:\\Release\\child', isActive: true, quantity: 1, serialNumbers: [],
    }
    installApiMock([child])
    const wrapper = mount(App, { attachTo: document.body, global: { plugins: [ElementPlus] } })
    await login(wrapper)
    await wrapper.get('[aria-label="选择项目号 PRJ-REAL-001"]').trigger('click')
    await flushPromises()
    vi.mocked(fetch).mockClear()
    await wrapper.get('[aria-label="选择项目号 PRJ-REAL-001-1"]').trigger('click')
    await flushPromises()
    expect(wrapper.get('.pdm-project-sidebar__summary').text()).toContain('泵组单元')
    let urls = vi.mocked(fetch).mock.calls.map(([input]) => String(input))
    expect(urls.some(url => url.endsWith('/api/projects/project-child'))).toBe(true)
    expect(urls.some(url => url.endsWith('/api/projects/project-child/bom-source-data'))).toBe(false)
    for (const path of ['/api/projects', '/api/customers', '/api/organization-directory', '/api/system-settings', '/api/role-permissions'])
      expect(urls.some(url => url.endsWith(path))).toBe(false)

    await wrapper.get('[aria-label="选择项目号 PRJ-REAL-001"]').trigger('click')
    await flushPromises()
    child.name = '更新后的子项目'
    child.canReadContent = false
    vi.mocked(fetch).mockClear()
    await wrapper.get('[aria-label="选择项目号 PRJ-REAL-001-1"]').trigger('click')
    await flushPromises()
    expect(wrapper.get('.pdm-project-sidebar__summary').text()).toContain('更新后的子项目')
    urls = vi.mocked(fetch).mock.calls.map(([input]) => String(input))
    expect(urls.some(url => url.endsWith('/api/projects/project-child'))).toBe(true)
    expect(urls.some(url => url.endsWith('/api/projects/project-child/bom-source-data'))).toBe(false)
    expect(urls.some(url => url.endsWith('/api/projects/project-child/documents'))).toBe(false)
    wrapper.unmount()
  })

  it('keeps the child project when BOM and procurement are clicked during the first switch', async () => {
    const child = {
      id: 'project-child',
      code: 'PRJ-REAL-001-1',
      name: '泵组单元',
      parentProjectId: projectId,
      childSequence: 1,
      owner: 'engineer',
      responsibleUsers: ['engineer'],
      vaultLocation: 'D:\\PDM\\PRJ-REAL-001-1',
      releaseLocation: 'D:\\Release\\PRJ-REAL-001-1',
      isActive: true,
      quantity: 1,
      serialNumbers: [],
    }
    installApiMock([child])
    const wrapper = mount(App, { attachTo: document.body, global: { plugins: [ElementPlus] } })
    await login(wrapper)

    const originalFetch = vi.mocked(fetch)
    let releaseChildRequest!: () => void
    const childRequestGate = new Promise<void>(resolve => { releaseChildRequest = resolve })
    let blockChildDetail = true
    vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      if (blockChildDetail && String(input).endsWith('/api/projects/project-child')) {
        blockChildDetail = false
        await childRequestGate
      }
      return originalFetch(input, init)
    }))

    await wrapper.get('[aria-label="选择项目号 PRJ-REAL-001-1"]').trigger('click')
    await new Promise(resolve => window.setTimeout(resolve, 0))
    await projectTabByText(wrapper, 'BOM').trigger('click')
    await projectTabByText(wrapper, '备料').trigger('click')
    releaseChildRequest()
    await flushPromises()

    expect(wrapper.get('.pdm-project-sidebar__summary').text()).toContain('PRJ-REAL-001-1 · 泵组单元')
    expect(wrapper.get('.pdm-project-tabs button.is-active').text()).toBe('备料')
    const requestedUrls = vi.mocked(fetch).mock.calls.map(([input]) => String(input))
    expect(requestedUrls).toContain('http://127.0.0.1:5080/api/projects/project-child/procurement-tracking')
    expect(requestedUrls).not.toContain(`http://127.0.0.1:5080/api/projects/${projectId}/procurement-tracking`)
    wrapper.unmount()
  })

  it.each([false, true])('restores the most recently used project and tab after restart (desktop: %s)', async desktop => {
    const fallbackProject = { id: 'project-fallback', code: 'PRJ-FALLBACK-001', name: '回退项目', owner: 'engineer', responsibleUsers: ['engineer'], vaultLocation: 'D:\\PDM\\PRJ-FALLBACK-001', releaseLocation: 'D:\\Release\\PRJ-FALLBACK-001', isActive: true, quantity: 1, serialNumbers: [] }
    installApiMock([fallbackProject])
    window.localStorage.setItem('upton-pdm-project-center', JSON.stringify({ projectId, tab: 'bom', username: 'engineer', companyId: '' }))
    window.localStorage.setItem('upton-pdm-active-navigation', 'admin')
    Object.defineProperty(window, 'chrome', {
      configurable: true,
      value: desktop ? { webview: { postMessage: vi.fn(), addEventListener: vi.fn() } } : undefined,
    })

    let wrapper = mount(App, { attachTo: document.body, global: { plugins: [ElementPlus] } })
    try {
      await openLogin(wrapper)
      await wrapper.get('input[name="username"]').setValue('engineer')
      await wrapper.get('input[name="password"]').setValue('correct-password')
      await wrapper.get('form[aria-label="登录PLM"]').trigger('submit')
      await flushPromises()

      expect(wrapper.find('.pdm-project-workspace').exists()).toBe(true)
      expect(wrapper.text()).toContain('PRJ-REAL-001')
      expect(wrapper.text()).not.toContain('PRJ-FALLBACK-001')
      expect(wrapper.get('.pdm-project-tabs button.is-active').text()).toBe('BOM')
      expect(wrapper.get('.pdm-sidebar__nav .pdm-nav-item.is-active').text()).toContain('项目中心')
      expect(JSON.parse(window.localStorage.getItem('upton-pdm-project-center:' + JSON.stringify(['engineer', ''])) ?? '{}')).toMatchObject({ projectId, tab: 'bom', username: 'engineer' })
      await buttonByText(wrapper, '退出').trigger('click')
      await flushPromises()
      await wrapper.get('input[name="username"]').setValue('admin')
      await wrapper.get('input[name="password"]').setValue('correct-password')
      await wrapper.get('form[aria-label="登录PLM"]').trigger('submit')
      await flushPromises()
      expect(wrapper.get('.pdm-project-tabs button.is-active').text()).toBe('概览')
      await projectTabByText(wrapper, '信息').trigger('click')
      await flushPromises()
      await buttonByText(wrapper, '退出').trigger('click')
      await flushPromises()
      await wrapper.get('input[name="username"]').setValue('engineer')
      await wrapper.get('input[name="password"]').setValue('correct-password')
      await wrapper.get('form[aria-label="登录PLM"]').trigger('submit')
      await flushPromises()
      expect(wrapper.get('.pdm-project-tabs button.is-active').text()).toBe('BOM')
      expect(JSON.parse(window.localStorage.getItem('upton-pdm-project-center:' + JSON.stringify(['admin', ''])) ?? '{}')).toMatchObject({ projectId, tab: 'information', username: 'admin' })
      wrapper.unmount()
      window.sessionStorage.clear()
      wrapper = mount(App, { attachTo: document.body, global: { plugins: [ElementPlus] } })
      await flushPromises()
      expect(wrapper.get('.pdm-project-tabs button.is-active').text()).toBe('BOM')
      expect(wrapper.text()).toContain('PRJ-REAL-001')
    } finally {
      wrapper.unmount()
    }
  })

  it('restores encrypted Windows client credentials without logging in automatically', async () => {
    const postMessage = vi.fn()
    Object.defineProperty(window, 'chrome', {
      configurable: true,
      value: { webview: { postMessage, addEventListener: vi.fn() } },
    })
    const wrapper = mount(App, { attachTo: document.body, global: { plugins: [ElementPlus] } })
    await flushPromises()
    expect(wrapper.find('[aria-label="未登录主页"]').exists()).toBe(true)
    expect(wrapper.find('form[aria-label="登录PLM"]').exists()).toBe(true)
    expect(vi.mocked(fetch).mock.calls.filter(([input]) => String(input).endsWith('/api/auth/login'))).toHaveLength(0)

    await openLogin(wrapper)

    expect(postMessage).toHaveBeenCalledWith({ type: 'credentials-request', payload: undefined })
    expect((wrapper.get('input[name="rememberCredentials"]').element as HTMLInputElement).checked).toBe(true)
    window.dispatchEvent(new CustomEvent('pdm-remembered-credentials', {
      detail: { username: 'engineer', password: 'correct-password', remember: true },
    }))
    await flushPromises()

    expect((wrapper.get('input[name="username"]').element as HTMLInputElement).value).toBe('engineer')
    expect((wrapper.get('input[name="password"]').element as HTMLInputElement).value).toBe('correct-password')
    expect(vi.mocked(fetch).mock.calls.filter(([input]) => String(input).endsWith('/api/auth/login'))).toHaveLength(0)
    expect(vi.mocked(fetch).mock.calls.filter(([input]) => String(input).endsWith('/api/projects'))).toHaveLength(0)

    await wrapper.get('form[aria-label="登录PLM"]').trigger('submit')
    await flushPromises()

    expect(postMessage).toHaveBeenCalledWith({
      type: 'credentials-save',
      payload: { username: 'engineer', password: 'correct-password' },
    })
    expect(wrapper.find('form[aria-label="登录PLM"]').exists()).toBe(false)
    expect(window.localStorage.getItem('upton-pdm-project-center:' + JSON.stringify(['engineer', '']))).toContain(projectId)
    expect([...Array(window.localStorage.length)].map((_, index) => window.localStorage.key(index)).join(' ')).not.toContain('credential')
    expect([...Array(window.localStorage.length)].map((_, index) => window.localStorage.getItem(window.localStorage.key(index) ?? '')).join(' ')).not.toContain('correct-password')
    expect(window.localStorage.getItem('upton-pdm-session')).not.toContain('correct-password')

    await buttonByText(wrapper, '退出').trigger('click')
    await flushPromises()
    expect(wrapper.find('form[aria-label="登录PLM"]').exists()).toBe(true)
    wrapper.unmount()
  })

  it('waits for manual confirmation before submitting rejected remembered credentials', async () => {
    const postMessage = vi.fn()
    Object.defineProperty(window, 'chrome', {
      configurable: true,
      value: { webview: { postMessage, addEventListener: vi.fn() } },
    })
    const wrapper = mount(App, { attachTo: document.body, global: { plugins: [ElementPlus] } })
    await openLogin(wrapper)

    window.dispatchEvent(new CustomEvent('pdm-remembered-credentials', {
      detail: { username: 'engineer', password: 'expired-password', remember: true },
    }))
    await flushPromises()

    expect((wrapper.get('input[name="username"]').element as HTMLInputElement).value).toBe('engineer')
    expect((wrapper.get('input[name="password"]').element as HTMLInputElement).value).toBe('expired-password')
    expect(vi.mocked(fetch).mock.calls.filter(([input]) => String(input).endsWith('/api/auth/login'))).toHaveLength(0)

    await wrapper.get('form[aria-label="登录PLM"]').trigger('submit')
    await flushPromises()

    expect(wrapper.text()).toContain('用户名或密码错误。')
    expect(postMessage).not.toHaveBeenCalledWith({ type: 'credentials-clear', payload: undefined })
    const loginRequests = vi.mocked(fetch).mock.calls.filter(([input]) => String(input).endsWith('/api/auth/login'))
    expect(loginRequests).toHaveLength(1)
  })

  it('renews a persisted session when an access token is rejected', async () => {
    const wrapper = mount(App, { attachTo: document.body, global: { plugins: [ElementPlus] } })
    await login(wrapper, false, 'admin')
    materialRequestsUnauthorized = true

    await buttonByText(wrapper, '料品管理').trigger('click')
    await flushPromises()

    expect(wrapper.find('form[aria-label="登录PLM"]').exists()).toBe(false)
    expect(window.localStorage.getItem('upton-pdm-session')).toContain('renewed-token')
    expect(vi.mocked(fetch).mock.calls.filter(([input]) => String(input).endsWith('/api/auth/resume'))).toHaveLength(1)
    expect(window.sessionStorage.getItem('upton-pdm-session')).toBeNull()
    wrapper.unmount()
  })

  it('clears a persisted session when its device token is invalid', async () => {
    const wrapper = mount(App, { attachTo: document.body, global: { plugins: [ElementPlus] } })
    await login(wrapper, false, 'admin')
    materialRequestsUnauthorized = true
    resumeRequestsUnauthorized = true

    await buttonByText(wrapper, '料品管理').trigger('click')
    await flushPromises()

    expect(wrapper.find('form[aria-label="登录PLM"]').exists()).toBe(true)
    expect(wrapper.text()).toContain('登录已失效，请重新登录。')
    expect(window.localStorage.getItem('upton-pdm-session')).toBeNull()
    wrapper.unmount()
  })

  it('把 Windows 客户端启动与工作区设置归入个人设置（系统设置里不再有客户端设置）', async () => {
    const postMessage = vi.fn()
    Object.defineProperty(window, 'chrome', {
      configurable: true,
      value: { webview: { postMessage, addEventListener: vi.fn() } },
    })
    const wrapper = mount(App, { attachTo: document.body, global: { plugins: [ElementPlus] } })
    await login(wrapper, false)

    expect(wrapper.findAll('.pdm-sidebar__footer button').map(button => button.text())).toEqual(['V2026.09.12.1234'])
    expect(wrapper.get('.pdm-sidebar__version').text()).toBe('V2026.09.12.1234')
    window.dispatchEvent(new CustomEvent('pdm-client-version', { detail: { version: '2026.09.12.1200-desktop-client' } }))
    await wrapper.vm.$nextTick()
    expect(wrapper.get('.pdm-sidebar__version').text()).toBe('V2026.09.12.1234')
    expect(wrapper.get('.pdm-sidebar__version').attributes('title')).toBe('版本 2026.09.12.1234-version-display，点击查看详情')
    expect(wrapper.find('.pdm-admin-workspace').exists()).toBe(false)
    expect(wrapper.findAll('button').some(button => button.text().includes('客户端设置'))).toBe(false)

    // 个人设置（右上角用户名）里的“客户端设置”页签承载这些本机项。
    await wrapper.get('.pdm-user-profile-trigger').trigger('click')
    await flushPromises()
    // 个人设置对话框 append-to-body，页签要查 document。
    expect([...document.querySelectorAll('.el-tabs__item')].map(item => item.textContent ?? '')).toContain('客户端设置')
    const clickDocumentButton = async (label: string) => {
      const button = [...document.querySelectorAll<HTMLButtonElement>('button')].find(item => item.textContent?.includes(label))
      if (!button) throw new Error(`Button not found in document: ${label}`)
      button.click()
      await flushPromises()
    }
    ;[...document.querySelectorAll<HTMLElement>('.el-tabs__item')]
      .find(item => item.textContent?.includes('客户端设置'))!.click()
    await flushPromises()
    expect(postMessage).toHaveBeenCalledWith({ type: 'desktop-settings-request', payload: undefined })
    expect(postMessage).toHaveBeenCalledWith({ type: 'workspace-maintenance-request', payload: undefined })

    window.dispatchEvent(new CustomEvent('pdm-desktop-settings', {
      detail: {
        available: true,
        startWithWindows: true,
        workspaceRoot: 'C:\\Users\\engineer\\AppData\\Local\\UPTON PDM\\Workspace',
        defaultWorkspaceRoot: 'C:\\Users\\engineer\\AppData\\Local\\UPTON PDM\\Workspace',
      },
    }))
    await flushPromises()
    expect(document.querySelector<HTMLInputElement>('input[aria-label="PLM受控工作区"]')!.value).toContain('UPTON PDM\\Workspace')
    window.dispatchEvent(new CustomEvent('pdm-workspace-maintenance', {
      detail: { available: true, workingFiles: 41, workingBytes: 2_048, snapshotFiles: 82, snapshotBytes: 3_145_728, recoveryFiles: 0, recoveryBytes: 0 },
    }))
    await flushPromises()
    expect(document.querySelector('[aria-label="工作区用量"]')!.textContent).toContain('项目工作文件41 个 · 2.0 KB不自动清理')
    expect(document.querySelector('[aria-label="工作区用量"]')!.textContent).toContain('可重建只读缓存82 个 · 3.0 MB可安全清理')
    const confirmClean = vi.spyOn(ElMessageBox, 'confirm').mockResolvedValue('confirm' as never)
    await clickDocumentButton('清理只读缓存')
    expect(confirmClean).toHaveBeenCalled()
    expect(postMessage).toHaveBeenCalledWith({ type: 'workspace-cache-clean', payload: undefined })
    confirmClean.mockRestore()
    await clickDocumentButton('已开启')
    expect(postMessage).toHaveBeenCalledWith({ type: 'desktop-settings-save', payload: { startWithWindows: false } })

    window.dispatchEvent(new CustomEvent('pdm-workspace-folder-selected', { detail: { workspaceRoot: 'D:\\PDM-Cache' } }))
    await flushPromises()
    expect(document.querySelector<HTMLInputElement>('input[aria-label="PLM受控工作区"]')!.value).toBe('D:\\PDM-Cache')

    await clickDocumentButton('保存工作区')
    expect(postMessage).toHaveBeenCalledWith({ type: 'desktop-settings-save', payload: { workspaceRoot: 'D:\\PDM-Cache' } })
  })

  it.each([['琥珀橙', 'o'], ['鸢尾紫', 'p'], ['石墨灰', 'g']])('个人设置切换%s会写入账号并立即应用', async (label, value) => {
    const wrapper = mount(App, { attachTo: document.body, global: { plugins: [ElementPlus] } })
    await login(wrapper, false)

    await wrapper.get('.pdm-user-profile-trigger').trigger('click')
    await flushPromises()
    const themeTab = [...document.querySelectorAll<HTMLElement>('.el-tabs__item')]
      .find(item => item.textContent?.includes('界面主题'))!
    themeTab.click()
    await flushPromises()
    const orange = [...document.querySelectorAll<HTMLElement>('.personal-theme-group .el-radio')]
      .find(item => item.textContent?.includes(label))!
    orange.querySelector<HTMLInputElement>('input')!.click()
    await flushPromises()

    const profileCall = vi.mocked(fetch).mock.calls
      .find(([input, init]) => String(input).endsWith('/api/auth/profile') && (init?.method ?? 'GET') === 'PUT')
    expect(profileCall).toBeTruthy()
    expect(String(profileCall![1]?.body)).toContain(`"theme":"${value}"`)
    expect(document.documentElement.dataset.pdmTheme).toBe(value)
    wrapper.unmount()
  })

  it('logs in and renders project, tree, BOM and release data returned by the API', async () => {
    const wrapper = mount(App, { attachTo: document.body, global: { plugins: [ElementPlus] } })
    await flushPromises()
    expect(wrapper.find('[aria-label="未登录主页"]').exists()).toBe(true)
    expect(wrapper.find('form[aria-label="登录PLM"]').exists()).toBe(true)
    expect(wrapper.find('.el-overlay-dialog').exists()).toBe(false)

    await login(wrapper, false)

    expect(wrapper.get('[aria-label="项目中心"]').attributes('aria-label')).toBe('项目中心')
    expect(wrapper.find('[aria-label="项目设计树"]').exists()).toBe(false)
    await runProjectAction(wrapper, 'open')
    await flushPromises()

    expect(wrapper.text()).toContain('PRJ-REAL-001 · 真实装配项目')
    expect(projectTabByText(wrapper, '图档').exists()).toBe(true)
    expect(projectTabByText(wrapper, 'BOM').exists()).toBe(true)
    await projectTabByText(wrapper, '文件').trigger('click')
    await flushPromises()
    expect(wrapper.text()).toContain('项目文件')
    expect(wrapper.get('.pdm-project-workspace').classes()).not.toContain('is-document-view')
    await projectTabByText(wrapper, '图档').trigger('click')
    await flushPromises()
    expect(wrapper.get('.pdm-project-workspace').classes()).not.toContain('is-document-view')
    expect(wrapper.text()).toContain('REAL-ASM-001')
    // 顶部工具栏不再显示图档切换区（版本、占用状态改为只在左侧属性卡与设计树中呈现）。
    expect(wrapper.get('.pdm-preview-toolbar').find('.pdm-preview-document-switcher').exists()).toBe(false)
    expect(wrapper.get('.pdm-preview-properties-bar').find('[aria-label="图档属性"]').exists()).toBe(true)
    const projectSidebar = wrapper.get('[aria-label="项目基本信息与全部项目号"]')
    expect(projectSidebar.find('[aria-label="BOM完整性"]').exists()).toBe(false)
    expect(projectSidebar.find('[aria-label="当前发布包"]').exists()).toBe(false)

    await projectTabByText(wrapper, 'BOM').trigger('click')
    await flushPromises()
    expect(wrapper.find('[aria-label="BOM维护"]').exists()).toBe(true)
    expect(wrapper.find('[aria-label="BOM维护"] h2').exists()).toBe(false)
    expect(wrapper.text()).toContain('标准件BOM（1）')

    // 发布已从BOM面板的子页签改为工作区顶部页签（BOM与备料之间）。
    const releaseViewTab = projectTabByText(wrapper, '发布')
    await releaseViewTab.trigger('click')
    await flushPromises()
    expect(wrapper.findAll('.pdm-release-stream-card')).toHaveLength(3)
    expect(wrapper.text()).not.toContain('历史组合发布')
    expect(wrapper.text()).not.toContain('PRJ-2026-018')
  })

  it('shows active edit permissions in my tasks', async () => {
    const wrapper = mount(App, { attachTo: document.body, global: { plugins: [ElementPlus] } })
    await login(wrapper, false)

    await wrapper.get('.pdm-message-button').trigger('click')
    await flushPromises()

    await vi.waitFor(() => expect(document.body.querySelector('[aria-label="消息中心"]')).not.toBeNull())
    const taskCenter = document.body.querySelector('[aria-label="消息中心"]')!
    expect(taskCenter.textContent).toContain('LOCK-001')
    expect(taskCenter.textContent).toContain('DESIGN-WS')
    expect(taskCenter.textContent).toContain('请及时存档')
    expect(taskCenter.textContent).toContain('我需要编辑')
    wrapper.unmount()
  })

  it('keeps project navigation usable while the new workspace feeds are rolling out', async () => {
    const existingFetch = vi.mocked(fetch)
    vi.stubGlobal('fetch', vi.fn((input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input).replace(/\?summary=true$/, '')
      if (url.endsWith('/api/approval-tasks/mine') || url.includes('/versions') || url.includes('/audit?take=')) {
        return Promise.resolve(json({ title: 'Not Found' }, 404))
      }
      return existingFetch(input, init)
    }))
    const wrapper = mount(App, { attachTo: document.body, global: { plugins: [ElementPlus] } })

    await login(wrapper, false)
    expect(wrapper.get('[aria-label="项目中心"]').text()).toContain('PRJ-REAL-001')
    await runProjectAction(wrapper, 'open')
    await flushPromises()
    await projectTabByText(wrapper, '记录').trigger('click')
    await flushPromises()
    expect(wrapper.text()).toContain('暂无项目记录。')
  })

  it('creates and manages projects before SolidWorks drawings are associated', async () => {
    const wrapper = mount(App, { attachTo: document.body, global: { plugins: [ElementPlus] } })
    await login(wrapper, false)

    expect(wrapper.get('[aria-label="项目中心"]').attributes('aria-label')).toBe('项目中心')
    expect(wrapper.text()).toContain('PRJ-REAL-001')
    await buttonByText(wrapper, '创建主项目').trigger('click')
    await wrapper.get('select[name="projectTypeCode"]').setValue('P')
    await wrapper.get('select[name="equipmentTypeCode"]').setValue('2')
    await flushPromises()
    wrapper.getComponent({ name: 'ElSelect' }).vm.$emit('update:modelValue', 'customer-1')
    await wrapper.vm.$nextTick()
    await wrapper.get('input[name="projectName"]').setValue('新建装配项目')
    await wrapper.get('input[name="projectAlias"]').setValue('测试别名')
    await wrapper.get('input[name="signedDate"]').setValue('2026-08-13')
    await wrapper.get('input[name="quantity"]').setValue('2')
    await wrapper.get('form[aria-label="创建PLM项目"]').trigger('submit')
    await flushPromises()

    const fetchMock = vi.mocked(fetch)
    const createRequest = fetchMock.mock.calls.find(([input, init]) => String(input).endsWith('/api/projects') && init?.method === 'POST')
    expect(createRequest).toBeDefined()
    expect(JSON.parse(String(createRequest?.[1]?.body))).toEqual({
      organizationId: '70000000-0000-0000-0000-000000000001',
      projectTypeCode: 'P',
      equipmentTypeCode: 2,
      customerId: 'customer-1',
      name: '新建装配项目',
      projectAlias: '测试别名',
      signedDate: '2026-08-13',
      quantity: 2,
      bomItemCategoryCode: '0301',
    })
    expect(wrapper.text()).toContain('P700001')
    expect(wrapper.text()).toContain('AK-2-C00465-001-00')
    expect(wrapper.text()).toContain('中山比亚迪电子有限公司')

    const createdRow = wrapper.findAll('tbody tr').find(row => row.text().includes('P700001'))
    expect(createdRow?.find('[aria-label="操作项目P700001"]').exists()).toBe(true)
    await runProjectAction(wrapper, 'create-child', 'P700001')
    await wrapper.get('input[name="childProjectName"]').setValue('子项目一')
    await wrapper.get('input[name="childQuantity"]').setValue('2')
    await wrapper.get('form[aria-label="创建PLM子项目"]').trigger('submit')
    await flushPromises()
    expect(wrapper.text()).toContain('P700001-1')
    expect(wrapper.text()).toContain('AK-2-C00465-001-01')
    expect(wrapper.text()).toContain('子项目一')

    await runProjectAction(wrapper, 'open', 'P700001')
    await flushPromises()
    await projectTabByText(wrapper, '文件').trigger('click')
    const confirmSwitch = vi.spyOn(ElMessageBox, 'confirm').mockResolvedValue('confirm' as never)
    await wrapper.get('[aria-label="选择项目号 P700001-1"]').trigger('click')
    await flushPromises()
    expect(confirmSwitch).not.toHaveBeenCalled()
    confirmSwitch.mockRestore()
    expect(wrapper.get('main.pdm-main').classes()).toContain('is-project-workspace')
    expect(wrapper.get('.pdm-project-tabs button.is-active').text()).toBe('文件')
    expect(wrapper.get('.pdm-project-sidebar__summary').text()).toContain('AK-2-C00465-001-01')
    expect(wrapper.get('.pdm-project-sidebar__summary').text()).toContain('70000003、70000004')
    expect(wrapper.find('.pdm-project-selected-summary').exists()).toBe(false)
  })

  it('configures U9C customer synchronization, role permissions and system settings without manual customer maintenance', async () => {
    const wrapper = mount(App, { attachTo: document.body, global: { plugins: [ElementPlus] } })
    await openLogin(wrapper)
    await wrapper.get('input[name="username"]').setValue('admin')
    await wrapper.get('input[name="password"]').setValue('correct-password')
    await wrapper.get('form[aria-label="登录PLM"]').trigger('submit')
    await flushPromises()

    await navigateTo(wrapper, '项目列表')

    await buttonByText(wrapper, '创建主项目').trigger('click')
    expect(wrapper.getComponent({ name: 'ElSelect' }).props('filterable')).toBe(true)
    expect(wrapper.find('input[name="customerCode"]').exists()).toBe(false)
    expect(wrapper.find('input[name="projectOwner"]').exists()).toBe(false)
    expect(wrapper.find('input[name="vaultLocation"]').exists()).toBe(false)
    await buttonByText(wrapper, '取消').trigger('click')

    await buttonByText(wrapper, '系统设置').trigger('click')
    await flushPromises()
    await buttonByText(wrapper, 'U9C接口').trigger('click')
    await flushPromises()
    expect(wrapper.find('.u9-integration-page > .pdm-pagebar').exists()).toBe(false)
    expect(wrapper.get('[aria-label="U9C接口管理"]').text()).toContain('基础设置')
    const customerTab = wrapper.findAll('[role="tab"]').find(tab => tab.text().includes('客户查询'))
    await customerTab!.trigger('click')
    await flushPromises()
    const crmPanel = wrapper.get('[aria-label="U9C客户同步"]')
    expect(crmPanel.text()).toContain('C00465')
    expect(crmPanel.text()).not.toContain('新增客户')
    expect(crmPanel.findAll('button').some(button => button.text().trim() === '编辑')).toBe(false)
    expect(crmPanel.find('input[name="u9CustomerBaseUrl"]').exists()).toBe(false)
    expect(crmPanel.find('input[name="u9CustomerUser"]').exists()).toBe(false)
    await crmPanel.get('input[name="u9CustomerAutoSyncEnabled"]').setValue(true)
    await crmPanel.get('select[name="u9CustomerAutoSyncIntervalMinutes"]').setValue('30')
    await buttonByText(wrapper, '保存同步计划').trigger('click')
    await flushPromises()
    expect(vi.mocked(fetch)).toHaveBeenCalledWith(expect.stringMatching(/\/api\/crm-integration$/), expect.objectContaining({ method: 'PUT' }))
    const settingsRequest = vi.mocked(fetch).mock.calls.find(([input, init]) => String(input).endsWith('/api/crm-integration') && init?.method === 'PUT')
    expect(JSON.parse(String(settingsRequest?.[1]?.body))).toMatchObject({ autoSyncEnabled: true, autoSyncIntervalMinutes: 30 })
    expect(crmPanel.text()).toContain('已启用，每30分钟同步')
    await buttonByText(wrapper, '测试连接').trigger('click')
    await flushPromises()
    await buttonByText(wrapper, '从U9C同步').trigger('click')
    await flushPromises()
    expect(vi.mocked(fetch)).toHaveBeenCalledWith(expect.stringMatching(/\/api\/crm-integration\/sync$/), expect.objectContaining({ method: 'POST' }))
    expect(wrapper.get('[aria-label="U9C客户同步"]').text()).toContain('C00888')
    expect(wrapper.get('[aria-label="U9C客户同步"]').text()).toContain('当前可见')

    await navigateTo(wrapper, '项目列表')
    const maintainButton = wrapper.findAll('button').find(button => button.text().trim() === '维护')
    expect(maintainButton).toBeUndefined()

    await buttonByText(wrapper, '系统设置').trigger('click')
    await buttonByText(wrapper, '用户设置').trigger('click')
    expect(wrapper.find('.pdm-pagebar').exists()).toBe(false)
    await buttonByText(wrapper, '角色权限').trigger('click')
    expect(wrapper.get('[aria-label="角色权限设置"]').text()).toContain('工程师')
    const engineerRoleRow = wrapper.findAll('.el-table__row').find(row => row.text().includes('Engineer'))
    expect(engineerRoleRow).toBeDefined()
    await engineerRoleRow!.findAll('button').find(button => button.text().trim() === '权限设置')!.trigger('click')
    await buttonByText(wrapper, '项目分工').trigger('click')
    expect(wrapper.text()).toContain('分配子项目设计人员')
    await buttonByText(wrapper, '项目内容').trigger('click')
    const documentPermission = wrapper.findAll('.pdm-permission-cell').find(item => item.text().includes('document.edit'))
    expect(documentPermission).toBeDefined()
    await documentPermission!.get('input[type="checkbox"]').setValue(false)
    await buttonByText(wrapper, '保存并立即生效').trigger('click')
    await flushPromises()
    expect(vi.mocked(fetch)).toHaveBeenCalledWith(expect.stringMatching(/\/api\/role-permissions\/Engineer$/), expect.objectContaining({ method: 'PUT' }))

    await buttonByText(wrapper, '编号与存储').trigger('click')
    expect(wrapper.find('.pdm-pagebar').exists()).toBe(false)
    expect(wrapper.get('[aria-label="系统设置"]').text()).toContain('图档存档根目录')
    await wrapper.get('.el-tabs__item#tab-checkout-policy').trigger('click')
    await flushPromises()
    expect(wrapper.get('[aria-label="系统设置"]').text()).toContain('建议60秒')
    expect(wrapper.get('[aria-label="系统设置"]').text()).toContain('在线有未提交修改时由负责人处理')
    await wrapper.get('.el-tabs__item#tab-equipment').trigger('click')
    await flushPromises()
    expect(wrapper.text()).toContain('设备类型设置')
    expect(wrapper.text()).toContain('00–99')

    await buttonByText(wrapper, '文件夹模板').trigger('click')
    expect(wrapper.find('.pdm-pagebar').exists()).toBe(false)
    expect(wrapper.get('.pdm-template-toolbar').text()).toContain('保存模板')

    await buttonByText(wrapper, '全局审计').trigger('click')
    expect(wrapper.find('.pdm-pagebar').exists()).toBe(false)
    expect(wrapper.text()).toContain('全局操作记录')
  })

  it('filters the real reference tree and shows a clear login error', async () => {
    const wrapper = mount(App, { attachTo: document.body, global: { plugins: [ElementPlus] } })
    await openLogin(wrapper)
    await wrapper.get('input[name="username"]').setValue('engineer')
    await wrapper.get('input[name="password"]').setValue('wrong-password')
    await wrapper.get('form[aria-label="登录PLM"]').trigger('submit')
    await flushPromises()
    expect(wrapper.text()).toContain('用户名或密码错误')

    await wrapper.get('input[name="password"]').setValue('correct-password')
    await wrapper.get('form[aria-label="登录PLM"]').trigger('submit')
    await flushPromises()
    await projectTabByText(wrapper, '图档').trigger('click')
    await flushPromises()
    await wrapper.get('input[type="search"]').setValue('REAL-PRT-001')
    await flushPromises()
    expect(wrapper.text()).toContain('REAL-PRT-001')
    expect(wrapper.text()).not.toContain('PRJ-2026-018')
    await wrapper.get('input[type="search"]').setValue('不存在的图号')
    await flushPromises()
    expect(wrapper.text()).toContain('没有匹配的图档')
  })

  it('shows a service connection error instead of the browser fetch error during login', async () => {
    vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input).replace(/\?summary=true$/, '')
      if (url.endsWith('/health')) return json({ status: 'ok' })
      if (url.endsWith('/api/auth/login')) throw new TypeError('Failed to fetch')
      return json({ title: `Unexpected URL: ${url}` }, 404)
    }))
    const wrapper = mount(App, { attachTo: document.body, global: { plugins: [ElementPlus] } })
    try {
      await openLogin(wrapper)
      await wrapper.get('input[name="username"]').setValue('engineer')
      await wrapper.get('input[name="password"]').setValue('correct-password')
      await wrapper.get('form[aria-label="登录PLM"]').trigger('submit')
      await flushPromises()

      expect(wrapper.text()).toContain('无法连接 PLM 服务')
      expect(wrapper.text()).not.toContain('Failed to fetch')
    } finally { wrapper.unmount() }
  })

  it('filters real 3D and 2D documents and keeps the preview panel minimal', async () => {
    nonStandardBomResponse = [{
      id: 'non-standard-root', kind: 'NonStandard', sequence: 1, drawingNumber: 'REAL-ASM-001', name: '真实总装配', quantity: 1,
      unit: '件', material: 'Q235B', revision: 'W2', isComplete: true, source: 'Auto', sourceDocumentId: 'doc-root',
      isManuallyExcluded: false, isPendingClassification: false,
    }]
    const wrapper = mount(App, { attachTo: document.body, global: { plugins: [ElementPlus] } })
    await login(wrapper)
    await projectTabByText(wrapper, '图档').trigger('click')
    await flushPromises()

    const structure = wrapper.get('[aria-label="项目设计树"]')
    await structure.get('button[role="tab"][aria-selected="false"]:nth-of-type(2)').trigger('click')
    await flushPromises()
    expect(structure.text()).toContain('REAL-PRT-001')
    expect(structure.text()).not.toContain('SLDDRW')

    await structure.get('input[type="search"]').setValue('仅匹配旧类型')
    const drawingFilter = structure.findAll('button[role="tab"]').find(button => button.text().includes('2D'))
    expect(drawingFilter).toBeTruthy()
    await drawingFilter!.trigger('click')
    await flushPromises()
    expect((structure.get('input[type="search"]').element as HTMLInputElement).value).toBe('')
    expect(structure.text()).toContain('真实总装工程图')
    const unarchivedDrawing = structure.findAll('button.pdm-tree-row').find(row => row.text().includes('遗漏工程图'))
    expect(unarchivedDrawing?.text()).toContain('未存档')
    expect(unarchivedDrawing?.text()).not.toContain('未发起')
    await unarchivedDrawing!.trigger('click')
    await flushPromises()
    // 顶部工具栏不再显示图档切换区，图档信息只保留在左侧属性卡里。
    const preview = wrapper.get('[aria-label="图档预览"]')
    expect(preview.find('.pdm-preview-document-switcher').exists()).toBe(false)
    expect(preview.find('.pdm-related-documents').exists()).toBe(false)
    const properties = preview.get('.pdm-preview-properties-bar').find('.pdm-preview-properties')
    expect(properties.exists()).toBe(true)
    expect(properties.text()).toContain('物料编码')
    expect(preview.find('#drawing-review-decision-host').exists()).toBe(true)
  })

  it('waits for an explicit drawing selection before loading a 2D preview', async () => {
    const postMessage = vi.fn()
    Object.defineProperty(window, 'chrome', {
      configurable: true,
      value: { webview: { postMessage, addEventListener: vi.fn() } },
    })
    const wrapper = mount(App, { attachTo: document.body, global: { plugins: [ElementPlus] } })
    await login(wrapper)
    await projectTabByText(wrapper, '图档').trigger('click')
    await flushPromises()
    await buttonByText(wrapper, '加载交互预览').trigger('click')
    await flushPromises()
    const previewsBeforeSwitch = postMessage.mock.calls.filter(([message]) => message.type === 'preview-document').length
    const baseFetch = globalThis.fetch
    let completeReload: (() => void) | undefined
    vi.stubGlobal('fetch', vi.fn((input: RequestInfo | URL, init?: RequestInit) => {
      if (!completeReload && String(input).endsWith(`/api/projects/${projectId}/drawing-reviews`)) {
        return new Promise<Response>(resolve => {
          completeReload = () => { void baseFetch(input, init).then(resolve) }
        })
      }
      return baseFetch(input, init)
    }))
    window.dispatchEvent(new Event('focus'))
    await flushPromises()
    expect(completeReload).toBeDefined()

    const drawingFilter = wrapper.findAll('button[role="tab"]').find(button => button.text().includes('2D'))
    await drawingFilter!.trigger('click')
    await flushPromises()
    expect(postMessage.mock.calls.filter(([message]) => message.type === 'preview-document')).toHaveLength(previewsBeforeSwitch)
    expect(wrapper.get('[aria-label="图档预览"]').text()).toContain('请选择图纸')
    completeReload!()
    await flushPromises()
    expect(wrapper.get('[aria-label="图档预览"]').text()).toContain('请选择图纸')
    expect(postMessage.mock.calls.filter(([message]) => message.type === 'preview-document')).toHaveLength(previewsBeforeSwitch)

    window.dispatchEvent(new Event('focus'))
    await flushPromises()
    expect(wrapper.get('[aria-label="图档预览"]').text()).toContain('请选择图纸')
    expect(postMessage.mock.calls.filter(([message]) => message.type === 'preview-document')).toHaveLength(previewsBeforeSwitch)

    await drawingFilter!.trigger('click')
    await flushPromises()
    expect(wrapper.get('[aria-label="图档预览"]').text()).toContain('请选择图纸')
    expect(postMessage.mock.calls.filter(([message]) => message.type === 'preview-document')).toHaveLength(previewsBeforeSwitch)

    await wrapper.get('.pdm-drawing-list .pdm-tree-row').trigger('click')
    await flushPromises()
    expect(postMessage.mock.calls.filter(([message]) => message.type === 'preview-document')).toHaveLength(previewsBeforeSwitch + 1)
    wrapper.unmount()
  })

  it('orders grouped tabs and restores the last page when returning to each group', async () => {
    const wrapper = mount(App, { attachTo: document.body, global: { plugins: [ElementPlus] } })
    await login(wrapper)
    await navigateTo(wrapper, '项目日历')
    const tabs = () => wrapper.get('.pdm-navigation-tabs').findAll('[role="tab"]').map(tab => tab.text())
    expect(tabs()).toEqual(['项目总览', '项目日历', '项目列表'])
    const calendar = wrapper.get('[data-navigation-page="project-calendar"]')
    await calendar.findAll('button').find(button => button.text() === '周')!.trigger('click')
    await navigateTo(wrapper, '标准程序')
    expect(wrapper.find('.pdm-navigation-tabs').exists()).toBe(false)
    expect(wrapper.get('.pdm-sidebar__nav .is-active').text()).toBe('标准程序')
    const templates = wrapper.get('[data-navigation-page="program-templates"]').element
    await buttonByText(wrapper, '项目总览').trigger('click'); await flushPromises()
    expect(calendar.isVisible()).toBe(true)
    expect(calendar.get('.pc-week-view').isVisible()).toBe(true)
    expect(wrapper.get('.pdm-navigation-tabs [aria-selected="true"]').text()).toBe('项目日历')
    await buttonByText(wrapper, '标准物料').trigger('click'); await flushPromises()
    expect(tabs()).toEqual(['标准物料', '标准结构', '常备件'])
    await navigateTo(wrapper, '常备件')
    expect(wrapper.get('[data-navigation-page="stocked-materials"]').isVisible()).toBe(true)
    await navigateTo(wrapper, '标准程序')
    expect(wrapper.get('[data-navigation-page="program-templates"]').element).toBe(templates)
    await buttonByText(wrapper, '标准物料').trigger('click'); await flushPromises()
    expect(wrapper.get('.pdm-navigation-tabs [aria-selected="true"]').text()).toBe('常备件')
    wrapper.unmount()
  })

  it('keeps program templates accessible without granting access to standard library pages', async () => {
    const originalFetch = vi.mocked(fetch).getMockImplementation()!
    vi.mocked(fetch).mockImplementation(async (input, init) => {
      const response = await originalFetch(input, init)
      if (!String(input).endsWith('/api/auth/login')) return response
      const session = await response.json()
      return json({ ...session, permissions: session.permissions.filter((permission: string) => permission !== 'standard-library.view') })
    })
    const wrapper = mount(App, { attachTo: document.body, global: { plugins: [ElementPlus] } })
    await login(wrapper)
    await buttonByText(wrapper, '标准程序').trigger('click'); await flushPromises()
    expect(wrapper.find('.pdm-navigation-tabs').exists()).toBe(false)
    expect(wrapper.get('.pdm-sidebar__nav .is-active').text()).toBe('标准程序')
    expect(wrapper.get('[data-navigation-page="program-templates"]').isVisible()).toBe(true)
    expect(wrapper.find('[data-navigation-page="standard-library"]').exists()).toBe(false)
    expect(wrapper.find('[data-navigation-page="standard-structure"]').exists()).toBe(false)
    wrapper.unmount()
  })

  it('keeps every visited navigation page mounted with its filters and active tab', async () => {
    const wrapper = mount(App, { attachTo: document.body, global: { plugins: [ElementPlus] } })
    await login(wrapper, true, 'admin')
    const navigate = (label: string) => navigateTo(wrapper, label)
    const pages = [
      ['项目日历', 'project-calendar'], ['项目总览', 'project-workbench'], ['项目列表', 'projects'],
      ['标准物料', 'standard-library'], ['标准结构', 'standard-structure'], ['料品管理', 'materials'],
      ['标准程序', 'program-templates'], ['我的待办', 'tasks'], ['制造中心', 'production-drawings'], ['系统设置', 'admin'],
    ] as const
    for (const [label, key] of pages) {
      await navigate(label)
      const page = wrapper.get(`[data-navigation-page="${key}"]`)
      const original = page.element
      const search = page.find('input[type="search"], input[aria-label^="搜索"]')
      if (search.exists()) await search.setValue(`remember-${key}`)
      if (key === 'project-calendar') await page.findAll('button').find(button => button.text() === '周')!.trigger('click')
      if (key === 'materials') await page.get('#tab-code-approvals').trigger('click')
      if (key === 'admin') await page.findAll('.pdm-admin-tabs button').find(button => button.text() === '审批流程')!.trigger('click')
      await flushPromises()
      await navigate(key === 'standard-structure' ? '项目列表' : '标准结构')
      expect(page.isVisible()).toBe(false)
      await navigate(label)
      expect(wrapper.get(`[data-navigation-page="${key}"]`).element).toBe(original)
      expect(page.isVisible()).toBe(true)
      if (search.exists()) expect(search.element).toHaveProperty('value', `remember-${key}`)
      if (key === 'project-calendar') expect(page.get('.pc-week-view').isVisible()).toBe(true)
      if (key === 'materials') expect(page.get('#tab-code-approvals').attributes('aria-selected')).toBe('true')
      if (key === 'admin') expect(page.get('.approval-workflow-settings').isVisible()).toBe(true)
      expect(wrapper.findAll('[data-navigation-page]').filter(page => page.isVisible())).toHaveLength(1)
    }
    for (let attempt = 0; attempt < 2; attempt++) {
      await wrapper.get('[data-navigation-page="materials"] #tab-materials').trigger('click')
      await navigate('我的待办')
      wrapper.get('[data-navigation-page="tasks"]').getComponent({ name: 'MyTasks' }).vm.$emit('openMaterialApprovals')
      await flushPromises()
      expect(wrapper.get('[data-navigation-page="materials"] #tab-code-approvals').attributes('aria-selected')).toBe('true')
    }
    await buttonByText(wrapper, '退出').trigger('click'); await flushPromises()
    expect(wrapper.findAll('[data-navigation-page]')).toHaveLength(0)
    wrapper.unmount()
  }, 20_000)

  it('retains the BOM draft when leaving through the sidebar', async () => {
    const wrapper = mount(App, { attachTo: document.body, global: { plugins: [ElementPlus] } })
    await login(wrapper)
    await projectTabByText(wrapper, 'BOM').trigger('click'); await flushPromises()
    const bom = wrapper.getComponent({ name: 'BomManager' })
    bom.vm.$emit('dirtyChange', true)
    await navigateTo(wrapper, '项目列表'); await flushPromises()
    expect(wrapper.get('.pdm-sidebar__nav .pdm-nav-item.is-active').text()).toContain('项目总览')
    await buttonByText(wrapper, '项目中心').trigger('click'); await flushPromises()
    expect(wrapper.getComponent({ name: 'BomManager' }).element).toBe(bom.element)
    expect(wrapper.get('.pdm-project-tabs button.is-active').text()).toBe('BOM')
    wrapper.unmount()
  })

  it('preserves the current project tab when returning from sidebar navigation', async () => {
    drawingReviewsResponse = [{ id: 'review-1', projectId, number: 'DR-TEST', state: 'WritingProperties', createdBy: 'designer', createdAt: '2026-09-30T00:00:00Z', markups: [], items: [{ id: 'item-1', drawingDocumentId: 'doc-drawing', drawingNumber: 'REAL-ASM-001', drawingState: 'Approved', modelState: 'NotRequired' }] }]
    const wrapper = mount(App, { attachTo: document.body, global: { plugins: [ElementPlus] } })
    await login(wrapper)
    for (const tab of ['图档', '信息', 'BOM']) {
      await projectTabByText(wrapper, tab).trigger('click')
      await flushPromises()
      if (tab === '图档') { wrapper.getComponent({ name: 'DrawingReviewPanel' }).vm.$emit('update:collapsed', true); await flushPromises() }
      await navigateTo(wrapper, '项目列表')
      await flushPromises()
      expect(wrapper.get('.pdm-sidebar__nav .pdm-nav-item.is-active').text()).toContain('项目总览')
      await buttonByText(wrapper, '项目中心').trigger('click')
      await flushPromises()
      expect(wrapper.get('.pdm-project-tabs button.is-active').text()).toBe(tab)
      expect(wrapper.get('.pdm-project-workspace').text()).toContain('PRJ-REAL-001')
      if (tab === '图档') {
        expect(wrapper.get('[aria-label="项目设计树"]').isVisible()).toBe(true)
        expect(wrapper.getComponent({ name: 'DrawingReviewPanel' }).props('collapsed')).toBe(true)
      }
      await buttonByText(wrapper, '项目中心').trigger('click')
      await flushPromises()
      expect(wrapper.get('.pdm-project-tabs button.is-active').text()).toBe(tab)
    }
    wrapper.unmount()
  })

  it('switches the workbench and document pages and makes navigation buttons respond', async () => {
    const wrapper = mount(App, { attachTo: document.body, global: { plugins: [ElementPlus] } })
    await login(wrapper)

    await projectTabByText(wrapper, '概览').trigger('click')
    expect(wrapper.get('[aria-label="工作台主页面"]').text()).toContain('项目团队')

    await projectTabByText(wrapper, '图档').trigger('click')
    expect(wrapper.find('[aria-label="项目设计树"]').exists()).toBe(true)

    await projectTabByText(wrapper, 'BOM').trigger('click')
    await flushPromises()
    expect(wrapper.get('button[role="tab"][aria-selected="true"]').text()).toContain('多级总览')
    expect(wrapper.get('[aria-label="BOM多级总览"]').text()).toContain('本级BOM料号')
    expect(wrapper.get('[aria-label="BOM多级总览"]').text()).toContain('上级BOM料号')
    await wrapper.findAll('button[role="tab"]').find(button => button.text().includes('源数据'))!.trigger('click')
    expect(wrapper.text()).toContain('归标准件')
    expect(wrapper.text()).toContain('归非标件')
    expect(wrapper.text()).not.toContain('归入电气件BOM')
    expect(wrapper.get('[aria-label="BOM维护"]').isVisible()).toBe(true)

    await projectTabByText(wrapper, '图档').trigger('click')
    await flushPromises()
    expect(wrapper.get('[aria-label="BOM维护"]').isVisible()).toBe(false)
    expect(wrapper.get('[aria-label="网页端图档预览状态"]').attributes('data-preview-state')).toBe('idle')
    await buttonByText(wrapper, '加载预览').trigger('click')
    await flushPromises()
    expect(wrapper.text()).toContain('该图档还没有生成STP/PDF预览')
    expect(wrapper.find('[aria-label="图档查看与操作"]').exists()).toBe(true)
    const projectSidebar = wrapper.get('[aria-label="项目基本信息与全部项目号"]')
    expect(projectSidebar.find('[aria-label="BOM完整性"]').exists()).toBe(false)
    expect(projectSidebar.find('[aria-label="当前发布包"]').exists()).toBe(false)
    expect(wrapper.get('.pdm-preview-layout').find('[aria-label="图档查看与操作"]').exists()).toBe(true)
    expect(wrapper.get('.pdm-preview-layout').find('[aria-label="BOM完整性"]').exists()).toBe(false)
    expect(wrapper.find('button[aria-label="适合窗口"]').exists()).toBe(false)
    await projectTabByText(wrapper, 'BOM').trigger('click')
    expect(wrapper.get('[aria-label="BOM维护"]').isVisible()).toBe(true)
    expect(wrapper.get('button[role="tab"][aria-selected="true"]').text()).toContain('源数据')
    await buttonByText(wrapper, '消息').trigger('click')
    await flushPromises()
    expect(document.body.querySelector('.pdm-message-drawer')).not.toBeNull()
    expect(document.body.textContent).toContain('全部待办')
    await navigateTo(wrapper, '项目列表')
    await runProjectAction(wrapper, 'open')
    await flushPromises()
    await projectTabByText(wrapper, '发布').trigger('click')
    await flushPromises()
    expect(wrapper.findAll('.pdm-release-stream-card')).toHaveLength(3)
    expect(document.body.textContent).not.toContain('历史组合发布')
    expect(document.body.textContent).not.toContain('审批与生产发包')
    expect(document.body.textContent).not.toContain('将在后续阶段开放')
  })

  it('uses occurrence ids, removes only exact duplicate instances, and marks unregistered references', async () => {
    const wrapper = mount(App, { attachTo: document.body, global: { plugins: [ElementPlus] } })
    await login(wrapper)
    await expandAllDocumentTree(wrapper)

    const tree = wrapper.get('[aria-label="项目设计树"]')
    const partRows = tree.findAll('.pdm-tree-row').filter(row => row.text().includes('REAL-PRT-001') && !row.text().includes('工程图'))
    expect(partRows).toHaveLength(3)
    expect(partRows.every(row => !row.text().includes('未入库'))).toBe(true)
    expect(tree.text()).not.toContain('旧快照重复项')
    expect(tree.text()).toContain('3-1')
    expect(tree.text()).toContain('未入库')
    expect(tree.text()).toContain('真实总装工程图')
    expect(tree.text()).toContain('遗漏工程图')
    const currentDrawing = tree.findAll('.pdm-tree-row').find(row => row.text().includes('真实总装工程图'))
    expect(currentDrawing?.text()).not.toContain('版本关系异常')
    expect(currentDrawing?.classes()).not.toContain('has-version-conflict')

    const relatedDrawing = tree.findAll('.pdm-tree-row').find(row => row.text().includes('遗漏工程图'))
    expect(relatedDrawing?.text()).toContain('未存档')
    expect(relatedDrawing?.text()).not.toContain('未进入快照')
    expect(relatedDrawing?.classes()).not.toContain('has-version-warning')
    expect(tree.text()).not.toContain('正常图档')
    expect(tree.text()).not.toContain('异常图档')

    const rootRow = tree.findAll('.pdm-tree-row').find(row => row.text().includes('真实总装配'))
    expect(rootRow?.text()).not.toContain('结构待更新')
    const stalePart = tree.findAll('.pdm-tree-row').find(row => row.text().includes('STALE-PRT-001'))
    expect(stalePart?.get('.pdm-tree-drawing-version').attributes('title')).toContain('引用版本较旧')
    expect(tree.text()).not.toContain('版本：结构实际 / 最新')

    await partRows[1].trigger('click')
    expect(tree.findAll('.pdm-tree-row.is-selected')).toHaveLength(1)
    expect(tree.get('.pdm-tree-row.is-selected').text()).toContain('REAL-PRT-001')
  })

  it('shows lightweight previews on selection and keeps the interactive preview loaded', async () => {
    const postMessage = vi.fn()
    Object.defineProperty(document, 'visibilityState', { configurable: true, value: 'visible' })
    Object.defineProperty(window, 'chrome', {
      configurable: true,
      value: { webview: { postMessage, addEventListener: vi.fn() } },
    })
    const wrapper = mount(App, { attachTo: document.body, global: { plugins: [ElementPlus] } })
    await login(wrapper)

    const slot = wrapper.get('[aria-label="客户端内嵌eDrawings预览区"]')
    expect(slot.attributes('data-preview-state')).toBe('idle')
    expect(postMessage.mock.calls.filter(([message]) => message.type === 'preview-document')).toHaveLength(0)
    await buttonByText(wrapper, '加载交互预览').trigger('click')
    await flushPromises()
    expect(slot.get('[aria-label="正在加载 eDrawings"]')).toBeTruthy()
    expect(slot.get('.plm-cube-icon__image').attributes('src')).toBe(approvedAppIcon)
    vi.spyOn(slot.element, 'getBoundingClientRect').mockReturnValue({
      x: 260, y: 180, left: 260, top: 180, right: 960, bottom: 700,
      width: 700, height: 520, toJSON: () => ({}),
    })
    window.dispatchEvent(new Event('resize'))
    await new Promise<void>(resolve => window.requestAnimationFrame(() => resolve()))
    await flushPromises()

    expect(postMessage).toHaveBeenCalledWith(expect.objectContaining({
      type: 'preview-host-bounds',
      payload: expect.objectContaining({ left: 260, top: 180, width: 700, height: 520, visible: true }),
    }))
    expect(postMessage).toHaveBeenCalledWith({
      type: 'preview-document',
      payload: {
        documentId: 'doc-root', fileName: 'REAL-ASM-001.SLDASM', revision: 'W2',
        drawingNumber: 'REAL-ASM-001', name: '真实总装配', specification: '', material: '', brand: '', surfaceTreatment: '', status: '工作中',
      },
    })
    expect(wrapper.text()).not.toContain('在客户端内预览')
    const previewToolbar = wrapper.get('.pdm-preview-toolbar')
    expect(previewToolbar.find('.pdm-preview-document-switcher').exists()).toBe(false)
    expect(wrapper.find('.pdm-preview-markup-row #drawing-review-decision-host').exists()).toBe(true)
    expect(previewToolbar.find('.pdm-selected-file').exists()).toBe(false)
    const markupToolbar = wrapper.get('.pdm-preview-markup-row [aria-label="图形批注工具"]')
    expect(markupToolbar.findAll('button').map(button => button.attributes('aria-label'))).toEqual([
      '引线批注', '云线批注', '框选批注', '手绘批注',
    ])
    expect(previewToolbar.findAll('.pdm-preview-command').map(button => button.attributes('aria-label'))).toEqual(['打开最新'])
    expect(wrapper.findAll('.pdm-preview-markup-row .pdm-preview-command').map(button => button.attributes('aria-label'))).toEqual([
      '保存批注',
    ])
    expect(previewToolbar.find('button[aria-label="使用位置"]').exists()).toBe(false)
    expect(previewToolbar.find('button[aria-label="作废图档"]').exists()).toBe(false)
    await markupToolbar.get('button[aria-label="引线批注"]').trigger('click')
    expect(postMessage).toHaveBeenCalledWith({ type: 'preview-host-command', payload: { command: 'markup-text-leader' } })

    window.dispatchEvent(new CustomEvent('pdm-preview-status', { detail: { state: 'ready', fileName: 'REAL-ASM-001.SLDASM' } }))
    await flushPromises()
    expect(slot.attributes('data-preview-state')).toBe('ready')
    expect(wrapper.find('[aria-label="eDrawings快捷操作"]').exists()).toBe(false)

    const previewDocumentCalls = postMessage.mock.calls.filter(([message]) => message.type === 'preview-document').length
    // 图档工具条已按需求去掉“更多”入口，预览保持不重新加载。
    expect(wrapper.find('button[aria-label="更多操作"]').exists()).toBe(false)
    expect(postMessage.mock.calls.filter(([message]) => message.type === 'preview-host-bounds').length).toBeGreaterThan(0)

    const overlay = document.createElement('div')
    overlay.className = 'el-overlay'
    vi.spyOn(overlay, 'getBoundingClientRect').mockReturnValue({
      x: 0, y: 0, left: 0, top: 0, right: window.innerWidth, bottom: window.innerHeight,
      width: window.innerWidth, height: window.innerHeight, toJSON: () => ({}),
    })
    document.body.append(overlay)
    await new Promise(resolve => window.setTimeout(resolve, 20))
    expect(postMessage).toHaveBeenCalledWith({ type: 'preview-host-suspend', payload: undefined })

    overlay.remove()
    await new Promise(resolve => window.setTimeout(resolve, 20))
    expect(postMessage.mock.calls.filter(([message]) => message.type === 'preview-host-bounds').length).toBeGreaterThan(1)
    expect(postMessage.mock.calls.filter(([message]) => message.type === 'preview-document')).toHaveLength(previewDocumentCalls)

    const boundsCallsBeforeProjectBrowser = postMessage.mock.calls.filter(([message]) => message.type === 'preview-host-bounds').length
    await wrapper.get('button[aria-label="浏览项目"]').trigger('click')
    await flushPromises()
    const projectBrowserBackdrop = wrapper.get('.pdm-dialog-backdrop')
    vi.spyOn(projectBrowserBackdrop.element, 'getBoundingClientRect').mockReturnValue({
      x: 0, y: 0, left: 0, top: 0, right: window.innerWidth, bottom: window.innerHeight,
      width: window.innerWidth, height: window.innerHeight, toJSON: () => ({}),
    })
    projectBrowserBackdrop.element.setAttribute('style', 'display: block')
    await new Promise(resolve => window.setTimeout(resolve, 20))
    expect(postMessage).toHaveBeenCalledWith({ type: 'preview-host-suspend', payload: undefined })

    await wrapper.get('button[aria-label="关闭项目浏览"]').trigger('click')
    await new Promise(resolve => window.setTimeout(resolve, 20))
    expect(postMessage.mock.calls.filter(([message]) => message.type === 'preview-host-bounds').length).toBeGreaterThan(boundsCallsBeforeProjectBrowser)

    const previewDocumentCallsBeforeReview = postMessage.mock.calls.filter(([message]) => message.type === 'preview-document').length
    await flushPromises()
    expect(wrapper.find('[aria-label="图纸审核面板"]').exists()).toBe(false)
    window.dispatchEvent(new Event('resize'))
    await new Promise<void>(resolve => window.requestAnimationFrame(() => resolve()))
    await flushPromises()
    expect(postMessage).toHaveBeenCalledWith(expect.objectContaining({
      type: 'preview-host-bounds',
      payload: expect.objectContaining({ left: 260, width: 700, visible: true }),
    }))
    expect(postMessage).toHaveBeenCalledWith(expect.objectContaining({
      type: 'review-overlay-bounds',
      payload: expect.objectContaining({ left: 926, width: 34, height: 520, visible: true }),
    }))
    expect(postMessage).toHaveBeenCalledWith(expect.objectContaining({
      type: 'review-overlay-state',
      payload: expect.objectContaining({ visible: true, selectedDocumentId: 'doc-root' }),
    }))
    expect(postMessage.mock.calls.filter(([message]) => message.type === 'preview-document')).toHaveLength(previewDocumentCallsBeforeReview)

    const reviewMessageListeners = vi.mocked(window.chrome!.webview!.addEventListener).mock.calls
      .filter(([type]) => type === 'message').map(([, listener]) => listener)
    expect(reviewMessageListeners.length).toBeGreaterThan(0)
    const collapseMessage = new MessageEvent('message', { data: { type: 'review-overlay-action', payload: { action: 'collapse', collapsed: false } } })
    for (const listener of reviewMessageListeners) listener(collapseMessage)
    await flushPromises()
    window.dispatchEvent(new Event('resize'))
    await new Promise<void>(resolve => window.requestAnimationFrame(() => resolve()))
    await flushPromises()
    expect(postMessage).toHaveBeenCalledWith(expect.objectContaining({
      type: 'review-overlay-bounds',
      payload: expect.objectContaining({ left: 660, width: 300, height: 520, visible: true }),
    }))
    expect(postMessage).toHaveBeenCalledWith(expect.objectContaining({
      type: 'review-overlay-state',
      payload: expect.objectContaining({ visible: true, collapsed: false }),
    }))

    // 客户端与网页端一致：图档属性行在客户端同样常驻显示。
    expect(wrapper.get('.pdm-preview-properties-bar').find('[aria-label="图档属性"]').exists()).toBe(true)

    // 审核结论栏与网页端同一份 DOM：位于批注工具行内、保存批注右侧，不再使用独立浮层窗口。
    const row = wrapper.get('.pdm-preview-markup-row')
    const decisionHost = row.get('#drawing-review-decision-host')
    expect(decisionHost.element.previousElementSibling?.getAttribute('aria-label')).toBe('保存批注')
    expect(row.find('.pdm-markup-toolbar').exists()).toBe(true)
    expect(postMessage.mock.calls.filter(([message]) => message.type === 'review-annotation-bounds')).toHaveLength(0)

    const collapseAgainMessage = new MessageEvent('message', { data: { type: 'review-overlay-action', payload: { action: 'collapse', collapsed: true } } })
    for (const listener of reviewMessageListeners) listener(collapseAgainMessage)
    await flushPromises()
    await new Promise<void>(resolve => window.requestAnimationFrame(() => resolve()))
    await flushPromises()
    // 收起后仍保留 34px 窄条（客户端不再整块隐藏），结论卡随收起隐藏。
    expect(postMessage).toHaveBeenCalledWith(expect.objectContaining({
      type: 'review-overlay-bounds',
      payload: expect.objectContaining({ left: 926, width: 34, visible: true }),
    }))
    // 结论栏常驻：审核栏收起后结论栏仍在工具条里（只是不可操作）。
    const barAfterCollapse = wrapper.find('#drawing-review-decision-host [aria-label="图纸审核结论"]')
    expect(barAfterCollapse.exists()).toBe(true)
    expect(barAfterCollapse.get('textarea[aria-label="审核意见"]').attributes('disabled')).toBeDefined()

    const drawingFilter = wrapper.findAll('button[role="tab"]').find(button => button.text().includes('2D'))
    expect(drawingFilter).toBeTruthy()
    await drawingFilter!.trigger('click')
    await flushPromises()
    // 切到 2D 不自动打开第一张图纸；用户明确选中后才加载交互预览。
    expect(postMessage.mock.calls.filter(([message]) => message.type === 'preview-document')).toHaveLength(previewDocumentCalls)
    const drawingRow = wrapper.findAll('.pdm-drawing-list .pdm-tree-row').find(row => row.text().includes('REAL-ASM-001'))
    expect(drawingRow).toBeTruthy()
    await drawingRow!.trigger('click')
    await flushPromises()
    expect(postMessage).toHaveBeenCalledWith({
      type: 'preview-document',
      payload: {
        documentId: 'doc-drawing', fileName: 'REAL-ASM-001.SLDDRW', revision: 'W2',
        drawingNumber: 'REAL-ASM-001', name: '真实总装工程图', specification: '', material: '', brand: '', surfaceTreatment: '', status: '工作中',
      },
    })
    expect(postMessage.mock.calls.filter(([message]) => message.type === 'preview-document')).toHaveLength(previewDocumentCalls + 1)
    expect(postMessage).not.toHaveBeenCalledWith({
      type: 'lightweight-preview-request',
      payload: { documentId: 'doc-drawing' },
    })
    window.dispatchEvent(new CustomEvent('pdm-preview-status', { detail: { state: 'ready', fileName: 'REAL-ASM-001.SLDDRW' } }))
    await flushPromises()
    expect(wrapper.find('[aria-label="eDrawings快捷操作"]').exists()).toBe(false)
    expect(wrapper.find('[aria-label="图纸页切换"]').exists()).toBe(false)

    const modelFilter = wrapper.findAll('button[role="tab"]').find(button => button.text().includes('3D'))
    expect(modelFilter).toBeTruthy()
    await modelFilter!.trigger('click')
    await flushPromises()
    expect(postMessage.mock.calls.filter(([message]) => message.type === 'preview-document')).toHaveLength(previewDocumentCalls + 2)
    window.dispatchEvent(new CustomEvent('pdm-preview-status', { detail: { state: 'ready', fileName: 'REAL-ASM-001.SLDASM' } }))
    await flushPromises()
    expect(postMessage.mock.calls.filter(([message]) => message.type === 'preview-document')).toHaveLength(previewDocumentCalls + 2)

    const hideCallsBeforeTabChange = postMessage.mock.calls.filter(([message]) => message.type === 'preview-host-hide').length
    const suspendCallsBeforeTabChange = postMessage.mock.calls.filter(([message]) => message.type === 'preview-host-suspend').length
    const bomTab = wrapper.findAll('.pdm-project-tabs button').find(button => button.text().includes('BOM'))
    expect(bomTab).toBeTruthy()
    await bomTab!.trigger('click')
    // 离开图档页只挂起已加载的预览，不释放 eDrawings 文档。
    expect(postMessage.mock.calls.filter(([message]) => message.type === 'preview-host-suspend').length).toBeGreaterThan(suspendCallsBeforeTabChange)
    expect(postMessage.mock.calls.filter(([message]) => message.type === 'preview-host-hide').length).toBe(hideCallsBeforeTabChange)

    const previewCallsBeforeReturn = postMessage.mock.calls.filter(([message]) => message.type === 'preview-document').length
    await projectTabByText(wrapper, '图档').trigger('click')
    await flushPromises()
    expect(wrapper.get('[aria-label="客户端内嵌eDrawings预览区"]').attributes('data-preview-state')).toBe('ready')
    expect(postMessage.mock.calls.filter(([message]) => message.type === 'preview-document')).toHaveLength(previewCallsBeforeReturn)
  })

  it('shows an actionable fallback when a historical version has no STP/PDF preview', async () => {
    const wrapper = mount(App, { attachTo: document.body, global: { plugins: [ElementPlus] } })
    await login(wrapper)
    await expandAllDocumentTree(wrapper)

    const preview = wrapper.get('[aria-label="网页端图档预览状态"]')
    expect(preview.attributes('data-preview-state')).toBe('idle')
    await buttonByText(wrapper, '加载预览').trigger('click')
    await flushPromises()
    expect(preview.attributes('data-preview-state')).toBe('unavailable')
    expect(preview.text()).toContain('该图档还没有生成STP/PDF预览')
    expect(preview.text()).not.toContain('正在加载 eDrawings')
    expect(wrapper.get('[aria-label="图档属性"]').text()).toContain('物料编码REAL-ASM-001')
    expect(wrapper.get('[aria-label="图档属性"]').text()).toContain('名称真实总装配')

    await buttonByText(wrapper, '查看并下载版本').trigger('click')
    await flushPromises()
    expect(document.body.textContent).toContain('图档历史版本对比')
    expect(document.body.textContent).toContain('下载左版原始文件')

    const partRow = wrapper.get('[aria-label="项目设计树"]').findAll('.pdm-tree-row').find(row => row.text().includes('REAL-PRT-001'))
    expect(partRow).toBeTruthy()
    await partRow!.trigger('click')
    await flushPromises()
    await new Promise(resolve => window.setTimeout(resolve, 20))
    await flushPromises()
    const properties = wrapper.get('[aria-label="图档属性"]')
    expect(properties.text()).toContain('型号10mm')
    expect(properties.text()).toContain('材质Q235B')
    expect(properties.text()).toContain('最近存档')
    expect(properties.text()).toContain('2026/8/12 11:04:05')
    expect(properties.findAll('dt').map(item => item.text())).toEqual(['物料编码', '名称', '型号', '品牌', '材质', '表面处理', '热处理', '图纸版本', '发布版本', '最近存档'])
  })

  it('opens only PLM-controlled document identities in SolidWorks from the entity button and tree menu', async () => {
    const postMessage = vi.fn()
    Object.defineProperty(window, 'chrome', {
      configurable: true,
      value: { webview: { postMessage, addEventListener: vi.fn() } },
    })
    const wrapper = mount(App, { attachTo: document.body, global: { plugins: [ElementPlus] } })
    await login(wrapper)
    window.dispatchEvent(new CustomEvent('pdm-solidworks-capability', { detail: { available: true } }))
    await flushPromises()

    await buttonByText(wrapper, '打开最新').trigger('click')
    expect(postMessage).toHaveBeenCalledWith({
      type: 'open-document',
      payload: expect.objectContaining({
        projectId,
        documentId: 'doc-root',
        fileName: 'REAL-ASM-001.SLDASM',
        mode: 'LatestReadOnly',
      }),
    })

    window.dispatchEvent(new CustomEvent('pdm-solidworks-status', { detail: { state: 'ready', message: '请求已发送' } }))
    await flushPromises()
    await wrapper.get('.pdm-tree-row.is-selected').trigger('contextmenu', { clientX: 320, clientY: 220 })
    expect(document.body.textContent).not.toContain('获取最新版本并编辑')
    expect(document.body.textContent).toContain('只读打开最新受控版')
    expect(document.body.textContent).toContain('获取权限并编辑最新受控版')
    expect(document.body.textContent).toContain('不占用编辑权限')
    await buttonByText(wrapper, '只读打开最近正式发布版').trigger('click')
    expect(postMessage).toHaveBeenCalledWith({
      type: 'open-document',
      payload: expect.objectContaining({ projectId, documentId: 'doc-root', mode: 'LatestReleased' }),
    })

    await wrapper.get('.pdm-tree-row.is-selected').trigger('contextmenu', { clientX: 320, clientY: 220 })
    await buttonByText(wrapper, '查看/对比历史版本').trigger('click')
    await flushPromises()
    await buttonByText(wrapper, '只读打开左版').trigger('click')
    expect(postMessage).toHaveBeenCalledWith({
      type: 'open-document',
      payload: expect.objectContaining({
        projectId,
        documentId: 'doc-root',
        mode: 'SpecificReadOnly',
        versionId: 'version-w1',
      }),
    })
  })

  it('loads real version choices and renders property, reference and BOM differences', async () => {
    const wrapper = mount(App, { attachTo: document.body, global: { plugins: [ElementPlus] } })
    await login(wrapper)
    await wrapper.get('.pdm-tree-row.is-selected').trigger('contextmenu', { clientX: 320, clientY: 220 })
    await buttonByText(wrapper, '查看/对比历史版本').trigger('click')
    await flushPromises()

    expect(document.body.textContent).toContain('W1')
    expect(document.body.textContent).toContain('W2')
    expect(document.body.textContent).toContain('Material')
    expect(document.body.textContent).toContain('ROOT/P-001')
    expect(document.body.textContent).toContain('BOM差异（1）')
    expect(document.body.textContent).toContain('不会覆盖当前文件')
  })

  it('keeps a SolidWorks comparison request until login and real workspace loading finish', async () => {
    const wrapper = mount(App, { attachTo: document.body, global: { plugins: [ElementPlus] } })
    window.dispatchEvent(new CustomEvent('pdm-open-version-compare', { detail: { documentId: 'doc-root', leftVersionId: 'version-w1', rightVersionId: 'version-w2' } }))
    await login(wrapper)
    await flushPromises()

    expect(document.body.textContent).toContain('图档历史版本对比')
    expect(document.body.textContent).toContain('W1')
    expect(document.body.textContent).toContain('W2')
    expect(document.body.textContent).toContain('数量变化')
  }, 10_000)

  it('keeps a SolidWorks project request until login and opens that project on the documents tab', async () => {
    const wrapper = mount(App, { attachTo: document.body, global: { plugins: [ElementPlus] } })
    window.dispatchEvent(new CustomEvent('pdm-open-project', { detail: { projectId, tab: 'documents' } }))

    await openLogin(wrapper)
    await wrapper.get('input[name="username"]').setValue('engineer')
    await wrapper.get('input[name="password"]').setValue('correct-password')
    await wrapper.get('form[aria-label="登录PLM"]').trigger('submit')
    await flushPromises()

    await vi.waitFor(() => expect(wrapper.get('input[aria-label="快速搜索项目"]').attributes('placeholder')).toContain('PRJ-REAL-001'))
    expect(wrapper.get('.pdm-project-tabs .is-active').text()).toContain('图档')
    expect(wrapper.find('[aria-label="项目设计树"]').exists()).toBe(true)
  })
})
