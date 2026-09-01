import { flushPromises, mount } from '@vue/test-utils'
import ElementPlus, { ElMessageBox } from 'element-plus'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import App from '../src/App.vue'

const projectId = '11111111-1111-1111-1111-111111111111'
let materialRequestsUnauthorized = false
let resumeRequestsUnauthorized = false
let drawingReviewsResponse: Array<Record<string, unknown>> = []

function json(value: unknown, status = 200) {
  return new Response(JSON.stringify(value), { status, headers: { 'Content-Type': 'application/json' } })
}

function installApiMock(projectsBeforeDefault: Array<Record<string, unknown>> = []) {
  const projects: Array<Record<string, unknown>> = [...projectsBeforeDefault, { id: projectId, code: 'PRJ-REAL-001', name: '真实装配项目', owner: 'engineer', responsibleUsers: ['engineer'], vaultLocation: 'D:\\PDM\\PRJ-REAL-001', releaseLocation: 'D:\\Release\\PRJ-REAL-001', isActive: true, quantity: 1, serialNumbers: [] }]
  const customers = [{ id: 'customer-1', code: 'C00465', name: '中山比亚迪电子有限公司', isActive: true }]
  let crmSettings = { baseUrl: 'http://10.7.7.188/U9', username: 'pdm', passwordConfigured: true, autoSyncEnabled: false, autoSyncIntervalMinutes: 60, lastSyncAt: null as string | null, lastSyncCount: 0, lastAutoSyncAttemptAt: null as string | null, lastAutoSyncError: null as string | null }
  let u9Settings = { baseUrl: 'http://10.7.7.188/U9', enterpriseCode: '01', organizationCode: '7', userCode: 'pdm', clientId: 'PDM', clientSecretConfigured: true, itemCreatePath: '/webapi/ItemMaster/Create', itemQueryPath: '/webapi/ItemMaster/Query', itemModifyPath: '/webapi/ItemMaster/Modify', itemDeletePath: '/webapi/ItemMaster/Delete', unitCodeMappings: {}, writeEnabled: false }
  const validationRules = { standard: ['drawingNumber', 'name', 'unit', 'specification', 'quantity', 'revision'], nonStandard: ['drawingNumber', 'name', 'unit', 'material', 'quantity', 'revision'], electrical: ['drawingNumber', 'name', 'unit', 'quantity', 'revision'] }
  const editLocks = [{ documentId: 'doc-lock', projectId, projectCode: 'PRJ-REAL-001', projectName: '真实装配项目', drawingNumber: 'LOCK-001', documentName: '长期编辑图档', fileName: 'LOCK-001.SLDPRT', checkedOutBy: 'designer', checkedOutAt: '2026-08-14T00:00:00Z', checkoutMachine: 'DESIGN-WS', lastHeartbeatAt: '2026-08-14T00:03:00Z', leaseExpiresAt: '2026-08-14T00:18:00Z', connectionState: 'Active', attentionLevel: 'Reminder', releaseRequestedBy: null, releaseRequestedAt: null, releaseRequestReason: null, ownedByCurrentUser: false, canRequestRelease: true, canForceRelease: false }]
  const engineerPermissions = ['project.view', 'project.create', 'project.child.create', 'project.staffing.manage', 'project.designer.assign', 'project.content.view', 'document.edit', 'bom.edit', 'release.manage', 'standard-library.view']
  const adminPermissions = [...engineerPermissions, 'material.view', 'material.manage', 'project.delete', 'project.execution.assign', 'approval.decide', 'settings.customer.manage', 'settings.organization.manage', 'settings.folder.manage', 'settings.storage.manage', 'system.role.view', 'system.role.edit', 'audit.view']
  const roleDirectory = {
    permissions: [
      { code: 'project.view', name: '查看负责项目', module: '项目管理', sensitive: false },
      { code: 'project.designer.assign', name: '分配子项目设计人员', module: '项目分工', sensitive: false },
      { code: 'document.edit', name: '登记、签出和存档图档', module: '项目内容', sensitive: true },
      { code: 'system.role.view', name: '查看角色权限', module: '角色权限', sensitive: false },
      { code: 'system.role.edit', name: '修改角色权限', module: '角色权限', sensitive: true },
    ],
    roles: [
      { role: 'Engineer', name: '工程师', description: '承担设计与图档工作。', baseRole: 'Engineer', isSystem: true, isSystemAdministrator: false, permissions: engineerPermissions, userCount: 1 },
      { role: 'Administrator', name: '系统管理员', description: '固定全部权限。', baseRole: 'Administrator', isSystem: true, isSystemAdministrator: true, permissions: adminPermissions, userCount: 1 },
    ],
  }
  vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = String(input)
    if (url.endsWith('/health')) return json({ status: 'ok' })
    if (url.endsWith('/api/approval-tasks/mine')) return json([])
    if (url.endsWith('/api/edit-locks')) return json(editLocks)
    if (url.endsWith('/api/audit?take=200')) return json([])
    if (url.endsWith('/api/auth/login')) {
      const credentials = JSON.parse(String(init?.body)) as { username: string; password: string }
      return credentials.password === 'correct-password'
        ? json({ accessToken: 'test-token', expiresAt: '2099-01-01T00:00:00Z', resumeToken: 'test-resume-token', username: credentials.username, displayName: credentials.username === 'admin' ? '系统管理员' : '真实工程师', role: credentials.username === 'admin' ? 'Administrator' : 'Engineer', permissions: credentials.username === 'admin' ? adminPermissions : engineerPermissions })
        : json({ title: 'Unauthorized' }, 401)
    }
    if (url.endsWith('/api/auth/resume')) return resumeRequestsUnauthorized
      ? json({ title: 'Unauthorized' }, 401)
      : json({ accessToken: 'renewed-token', expiresAt: '2099-01-01T00:00:00Z', resumeToken: 'renewed-resume-token', username: 'engineer', displayName: '真实工程师', role: 'Engineer', permissions: engineerPermissions })
    if (url.endsWith('/api/auth/me')) return json({ username: 'admin', displayName: '系统管理员', nickname: null, gender: 'unspecified', landline: null, mobilePhone: null, email: null })
    if (url.endsWith('/api/password-reset-requests')) return json([])
    if (url.endsWith('/api/materials')) return materialRequestsUnauthorized ? json({ title: 'Unauthorized' }, 401) : json([])
    if (url.includes('/api/material-code/applications')) return json([])
    if (url.endsWith('/api/material-category-rules')) return json([])
    if (url.endsWith('/api/material-sync-tasks')) return json([])
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
        { id: 'doc-root', projectId, drawingNumber: 'REAL-ASM-001', name: '真实总装配', fileName: 'REAL-ASM-001.SLDASM', kind: 0, revision: { display: 'W2' }, checkedOutBy: 'engineer' },
        { id: 'doc-part', projectId, drawingNumber: 'REAL-PRT-001', name: '真实底板', fileName: 'REAL-PRT-001.SLDPRT', kind: 1, revision: { display: 'A' }, checkedOutBy: null },
        { id: 'doc-stale', projectId, drawingNumber: 'STALE-PRT-001', name: '历史版本子件', fileName: 'STALE-PRT-001.SLDPRT', kind: 1, revision: { display: 'W4' }, checkedOutBy: null },
        { id: 'doc-drawing', projectId, drawingNumber: 'REAL-ASM-001', name: '真实总装工程图', fileName: 'REAL-ASM-001.SLDDRW', kind: 2, revision: { display: 'W2' }, checkedOutBy: null },
        { id: 'doc-drawing-missing', projectId, drawingNumber: 'REAL-PRT-001', name: '遗漏工程图', fileName: 'REAL-PRT-001.SLDDRW', kind: 2, revision: { display: 'W1' }, checkedOutBy: null },
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
    if (url.endsWith('/boms/NonStandard')) return json([])
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
    await buttonByText(wrapper, '项目列表').trigger('click')
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
    drawingReviewsResponse = []
    window.sessionStorage.clear()
    window.localStorage.clear()
    Object.defineProperty(window, 'chrome', { configurable: true, value: undefined })
    installApiMock()
  })

  it('applies the saved theme to web buttons and synchronizes it to the Windows client', async () => {
    const postMessage = vi.fn()
    window.localStorage.setItem('pdm_theme', 'c')
    Object.defineProperty(window, 'chrome', {
      configurable: true,
      value: { webview: { postMessage, addEventListener: vi.fn() } },
    })

    const wrapper = mount(App, { attachTo: document.body, global: { plugins: [ElementPlus] } })
    expect(wrapper.get('.pdm-app-shell').classes()).toContain('theme-c')
    expect(document.documentElement.dataset.pdmTheme).toBe('c')
    expect(postMessage).toHaveBeenCalledWith({ type: 'theme-change', payload: { theme: 'c' } })

    wrapper.unmount()
    expect(document.documentElement.dataset.pdmTheme).toBeUndefined()
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

  it('keeps new workflow actions visible to the fixed administrator before a stale session permission list is renewed', async () => {
    const wrapper = mount(App, { attachTo: document.body, global: { plugins: [ElementPlus] } })
    await openLogin(wrapper)
    await wrapper.get('input[name="username"]').setValue('admin')
    await wrapper.get('input[name="password"]').setValue('correct-password')
    await wrapper.get('form[aria-label="登录PLM"]').trigger('submit')
    await flushPromises()

    await projectTabByText(wrapper, '图档').trigger('click')
    await flushPromises()
    await wrapper.get('button[aria-label="图纸审核"]').trigger('click')

    expect(wrapper.find('[aria-label="图纸审核面板"]').exists()).toBe(true)
    expect(buttonByText(wrapper, '选择范围并发起审核').exists()).toBe(true)
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

  it('opens the standard structure placeholder below standard materials', async () => {
    const wrapper = mount(App, { attachTo: document.body, global: { plugins: [ElementPlus] } })
    await login(wrapper, false)

    const navigationLabels = wrapper.findAll('.pdm-sidebar__nav .pdm-nav-item').map(item => item.text().trim())
    expect(navigationLabels.slice(2, 4)).toEqual(['标准物料', '标准结构'])
    expect(navigationLabels).not.toContain('料品管理')

    await buttonByText(wrapper, '标准结构').trigger('click')
    await flushPromises()

    expect(wrapper.get('.pdm-sidebar__nav .pdm-nav-item.is-active').text()).toContain('标准结构')
    const standardStructurePage = wrapper.get('[aria-label="标准结构"]')
    expect(standardStructurePage.text()).toContain('开发中')
    const standardStructureCube = standardStructurePage.get('.pdm-standard-structure-cube')
    expect(standardStructureCube.classes()).toContain('is-axial')
    expect(standardStructureCube.attributes('style')).toContain('--plm-cube-box-size: 132px')
    expect(standardStructureCube.attributes('style')).toContain('--plm-cube-size: 76px')
    expect(standardStructurePage.findAll('.plm-cube-icon__face')).toHaveLength(6)
    expect(window.localStorage.getItem('upton-pdm-active-navigation')).toBe('standard-structure')
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

    await wrapper.get('button[aria-label="图纸审核"]').trigger('click')
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
        modelState: 'Approved', drawingState: 'Approved', effectiveModelVersionId: 'version-w2', effectiveDrawingVersionId: 'drawing-version-w2',
      }],
    }]
    const wrapper = mount(App, { attachTo: document.body, global: { plugins: [ElementPlus] } })
    await login(wrapper)
    window.dispatchEvent(new CustomEvent('pdm-solidworks-capability', { detail: { available: true } }))
    await flushPromises()

    await buttonByText(wrapper, '打开审核版（只读）').trigger('click')
    expect(postMessage).toHaveBeenCalledWith({
      type: 'open-document',
      payload: expect.objectContaining({
        projectId,
        documentId: 'doc-root',
        mode: 'SpecificReadOnly',
        versionId: 'version-w2',
      }),
    })

    window.dispatchEvent(new CustomEvent('pdm-solidworks-status', { detail: { state: 'ready', message: '请求已发送' } }))
    await flushPromises()
    await buttonByText(wrapper, '回写审核标记').trigger('click')
    expect(postMessage).toHaveBeenCalledWith({
      type: 'open-document',
      payload: expect.objectContaining({
        projectId,
        documentId: 'doc-root',
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

  it('uses the project center overview as the default home and keeps the project list separate', async () => {
    const wrapper = mount(App, { attachTo: document.body, global: { plugins: [ElementPlus] } })
    await openLogin(wrapper)
    await wrapper.get('input[name="username"]').setValue('engineer')
    await wrapper.get('input[name="password"]').setValue('correct-password')
    await wrapper.get('form[aria-label="登录PLM"]').trigger('submit')
    await flushPromises()

    expect(wrapper.find('.pdm-project-workspace').exists()).toBe(true)
    expect(wrapper.findAll('.pdm-sidebar__nav .pdm-nav-item').slice(0, 2).map(item => item.text().trim())).toEqual(['项目中心', '项目列表'])
    expect(wrapper.get('.pdm-sidebar__nav .pdm-nav-item.is-active').text()).toContain('项目中心')
    await projectTabByText(wrapper, 'BOM').trigger('click')
    await flushPromises()

    wrapper.unmount()
    window.sessionStorage.clear()
    const restoredProjectPage = mount(App, { attachTo: document.body, global: { plugins: [ElementPlus] } })
    await flushPromises()
    expect(restoredProjectPage.find('.pdm-project-workspace').exists()).toBe(true)
    expect(restoredProjectPage.get('.pdm-project-tabs button.is-active').text()).toBe('概览')

    await buttonByText(restoredProjectPage, '项目列表').trigger('click')
    expect(restoredProjectPage.get('[aria-label="项目中心"]').text()).toContain('PRJ-REAL-001')
    expect(restoredProjectPage.get('.pdm-sidebar__nav .pdm-nav-item.is-active').text()).toContain('项目列表')
    expect(window.localStorage.getItem('upton-pdm-active-navigation')).toBe('projects')

    restoredProjectPage.unmount()
    const restoredHome = mount(App, { attachTo: document.body, global: { plugins: [ElementPlus] } })
    await flushPromises()
    expect(restoredHome.text()).toContain('PRJ-REAL-001')
    expect(restoredHome.get('.pdm-sidebar__nav .pdm-nav-item.is-active').text()).toContain('项目中心')
    expect(restoredHome.find('.pdm-project-workspace').exists()).toBe(true)
    expect(restoredHome.get('.pdm-project-tabs button.is-active').text()).toBe('概览')
    expect(JSON.parse(window.localStorage.getItem('upton-pdm-project-center') ?? '{}')).toMatchObject({ projectId, tab: 'overview', username: 'engineer' })
    expect(window.localStorage.getItem('upton-pdm-active-navigation')).toBe('project-center')

    restoredHome.unmount()
    const restored = mount(App, { attachTo: document.body, global: { plugins: [ElementPlus] } })
    await flushPromises()
    expect(restored.find('.pdm-project-workspace').exists()).toBe(true)
    expect(restored.get('.pdm-project-tabs button.is-active').text()).toBe('概览')
    restored.unmount()
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

  it('opens the most recently used project overview after Windows client login', async () => {
    const fallbackProject = { id: 'project-fallback', code: 'PRJ-FALLBACK-001', name: '回退项目', owner: 'engineer', responsibleUsers: ['engineer'], vaultLocation: 'D:\\PDM\\PRJ-FALLBACK-001', releaseLocation: 'D:\\Release\\PRJ-FALLBACK-001', isActive: true, quantity: 1, serialNumbers: [] }
    installApiMock([fallbackProject])
    window.localStorage.setItem('upton-pdm-project-center', JSON.stringify({ projectId, tab: 'bom', username: 'engineer', companyId: '' }))
    window.localStorage.setItem('upton-pdm-active-navigation', 'admin')
    Object.defineProperty(window, 'chrome', {
      configurable: true,
      value: { webview: { postMessage: vi.fn(), addEventListener: vi.fn() } },
    })

    const wrapper = mount(App, { attachTo: document.body, global: { plugins: [ElementPlus] } })
    try {
      await openLogin(wrapper)
      await wrapper.get('input[name="username"]').setValue('engineer')
      await wrapper.get('input[name="password"]').setValue('correct-password')
      await wrapper.get('form[aria-label="登录PLM"]').trigger('submit')
      await flushPromises()

      expect(wrapper.find('.pdm-project-workspace').exists()).toBe(true)
      expect(wrapper.text()).toContain('PRJ-REAL-001')
      expect(wrapper.text()).not.toContain('PRJ-FALLBACK-001')
      expect(wrapper.get('.pdm-project-tabs button.is-active').text()).toBe('概览')
      expect(wrapper.get('.pdm-sidebar__nav .pdm-nav-item.is-active').text()).toContain('项目中心')
      expect(JSON.parse(window.localStorage.getItem('upton-pdm-project-center') ?? '{}')).toMatchObject({ projectId, tab: 'overview', username: 'engineer' })
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
    expect(window.localStorage.getItem('upton-pdm-project-center')).toContain(projectId)
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

  it('moves Windows client startup and workspace settings under system management', async () => {
    const postMessage = vi.fn()
    Object.defineProperty(window, 'chrome', {
      configurable: true,
      value: { webview: { postMessage, addEventListener: vi.fn() } },
    })
    const wrapper = mount(App, { attachTo: document.body, global: { plugins: [ElementPlus] } })
    await login(wrapper, false)

    expect(wrapper.findAll('.pdm-sidebar__footer button').map(button => button.text())).toEqual(['系统管理'])
    await buttonByText(wrapper, '系统管理').trigger('click')
    expect(buttonByText(wrapper, '客户端设置')).toBeDefined()
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
    expect((wrapper.get('input[aria-label="PLM受控工作区"]').element as HTMLInputElement).value).toContain('UPTON PDM\\Workspace')
    window.dispatchEvent(new CustomEvent('pdm-workspace-maintenance', {
      detail: { available: true, workingFiles: 41, workingBytes: 2_048, snapshotFiles: 82, snapshotBytes: 3_145_728, recoveryFiles: 0, recoveryBytes: 0 },
    }))
    await flushPromises()
    expect(wrapper.get('[aria-label="工作区用量"]').text()).toContain('项目工作文件41 个 · 2.0 KB不自动清理')
    expect(wrapper.get('[aria-label="工作区用量"]').text()).toContain('可重建只读缓存82 个 · 3.0 MB可安全清理')
    const confirmClean = vi.spyOn(ElMessageBox, 'confirm').mockResolvedValue('confirm' as never)
    await buttonByText(wrapper, '清理只读缓存').trigger('click')
    await flushPromises()
    expect(confirmClean).toHaveBeenCalled()
    expect(postMessage).toHaveBeenCalledWith({ type: 'workspace-cache-clean', payload: undefined })
    confirmClean.mockRestore()
    await buttonByText(wrapper, '已开启').trigger('click')
    expect(postMessage).toHaveBeenCalledWith({ type: 'desktop-settings-save', payload: { startWithWindows: false } })

    window.dispatchEvent(new CustomEvent('pdm-workspace-folder-selected', { detail: { workspaceRoot: 'D:\\PDM-Cache' } }))
    await flushPromises()
    expect((wrapper.get('input[aria-label="PLM受控工作区"]').element as HTMLInputElement).value).toBe('D:\\PDM-Cache')

    await buttonByText(wrapper, '保存工作区').trigger('click')
    expect(postMessage).toHaveBeenCalledWith({ type: 'desktop-settings-save', payload: { workspaceRoot: 'D:\\PDM-Cache' } })
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
    expect(wrapper.get('button[aria-label="进入项目图档"]').text()).toContain('项目图档')
    expect(wrapper.get('button[aria-label="进入BOM数据"]').text()).toContain('BOM数据')
    await projectTabByText(wrapper, '文件').trigger('click')
    await flushPromises()
    expect(wrapper.text()).toContain('项目文件夹')
    expect(wrapper.get('.pdm-project-workspace').classes()).not.toContain('is-document-view')
    await projectTabByText(wrapper, '图档').trigger('click')
    await flushPromises()
    expect(wrapper.get('.pdm-project-workspace').classes()).not.toContain('is-document-view')
    expect(wrapper.text()).toContain('REAL-ASM-001')
    expect(wrapper.get('[aria-label="工作版本 W2"]').text()).toBe('W2')
    expect(wrapper.text()).toContain('可编辑')
    expect(wrapper.text()).not.toContain('engineer编辑')
    const projectSidebar = wrapper.get('[aria-label="项目基本信息与全部项目号"]')
    expect(projectSidebar.find('[aria-label="BOM完整性"]').exists()).toBe(false)
    expect(projectSidebar.find('[aria-label="当前发布包"]').exists()).toBe(false)

    await projectTabByText(wrapper, 'BOM').trigger('click')
    await flushPromises()
    expect(wrapper.find('[aria-label="BOM维护"]').exists()).toBe(true)
    expect(wrapper.find('[aria-label="BOM维护"] h2').exists()).toBe(false)
    expect(wrapper.text()).toContain('标准件BOM（1）')

    await projectTabByText(wrapper, '发布').trigger('click')
    await flushPromises()
    expect(wrapper.text()).toContain('RP-REAL-001')
    expect(wrapper.text()).not.toContain('PRJ-2026-018')
  })

  it('shows active edit permissions in my tasks', async () => {
    const wrapper = mount(App, { attachTo: document.body, global: { plugins: [ElementPlus] } })
    await login(wrapper, false)

    await buttonByText(wrapper, '我的待办').trigger('click')
    await flushPromises()

    const taskCenter = wrapper.get('[aria-label="我的待办"]')
    expect(taskCenter.text()).toContain('LOCK-001')
    expect(taskCenter.text()).toContain('DESIGN-WS')
    expect(taskCenter.text()).toContain('请及时存档')
    expect(taskCenter.text()).toContain('催办／申请释放')
  })

  it('keeps project navigation usable while the new workspace feeds are rolling out', async () => {
    const existingFetch = vi.mocked(fetch)
    vi.stubGlobal('fetch', vi.fn((input: RequestInfo | URL, init?: RequestInit) => {
      const url = String(input)
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
    await projectTabByText(wrapper, '版本').trigger('click')
    await flushPromises()
    expect(wrapper.text()).toContain('当前项目还没有版本记录')
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

    await buttonByText(wrapper, '项目列表').trigger('click')

    await buttonByText(wrapper, '创建主项目').trigger('click')
    expect(wrapper.getComponent({ name: 'ElSelect' }).props('filterable')).toBe(true)
    expect(wrapper.find('input[name="customerCode"]').exists()).toBe(false)
    expect(wrapper.find('input[name="projectOwner"]').exists()).toBe(false)
    expect(wrapper.find('input[name="vaultLocation"]').exists()).toBe(false)
    await buttonByText(wrapper, '取消').trigger('click')

    await buttonByText(wrapper, '系统管理').trigger('click')
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

    await buttonByText(wrapper, '项目列表').trigger('click')
    const maintainButton = wrapper.findAll('button').find(button => button.text().trim() === '维护')
    expect(maintainButton).toBeUndefined()

    await buttonByText(wrapper, '系统管理').trigger('click')
    await buttonByText(wrapper, '用户设置').trigger('click')
    expect(wrapper.find('.pdm-pagebar').exists()).toBe(false)
    await buttonByText(wrapper, '角色权限').trigger('click')
    expect(wrapper.get('[aria-label="角色权限设置"]').text()).toContain('工程师')
    const engineerRoleRow = wrapper.findAll('.el-table__row').find(row => row.text().includes('Engineer'))
    expect(engineerRoleRow).toBeDefined()
    await engineerRoleRow!.findAll('button').find(button => button.text().trim() === '基础权限')!.trigger('click')
    expect(wrapper.text()).toContain('分配子项目设计人员')
    await buttonByText(wrapper, '取消').trigger('click')
    await engineerRoleRow!.findAll('button').find(button => button.text().trim() === '单据权限')!.trigger('click')
    const documentPermission = wrapper.findAll('.pdm-permission-card').find(item => item.text().includes('document.edit'))
    expect(documentPermission).toBeDefined()
    await documentPermission!.get('input[type="checkbox"]').setValue(false)
    await buttonByText(wrapper, '保存并立即生效').trigger('click')
    await flushPromises()
    expect(vi.mocked(fetch)).toHaveBeenCalledWith(expect.stringMatching(/\/api\/role-permissions\/Engineer$/), expect.objectContaining({ method: 'PUT' }))

    await buttonByText(wrapper, '编号与存储').trigger('click')
    expect(wrapper.find('.pdm-pagebar').exists()).toBe(false)
    expect(wrapper.get('[aria-label="系统设置"]').text()).toContain('根目录\\项目号')
    await wrapper.get('.el-tabs__item#tab-checkout-policy').trigger('click')
    await flushPromises()
    expect(wrapper.get('[aria-label="系统设置"]').text()).toContain('建议180秒')
    expect(wrapper.get('[aria-label="系统设置"]').text()).toContain('建议48小时；旧会话将禁止提交')
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

  it('filters real 3D and 2D documents and switches the related preview object', async () => {
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
    expect(wrapper.get('[aria-label="图档预览"]').text()).toContain('2D工程图')
    expect(wrapper.get('[aria-label="图档预览"]').text()).toContain('关联模型')

    const relatedModel = wrapper.findAll('.pdm-related-documents button').find(button => button.text().includes('REAL-ASM-001'))
    expect(relatedModel).toBeTruthy()
    await relatedModel!.trigger('click')
    await flushPromises()
    expect(wrapper.get('[aria-label="图档预览"]').text()).toContain('3D模型')
  })

  it('switches the workbench and document pages and makes navigation buttons respond', async () => {
    const wrapper = mount(App, { attachTo: document.body, global: { plugins: [ElementPlus] } })
    await login(wrapper)

    await projectTabByText(wrapper, '概览').trigger('click')
    expect(wrapper.get('[aria-label="工作台主页面"]').text()).toContain('项目图档')

    await wrapper.get('button[aria-label="进入项目图档"]').trigger('click')
    expect(wrapper.find('[aria-label="项目设计树"]').exists()).toBe(true)

    await projectTabByText(wrapper, 'BOM').trigger('click')
    expect(wrapper.get('button[role="tab"][aria-selected="true"]').text()).toContain('多级总览')
    expect(wrapper.get('[aria-label="BOM多级总览"]').text()).toContain('本级BOM料号')
    expect(wrapper.get('[aria-label="BOM多级总览"]').text()).toContain('上级BOM料号')
    await wrapper.findAll('button[role="tab"]').find(button => button.text().includes('源数据'))!.trigger('click')
    expect(wrapper.text()).toContain('归入标准件BOM')
    expect(wrapper.text()).toContain('归入非标件BOM')
    expect(wrapper.text()).not.toContain('归入电气BOM')
    expect(wrapper.get('[aria-label="BOM维护"]').isVisible()).toBe(true)

    await projectTabByText(wrapper, '图档').trigger('click')
    await flushPromises()
    expect(wrapper.get('[aria-label="BOM维护"]').isVisible()).toBe(false)
    expect(wrapper.get('[aria-label="网页端图档预览状态"]').attributes('data-preview-state')).toBe('idle')
    await buttonByText(wrapper, '加载预览').trigger('click')
    await flushPromises()
    expect(wrapper.text()).toContain('该历史版本尚未生成STP/PDF预览')
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
    expect(wrapper.get('[aria-label="我的待办"]').text()).toContain('全部待办')
    await buttonByText(wrapper, '项目列表').trigger('click')
    await runProjectAction(wrapper, 'open')
    await flushPromises()
    await projectTabByText(wrapper, '发布').trigger('click')
    await flushPromises()
    expect(document.body.textContent).toContain('RP-REAL-001')
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
    const recoveredOccurrence = partRows.find(row => row.text().includes('A / A'))
    expect(recoveredOccurrence?.text()).toContain('A / A')
    expect(recoveredOccurrence?.text()).not.toContain('未入库')
    expect(tree.text()).not.toContain('旧快照重复项')
    expect(tree.text()).toContain('3-1')
    expect(tree.text()).toContain('未入库')
    expect(tree.text()).toContain('真实总装工程图')
    expect(tree.text()).toContain('遗漏工程图')
    const currentDrawing = tree.findAll('.pdm-tree-row').find(row => row.text().includes('真实总装工程图'))
    expect(currentDrawing?.text()).toContain('W2 / W2')
    expect(currentDrawing?.text()).not.toContain('版本关系异常')
    expect(currentDrawing?.classes()).not.toContain('has-version-conflict')

    const relatedDrawing = tree.findAll('.pdm-tree-row').find(row => row.text().includes('遗漏工程图'))
    expect(relatedDrawing?.text()).toContain('W1 / W1')
    expect(relatedDrawing?.text()).not.toContain('未进入快照')
    expect(relatedDrawing?.classes()).not.toContain('has-version-warning')
    expect(tree.text()).not.toContain('正常图档')
    expect(tree.text()).not.toContain('异常图档')

    const rootRow = tree.findAll('.pdm-tree-row').find(row => row.text().includes('真实总装配'))
    expect(rootRow?.text()).toContain('W2 / W2')
    expect(rootRow?.text()).not.toContain('结构待更新')
    const stalePart = tree.findAll('.pdm-tree-row').find(row => row.text().includes('STALE-PRT-001'))
    expect(stalePart?.text()).toContain('W3 / W4')
    expect(stalePart?.text()).toContain('结构待更新')
    expect(tree.text()).not.toContain('版本：结构实际 / 最新')

    await partRows[1].trigger('click')
    expect(tree.findAll('.pdm-tree-row.is-selected')).toHaveLength(1)
    expect(tree.get('.pdm-tree-row.is-selected').text()).toContain('REAL-PRT-001')
  })

  it('loads the first preview on demand and then keeps automatic preview within the drawings tab', async () => {
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
    await buttonByText(wrapper, '加载预览').trigger('click')
    await flushPromises()
    expect(slot.get('[aria-label="正在加载 eDrawings"]')).toBeTruthy()
    expect(slot.get('.plm-cube-icon').classes()).toContain('is-axial')
    expect(slot.findAll('.plm-cube-icon__face')).toHaveLength(6)
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
    expect(previewToolbar.get('.pdm-preview-document-name').text()).toBe('真实总装配')
    expect(previewToolbar.find('.pdm-selected-file').exists()).toBe(false)
    const markupToolbar = previewToolbar.get('[aria-label="图形批注工具"]')
    expect(markupToolbar.findAll('button')).toHaveLength(4)
    await markupToolbar.get('button[aria-label="引线批注"]').trigger('click')
    expect(postMessage).toHaveBeenCalledWith({ type: 'preview-host-command', payload: { command: 'markup-text-leader' } })

    window.dispatchEvent(new CustomEvent('pdm-preview-status', { detail: { state: 'ready', fileName: 'REAL-ASM-001.SLDASM' } }))
    await flushPromises()
    expect(slot.attributes('data-preview-state')).toBe('ready')
    expect(wrapper.find('[aria-label="eDrawings快捷操作"]').exists()).toBe(false)

    const previewDocumentCalls = postMessage.mock.calls.filter(([message]) => message.type === 'preview-document').length
    await wrapper.get('button[aria-label="更多操作"]').trigger('click')
    await flushPromises()
    expect(document.body.textContent).toContain('图档历史版本对比')
    expect(postMessage).toHaveBeenCalledWith({ type: 'preview-host-suspend', payload: undefined })

    wrapper.getComponent({ name: 'ElDrawer' }).vm.$emit('update:modelValue', false)
    await flushPromises()
    await new Promise(resolve => window.setTimeout(resolve, 400))
    expect(postMessage.mock.calls.filter(([message]) => message.type === 'preview-host-bounds').length).toBeGreaterThan(1)

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
    await wrapper.get('button[aria-label="图纸审核"]').trigger('click')
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
      payload: expect.objectContaining({ left: 600, width: 360, height: 520, visible: true }),
    }))
    expect(postMessage).toHaveBeenCalledWith(expect.objectContaining({
      type: 'review-overlay-state',
      payload: expect.objectContaining({ visible: true, selectedDocumentId: 'doc-root' }),
    }))
    expect(postMessage.mock.calls.filter(([message]) => message.type === 'preview-document')).toHaveLength(previewDocumentCallsBeforeReview)

    const reviewMessageListener = vi.mocked(window.chrome!.webview!.addEventListener).mock.calls.find(([type]) => type === 'message')?.[1]
    expect(reviewMessageListener).toBeTruthy()
    reviewMessageListener!(new MessageEvent('message', { data: { type: 'review-overlay-action', payload: { action: 'close' } } }))
    await flushPromises()
    expect(postMessage).toHaveBeenCalledWith(expect.objectContaining({
      type: 'review-overlay-state',
      payload: expect.objectContaining({ visible: false }),
    }))

    const drawingFilter = wrapper.findAll('button[role="tab"]').find(button => button.text().includes('2D'))
    expect(drawingFilter).toBeTruthy()
    await drawingFilter!.trigger('click')
    await flushPromises()
    expect(postMessage).toHaveBeenCalledWith({ type: 'preview-host-hide', payload: undefined })
    expect(postMessage).toHaveBeenCalledWith({
      type: 'preview-document',
      payload: {
        documentId: 'doc-drawing', fileName: 'REAL-ASM-001.SLDDRW', revision: 'W2',
        drawingNumber: 'REAL-ASM-001', name: '真实总装工程图', specification: '', material: '', brand: '', surfaceTreatment: '', status: '工作中',
      },
    })
    window.dispatchEvent(new CustomEvent('pdm-preview-status', { detail: { state: 'ready', fileName: 'REAL-ASM-001.SLDDRW' } }))
    await flushPromises()
    expect(wrapper.find('[aria-label="eDrawings快捷操作"]').exists()).toBe(false)
    expect(wrapper.find('[aria-label="图纸页切换"]').exists()).toBe(false)

    await wrapper.get('.pdm-related-documents button').trigger('click')
    await flushPromises()
    expect(postMessage.mock.calls.filter(([message]) => message.type === 'preview-document')).toHaveLength(3)

    const hideCallsBeforeTabChange = postMessage.mock.calls.filter(([message]) => message.type === 'preview-host-hide').length
    const bomTab = wrapper.findAll('.pdm-project-tabs button').find(button => button.text().includes('BOM'))
    expect(bomTab).toBeTruthy()
    await bomTab!.trigger('click')
    expect(postMessage.mock.calls.filter(([message]) => message.type === 'preview-host-hide').length).toBeGreaterThan(hideCallsBeforeTabChange)

    const previewCallsBeforeReturn = postMessage.mock.calls.filter(([message]) => message.type === 'preview-document').length
    await projectTabByText(wrapper, '图档').trigger('click')
    await flushPromises()
    expect(wrapper.get('[aria-label="客户端内嵌eDrawings预览区"]').attributes('data-preview-state')).toBe('idle')
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
    expect(preview.text()).toContain('该历史版本尚未生成STP/PDF预览')
    expect(preview.text()).not.toContain('正在加载 eDrawings')
    expect(preview.get('[aria-label="图档属性"]').text()).toContain('物料/图号REAL-ASM-001')
    expect(preview.get('[aria-label="图档属性"]').text()).toContain('名称真实总装配')

    await buttonByText(wrapper, '查看并下载版本').trigger('click')
    await flushPromises()
    expect(document.body.textContent).toContain('图档历史版本对比')
    expect(document.body.textContent).toContain('下载左侧')

    const partRow = wrapper.get('[aria-label="项目设计树"]').findAll('.pdm-tree-row').find(row => row.text().includes('REAL-PRT-001'))
    expect(partRow).toBeTruthy()
    await partRow!.trigger('click')
    await flushPromises()
    await new Promise(resolve => window.setTimeout(resolve, 20))
    await flushPromises()
    expect(wrapper.get('[aria-label="图档属性"]').text()).toContain('规格/型号10mm')
    expect(wrapper.get('[aria-label="图档属性"]').text()).toContain('材质Q235B')
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
    await buttonByText(wrapper, '打开最新正式发布版（只读）').trigger('click')
    expect(postMessage).toHaveBeenCalledWith({
      type: 'open-document',
      payload: expect.objectContaining({ projectId, documentId: 'doc-root', mode: 'LatestReleased' }),
    })

    await projectTabByText(wrapper, '版本').trigger('click')
    await flushPromises()
    await buttonByText(wrapper, '查看与对比').trigger('click')
    await flushPromises()
    await buttonByText(wrapper, 'SolidWorks只读打开左侧').trigger('click')
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
    await projectTabByText(wrapper, '版本').trigger('click')
    await flushPromises()
    await buttonByText(wrapper, '查看与对比').trigger('click')
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
  })

  it('keeps a SolidWorks project request until login and opens that project on the documents tab', async () => {
    const wrapper = mount(App, { attachTo: document.body, global: { plugins: [ElementPlus] } })
    window.dispatchEvent(new CustomEvent('pdm-open-project', { detail: { projectId, tab: 'documents' } }))

    await openLogin(wrapper)
    await wrapper.get('input[name="username"]').setValue('engineer')
    await wrapper.get('input[name="password"]').setValue('correct-password')
    await wrapper.get('form[aria-label="登录PLM"]').trigger('submit')
    await flushPromises()

    expect(wrapper.get('button[aria-label="浏览项目"]').text()).toContain('PRJ-REAL-001')
    expect(wrapper.get('.pdm-project-tabs .is-active').text()).toContain('图档')
    expect(wrapper.find('[aria-label="项目设计树"]').exists()).toBe(true)
  })
})
