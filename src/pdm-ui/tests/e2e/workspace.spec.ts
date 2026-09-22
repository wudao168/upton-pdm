import { expect, test, type Page } from '@playwright/test'
import { tmpdir } from 'node:os'
import { join } from 'node:path'

const projectId = '11111111-1111-1111-1111-111111111111'
let materialCodeApplications: Array<Record<string, unknown>> = []

async function enterProject(page: Page) {
  const projectNavigation = page.getByRole('navigation', { name: '项目功能' })
  const projectEntry = page.locator('button.pdm-project-code-link').first()
  await expect.poll(async () => await projectNavigation.isVisible() || await projectEntry.isVisible()).toBe(true)
  if (await projectNavigation.isVisible()) return
  await projectEntry.evaluate(button => (button as HTMLButtonElement).click())
  await expect(projectNavigation).toBeVisible()
}

const versions = [
  { id: 'version-w1', documentId: 'doc-root', revision: { display: 'W1' }, status: 0, fileLength: 1024, sha256: 'A'.repeat(64), createdBy: 'engineer', createdAt: '2026-08-10T01:00:00Z', changeNote: '首次存档' },
  { id: 'version-w2', documentId: 'doc-root', revision: { display: 'W2' }, status: 0, fileLength: 2048, sha256: 'B'.repeat(64), createdBy: 'engineer', createdAt: '2026-08-11T01:00:00Z', changeNote: '完善结构' },
]
const organizationDirectory = {
  organizations: [
    { id: 'org-ks', name: '昆山阿普顿自动化系统有限公司', projectCompanyCode: '7', modelCompanyCode: 'AK', crmCompanyName: '昆山阿普顿自动化系统有限公司', isActive: true, currentProjectSequence: 1, currentSerialSequence: 1 },
    { id: 'org-gz', name: '广州阿普顿自动化系统有限公司', projectCompanyCode: '3', modelCompanyCode: 'AG', crmCompanyName: '广州阿普顿自动化系统有限公司', isActive: true, currentProjectSequence: 1, currentSerialSequence: 1 },
  ],
  units: [
    { id: 'ks-division', organizationId: 'org-ks', code: 'KS-AUTO', name: '昆山自动化事业部', kind: 'BusinessDivision', isActive: true, sortOrder: 1 },
    { id: 'ks-department', organizationId: 'org-ks', parentUnitId: 'ks-division', code: 'KS-AUTO-DESIGN', name: '昆山设计部', kind: 'Department', isActive: true, sortOrder: 1 },
    { id: 'ks-other', organizationId: 'org-ks', code: 'KS-OTHER', name: '昆山其他部门', kind: 'BusinessDivision', isActive: true, sortOrder: 2 },
    { id: 'gz-division', organizationId: 'org-gz', code: 'GZ-AUTO', name: '广州自动化事业部', kind: 'BusinessDivision', isActive: true, sortOrder: 1 },
  ],
  memberships: [
    { unitId: 'ks-department', username: 'engineer', isPrimary: true },
    { unitId: 'gz-division', username: 'gz-user', isPrimary: true },
  ],
  managers: [{ unitId: 'ks-division', primaryManager: 'engineer', collaborativeManagers: [] }],
  users: [
    { username: 'admin', displayName: '系统管理员', role: 'Administrator', isActive: true },
    { username: 'engineer', displayName: '真实工程师', role: 'Engineer', isActive: true },
    { username: 'gz-user', displayName: '广州设计员', role: 'Engineer', isActive: true },
    { username: 'new-user', displayName: '待分配人员', role: 'Engineer', isActive: true },
  ],
}

const referenceChildren = Array.from({ length: 40 }, (_, index) => ({
  nodeId: `node-part-${index + 1}`,
  documentId: index === 0 ? 'doc-part' : null,
  instancePath: `REAL-ASM-001/REAL-PRT-${String(index + 1).padStart(3, '0')}`,
  fileName: `REAL-PRT-${String(index + 1).padStart(3, '0')}.SLDPRT`,
  displayName: `真实零件 ${index + 1}`,
  kind: 1,
  configuration: '默认',
  quantity: index + 1,
  status: 0,
  revision: null,
  checkedOutBy: null,
  children: [],
}))

test.beforeEach(async ({ page }) => {
  materialCodeApplications = []
  let currentUsername = 'engineer'
  await page.route(/^http:\/\/127\.0\.0\.1:(?:5080|5173|519[3-5])\/(?:api(?:\/.*)?|health)(?:\?.*)?$/, async (route) => {
    const path = new URL(route.request().url()).pathname
    const fulfill = (body: unknown, status = 200) => route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(body) })
    if (path === '/health') return fulfill({ status: 'ok' })
    if (path === '/api/auth/login') {
      const credentials = route.request().postDataJSON() as { username?: string }
      const administrator = credentials.username === 'admin'
      currentUsername = administrator ? 'admin' : 'engineer'
      return fulfill({ accessToken: 'e2e-token', expiresAt: '2099-01-01T00:00:00Z', resumeToken: 'e2e-resume-token', username: currentUsername, displayName: administrator ? '系统管理员' : '真实工程师', role: administrator ? 'Administrator' : 'Engineer', permissions: administrator ? ['project.view', 'project.create', 'project.child.create', 'project.content.view', 'project.content.reset', 'document.edit', 'document.recycle', 'bom.edit', 'release.manage', 'material.view', 'material.manage', 'settings.customer.manage', 'settings.organization.manage', 'settings.folder.manage', 'settings.storage.manage', 'system.role.view', 'system.role.edit', 'audit.view'] : ['project.view', 'project.create', 'project.child.create', 'project.content.view', 'document.edit', 'bom.edit', 'bom.mechanical.edit', 'bom.electrical.edit', 'material.view', 'release.manage'], primaryCompanyId: 'org-ks', activeCompanyId: 'org-ks', activeCompanyName: '昆山阿普顿自动化系统有限公司', crossCompanyView: administrator, accessibleCompanies: administrator ? [{ id: 'org-ks', name: '昆山阿普顿自动化系统有限公司', code: '7' }, { id: 'org-gz', name: '广州阿普顿自动化系统有限公司', code: '3' }] : [{ id: 'org-ks', name: '昆山阿普顿自动化系统有限公司', code: '7' }] })
    }
    if (path === '/api/auth/me') return fulfill({ username: currentUsername, displayName: currentUsername === 'admin' ? '系统管理员' : '真实工程师', nickname: null, gender: 'unspecified', landline: null, mobilePhone: null, email: null })
    if (path === '/api/password-reset-requests') return fulfill([])
    if (path === '/api/approval-tasks/mine') return fulfill([])
    if (path === '/api/validation-plan-approval-tasks/mine') return fulfill([])
    if (path === '/api/notifications/mine') return fulfill([])
    if (path === '/api/bom-validation-rules') return fulfill({ standard: ['drawingNumber', 'name', 'unit', 'specification', 'quantity', 'revision'], nonStandard: ['drawingNumber', 'name', 'unit', 'material', 'quantity', 'revision'], electrical: ['drawingNumber', 'name', 'unit', 'quantity', 'revision'] })
    if (path === '/api/material-code/applications') return fulfill(materialCodeApplications)
    if (path === '/api/materials/pending-approval') return fulfill([])
    if (path === '/api/material-sync-tasks') return fulfill([])
    if (path === '/api/material-sync-batches') return fulfill([])
    if (path === '/api/materials') {
      const query = new URL(route.request().url()).searchParams.get('query') ?? ''
      return fulfill(query.toLocaleLowerCase().includes('ph602') ? [
        { id: 'material-ph602-a', materialCode: '01020000601', name: 'PH602候选A', kind: 'Standard', supplyMode: 'Purchase', unitCode: '001', specification: 'PH602', brand: 'AIRTAC', approvalStatus: 'Approved', syncStatus: 'Succeeded', createdBy: 'admin', createdAt: '2026-09-01T01:00:00Z', updatedBy: 'admin', updatedAt: '2026-09-03T01:00:00Z', rowVersion: 1, isArchived: false },
        { id: 'material-ph602-b', materialCode: '01020000602', name: 'PH602候选B', kind: 'Standard', supplyMode: 'Purchase', unitCode: '001', specification: 'PH602', brand: 'FESTO', approvalStatus: 'Approved', syncStatus: 'Succeeded', createdBy: 'admin', createdAt: '2026-09-01T01:00:00Z', updatedBy: 'admin', updatedAt: '2026-09-03T01:00:00Z', rowVersion: 1, isArchived: false },
      ] : [])
    }
    if (path === '/api/materials/page') {
      const includeArchived = new URL(route.request().url()).searchParams.get('includeArchived') === 'true'
      const items = includeArchived ? [{
        id: 'material-archived', materialCode: '01021000001', name: '测试停用料品', kind: 'Standard', supplyMode: 'Purchase', unitCode: '001',
        specification: 'TEST-001', material: null, remark: null, brand: 'UPTON', surfaceTreatment: null, weight: null, weightUnit: null,
        approvalStatus: 'Approved', approvedBy: 'admin', approvedAt: '2026-09-01T01:00:00Z', categoryCode: '0102', u9CategoryCode: '0102',
        u9ItemId: null, u9ItemCode: null, syncStatus: 'Succeeded', createdBy: 'admin', createdAt: '2026-09-01T01:00:00Z',
        updatedBy: 'admin', updatedAt: '2026-09-03T01:00:00Z', rowVersion: 3, isArchived: true, archivedBy: 'admin',
        archivedAt: '2026-09-03T01:00:00Z', u9SyncConfirmed: true, sourceSystem: 'Pdm', masterOwner: 'Pdm', referenceCount: 0,
      }] : []
      return fulfill({ items, total: items.length, page: 1, pageSize: 50 })
    }
    if (path === '/api/material-categories') return fulfill([{ code: '0102', name: '机械外购件', parentCode: null, pdmKind: 'Standard', defaultSupplyMode: 'Purchase', allowCreate: true, isVisible: true, isActive: true, numberPrefix: '0102', sequenceLength: 7, counterScope: '0102', sortOrder: 1, updatedBy: 'admin', updatedAt: '2026-09-01T01:00:00Z', rowVersion: 1 }])
    if (path === '/api/material-numbering-settings') return fulfill({ startSequence: 1000000, sequenceLength: 7 })
    if (path === '/api/material-duplicate-rules') return fulfill([{ categoryCode: '0102', fields: ['Specification', 'Brand'] }])
    if (path === '/api/material-inventory') return fulfill({
      items: [], total: 0, page: 1, pageSize: 50,
      warehouseNames: [], brandNames: [], projectCodes: [], projectSubprojects: [],
      lastSuccessfulRefreshAt: null,
    })
    if (path === '/api/material-relations/templates') return fulfill([])
    if (/^\/api\/material-relations\/projects\/[^/]+\/completeness$/.test(path)) return fulfill({
      projectId: path.split('/')[4], isComplete: true, mainMaterialCount: 0, incompleteGroupCount: 0, mainMaterials: [],
    })
    if (path === '/api/program-templates/tasks/mine') return fulfill([])
    if (path === '/api/customers') return fulfill([{ id: 'customer-1', code: 'C00465', name: '中山比亚迪电子有限公司', isActive: true }])
    if (path === '/api/organization-directory') return fulfill(organizationDirectory)
    if (path === '/api/crm-integration') return fulfill({ baseUrl: '', username: '', passwordConfigured: false, autoSyncEnabled: false, autoSyncIntervalMinutes: 60, lastSyncAt: null, lastSyncCount: 0, lastAutoSyncAttemptAt: null, lastAutoSyncError: null })
    if (path === '/api/u9-material-integration') return fulfill({ baseUrl: '', enterpriseCode: '', organizationCode: '', userCode: '', clientId: '', clientSecretConfigured: false, itemCreatePath: '', itemQueryPath: '', itemModifyPath: '', itemDeletePath: '', unitCodeMappings: {}, writeEnabled: false, updatedBy: null, updatedAt: null })
    if (path === '/api/u9-material-full-sync/status') return fulfill({ scheduleTime: '02:00', checkIntervalMinutes: 30, categories: [], latestRun: null })
    if (path === '/api/u9-inventory-sync/status') return fulfill({ settings: { autoSyncEnabled: false, syncIntervalMinutes: 60, queryPath: '' }, latestRun: null })
    if (path === '/api/u9-procurement-sync/status') return fulfill({ settings: { autoSyncEnabled: false, syncIntervalMinutes: 60, queryPath: '' }, latestRun: null })
    if (path === '/api/role-permissions') return fulfill({ permissions: [], roles: [] })
    if (path === '/api/system-settings') return fulfill({ vaultRoot: 'D:\\PDM\\Vault', releaseRoot: 'D:\\PDM\\Release', checkoutHeartbeatSeconds: 180, checkoutLeaseMinutes: 15, checkoutOfflineGraceMinutes: 60, checkoutReminderHours: 4, checkoutStrongReminderHours: 8, checkoutOverdueHours: 24, checkoutForceReleaseHours: 48 })
    if (path === '/api/system-settings/equipment-types') return fulfill([])
    if (path === '/api/folder-template') return fulfill([])
    if (path === '/api/edit-locks') return fulfill([])
    if (path === '/api/project-numbering/options') return fulfill({ organizations: [{ id: '70000000-0000-0000-0000-000000000001', name: '昆山阿普顿自动化系统有限公司', projectCompanyCode: '7', modelCompanyCode: 'AK', crmCompanyName: '昆山阿普顿自动化系统有限公司' }], projectTypes: [{ code: 'P', name: '标准项目' }], equipmentTypes: [{ code: 2, name: '类型02' }] })
    if (path === '/api/projects') return fulfill([{ id: projectId, code: 'PRJ-REAL-001', name: '真实装配项目', owner: '真实工程师', vaultLocation: 'D:\\PDM\\PRJ-REAL-001', releaseLocation: 'D:\\Release\\PRJ-REAL-001', isActive: true, quantity: 1, serialNumbers: ['70000001'], executionUnitName: '自动化事业部', primaryProjectManager: 'project-manager', collaborativeProjectManagers: ['project-manager-2'], designLead: 'design-lead', designers: [] }])
    if (/^\/api\/projects\/[^/]+\/content-reset\/readiness$/.test(path)) {
      const resetProjectId = path.split('/')[3]
      return fulfill({ project: { id: resetProjectId, code: resetProjectId === projectId ? 'PRJ-REAL-001' : 'P700002', name: '测试项目' }, includeChildren: false, includedProjects: [{ id: resetProjectId, code: resetProjectId === projectId ? 'PRJ-REAL-001' : 'P700002', name: '测试项目' }], canReset: true, blockers: [], counts: {}, restorableSnapshots: [] })
    }
    if (path === `/api/projects/${projectId}`) return fulfill({ id: projectId, code: 'PRJ-REAL-001', name: '真实装配项目', owner: '真实工程师', vaultLocation: 'D:\\PDM\\PRJ-REAL-001', releaseLocation: 'D:\\Release\\PRJ-REAL-001', isActive: true, quantity: 1, serialNumbers: ['70000001'], executionUnitName: '自动化事业部', primaryProjectManager: 'project-manager', collaborativeProjectManagers: ['project-manager-2'], designLead: 'design-lead', designers: [] })
    if (path === `/api/projects/${projectId}/versions`) return fulfill(versions.map(version => ({ ...version, drawingNumber: 'REAL-ASM-001', documentName: '真实总装配', fileName: 'REAL-ASM-001.SLDASM' })))
    if (path === `/api/projects/${projectId}/audit`) return fulfill([])
    if (path === `/api/projects/${projectId}/drawing-reviews`) return fulfill([])
    if (path === `/api/projects/${projectId}/drawing-review-candidates`) return fulfill([])
    if (path === `/api/projects/${projectId}/plan/portfolio`) return fulfill({ projects: [], completionPercent: 0, plannedFinish: null })
    if (path === `/api/projects/${projectId}/validation-plan`) return fulfill(null)
    if (path === `/api/projects/${projectId}/procurement-tracking`) return fulfill({ items: [] })
    if (path === `/api/projects/${projectId}/files`) return fulfill([])
    if (path === `/api/projects/${projectId}/folders`) return fulfill([
      { id: 'folder-root', rootProjectId: projectId, parentFolderId: null, targetProjectId: projectId, folderKey: 'root', templateKey: 'root', name: 'PRJ-REAL-001', purpose: 0, sortOrder: 0, isSystem: true, inheritPermissions: true, effectiveAccess: 127, permissions: [] },
      { id: 'folder-mechanical', rootProjectId: projectId, parentFolderId: 'folder-root', targetProjectId: null, folderKey: 'mechanical', templateKey: 'mechanical', name: '机械图纸', purpose: 1, sortOrder: 10, isSystem: true, inheritPermissions: true, effectiveAccess: 15, permissions: [] },
      { id: 'folder-main-mechanical', rootProjectId: projectId, parentFolderId: 'folder-mechanical', targetProjectId: projectId, folderKey: 'mechanical.project:main', templateKey: 'mechanical.project', name: 'PRJ-REAL-001-0', purpose: 3, sortOrder: 10, isSystem: true, inheritPermissions: true, effectiveAccess: 15, permissions: [] },
      { id: 'folder-electrical', rootProjectId: projectId, parentFolderId: 'folder-root', targetProjectId: null, folderKey: 'electrical', templateKey: 'electrical', name: '电气图纸', purpose: 2, sortOrder: 20, isSystem: true, inheritPermissions: true, effectiveAccess: 15, permissions: [] },
      { id: 'folder-main-electrical', rootProjectId: projectId, parentFolderId: 'folder-electrical', targetProjectId: projectId, folderKey: 'electrical.project:main', templateKey: 'electrical.project', name: 'PRJ-REAL-001-0', purpose: 3, sortOrder: 10, isSystem: true, inheritPermissions: true, effectiveAccess: 15, permissions: [] },
    ])
    if (path === `/api/projects/${projectId}/document-relations`) return fulfill([{ modelDocumentId: 'doc-root', drawingDocumentId: 'doc-drawing' }])
    if (path === `/api/projects/${projectId}/folder-documents`) return fulfill([{ id: 'doc-root', projectId, folderId: 'folder-main-mechanical', drawingNumber: 'REAL-ASM-001', name: '真实总装配', fileName: 'REAL-ASM-001.SLDASM', kind: 0, lifecycleState: 0, revision: { display: 'W2' }, checkedOutBy: 'engineer' }, { id: 'doc-part', projectId, folderId: 'folder-main-mechanical', drawingNumber: 'REAL-PRT-001', name: '真实底板', fileName: 'REAL-PRT-001.SLDPRT', kind: 1, lifecycleState: 0, revision: { display: 'A' }, checkedOutBy: null }, { id: 'doc-drawing', projectId, folderId: 'folder-main-mechanical', drawingNumber: 'REAL-ASM-001', name: '真实总装工程图', fileName: 'REAL-ASM-001.SLDDRW', kind: 2, lifecycleState: 0, revision: { display: 'W1' }, checkedOutBy: null }])
    if (path.endsWith('/documents')) return fulfill([{ id: 'doc-root', drawingNumber: 'REAL-ASM-001', name: '真实总装配', fileName: 'REAL-ASM-001.SLDASM', kind: 0, revision: { display: 'W2' }, checkedOutBy: 'engineer' }, { id: 'doc-part', drawingNumber: 'REAL-PRT-001', name: '真实底板', fileName: 'REAL-PRT-001.SLDPRT', kind: 1, revision: { display: 'A' }, checkedOutBy: null }, { id: 'doc-drawing', drawingNumber: 'REAL-ASM-001', name: '真实总装工程图', fileName: 'REAL-ASM-001.SLDDRW', kind: 2, revision: { display: 'W1' }, checkedOutBy: null }])
    if (path.endsWith('/reference-tree')) return fulfill({ nodeId: 'node-root', documentId: 'doc-root', instancePath: 'REAL-ASM-001', fileName: 'REAL-ASM-001.SLDASM', displayName: '真实总装配', kind: 0, configuration: '默认', quantity: 1, status: 0, revision: null, checkedOutBy: 'engineer', children: referenceChildren })
    if (path.endsWith('/boms/Standard')) return fulfill([
      { id: 'bom-standard-1', kind: 'Standard', sequence: 1, drawingNumber: 'REAL-STD-001', name: '标准紧固件', quantity: 4, unit: '件', material: null, specification: 'M8', brand: 'FESTO', revision: 'A', isComplete: true, source: 'Auto', isWearPart: true, isManuallyOverridden: false, isPendingRemoval: false },
      { id: 'bom-standard-2', kind: 'Standard', sequence: 2, drawingNumber: 'REAL-STD-001', name: '标准紧固件', quantity: 6, unit: '件', material: null, specification: 'M8', brand: 'FESTO', revision: 'A', isComplete: true, source: 'Auto', isWearPart: true, isManuallyOverridden: false, isPendingRemoval: false },
    ])
    if (path.endsWith('/boms/NonStandard')) return fulfill([{ id: 'bom-non-standard-1', kind: 'NonStandard', sequence: 1, drawingNumber: 'REAL-PRT-001', name: '真实底板', quantity: 2, unit: '件', material: 'Q235B', specification: '10mm', revision: 'A', isComplete: true, source: 'Auto', isWearPart: true, isManuallyOverridden: false, isPendingRemoval: false }])
    if (path.endsWith('/boms/Unclassified')) return fulfill([])
    if (path.endsWith('/boms/Electrical')) return fulfill([{ id: 'bom-electrical-1', kind: 'Electrical', sequence: 1, drawingNumber: 'REAL-EL-001', name: '真实传感器', quantity: 1, unit: '件', material: null, specification: 'PNP', revision: 'A', isComplete: false }])
    if (path.endsWith('/bom-source-data')) return fulfill([])
    if (path.endsWith('/bom-versions')) return fulfill([])
    if (path.endsWith('/bom-baselines')) return fulfill([])
    if (path.endsWith('/bom-headers')) return fulfill([])
    if (path.endsWith('/boms/empty-declarations')) return fulfill([])
    if (path.endsWith('/release-packages')) return fulfill([{ id: 'package-1', number: 'RP-REAL-001', state: 2, approvalTasks: [{ stage: 1, assignee: '工艺工程师', decisionBy: '工艺工程师', decision: 0, decidedAt: '2026-08-11T01:00:00Z' }, { stage: 2, assignee: '批准人', decisionBy: null, decision: null, decidedAt: null }], publishedAt: null }])
    if (path === '/api/documents/doc-root/versions') return fulfill(versions)
    if (path === '/api/documents/doc-root/where-used') return fulfill([{ documentId: 'doc-root', parentDocumentId: 'doc-parent', projectId, projectCode: 'PRJ-REAL-001', projectName: '真实装配项目', parentDrawingNumber: 'REAL-TOP-001', parentName: '上层总装', parentFileName: 'REAL-TOP-001.SLDASM', parentKind: 0, parentState: 0, parentRevision: { display: 'W3' }, instancePath: 'REAL-TOP-001/REAL-ASM-001-1', configuration: '默认', quantity: 1 }])
    if (path === '/api/documents/doc-root/versions/compare') return fulfill({ documentId: 'doc-root', left: versions[0], right: versions[1], propertyChanges: [], referenceChanges: [], bomChanges: [] })
    if (path === '/api/audit') return fulfill([{ id: 'audit-1', occurredAt: '2026-08-11T01:00:00Z', actor: 'engineer', action: 'VersionViewed', entityType: 'Document', entityId: 'doc-root', detail: '查看 W2' }])
    if (path.endsWith('/storage-status')) return fulfill({ vaultAvailable: true, releaseAvailable: true })
    return fulfill({ title: `Unexpected route: ${path}` }, 404)
  })
})

test('program template owner can delete a saved draft after confirmation', async ({ page }, testInfo) => {
  let drafts = [{
    id: 'template-draft-1', code: 'PT-PLC-0001', assetType: 'PlcProgram', originCompanyId: 'org-ks', originCompanyName: '昆山阿普顿自动化系统有限公司',
    currentPublishedRevisionId: null, isArchived: false, createdBy: 'engineer', createdAt: '2026-09-17T01:00:00Z',
    revisions: [{
      id: 'revision-draft-1', version: 'v1.0.0', attemptNumber: 1, state: 'Draft', name: '待删除程序草稿', category: '控制', description: '浏览器删除验证',
      vendor: 'Siemens', platform: 'TIA Portal', softwareVersion: 'V19', applicableSeries: 'S7-1500', tags: [], changeNote: '',
      packageFileName: 'draft.rar', packageFileLength: 128, packageSha256: 'A'.repeat(64), evidenceFileName: null, evidenceFileLength: null, evidenceSha256: null,
      createdBy: 'engineer', createdAt: '2026-09-17T01:00:00Z', submittedAt: null, publishedAt: null, rowVersion: 3, parameters: [],
    }],
  }]
  await page.route('**/api/auth/login', route => route.fulfill({ json: {
    accessToken: 'e2e-token', expiresAt: '2099-01-01T00:00:00Z', resumeToken: 'e2e-resume-token', username: 'engineer', displayName: '真实工程师', role: 'Engineer',
    permissions: ['project.view', 'project.create', 'project.child.create', 'project.content.view', 'document.edit', 'bom.edit', 'bom.mechanical.edit', 'bom.electrical.edit', 'material.view', 'release.manage', 'program-template.view', 'program-template.submit'],
    primaryCompanyId: 'org-ks', activeCompanyId: 'org-ks', activeCompanyName: '昆山阿普顿自动化系统有限公司', crossCompanyView: false,
    accessibleCompanies: [{ id: 'org-ks', name: '昆山阿普顿自动化系统有限公司', code: '7' }],
  } }))
  await page.route(/^http:\/\/127\.0\.0\.1:(?:5080|5173|519[3-5])\/api\/program-templates(?:\/.*)?(?:\?.*)?$/, route => {
    const url = new URL(route.request().url())
    if (url.pathname === '/api/program-templates/tasks/mine') return route.fulfill({ json: [] })
    if (url.pathname === '/api/program-templates/options') return route.fulfill({ json: { categories: [], vendors: [], platforms: [] } })
    if (url.pathname === '/api/program-templates' && route.request().method() === 'GET')
      return route.fulfill({ json: url.searchParams.get('mine') === 'true' ? drafts : [] })
    if (url.pathname === '/api/program-templates/template-draft-1') return route.fulfill({ json: drafts[0] })
    if (url.pathname === '/api/program-templates/revisions/revision-draft-1' && route.request().method() === 'DELETE') {
      expect(url.searchParams.get('expectedRowVersion')).toBe('3')
      drafts = []
      return route.fulfill({ status: 204 })
    }
    return route.fulfill({ status: 404, json: { title: `Unexpected program template route: ${url.pathname}` } })
  })
  const errors: string[] = []
  page.on('pageerror', error => errors.push(error.message))
  page.on('console', message => { if (message.type() === 'error') errors.push(message.text()) })
  await page.addInitScript(() => { window.setInterval = (() => 0) as unknown as typeof window.setInterval })
  await page.goto('/')
  const login = page.getByLabel('登录PLM')
  await login.getByRole('textbox', { name: '账号' }).fill('engineer')
  await login.getByRole('textbox', { name: '密码' }).fill('correct-password')
  await login.getByRole('button', { name: '登录', exact: true }).click()
  await page.getByRole('button', { name: '程序模板', exact: true }).click()
  const library = page.getByLabel('程序模板库')
  await expect(library).toContainText('待删除程序草稿')
  await library.getByRole('button', { name: '查看' }).click()
  await expect(page.getByRole('button', { name: '删除草稿' })).toBeVisible()
  await page.getByRole('button', { name: '删除草稿' }).click()
  const confirmation = page.getByRole('dialog', { name: '删除程序模板草稿' })
  await expect(confirmation).toContainText('无法恢复')
  await page.screenshot({ path: testInfo.outputPath('program-template-delete-confirmation.png') })
  await confirmation.getByRole('button', { name: '确认删除' }).click()
  await expect(library).not.toContainText('待删除程序草稿')
  expect(errors).toEqual([])
})

test('project overview prioritizes current status, actions, tasks, team and locations', async ({ page }, testInfo) => {
  const overviewProject = { id: projectId, code: 'PRJ-REAL-001', name: '真实装配项目', owner: 'engineer', stage: 'Design', vaultLocation: 'D:\\PDM\\PRJ-REAL-001', releaseLocation: 'D:\\Release\\PRJ-REAL-001', isActive: true, quantity: 1, serialNumbers: ['70000001'], executionUnitName: '自动化事业部', primaryProjectManager: 'engineer', collaborativeProjectManagers: [], designLead: 'engineer', designers: ['engineer'] }
  await page.route('**/api/projects', route => route.fulfill({ json: [overviewProject] }))
  await page.route(`**/api/projects/${projectId}`, route => route.fulfill({ json: overviewProject }))
  await page.route(`**/api/projects/${projectId}/plan/portfolio`, route => route.fulfill({ json: {
    rootProjectId: projectId, currentStage: 'Design', completionPercent: 62, laggingProjectCount: 0, riskProjectCount: 0, plannedStart: '2026-08-01', plannedFinish: '2026-10-18',
    projects: [{ projectId, projectCode: 'PRJ-REAL-001', projectName: '真实装配项目', isRoot: true, hasPlan: true, currentStage: 'Design', completionPercent: 62, plannedFinish: '2026-10-18', isLagging: false, isAtRisk: false, plan: {
      id: 'plan-1', projectId, currentStage: 'Design', plannedStart: '2026-08-01', plannedFinish: '2026-10-18', approvalStatus: 'Approved', stages: [{ code: 'Design', name: '设计' }], tasks: [
        { id: 'task-drawing', name: '完成图纸审核', stage: 'Design', assignee: 'engineer', plannedStart: '2026-09-16', plannedFinish: '2026-09-18', completionPercent: 40, status: 'InProgress', predecessorTaskIds: [], weight: 1, isMilestone: false, isRequired: true, sortOrder: 1, durationDays: 3 },
        { id: 'task-bom', name: '标准件BOM核对', stage: 'Design', assignee: 'engineer', plannedStart: '2026-09-17', plannedFinish: '2026-09-19', completionPercent: 0, status: 'NotStarted', predecessorTaskIds: [], weight: 1, isMilestone: false, isRequired: true, sortOrder: 2, durationDays: 3 },
      ],
    } }],
  } }))
  await page.route(`**/api/projects/${projectId}/validation-plan`, route => route.fulfill({ json: { id: 'validation-1', projectId, revisionNumber: 1, state: 'PendingApproval', items: [], approvalTasks: [], attachments: [], createdBy: 'engineer', createdAt: '2026-09-10', updatedBy: 'engineer', updatedAt: '2026-09-10', rowVersion: 1 } }))
  await page.route(`**/api/material-relations/projects/${projectId}/completeness`, route => route.fulfill({ json: { projectId, isComplete: false, mainMaterialCount: 2, incompleteGroupCount: 2, mainMaterials: [] } }))
  await page.route(`**/api/projects/${projectId}/procurement-tracking`, route => route.fulfill({ json: { projectId, projectCode: 'PRJ-REAL-001', hasPublishedBom: true, items: [
    { sequence: 1, projectCode: 'PRJ-REAL-001', materialCode: 'MAT-001', materialName: '关键传感器', impactStage: 'Assembly', quantity: 1, bomKind: 'Electrical', purchaseRequisitionNumbers: [], purchaseRequisitionStatus: '未请购', purchaseOrderNumbers: [], purchaseOrderStatus: '未采购', purchaseQuantity: 0, arrivedQuantity: 0, details: [] },
    { sequence: 2, projectCode: 'PRJ-REAL-001', materialCode: 'MAT-002', materialName: '普通紧固件', impactStage: null, quantity: 2, bomKind: 'Standard', purchaseRequisitionNumbers: [], purchaseRequisitionStatus: '未请购', purchaseOrderNumbers: [], purchaseOrderStatus: '未采购', purchaseQuantity: 0, arrivedQuantity: 0, details: [] },
  ] } }))
  const errors: string[] = []
  page.on('pageerror', error => errors.push(error.message))
  page.on('console', message => { if (message.type() === 'error') errors.push(message.text()) })
  await page.addInitScript(() => { window.setInterval = (() => 0) as unknown as typeof window.setInterval })
  await page.setViewportSize({ width: 1236, height: 1114 })
  await page.goto('/')
  const login = page.getByLabel('登录PLM')
  await login.getByRole('textbox', { name: '账号' }).fill('engineer')
  await login.getByRole('textbox', { name: '密码' }).fill('correct-password')
  await login.getByRole('button', { name: '登录', exact: true }).click()
  await enterProject(page)

  const overview = page.getByLabel('工作台主页面')
  await expect(overview.getByLabel('项目状态与下一步')).toContainText('设计阶段')
  await expect(overview.getByLabel('项目状态与下一步')).toContainText('项目进度 62%')
  await expect(overview.getByLabel('项目状态与下一步')).toContainText('2026/10/18')
  await expect(overview.getByLabel('图档与审核')).toContainText('3D 41')
  await expect(overview.getByLabel('图档与审核')).toContainText('2D 1')
  await expect(overview.getByLabel('BOM与物料')).toContainText('关联物料待核对 2')
  await expect(overview.getByLabel('发布与备料')).toContainText('关键物料1')
  await expect(overview.getByLabel('当前阶段任务')).toContainText('完成图纸审核')
  await expect(overview.getByLabel('项目团队')).toContainText('真实工程师')
  await expect(overview.getByLabel('项目位置')).toContainText('D:\\PDM\\PRJ-REAL-001')
  const overviewBox = await overview.boundingBox()
  expect(overviewBox).not.toBeNull()
  expect((overviewBox?.y ?? 0) + (overviewBox?.height ?? 0)).toBeLessThanOrEqual(page.viewportSize()!.height + 1)
  expect(await overview.getByLabel('当前阶段任务').locator('tbody td').first().evaluate(element => getComputedStyle(element).fontSize)).toBe('10px')
  await expect(page.locator('vite-error-overlay')).toHaveCount(0)
  await page.screenshot({ path: testInfo.outputPath('project-overview-command-center.png') })
  await overview.getByRole('button', { name: '进入BOM数据' }).click()
  await expect(page.getByRole('button', { name: 'BOM', exact: true })).toHaveClass(/is-active/)
  expect(errors).toEqual([])
})

test('procurement BOM kind filter stays on one line and filters all three kinds', async ({ page }, testInfo) => {
  await page.route(`**/api/projects/${projectId}/procurement-tracking`, route => route.fulfill({ json: {
    projectId, projectCode: 'PRJ-REAL-001', hasPublishedBom: true, lastSuccessfulRefreshAt: '2026-09-17T12:00:00Z', items: [
      { sequence: 1, projectCode: 'PRJ-REAL-001', materialCode: 'STD-01', materialName: '标准紧固件', brand: 'UPTON', quantity: 1, bomKind: 'Standard', purchaseRequisitionNumbers: [], purchaseRequisitionStatus: '未请购', purchaseOrderNumbers: [], purchaseOrderStatus: '未采购', purchaseQuantity: 0, arrivedQuantity: 0, details: [] },
      { sequence: 2, projectCode: 'PRJ-REAL-001', materialCode: 'NONSTD-01', materialName: '非标底板', brand: 'UPTON', quantity: 1, bomKind: 'NonStandard', purchaseRequisitionNumbers: [], purchaseRequisitionStatus: '未请购', purchaseOrderNumbers: [], purchaseOrderStatus: '未采购', purchaseQuantity: 0, arrivedQuantity: 0, details: [] },
      { sequence: 3, projectCode: 'PRJ-REAL-001', materialCode: 'ELEC-01', materialName: '电气传感器', brand: 'UPTON', quantity: 1, bomKind: 'Electrical', purchaseRequisitionNumbers: [], purchaseRequisitionStatus: '未请购', purchaseOrderNumbers: [], purchaseOrderStatus: '未采购', purchaseQuantity: 0, arrivedQuantity: 0, details: [] },
    ],
  } }))
  const errors: string[] = []
  page.on('pageerror', error => errors.push(error.message))
  page.on('console', message => { if (message.type() === 'error') errors.push(message.text()) })
  await page.addInitScript(() => { window.setInterval = (() => 0) as unknown as typeof window.setInterval })
  await page.setViewportSize({ width: 1920, height: 1080 })
  await page.goto('/')
  const login = page.getByLabel('登录PLM')
  await login.getByRole('textbox', { name: '账号' }).fill('engineer')
  await login.getByRole('textbox', { name: '密码' }).fill('correct-password')
  await login.getByRole('button', { name: '登录', exact: true }).click()
  await enterProject(page)
  await page.getByRole('button', { name: '备料', exact: true }).click()

  const kindFilter = page.getByLabel('筛选物料分类')
  await expect(kindFilter.locator('option')).toHaveText(['全部分类', '标准件', '非标件', '电气件'])
  const layout = await page.locator('.procurement-tracking__heading').evaluate(heading => {
    const filters = heading.querySelector<HTMLElement>('.procurement-tracking__filters')!
    const controls = [...heading.querySelectorAll<HTMLElement>('.procurement-tracking__filters > :not(datalist), .procurement-tracking__actions > *, .procurement-tracking__updated')]
      .filter(element => element.getBoundingClientRect().width > 0)
    const centers = controls.map(element => {
      const rect = element.getBoundingClientRect()
      return Math.round(rect.top + rect.height / 2)
    })
    return {
      filterWrap: getComputedStyle(filters).flexWrap,
      headingOverflow: heading.scrollWidth - heading.clientWidth,
      centerSpread: Math.max(...centers) - Math.min(...centers),
    }
  })
  expect(layout.filterWrap).toBe('nowrap')
  expect(layout.headingOverflow).toBeLessThanOrEqual(1)
  expect(layout.centerSpread).toBeLessThanOrEqual(1)

  await kindFilter.selectOption('非标件')
  await expect(page.getByText('NONSTD-01', { exact: true })).toBeVisible()
  await expect(page.getByText('STD-01', { exact: true })).toHaveCount(0)
  await expect(page.getByText('ELEC-01', { exact: true })).toHaveCount(0)
  await page.screenshot({ path: testInfo.outputPath('procurement-kind-filter-one-line.png'), fullPage: false })
  await expect(page.locator('vite-error-overlay')).toHaveCount(0)
  expect(errors).toEqual([])
})

test('wear-part BOM aggregates categories and stays outside release', async ({ page }, testInfo) => {
  const errors: string[] = []
  page.on('pageerror', error => errors.push(error.message))
  page.on('console', message => { if (message.type() === 'error') errors.push(message.text()) })
  await page.addInitScript(() => { window.setInterval = (() => 0) as unknown as typeof window.setInterval })
  await page.setViewportSize({ width: 1988, height: 1114 })
  await page.goto('/')
  const login = page.getByLabel('登录PLM')
  await login.getByRole('textbox', { name: '账号' }).fill('engineer')
  await login.getByRole('textbox', { name: '密码' }).fill('correct-password')
  await login.getByRole('button', { name: '登录', exact: true }).click()
  await enterProject(page)
  await page.getByRole('button', { name: 'BOM', exact: true }).click()
  await page.getByRole('tab', { name: /易损件BOM/ }).click()

  await expect(page.getByRole('tab', { name: '易损件BOM（2）' })).toHaveAttribute('aria-selected', 'true')
  const summary = page.getByLabel('易损件BOM统计说明')
  await expect(summary).toContainText('统计物料2 种')
  await expect(summary).toContainText('结构实例3 条')
  await expect(summary).toContainText('合计数量12')
  await expect(summary).toContainText('不参与审批、版本、发布包及 U9C')
  await expect(page.getByRole('button', { name: '发起发布' })).toHaveCount(0)
  await expect(page.getByRole('button', { name: '保存BOM' })).toHaveCount(0)
  await expect(page.locator('vite-error-overlay')).toHaveCount(0)
  await page.screenshot({ path: testInfo.outputPath('wear-part-bom-statistics.png'), fullPage: true })
  expect(errors).toEqual([])
})

test('released BOM rows expose linked 2D and 3D preview downloads', async ({ page }, testInfo) => {
  const releasedDocuments = [
    { id: 'doc-root', projectId, folderId: 'folder-main-mechanical', drawingNumber: 'REAL-ASM-001', name: '真实总装配', fileName: 'REAL-ASM-001.SLDASM', kind: 0, lifecycleState: 2, revision: { display: 'W2' }, storedVersionCount: 2, checkedOutBy: null },
    { id: 'doc-drawing', projectId, folderId: 'folder-main-mechanical', drawingNumber: 'REAL-ASM-001', name: '真实总装工程图', fileName: 'REAL-ASM-001.SLDDRW', kind: 2, lifecycleState: 2, revision: { display: 'W2' }, storedVersionCount: 2, checkedOutBy: null },
  ]
  await page.route(`**/api/projects/${projectId}/folder-documents`, route => route.fulfill({ json: releasedDocuments }))
  await page.route(`**/api/projects/${projectId}/bom-source-data`, route => route.fulfill({ json: [{
    id: 'source-non-standard-1', kind: 'NonStandard', sequence: 1, drawingNumber: 'REAL-ASM-001', name: '真实总装配', quantity: 1, unit: '件', material: 'Q235B', specification: '总装', heatTreatment: '调质', revision: 'W2', isComplete: true, source: 'Auto', sourceDocumentId: 'doc-root', isWearPart: false, isManuallyOverridden: false, isPendingRemoval: false,
  }] }))
  await page.route(`**/api/projects/${projectId}/boms/NonStandard`, route => route.fulfill({ json: [{
    id: 'bom-non-standard-1', kind: 'NonStandard', sequence: 1, drawingNumber: 'REAL-ASM-001', name: '真实总装配', quantity: 1, unit: '件', material: 'Q235B', specification: '总装', heatTreatment: '淬火', revision: 'W2', isComplete: true, source: 'Auto', sourceDocumentId: 'doc-root', isWearPart: false, isManuallyOverridden: false, isPendingRemoval: false,
  }] }))
  await page.route(`**/api/projects/${projectId}/drawing-review-candidates`, route => route.fulfill({ json: [{
    candidateId: 'review-bom-non-standard-1', bomItemId: 'bom-non-standard-1', modelDocumentId: 'doc-root', drawingDocumentId: 'doc-drawing', drawingNumber: 'REAL-ASM-001', name: '真实总装配', bomKinds: ['NonStandard'], modelRevision: 'W2', drawingRevision: 'W2', state: 'Ready', reason: '当前工程图尚未发起审核', selectable: true,
  }] }))
  await page.route('**/api/documents/doc-drawing/versions', route => route.fulfill({ json: [{
    id: 'drawing-release-w2', documentId: 'doc-drawing', revision: { display: 'W2' }, status: 1, fileLength: 100, sha256: 'D'.repeat(64), createdBy: 'engineer', createdAt: '2026-09-16T01:00:00Z', changeNote: '发布',
    preview: { format: 1, storageRelativePath: '.release-previews/drawing.pdf', fileLength: 80, sha256: 'P'.repeat(64), sourceSha256: 'D'.repeat(64) },
  }] }))
  await page.route('**/api/documents/doc-drawing/versions/drawing-release-w2/preview', route => route.fulfill({ contentType: 'application/pdf', body: '%PDF-1.4\n%%EOF' }))

  const errors: string[] = []
  page.on('pageerror', error => errors.push(error.message))
  page.on('console', message => { if (message.type() === 'error') errors.push(message.text()) })
  await page.addInitScript(() => { window.setInterval = (() => 0) as unknown as typeof window.setInterval })
  await page.setViewportSize({ width: 1925, height: 1114 })
  await page.goto('/')
  const login = page.getByLabel('登录PLM')
  await login.getByRole('textbox', { name: '账号' }).fill('engineer')
  await login.getByRole('textbox', { name: '密码' }).fill('correct-password')
  await login.getByRole('button', { name: '登录', exact: true }).click()
  await enterProject(page)
  await page.getByRole('button', { name: 'BOM', exact: true }).click()

  const table = page.locator('.pdm-bom-table')
  await page.getByRole('tab', { name: '源数据（1）' }).click()
  await expect(page.getByRole('tab', { name: '源数据（1）' })).toHaveAttribute('aria-selected', 'true')
  await expect(table.getByRole('columnheader', { name: '热处理', exact: true })).toBeVisible()
  await expect(table.locator('tbody tr').first()).toContainText('调质')
  await expect(table.getByRole('button', { name: '编辑热处理' })).toHaveCount(0)
  await page.screenshot({ path: join(tmpdir(), `pdm-bom-source-heat-treatment-${Date.now()}.png`) })

  await page.getByRole('tab', { name: /非标件BOM/ }).click()

  await expect(table.getByRole('columnheader', { name: '图纸', exact: true })).toBeVisible()
  await expect(table.getByRole('columnheader', { name: '热处理', exact: true })).toBeVisible()
  await expect(table.getByRole('button', { name: '编辑热处理' })).toHaveText('淬火')
  await expect(table.getByRole('button', { name: '下载2D图纸 REAL-ASM-001' })).toBeVisible()
  await expect(table.getByRole('button', { name: '下载3D图纸 REAL-ASM-001' })).toBeVisible()
  await expect(table.getByText('未审核', { exact: true })).toHaveAttribute('title', '当前工程图尚未发起审核')
  expect(await table.locator('tbody tr').first().evaluate(element => getComputedStyle(element).height)).toBe('30px')
  await page.screenshot({ path: testInfo.outputPath('non-standard-drawing-review-warning.png'), fullPage: false })

  const downloadPromise = page.waitForEvent('download')
  await table.getByRole('button', { name: '下载2D图纸 REAL-ASM-001' }).click()
  const download = await downloadPromise
  expect(download.suggestedFilename()).toBe('REAL-ASM-001.pdf')

  await page.getByRole('tab', { name: /标准件BOM/ }).click()
  await expect(table.getByRole('columnheader', { name: '热处理', exact: true })).toHaveCount(0)
  await expect(page.getByText('查看版本', { exact: true })).toHaveCount(0)
  await expect(page.getByRole('button', { name: '对比发布', exact: true })).toBeVisible()
  const filterRows = await page.locator('.pdm-bom-filters > *').evaluateAll(elements =>
    elements.map(element => Math.round(element.getBoundingClientRect().top)),
  )
  expect(new Set(filterRows).size).toBe(1)
  const filterWidth = await page.locator('.pdm-bom-filters').evaluate(element => ({
    clientWidth: element.clientWidth,
    scrollWidth: element.scrollWidth,
  }))
  expect(filterWidth.scrollWidth).toBeLessThanOrEqual(filterWidth.clientWidth)
  await page.getByRole('button', { name: '列设置', exact: true }).click()
  const columnDialog = page.getByRole('dialog', { name: 'BOM列设置' })
  const heatTreatmentCheckbox = columnDialog.getByRole('checkbox', { name: '热处理' })
  await expect(heatTreatmentCheckbox).not.toBeChecked()
  await columnDialog.locator('label.el-checkbox').filter({ hasText: '热处理' }).click()
  await expect(heatTreatmentCheckbox).toBeChecked()
  await columnDialog.getByRole('button', { name: '保存', exact: true }).click()
  await expect(table.getByRole('columnheader', { name: '热处理', exact: true })).toBeVisible()
  await expect(page.locator('vite-error-overlay')).toHaveCount(0)
  await page.screenshot({ path: join(tmpdir(), `pdm-bom-drawing-links-${Date.now()}.png`) })
  expect(errors).toEqual([])
})

test('related materials use a scoped right drawer for electrical BOM', async ({ page }, testInfo) => {
  await page.route(`**/api/projects/${projectId}/boms/Electrical`, route => route.fulfill({ json: [
    { id: 'bom-electrical-1', kind: 'Electrical', sequence: 1, drawingNumber: 'REAL-EL-001', name: '真实传感器', quantity: 1, unit: '件', material: null, specification: 'PNP', revision: 'A', isComplete: false },
    { id: 'bom-electrical-unconfigured', kind: 'Electrical', sequence: 2, drawingNumber: 'REAL-EL-UNCONFIGURED', name: '无关联配置电气料', quantity: 1, unit: '件', material: null, specification: 'NPN', revision: 'A', isComplete: false },
  ] }))
  await page.route(`**/api/material-relations/projects/${projectId}/completeness`, route => route.fulfill({ json: {
    projectId, isComplete: false, mainMaterialCount: 2, incompleteGroupCount: 2,
    mainMaterials: [
      { mainBomItemId: 'bom-standard-1', mainMaterialCode: 'REAL-STD-001', mainMaterialName: '标准紧固件', mainQuantity: 4, templateId: 'standard-template', revisionId: 'standard-revision', revisionVersion: 1, isComplete: false, groups: [{ groupId: 'standard-group', groupName: '标准配件', isRequired: true, selectionMode: 'Single', maxSelection: 1, isComplete: false, status: '待核对', expectedQuantity: 0, actualQuantity: 0, selectedOptionIds: [], reviewDecision: null, options: [{ id: 'standard-option', materialId: 'standard-accessory', materialCode: 'STD-ACC-001', materialName: '标准附件', materialKind: 'Standard', unitCode: '001', quantityMode: 'PerMainQuantity', quantityPerSet: 1, isDefault: true, sortOrder: 1 }] }] },
      { mainBomItemId: 'bom-electrical-1', mainMaterialCode: 'REAL-EL-001', mainMaterialName: '真实传感器', mainQuantity: 1, templateId: 'electrical-template', revisionId: 'electrical-revision', revisionVersion: 2, isComplete: false, groups: [{ groupId: 'electrical-group', groupName: '传感器附件', isRequired: true, selectionMode: 'Single', maxSelection: 1, isComplete: false, status: '待核对', expectedQuantity: 0, actualQuantity: 0, selectedOptionIds: [], reviewDecision: null, options: [{ id: 'electrical-option', materialId: 'electrical-accessory', materialCode: 'EL-ACC-001', materialName: '电气附件', materialKind: 'Electrical', unitCode: '001', quantityMode: 'PerMainQuantity', quantityPerSet: 1, isDefault: true, sortOrder: 1, specification: 'PNP-M18', brand: 'UPTON', selectionAdvice: '优先用于高速工位' }] }] },
    ],
  } }))
  const errors: string[] = []
  page.on('pageerror', error => errors.push(error.message))
  page.on('console', message => { if (message.type() === 'error') errors.push(message.text()) })
  await page.addInitScript(() => { window.setInterval = (() => 0) as unknown as typeof window.setInterval })
  await page.setViewportSize({ width: 1925, height: 1114 })
  await page.goto('/')
  const login = page.getByLabel('登录PLM')
  await login.getByRole('textbox', { name: '账号' }).fill('engineer')
  await login.getByRole('textbox', { name: '密码' }).fill('correct-password')
  await login.getByRole('button', { name: '登录', exact: true }).click()
  await enterProject(page)
  await page.getByRole('button', { name: 'BOM', exact: true }).click()
  await page.getByRole('tab', { name: /电气BOM/ }).click()
  await page.getByRole('button', { name: '关联物料', exact: true }).click()

  const drawer = page.getByRole('dialog', { name: 'BOM 关联物料核对' })
  await expect(drawer).toBeVisible()
  const mainPane = drawer.getByLabel('主物料明细')
  const relationPane = drawer.getByLabel('关联物料明细')
  await expect(mainPane).toContainText('REAL-EL-001')
  await expect(mainPane).not.toContainText('REAL-EL-UNCONFIGURED')
  await expect(mainPane).not.toContainText('REAL-STD-001')
  await expect(mainPane).toContainText('共 1 条')
  const optionControl = relationPane.getByLabel('选择关联物料 EL-ACC-001')
  await expect(optionControl).toBeVisible()
  await expect(relationPane.getByRole('columnheader')).toHaveText(['选择', '名称', '型号', '品牌', '选型建议', '数量'])
  await expect(relationPane).toContainText('PNP-M18')
  await expect(relationPane).toContainText('UPTON')
  await expect(relationPane).toContainText('优先用于高速工位')
  await expect(relationPane.locator('.pdm-material-relation-preferred')).toHaveText('优先')
  await expect(relationPane.locator('header')).not.toContainText('REAL-EL-001')
  const noAccessoryButton = relationPane.getByRole('button', { name: '无需配套', exact: true })
  await expect(noAccessoryButton).toBeVisible()
  const noAccessoryReason = relationPane.getByLabel('REAL-EL-001 传感器附件 无需配套原因')
  const reopenButton = relationPane.getByRole('button', { name: '重新核对', exact: true })
  const clearButton = relationPane.getByRole('button', { name: '清除选择', exact: true })
  await expect(noAccessoryReason).toBeVisible()
  await expect(noAccessoryReason).toBeDisabled()
  await expect(reopenButton).toBeDisabled()
  await expect(clearButton).toBeVisible()
  await expect(clearButton).toBeDisabled()
  const noAccessoryBox = await noAccessoryButton.boundingBox()
  expect(Math.round(noAccessoryBox?.width ?? 0)).toBe(80)
  expect(Math.round(noAccessoryBox?.height ?? 0)).toBe(30)
  const actionBoxes = await Promise.all([reopenButton, noAccessoryButton, clearButton].map(button => button.boundingBox()))
  expect(actionBoxes.every(box => Math.round(box?.width ?? 0) === 80 && Math.round(box?.height ?? 0) === 30)).toBe(true)
  expect(new Set(actionBoxes.map(box => Math.round(box?.y ?? 0))).size).toBe(1)
  const groupTitle = relationPane.locator('.pdm-material-relation-group-title')
  expect(await groupTitle.evaluate(element => element.scrollWidth <= element.clientWidth && element.getBoundingClientRect().height <= 32)).toBe(true)
  const optionCell = optionControl.locator('xpath=..')
  const [optionControlBox, optionCellBox] = await Promise.all([optionControl.boundingBox(), optionCell.boundingBox()])
  expect(optionControlBox).not.toBeNull()
  expect(optionCellBox).not.toBeNull()
  expect(optionControlBox!.x).toBeGreaterThanOrEqual(optionCellBox!.x)
  expect(optionControlBox!.x + optionControlBox!.width).toBeLessThanOrEqual(optionCellBox!.x + optionCellBox!.width)
  expect(Math.abs((optionControlBox!.x + optionControlBox!.width / 2) - (optionCellBox!.x + optionCellBox!.width / 2))).toBeLessThanOrEqual(1)
  const relationCells = relationPane.locator('table th, table td')
  expect(await relationCells.evaluateAll(cells => cells.every(cell => getComputedStyle(cell).textAlign === 'center' && getComputedStyle(cell).verticalAlign === 'middle'))).toBe(true)
  const mainCells = mainPane.locator('table th, table td')
  expect(await mainCells.evaluateAll(cells => cells.every(cell => getComputedStyle(cell).textAlign === 'center' && getComputedStyle(cell).verticalAlign === 'middle'))).toBe(true)
  expect(await relationPane.locator('.pdm-material-relation-option-list').evaluate(element => element.scrollWidth <= element.clientWidth)).toBe(true)
  await optionControl.check()
  await expect(clearButton).toBeEnabled()
  await clearButton.click()
  await expect(optionControl).not.toBeChecked()
  await expect(clearButton).toBeDisabled()
  await noAccessoryButton.click()
  await page.getByRole('button', { name: '确认无需配套', exact: true }).click()
  await expect(noAccessoryReason).toBeEnabled()
  await expect(reopenButton).toBeEnabled()
  await expect(noAccessoryButton).toBeDisabled()
  await reopenButton.click()
  await expect(noAccessoryReason).toBeDisabled()
  await expect(noAccessoryButton).toBeEnabled()
  await expect.poll(async () => {
    const box = await drawer.boundingBox()
    return (box?.x ?? 0) + (box?.width ?? 0)
  }).toBeLessThanOrEqual(page.viewportSize()!.width + 1)
  const [mainBox, relationBox] = await Promise.all([mainPane.boundingBox(), relationPane.boundingBox()])
  const drawerBox = await drawer.boundingBox()
  expect(mainBox).not.toBeNull()
  expect(relationBox).not.toBeNull()
  expect(drawerBox).not.toBeNull()
  expect(Math.abs((mainBox?.width ?? 0) - (relationBox?.width ?? 0))).toBeLessThanOrEqual(2)
  expect((drawerBox?.x ?? 0) + (drawerBox?.width ?? 0)).toBeLessThanOrEqual(page.viewportSize()!.width + 1)
  expect(await mainPane.locator('tbody td').first().evaluate(element => getComputedStyle(element).fontSize)).toBe('12px')
  expect(await relationPane.locator('tbody td').first().evaluate(element => getComputedStyle(element).fontSize)).toBe('12px')
  await expect(drawer.getByRole('button', { name: '保存核对结果' })).toBeInViewport()
  await expect(page.locator('vite-error-overlay')).toHaveCount(0)
  await page.screenshot({ path: testInfo.outputPath('electrical-related-material-drawer.png') })
  expect(errors).toEqual([])
})

test('project settings keeps the confirmed project-copy scope', async ({ page }, testInfo) => {
  const targetId = '33333333-3333-3333-3333-333333333333'
  const sourceId = '44444444-4444-4444-4444-444444444444'
  const baseProject = { owner: 'admin', vaultLocation: 'D:\\PDM\\TEST', releaseLocation: 'D:\\Release\\TEST', isActive: true,
    quantity: 1, serialNumbers: [], collaborativeProjectManagers: [], designers: [], canReadContent: true }
  let previewBody: Record<string, unknown> | null = null
  await page.route('**/api/projects', route => route.fulfill({ json: [
    { ...baseProject, id: targetId, code: 'P700002', name: '新建目标项目' },
    { ...baseProject, id: sourceId, code: 'P700001', name: '现有源项目' },
  ] }))
  await page.route(new RegExp(`/api/projects/(?:${targetId}|${sourceId})$`), route => {
    const id = new URL(route.request().url()).pathname.split('/').at(-1)!
    return route.fulfill({ json: { ...baseProject, id, code: id === targetId ? 'P700002' : 'P700001', name: id === targetId ? '新建目标项目' : '现有源项目' } })
  })
  await page.route(`**/api/projects/${targetId}/**`, route => {
    const path = new URL(route.request().url()).pathname
    if (path.endsWith('/reference-tree')) return route.fulfill({ json: null })
    if (path.endsWith('/plan/portfolio')) return route.fulfill({ json: { projects: [], completionPercent: 0, plannedFinish: null } })
    if (path.endsWith('/validation-plan')) return route.fulfill({ json: null })
    if (path.endsWith('/procurement-tracking')) return route.fulfill({ json: { items: [] } })
    if (['/folders', '/documents', '/folder-documents', '/document-relations', '/boms/Standard', '/boms/NonStandard', '/boms/Unclassified', '/boms/Electrical', '/bom-source-data', '/boms/empty-declarations', '/bom-versions', '/bom-baselines', '/drawing-reviews', '/release-packages'].some(suffix => path.endsWith(suffix))) return route.fulfill({ json: [] })
    return route.fallback()
  })
  await page.route(`**/api/projects/${targetId}/copy-preview`, async route => {
    previewBody = route.request().postDataJSON()
    await route.fulfill({ json: {
      sourceProjectId: sourceId, targetProjectId: targetId, modelCount: 12, drawingCount: 8, bomItemCount: 36,
      validationItemCount: 5, projectFileCount: 1, totalBytes: 10485760, blockingReasons: [], warnings: [], canExecute: true,
      folders: [
        { id: 'gas-folder', name: '气路时序', path: '机械图纸 / 气路时序', templateKey: 'mechanical.air-sequence', fileCount: 1, totalBytes: 1024, defaultSelected: true },
        { id: 'other-folder', name: '其他资料', path: '机械图纸 / 其他资料', templateKey: 'mechanical.other', fileCount: 2, totalBytes: 2048, defaultSelected: false },
      ],
    } })
  })
  const errors: string[] = []
  page.on('pageerror', error => errors.push(error.message))
  page.on('console', message => { if (message.type() === 'error') errors.push(message.text()) })
  await page.addInitScript(() => { window.setInterval = (() => 0) as unknown as typeof window.setInterval })
  await page.setViewportSize({ width: 1988, height: 1114 })
  await page.goto('/')
  const login = page.getByLabel('登录PLM')
  await login.getByRole('textbox', { name: '账号' }).fill('admin')
  await login.getByRole('textbox', { name: '密码' }).fill('correct-password')
  await login.getByRole('button', { name: '登录', exact: true }).click()
  await expect(page.locator('section.pdm-project-workspace')).toBeVisible()
  const projectTabs = page.getByRole('navigation', { name: '项目功能' })
  await expect(projectTabs.getByRole('button').last()).toHaveAccessibleName('项目设置')
  await page.screenshot({ path: testInfo.outputPath('project-settings-entry.png') })
  await projectTabs.getByRole('button', { name: '项目设置' }).click()

  const dialog = page.getByRole('dialog', { name: /项目设置 · P700002/ })
  const copyTab = dialog.getByRole('tab', { name: '项目复制' })
  const resetTab = dialog.getByRole('tab', { name: '项目重置' })
  await expect(copyTab).toHaveAttribute('aria-selected', 'true')
  await expect(dialog.getByRole('heading', { name: '项目复制' })).toBeVisible()
  await expect(dialog.getByRole('heading', { name: '项目重置' })).toBeHidden()
  await expect(dialog).toContainText('目标项目')
  await dialog.getByRole('combobox').click()
  await page.getByRole('option', { name: 'P700001 · 现有源项目' }).click()
  await expect(dialog.getByRole('checkbox', { name: '最新 3D 图档' })).toBeChecked()
  await expect(dialog.getByRole('checkbox', { name: '最新 2D 图档' })).toBeChecked()
  await expect(dialog.getByRole('checkbox', { name: 'BOM' })).toBeChecked()
  await expect(dialog.getByRole('checkbox', { name: '验证计划检查项目' })).toBeChecked()
  await expect(dialog.getByRole('checkbox', { name: /气路时序/ })).toBeChecked()
  await expect(dialog.getByRole('checkbox', { name: /其他资料/ })).not.toBeChecked()
  await expect(dialog).toContainText('验证检查项 5')
  await expect(dialog).toContainText('共 10.0 MB')
  expect(previewBody).toMatchObject({ sourceProjectId: sourceId, copyModels: true, copyDrawings: true, copyBom: true, copyValidationItems: true, folderIds: null })
  await expect(page.locator('vite-error-overlay')).toHaveCount(0)
  await page.screenshot({ path: testInfo.outputPath('project-settings-copy.png') })
  await resetTab.click()
  await expect(resetTab).toHaveAttribute('aria-selected', 'true')
  await expect(dialog.getByRole('heading', { name: '项目复制' })).toBeHidden()
  await expect(dialog.getByRole('heading', { name: '项目重置' })).toBeVisible()
  await expect(dialog).toContainText('当前项目没有可重置的业务内容')
  await page.screenshot({ path: testInfo.outputPath('project-settings-reset.png') })
  expect(await page.title()).toBe('UPTON-PLM')
  expect(errors).toEqual([])
})

test('project plan week header centers ISO week and places Monday day on the gridline', async ({ page }) => {
  const plan = {
    id: 'plan-week-header', projectId, templateId: 'template-1', templateName: '设备模板', currentStage: 'Design',
    approvalStatus: 'Draft', baselineVersion: 0, plannedStart: '2026-09-07', plannedFinish: '2026-11-08', forecastFinish: '2026-11-08', rowVersion: 1,
    stages: [{ code: 'Design', name: '设计', participatesInDelivery: true, durationRatio: 1, progressRatio: 1 }],
    stageSchedules: [{ stage: 'Design', startDate: '2026-09-07', durationDays: 63 }],
    tasks: [{
      id: 'task-week-header', name: '机械设计', stage: 'Design', assignee: 'engineer', durationDays: 63,
      plannedStart: '2026-09-07', plannedFinish: '2026-11-08', completionPercent: 0, status: 'NotStarted',
      predecessorTaskIds: [], weight: 1, isMilestone: false, isRequired: true, sortOrder: 10,
    }],
    createdBy: 'admin', createdAt: '2026-09-10T00:00:00Z', updatedBy: 'admin', updatedAt: '2026-09-10T00:00:00Z',
  }
  await page.route(/\/api\/(?:project-plan-templates|projects\/[^/]+\/plan(?:\/portfolio)?)(?:\?.*)?$/, async route => {
    const path = new URL(route.request().url()).pathname
    if (path === '/api/project-plan-templates') return route.fulfill({ json: [] })
    if (path.endsWith('/portfolio')) return route.fulfill({ json: { rootProjectId: projectId, currentStage: 'Design', completionPercent: 0, laggingProjectCount: 0, riskProjectCount: 0, projects: [] } })
    return route.fulfill({ json: plan })
  })
  const errors: string[] = []
  page.on('pageerror', error => errors.push(error.message))
  page.on('console', message => { if (message.type() === 'error' || message.type() === 'warning') errors.push(message.text()) })
  await page.setViewportSize({ width: 1988, height: 1114 })
  await page.goto('/')
  const login = page.getByLabel('登录PLM')
  await login.getByRole('textbox', { name: '账号' }).fill('admin')
  await login.getByRole('textbox', { name: '密码' }).fill('correct-password')
  await login.getByRole('button', { name: '登录', exact: true }).click()
  await enterProject(page)
  await page.getByRole('button', { name: '项目计划', exact: true }).click()

  await page.getByLabel('甘特图缩放').getByRole('button', { name: '周', exact: true }).click()
  await expect(page.locator('.pdm-plan-switch')).toHaveText('休息日')
  await expect(page.locator('.pdm-gantt-calendar-band.is-year')).toHaveText('2026年')
  await expect(page.locator('.pdm-gantt-calendar-band.is-month')).toHaveText(['8月', '9月', '10月', '11月'])
  const ticks = page.locator('.pdm-gantt-tick')
  await expect(ticks.first().locator('.pdm-gantt-tick__week')).toHaveText(/^W\d{2}$/)
  await expect(ticks.first().locator('.pdm-gantt-tick__week-start')).toHaveText(/^\d{1,2}$/)
  const linePlacement = await ticks.nth(1).evaluate(tick => {
    const tickBox = tick.getBoundingClientRect()
    const dateBox = tick.querySelector<HTMLElement>('.pdm-gantt-tick__week-start')!.getBoundingClientRect()
    return { lineX: tickBox.left, dateCenterX: dateBox.left + dateBox.width / 2 }
  })
  expect(Math.abs(linePlacement.dateCenterX - linePlacement.lineX)).toBeLessThanOrEqual(1)
  const starts = await ticks.locator('time').evaluateAll(items => items.map(item => item.getAttribute('datetime')!))
  expect(starts.length).toBeGreaterThan(2)
  for (let index = 1; index < starts.length; index += 1) {
    expect(new Date(starts[index]!).getTime() - new Date(starts[index - 1]!).getTime()).toBe(7 * 86_400_000)
  }
  await expect(page.locator('vite-error-overlay')).toHaveCount(0)
  await page.screenshot({ path: join(tmpdir(), 'pdm-plan-week-header.png') })
  expect(await page.title()).toBe('UPTON-PLM')
  expect(errors).toEqual([])
})

test('hierarchy category links open the owning project BOM', async ({ page }, testInfo) => {
  const childId = '22222222-2222-2222-2222-222222222222'
  const base = { owner: '真实工程师', vaultLocation: 'D:\\PDM\\TEST', releaseLocation: 'D:\\Release\\TEST', isActive: true, quantity: 1, serialNumbers: [], designers: [], rootProjectId: projectId }
  const root = { ...base, id: projectId, code: 'PRJ-REAL-001', name: '导航测试产线', bomItemCategoryCode: '0301' }
  const child = { ...base, id: childId, code: 'PRJ-REAL-001-1', name: '导航测试子项目', parentProjectId: projectId, childSequence: 1, bomItemCategoryCode: '0302' }
  await page.route('**/api/projects**', async route => {
    const url = new URL(route.request().url())
    if (url.pathname === '/api/projects') return route.fulfill({ json: [root, child] })
    if (url.pathname === `/api/projects/${projectId}`) return route.fulfill({ json: root })
    if (url.pathname === `/api/projects/${childId}`) return route.fulfill({ json: child })
    if (url.pathname.startsWith(`/api/projects/${childId}/`)) return route.fallback({ url: url.toString().replace(childId, projectId) })
    return route.fallback()
  })
  const errors: string[] = []
  page.on('pageerror', error => errors.push(error.message))
  page.on('console', message => { if (message.type() === 'error' || message.type() === 'warning') errors.push(message.text()) })
  await page.setViewportSize({ width: 1988, height: 1114 })
  await page.goto('/')
  const login = page.getByLabel('登录PLM')
  await login.getByRole('textbox', { name: '账号' }).fill('engineer')
  await login.getByRole('textbox', { name: '密码' }).fill('correct-password')
  await login.getByRole('button', { name: '登录', exact: true }).click()
  await enterProject(page)
  await page.getByRole('button', { name: 'BOM', exact: true }).click()
  await page.getByRole('tab', { name: '多级总览' }).click()
  for (const category of ['标准件BOM', '非标件BOM', '电气BOM']) {
    const expandChild = page.getByRole('button', { name: '展开PRJ-REAL-001-1的三类BOM', exact: true })
    if (await expandChild.isVisible()) await expandChild.click()
    await expect(page.getByRole('button', { name: `进入PRJ-REAL-001-1的${category}`, exact: true })).toBeVisible()
    if (category === '标准件BOM') await page.screenshot({ path: testInfo.outputPath('hierarchy-links.png') })
    await page.getByRole('button', { name: `进入PRJ-REAL-001-1的${category}`, exact: true }).click()
    await expect(page.getByRole('tab', { name: category })).toHaveAttribute('aria-selected', 'true')
    await expect(page.locator('.pdm-project-detail')).toContainText('导航测试子项目')
    await expect(page.getByLabel('BOM维护')).toBeVisible()
    if (category === '标准件BOM') await page.screenshot({ path: testInfo.outputPath('child-standard-bom.png') })
    await page.getByRole('tab', { name: '多级总览' }).click()
  }
  await page.getByRole('button', { name: '进入PRJ-REAL-001的标准件BOM', exact: true }).click()
  await expect(page.getByRole('tab', { name: '标准件BOM' })).toHaveAttribute('aria-selected', 'true')
  await expect(page.locator('.pdm-project-detail')).toContainText('导航测试产线')
  await expect(page.locator('vite-error-overlay')).toHaveCount(0)
  expect(await page.title()).not.toBe('')
  expect(errors).toEqual([])
})

test('project plan toolbar uses uniform buttons and shows actual completion date', async ({ page }, testInfo) => {
  const plan = {
    id: 'plan-1', projectId, templateId: 'template-1', templateName: '设备模板', currentStage: 'Design',
    approvalStatus: 'Draft', baselineVersion: 0, plannedStart: '2026-09-10', plannedFinish: '2026-09-22', forecastFinish: '2026-09-22', rowVersion: 1,
    stages: [{ code: 'Design', name: '设计', participatesInDelivery: true, durationRatio: 1, progressRatio: 1 }],
    stageSchedules: [{ stage: 'Design', startDate: '2026-09-10', durationDays: 13 }],
    tasks: [{
      id: 'task-1', name: '机械设计', stage: 'Design', assignee: 'engineer', durationDays: 13,
      plannedStart: '2026-09-10', plannedFinish: '2026-09-22', actualStart: '2026-09-10', actualFinish: '2026-09-20',
      completionPercent: 100, status: 'Completed', predecessorTaskIds: [], weight: 1, isMilestone: false, isRequired: true, sortOrder: 10,
    }],
    createdBy: 'admin', createdAt: '2026-09-10T00:00:00Z', updatedBy: 'admin', updatedAt: '2026-09-10T00:00:00Z',
  }
  await page.route(/\/api\/(?:project-plan-templates|projects\/[^/]+\/plan(?:\/portfolio)?)(?:\?.*)?$/, async route => {
    const path = new URL(route.request().url()).pathname
    const body = path === '/api/project-plan-templates'
      ? []
      : path.endsWith('/portfolio')
        ? { rootProjectId: projectId, currentStage: 'Design', completionPercent: 100, laggingProjectCount: 0, riskProjectCount: 0, projects: [] }
        : plan
    await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(body) })
  })
  const errors: string[] = []
  page.on('console', message => {
    if (message.type() === 'error') errors.push(`${message.location().url}: ${message.text()}`)
  })
  await page.setViewportSize({ width: 1920, height: 1080 })
  await page.goto('/')
  const login = page.getByLabel('登录PLM')
  await login.getByRole('textbox', { name: '账号' }).fill('admin')
  await login.getByRole('textbox', { name: '密码' }).fill('correct-password')
  await login.getByRole('button', { name: '登录', exact: true }).click()
  await enterProject(page)
  await page.getByRole('button', { name: '项目计划', exact: true }).click()

  const toolbar = page.locator('.pdm-plan-toolbar')
  await expect(toolbar).toBeVisible()
  const buttonSizes = await toolbar.locator('.pdm-plan-view-tabs > button, .pdm-plan-toolbar__actions > button').evaluateAll(buttons => buttons.map(button => {
    const rect = button.getBoundingClientRect()
    return { text: button.textContent?.trim(), width: rect.width, height: rect.height }
  }))
  expect(buttonSizes.length).toBeGreaterThan(2)
  expect(buttonSizes.every(button => button.width === 80 && button.height === 32)).toBe(true)
  const headerCells = await page.locator('.pdm-gantt-info-head > span').evaluateAll(cells => cells.map(cell => {
    const rect = cell.getBoundingClientRect()
    return { text: cell.textContent?.trim(), left: rect.left, centerY: rect.top + rect.height / 2 }
  }))
  expect(headerCells.map(cell => cell.text)).toEqual(['项目 / 任务', '阶段', '责任人', '进度', '计划日期', '工期', '完成日期'])
  expect(headerCells.every((cell, index) => index === 0 || cell.left > headerCells[index - 1]!.left), JSON.stringify(headerCells)).toBe(true)
  expect(Math.max(...headerCells.map(cell => cell.centerY)) - Math.min(...headerCells.map(cell => cell.centerY))).toBeLessThanOrEqual(1)
  await expect(page.locator('.pdm-gantt-info-row.is-task > span').nth(6)).toHaveText('2026-09-20')
  await toolbar.getByRole('button', { name: '日', exact: true }).click()
  await expect(toolbar.getByRole('button', { name: '日', exact: true })).toHaveClass(/is-active/)
  await toolbar.getByRole('button', { name: '周', exact: true }).click()
  await expect(toolbar.getByRole('button', { name: '周', exact: true })).toHaveClass(/is-active/)
  await expect(page.locator('.pdm-gantt-tick', { hasText: 'W37' }).first()).toBeVisible()
  const todayMarker = await page.locator('.pdm-gantt-today.is-header').evaluate(marker => {
    const line = marker.getBoundingClientRect()
    const label = marker.querySelector<HTMLElement>('.pdm-gantt-today__label')!.getBoundingClientRect()
    return { text: marker.textContent?.trim(), lineX: line.left, labelCenterX: label.left + label.width / 2 }
  })
  expect(todayMarker.text).toBe('今天')
  expect(Math.abs(todayMarker.labelCenterX - todayMarker.lineX)).toBeLessThanOrEqual(1)
  await expect(page.locator('vite-error-overlay')).toHaveCount(0)
  expect(errors).toEqual([])
  await page.screenshot({ path: testInfo.outputPath('project-plan-toolbar-80x32-completion-date.png'), fullPage: false })
  await page.setViewportSize({ width: 1285, height: 1114 })
  await page.screenshot({ path: testInfo.outputPath('project-plan-week-number-today-1285.png'), fullPage: false })
})

test('project plan stages can overlap and moving one stage keeps unrelated stages unchanged', async ({ page }, testInfo) => {
  const project = { id: projectId, code: 'PRJ-REAL-001', name: '并行排期项目', owner: 'admin', primaryProjectManager: 'admin', collaborativeProjectManagers: [],
    vaultLocation: 'D:\\PDM\\PRJ-REAL-001', releaseLocation: 'D:\\Release\\PRJ-REAL-001', isActive: true, quantity: 1, serialNumbers: [], designers: [] }
  const plan = {
    id: 'parallel-plan', projectId, templateId: 'template-1', templateName: '并行模板', currentStage: 'Design',
    approvalStatus: 'Draft', baselineVersion: 0, plannedStart: '2026-09-05', plannedFinish: '2026-09-25', forecastFinish: '2026-09-25', rowVersion: 1,
    stages: [
      { code: 'Design', name: '设计', participatesInDelivery: true, durationRatio: .5, progressRatio: .5 },
      { code: 'Preparation', name: '备料', participatesInDelivery: true, durationRatio: .5, progressRatio: .5 },
    ],
    stageSchedules: [
      { stage: 'Design', startDate: '2026-09-05', durationDays: 11 },
      { stage: 'Preparation', startDate: '2026-09-16', durationDays: 10 },
    ],
    tasks: [
      { id: 'design-task', name: '设计任务', stage: 'Design', assignee: 'admin', durationDays: 14, plannedStart: '2026-09-05', plannedFinish: '2026-09-18', completionPercent: 0, status: 'NotStarted', predecessorTaskIds: [], weight: 1, isMilestone: false, isRequired: true, sortOrder: 10 },
      { id: 'prepare-task', name: '备料任务', stage: 'Preparation', assignee: 'admin', durationDays: 16, plannedStart: '2026-09-10', plannedFinish: '2026-09-25', completionPercent: 0, status: 'NotStarted', predecessorTaskIds: [], weight: 1, isMilestone: false, isRequired: true, sortOrder: 20 },
    ],
    createdBy: 'admin', createdAt: '2026-09-10T00:00:00Z', updatedBy: 'admin', updatedAt: '2026-09-10T00:00:00Z',
  }
  let saved: { tasks: typeof plan.tasks; changeReason: string } | null = null
  await page.route('**/api/projects**', async route => {
    const url = new URL(route.request().url())
    if (url.pathname === '/api/projects') return route.fulfill({ json: [project] })
    if (url.pathname === `/api/projects/${projectId}`) return route.fulfill({ json: project })
    return route.fallback()
  })
  await page.route(/\/api\/(?:project-plan-templates|projects\/[^/]+\/plan(?:\/portfolio)?)(?:\?.*)?$/, async route => {
    const path = new URL(route.request().url()).pathname
    if (path === '/api/project-plan-templates') return route.fulfill({ json: [] })
    if (path.endsWith('/portfolio')) return route.fulfill({ json: { rootProjectId: projectId, currentStage: 'Design', completionPercent: 0, laggingProjectCount: 0, riskProjectCount: 0, projects: [] } })
    if (route.request().method() === 'PUT') {
      saved = route.request().postDataJSON() as typeof saved
      return route.fulfill({ json: { ...plan, ...(saved ?? {}), rowVersion: 2 } })
    }
    return route.fulfill({ json: plan })
  })
  const errors: string[] = []
  page.on('pageerror', error => errors.push(error.message))
  page.on('console', message => { if (message.type() === 'error' || message.type() === 'warning') errors.push(message.text()) })
  await page.setViewportSize({ width: 1988, height: 1114 })
  await page.goto('/')
  const login = page.getByLabel('登录PLM')
  await login.getByRole('textbox', { name: '账号' }).fill('admin')
  await login.getByRole('textbox', { name: '密码' }).fill('correct-password')
  await login.getByRole('button', { name: '登录', exact: true }).click()
  await enterProject(page)
  await page.getByRole('button', { name: '项目计划', exact: true }).click()

  await expect(page.getByText(/拖动阶段条可整体平移/)).toHaveCount(0)
  await expect(page.getByText(/保存主项目计划会同步跟随且未批准/)).toHaveCount(0)
  await expect(page.locator('.pdm-plan-summary')).toContainText('生效信息')
  await expect(page.locator('.pdm-plan-summary')).toContainText('草稿')
  await expect(page.getByText('资源冲突', { exact: true })).toHaveCount(0)
  await expect(page.getByRole('button', { name: '收起阶段', exact: true })).toHaveCount(0)
  await expect(page.getByRole('button', { name: '折叠列', exact: true })).toHaveCount(0)
  await page.getByRole('button', { name: '收起全部阶段', exact: true }).click()
  await expect(page.getByRole('button', { name: '展开全部阶段', exact: true })).toBeVisible()
  await page.getByRole('button', { name: '展开全部阶段', exact: true }).click()
  await page.getByRole('button', { name: '折叠信息列', exact: true }).click()
  await expect(page.getByRole('button', { name: '展开信息列', exact: true })).toBeVisible()
  await page.getByRole('button', { name: '展开信息列', exact: true }).click()
  const infoRowHeights = await page.locator('.pdm-gantt-info-row').evaluateAll(rows => rows.map(row => row.getBoundingClientRect().height))
  const timelineRowHeights = await page.locator('.pdm-gantt-timeline-row').evaluateAll(rows => rows.map(row => row.getBoundingClientRect().height))
  expect([...new Set(infoRowHeights)]).toEqual([40])
  expect([...new Set(timelineRowHeights)]).toEqual([40])
  const stageBars = page.locator('.pdm-gantt-timeline-row.is-stage .pdm-gantt-bar')
  await expect(stageBars).toHaveCount(2)
  const boxes = await stageBars.evaluateAll(items => items.map(item => {
    const rect = item.getBoundingClientRect()
    return { left: rect.left, right: rect.right }
  }))
  expect(boxes[0]!.right).toBeGreaterThan(boxes[1]!.left)
  const first = await stageBars.first().boundingBox()
  expect(first).not.toBeNull()
  await page.mouse.move(first!.x + first!.width / 2, first!.y + first!.height / 2)
  await page.mouse.down()
  await page.mouse.move(first!.x + first!.width / 2 + 60, first!.y + first!.height / 2)
  await page.mouse.up()
  await expect.poll(() => saved).not.toBeNull()
  const design = saved!.tasks.find(task => task.id === 'design-task')!
  const preparation = saved!.tasks.find(task => task.id === 'prepare-task')!
  expect(design.plannedStart).not.toBe('2026-09-05')
  expect(preparation.plannedStart).toBe('2026-09-10')
  expect(preparation.plannedFinish).toBe('2026-09-25')
  expect(saved!.changeReason).toContain('其他阶段不再保持首尾连续')
  await expect(page.locator('vite-error-overlay')).toHaveCount(0)
  expect(errors).toEqual([])
  await page.screenshot({ path: testInfo.outputPath('project-plan-overlapping-stages.png'), fullPage: false })
})

test('engineer logs in and reads the API-backed PLM workspace', async ({ page }, testInfo) => {
  await page.setViewportSize({ width: 1920, height: 1080 })
  const projectTabErrors: string[] = []
  page.on('pageerror', error => projectTabErrors.push(error.message))
  page.on('console', message => {
    if (message.type() === 'error' || message.type() === 'warning') projectTabErrors.push(message.text())
  })
  const contentResponses: number[] = []
  page.on('response', response => {
    if (response.url().includes('globalStatusContent')) contentResponses.push(response.status())
  })
  await page.addInitScript(() => {
    Object.defineProperty(window, 'pdmHostMessages', {
      configurable: true,
      value: [],
      writable: true,
    })
    Object.defineProperty(window.chrome, 'webview', {
      configurable: true,
      value: { postMessage: (message: unknown) => (window as unknown as { pdmHostMessages: unknown[] }).pdmHostMessages.push(message), addEventListener: () => undefined },
    })
  })
  await page.goto('/')

  await expect(page.getByRole('main', { name: '未登录主页' })).toBeVisible()
  const loginForm = page.getByLabel('登录PLM')
  await loginForm.getByRole('textbox', { name: '账号' }).fill('engineer')
  await loginForm.getByRole('textbox', { name: '密码' }).fill('correct-password')
  await loginForm.getByRole('button', { name: '登录', exact: true }).click()
  await expect(page.getByRole('button', { name: '项目中心', exact: true })).toBeVisible()
  const titlebar = page.locator('.pdm-titlebar')
  await expect(titlebar).toContainText('昆山阿普顿自动化系统有限公司')
  await expect(titlebar).toContainText('工程师')
  await expect(titlebar.getByRole('button', { name: '消息' })).toBeVisible()
  await expect(titlebar.getByRole('button', { name: '主题' })).toBeVisible()
  await expect(titlebar.getByRole('button', { name: '退出' })).toBeVisible()
  const idleStatus = titlebar.locator('.pdm-global-status.is-idle')
  await expect(idleStatus).toBeVisible()
  await expect(idleStatus.locator('.pdm-global-status__text')).not.toBeEmpty()
  await expect(idleStatus.locator('.pdm-global-status__source')).not.toBeEmpty()
  await expect(idleStatus).not.toContainText('UPTON')
  await idleStatus.click()
  await expect(page.locator('.pdm-global-status-detail')).toContainText('来源：')
  await page.screenshot({ path: testInfo.outputPath('global-status-content.png'), fullPage: false })
  await page.keyboard.press('Escape')
  await page.setViewportSize({ width: 1285, height: 1114 })
  const statusGeometry = await page.evaluate(() => {
    const company = document.querySelector<HTMLElement>('.pdm-tenant-company')?.getBoundingClientRect()
    const statusElement = document.querySelector<HTMLElement>('.pdm-global-status')
    const textElement = statusElement?.querySelector<HTMLElement>('.pdm-global-status__text')
    const sourceElement = statusElement?.querySelector<HTMLElement>('.pdm-global-status__source')
    const status = statusElement?.getBoundingClientRect()
    const text = textElement?.getBoundingClientRect()
    const source = sourceElement?.getBoundingClientRect()
    return {
      gap: company && status ? status.left - company.right : -1,
      overflow: document.documentElement.scrollWidth - window.innerWidth,
      lineHeight: statusElement ? getComputedStyle(statusElement).lineHeight : '',
      sourceFontWeight: sourceElement ? getComputedStyle(sourceElement).fontWeight : '',
      textInside: Boolean(status && text && text.top >= status.top && text.bottom <= status.bottom),
      sourceInside: Boolean(status && source && source.top >= status.top && source.bottom <= status.bottom),
    }
  })
  expect(Math.abs(statusGeometry.gap - 50)).toBeLessThanOrEqual(1)
  expect(statusGeometry.overflow).toBeLessThanOrEqual(0)
  expect(statusGeometry.lineHeight).toBe('20px')
  expect(statusGeometry.sourceFontWeight).toBe('400')
  expect(statusGeometry.textInside).toBe(true)
  expect(statusGeometry.sourceInside).toBe(true)
  await page.screenshot({ path: testInfo.outputPath('global-status-content-1285.png'), fullPage: false })
  expect(contentResponses).toContain(200)
  await page.setViewportSize({ width: 1920, height: 1080 })
  const crmShellLayout = await page.evaluate(() => {
    const sidebar = document.querySelector<HTMLElement>('.pdm-sidebar')
    const brand = document.querySelector<HTMLElement>('.pdm-sidebar__brand')
    const navItem = document.querySelector<HTMLElement>('.pdm-nav-item')
    const titlebar = document.querySelector<HTMLElement>('.pdm-titlebar')
    const main = document.querySelector<HTMLElement>('.pdm-main')
    const sidebarStyle = sidebar ? getComputedStyle(sidebar) : null
    const titlebarStyle = titlebar ? getComputedStyle(titlebar) : null
    const mainStyle = main ? getComputedStyle(main) : null
    return {
      sidebarWidth: sidebar?.getBoundingClientRect().width ?? 0,
      brandHeight: brand?.getBoundingClientRect().height ?? 0,
      navItemHeight: navItem?.getBoundingClientRect().height ?? 0,
      titlebarHeight: titlebar?.getBoundingClientRect().height ?? 0,
      sidebarBackground: sidebarStyle?.backgroundImage ?? '',
      titlebarBackground: titlebarStyle?.backgroundColor ?? '',
      mainPadding: mainStyle?.paddingTop ?? '',
    }
  })
  expect(crmShellLayout.sidebarWidth).toBe(155)
  expect(crmShellLayout.brandHeight).toBe(62)
  expect(crmShellLayout.navItemHeight).toBe(56)
  expect(crmShellLayout.titlebarHeight).toBe(62)
  expect(crmShellLayout.sidebarBackground).toContain('linear-gradient')
  expect(crmShellLayout.titlebarBackground).toBe('rgb(255, 255, 255)')
  expect(crmShellLayout.mainPadding).toBe('5px')
  await page.screenshot({ path: testInfo.outputPath('crm-shell-project-center.png'), fullPage: false })
  await page.getByRole('banner').getByRole('button', { name: '主题' }).click()
  await page.getByRole('menuitem', { name: /石墨青绿/ }).click()
  await expect(page.locator('.pdm-app-shell')).toHaveClass(/theme-c/)
  await expect.poll(() => page.evaluate(() => localStorage.getItem('pdm_theme'))).toBe('c')
  await page.getByRole('banner').getByRole('button', { name: '消息' }).click()
  await expect(page.getByLabel('我的待办')).toBeVisible()
  await page.getByRole('button', { name: '项目列表', exact: true }).click()
  await expect(page.getByRole('columnheader', { name: '序列号' })).toBeVisible()
  await expect(page.getByRole('cell', { name: '70000001' })).toBeVisible()
  await page.getByLabel('事业部筛选').selectOption('自动化事业部')
  await page.getByLabel('项目经理筛选').selectOption('project-manager-2')
  await page.getByLabel('主设工程师筛选').selectOption('design-lead')
  await expect(page.getByRole('button', { name: '进入项目 PRJ-REAL-001', exact: true })).toBeVisible()
  await page.getByRole('button', { name: '进入项目 PRJ-REAL-001', exact: true }).click()

  await expect(page.locator('.pdm-project-sidebar__summary').getByText('PRJ-REAL-001 · 真实装配项目', { exact: true })).toBeVisible()
  await expect(page.getByRole('banner').getByText('真实工程师', { exact: true })).toBeVisible()
  await expect(page.locator('.pdm-project-tabs button')).toHaveText(['概览', '文件', '项目计划', '验证计划', '图档', 'BOM', '备料', '发布', '记录', '设置'])
  await page.getByRole('button', { name: '文件', exact: true }).click()
  await expect(page.getByText('项目文件夹', { exact: true })).toBeVisible()
  await page.screenshot({ path: testInfo.outputPath('project-tabs-overview-file-project-plan-1920.png'), fullPage: false })
  await page.setViewportSize({ width: 1285, height: 1114 })
  await expect(page.locator('.pdm-project-tabs button')).toHaveText(['概览', '文件', '项目计划', '验证计划', '图档', 'BOM', '备料', '发布', '记录', '设置'])
  await page.screenshot({ path: testInfo.outputPath('project-tabs-overview-file-project-plan-1285.png'), fullPage: false })
  await expect(page.locator('vite-error-overlay')).toHaveCount(0)
  expect(projectTabErrors).toEqual([])
  await page.setViewportSize({ width: 1920, height: 1080 })
  await expect(page.getByText('机械图纸', { exact: true })).toBeVisible()
  await page.getByText('PRJ-REAL-001-0', { exact: true }).first().click()
  const fileDetails = page.getByRole('table', { name: '受控图档' })
  await expect(fileDetails).toBeVisible()
  await expect(fileDetails.getByRole('columnheader')).toHaveCount(7)
  const fileDetailWidths = await fileDetails.evaluate(table => {
    const headers = [...table.querySelectorAll<HTMLElement>('th')]
    return { table: table.getBoundingClientRect().width, number: headers[0]?.getBoundingClientRect().width ?? 0, name: headers[1]?.getBoundingClientRect().width ?? 0, updated: headers[6]?.getBoundingClientRect().width ?? 0 }
  })
  expect(fileDetailWidths.table).toBeGreaterThanOrEqual(1080)
  expect(fileDetailWidths.number).toBeGreaterThanOrEqual(200)
  expect(fileDetailWidths.name).toBeGreaterThanOrEqual(260)
  expect(fileDetailWidths.updated).toBeGreaterThanOrEqual(170)
  await page.getByRole('button', { name: '图档', exact: true }).click()
  await page.evaluate(({ projectId }) => window.dispatchEvent(new CustomEvent('pdm-workspace-local-state', { detail: {
    projectId,
    projectCode: 'PRJ-REAL-001',
    projectDirectory: 'E:\\Workspace\\View\\PRJ-REAL-001',
    projectDirectoryExists: true,
    items: [
      { documentId: 'doc-root', fileName: 'REAL-ASM-001.SLDASM', fullPath: 'E:\\Workspace\\View\\PRJ-REAL-001\\REAL-ASM-001.SLDASM', localState: 'Editable', localStateLabel: '可编辑', localRevision: 'W2', latestRevision: 'W2', message: '已由你检出，可以继续编辑。', isReadOnly: false },
      { documentId: 'doc-part', fileName: 'REAL-PRT-001.SLDPRT', fullPath: 'E:\\Workspace\\View\\PRJ-REAL-001\\REAL-PRT-001.SLDPRT', localState: 'NeedsUpdate', localStateLabel: '需要更新', localRevision: 'W1', latestRevision: 'A', message: '本地版本需要更新。', isReadOnly: true },
    ],
  } })), { projectId })
  await expect(page.getByLabel('项目设计树')).toContainText('REAL-PRT-001')
  await expect(page.getByLabel('工作区位置')).toContainText('可编辑')
  await expect(page.getByLabel('项目设计树')).toContainText('需要更新')
  await page.getByRole('button', { name: '打开文件夹', exact: true }).click()
  await expect.poll(() => page.evaluate(() => (window as unknown as { pdmHostMessages: Array<{ type?: string }> }).pdmHostMessages.some(message => message.type === 'workspace-open-folder'))).toBe(true)
  await expect(page.getByLabel('工作版本 W2')).toHaveText('W2')
  await expect(page.getByLabel('业务状态 工作中')).toHaveText('工作中')
  await expect(page.getByLabel('图档预览').getByText('可编辑', { exact: true })).toBeVisible()
  await expect(page.getByLabel('项目基本信息与全部项目号').getByLabel('BOM完整性')).toHaveCount(0)
  await expect(page.getByLabel('项目基本信息与全部项目号').getByLabel('当前发布包')).toHaveCount(0)
  const previewLayout = await page.evaluate(() => {
    const preview = document.querySelector<HTMLElement>('[aria-label="客户端内嵌eDrawings预览区"]')?.getBoundingClientRect()
    const controls = document.querySelector<HTMLElement>('[aria-label="图档查看与操作"]')?.getBoundingClientRect()
    const family = document.querySelector<HTMLElement>('.pdm-project-family')?.getBoundingClientRect()
    const sidebar = document.querySelector<HTMLElement>('.pdm-project-sidebar')?.getBoundingClientRect()
    return { previewTop: preview?.top ?? -1, controlsBottom: controls?.bottom ?? -1, familyBottom: family?.bottom ?? -1, sidebarWidth: sidebar?.width ?? -1, sidebarBottom: sidebar?.bottom ?? -1, viewportHeight: window.innerHeight, pageHeight: document.documentElement.scrollHeight }
  })
  expect(previewLayout.previewTop).toBeGreaterThanOrEqual(previewLayout.controlsBottom)
  expect(previewLayout.previewTop - previewLayout.controlsBottom).toBeLessThanOrEqual(1)
  expect(Math.abs(previewLayout.sidebarWidth - 200)).toBeLessThanOrEqual(2)
  expect(Math.abs(previewLayout.familyBottom - previewLayout.sidebarBottom)).toBeLessThanOrEqual(2)
  expect(previewLayout.sidebarBottom).toBeLessThanOrEqual(previewLayout.viewportHeight)
  expect(previewLayout.pageHeight).toBeLessThanOrEqual(previewLayout.viewportHeight)
  await expect(page.getByText('PRJ-2026-018')).toHaveCount(0)

  const markupToolbar = page.getByLabel('图形批注工具')
  await expect(markupToolbar.locator('button')).toHaveCount(4)
  expect(await markupToolbar.locator('button').evaluateAll(buttons => buttons.map(button => button.getAttribute('aria-label')))).toEqual([
    '引线批注',
    '云线批注',
    '框选批注',
    '手绘批注',
  ])
  await expect(page.getByRole('button', { name: '使用位置' })).toHaveCount(0)
  await expect(page.getByRole('button', { name: '作废图档' })).toHaveCount(0)
  await expect(page.getByLabel('图档属性')).toHaveCount(0)
  await page.getByRole('button', { name: '保存批注' }).click()
  await expect.poll(() => page.evaluate(() => (window as unknown as { pdmHostMessages: Array<{ type?: string }> }).pdmHostMessages.some(message => message.type === 'preview-host-save-markup'))).toBe(true)
  await page.evaluate(() => window.dispatchEvent(new CustomEvent('pdm-preview-markup-status', { detail: { state: 'saved', message: '批注已保存。' } })))

  await page.evaluate(() => window.dispatchEvent(new CustomEvent('pdm-solidworks-capability', { detail: { available: true } })))
  const previewCommands = page.locator('.pdm-preview-command')
  await expect(previewCommands).toHaveCount(3)
  expect(await previewCommands.evaluateAll(buttons => buttons.map(button => Math.round(button.getBoundingClientRect().width)))).toEqual([100, 100, 100])
  await page.evaluate(() => window.dispatchEvent(new CustomEvent('pdm-preview-markup-status', { detail: { state: 'dirty', message: '批注尚未保存。' } })))
  await expect(page.getByRole('button', { name: '保存批注' })).toHaveClass(/is-markup-dirty/)
  const solidWorksButton = page.getByRole('button', { name: '打开最新' })
  await expect(solidWorksButton).toBeEnabled()
  await page.screenshot({ path: testInfo.outputPath('controlled-open-actions.png'), fullPage: false })
  await solidWorksButton.click()
  await expect.poll(() => page.evaluate(() => (window as unknown as { pdmHostMessages: Array<{ type?: string; payload?: { mode?: string } }> }).pdmHostMessages?.filter(message => message.type === 'open-document').at(-1))).toMatchObject({ type: 'open-document', payload: { mode: 'LatestReadOnly' } })

  await page.getByRole('treeitem').first().click({ button: 'right' })
  await expect(page.getByRole('menuitem', { name: '获取最新版本并编辑' })).toHaveCount(0)
  const releasedButton = page.getByRole('menuitem', { name: '只读打开最近正式发布版' })
  await expect(releasedButton).toBeVisible()
  await page.screenshot({ path: testInfo.outputPath('controlled-open-context-menu.png'), fullPage: false })
  await releasedButton.click()
  await expect.poll(() => page.evaluate(() => (window as unknown as { pdmHostMessages: Array<{ type?: string; payload?: { mode?: string } }> }).pdmHostMessages?.filter(message => message.type === 'open-document').at(-1))).toMatchObject({ type: 'open-document', payload: { mode: 'LatestReleased' } })

  await page.getByRole('tab', { name: /^2D/ }).click()
  await expect(page.getByLabel('项目设计树')).toContainText('真实总装工程图')
  await expect(page.getByLabel('图档预览')).toContainText('2D工程图')
  await expect(page.getByLabel('图档预览')).toContainText('关联模型')
  await page.getByLabel('图档预览').getByRole('button', { name: 'REAL-ASM-001' }).click()
  await expect(page.getByLabel('图档预览')).toContainText('3D模型')

  await page.getByRole('button', { name: 'BOM', exact: true }).click()
  await page.getByRole('tab', { name: '标准件BOM' }).click()
  await expect(page.locator('.pdm-edit-table tbody tr').first().getByRole('button', { name: '编辑物料编码' })).toHaveText('REAL-STD-001')
  await page.locator('.pdm-edit-table tbody tr').first().getByLabel('选择物料').check()
  await expect(page.getByText(/已选择 1 项/)).toBeVisible()
  await page.getByRole('button', { name: '编辑', exact: true }).click()
  const batchDialog = page.getByRole('dialog', { name: '批量编辑BOM属性' })
  await expect(batchDialog).toBeVisible()
  await expect(batchDialog.getByText('1 条物料 · 0 个属性')).toBeVisible()
  await batchDialog.getByLabel('修改品牌').check()
  await batchDialog.getByPlaceholder('勾选后留空即清空').nth(2).fill('UPTON')
  await batchDialog.getByLabel('修改易损件').check()
  await batchDialog.getByLabel('易损件批量值').selectOption({ label: '否' })
  await expect(batchDialog.getByText('1 条物料 · 2 个属性')).toBeVisible()
  await page.screenshot({ path: join(tmpdir(), 'pdm-bom-batch-wearpart-20260915.png'), fullPage: false })
  await batchDialog.getByRole('button', { name: '取消' }).click()
  await expect(batchDialog).toHaveCount(0)
})

test('BOM duplicate candidates require confirmation and summary quantity is editable', async ({ page }, testInfo) => {
  const consoleMessages: string[] = []
  const failedResponses: string[] = []
  page.on('console', message => {
    if (message.type() === 'error' || message.type() === 'warning') consoleMessages.push(`${message.type()}: ${message.text()}`)
  })
  page.on('response', response => {
    if (response.status() >= 400) failedResponses.push(`${response.status()} ${response.url()}`)
  })
  await page.setViewportSize({ width: 1440, height: 900 })
  await page.goto('/')
  const loginForm = page.getByLabel('登录PLM')
  await loginForm.getByRole('textbox', { name: '账号' }).fill('engineer')
  await loginForm.getByRole('textbox', { name: '密码' }).fill('correct-password')
  await loginForm.getByRole('button', { name: '登录', exact: true }).click()
  await enterProject(page)
  await page.getByRole('button', { name: 'BOM', exact: true }).click()
  await page.getByRole('tab', { name: '标准件BOM' }).click()

  const materialCodeActions = page.locator('.pdm-bom-selection-actions .pdm-bom-material-code-toolbar-action')
  await expect(materialCodeActions).toHaveText(['引用物料', '关联物料', '核对料号', '申请料号'])
  expect(await materialCodeActions.evaluateAll(buttons => buttons.map(button => {
    const box = button.getBoundingClientRect()
    return { width: box.width, height: box.height }
  }))).toEqual([
    { width: 70, height: 28 },
    { width: 70, height: 28 },
    { width: 70, height: 28 },
    { width: 70, height: 28 },
  ])
  await page.screenshot({ path: testInfo.outputPath('bom-material-code-actions.png'), fullPage: false })
  await page.getByRole('button', { name: '引用物料', exact: true }).click()
  const materialReferenceDialog = page.getByRole('dialog', { name: '引用物料' })
  await expect(materialReferenceDialog).toBeVisible()
  await expect(materialReferenceDialog.getByRole('button', { name: '普通料品', exact: true })).toBeVisible()
  await expect(materialReferenceDialog.getByRole('button', { name: 'UKIT套件', exact: true })).toBeVisible()
  await expect(page.getByRole('button', { name: '引用套件', exact: true })).toHaveCount(0)
  await page.screenshot({ path: testInfo.outputPath('bom-unified-material-reference.png'), fullPage: false })
  await materialReferenceDialog.getByRole('button', { name: '关闭物料引用' }).click()

  const row = page.locator('.pdm-edit-table tbody tr').first()
  await expect(row.getByRole('button', { name: '编辑数量' })).toHaveText('10')
  await row.getByRole('button', { name: '编辑数量' }).click()
  await row.getByLabel('内联编辑数量').fill('2')
  await row.getByLabel('内联编辑数量').press('Enter')
  const quantityDialog = page.getByRole('dialog', { name: '分配汇总数量到结构位置' })
  await expect(quantityDialog).toBeVisible()
  await expect(quantityDialog.getByText('汇总数量 10 → 2')).toBeVisible()
  await expect(quantityDialog.getByText('REAL-STD-001')).toBeVisible()
  await expect(quantityDialog.getByText('标准紧固件')).toBeVisible()
  await expect(quantityDialog.getByText('M8')).toBeVisible()
  await expect(quantityDialog.getByText('FESTO')).toBeVisible()
  await expect(quantityDialog.getByRole('row')).toHaveCount(3)
  await expect(row.getByRole('button', { name: '编辑数量' })).toHaveText('10')
  await expect(quantityDialog.getByRole('button', { name: '确认分配' })).toBeDisabled()
  const quantityInputs = quantityDialog.getByRole('spinbutton')
  await quantityInputs.nth(0).fill('0')
  await quantityInputs.nth(1).fill('2')
  await expect(quantityDialog.getByText('待分配 0')).toBeVisible()
  await expect(quantityDialog.getByText('其中 1 个位置数量为 0')).toBeVisible()
  await page.screenshot({ path: testInfo.outputPath('summary-quantity-location.png'), fullPage: false })
  await quantityDialog.getByRole('button', { name: '确认分配' }).click()
  await expect(page.getByText('保存BOM时这些位置将自动移入回收站')).toBeVisible()
  await page.getByRole('button', { name: '确认删除并继续' }).click()
  await expect(quantityDialog).toHaveCount(0)
  await expect(row.getByRole('button', { name: '编辑数量' })).toHaveText('2')

  await row.getByRole('button', { name: '编辑型号' }).click()
  await row.getByLabel('内联编辑型号').fill('PH602')
  await row.getByLabel('内联编辑型号').press('Enter')
  const duplicateDialog = page.getByRole('dialog', { name: '重复料品，请确认选择' })
  await expect(duplicateDialog).toBeVisible()
  await expect(duplicateDialog.getByRole('row')).toHaveCount(3)
  await expect.poll(() => duplicateDialog.evaluate(element => getComputedStyle(element).transform)).toBe('none')
  const dialogBox = await duplicateDialog.boundingBox()
  const confirmButtonBoxes = await duplicateDialog.getByRole('button', { name: '确认选择' }).evaluateAll(buttons => buttons.map(button => {
    const box = button.getBoundingClientRect()
    return { left: box.left, right: box.right }
  }))
  const viewportWidth = await page.evaluate(() => window.innerWidth)
  expect(dialogBox?.width).toBeGreaterThan(900)
  expect(confirmButtonBoxes.every(box => box.left >= 0 && box.right <= viewportWidth)).toBe(true)
  await expect(row.getByRole('button', { name: '编辑物料编码' })).toHaveText('REAL-STD-001')
  await page.screenshot({ path: testInfo.outputPath('duplicate-material-choice.png'), fullPage: false })
  await duplicateDialog.getByRole('row').filter({ hasText: '01020000602' }).getByRole('button', { name: '确认选择' }).click()
  await expect(duplicateDialog).toHaveCount(0)
  await expect(row.getByRole('button', { name: '编辑物料编码' })).toHaveText('01020000602')
  await page.screenshot({ path: testInfo.outputPath('bom-duplicate-confirmation.png'), fullPage: false })
  expect({ consoleMessages, failedResponses }).toEqual({ consoleMessages: [], failedResponses: [] })
})

test('project numbers remain fully visible at the compact adaptive width', async ({ page }, testInfo) => {
  await page.setViewportSize({ width: 982, height: 994 })
  await page.goto('/')
  const loginForm = page.getByLabel('登录PLM')
  await loginForm.getByRole('textbox', { name: '账号' }).fill('engineer')
  await loginForm.getByRole('textbox', { name: '密码' }).fill('correct-password')
  await loginForm.getByRole('button', { name: '登录', exact: true }).click()

  await expect(page.locator('.pdm-project-sidebar__summary').getByText('PRJ-REAL-001 · 真实装配项目', { exact: true })).toBeVisible()
  await page.getByRole('button', { name: '项目列表', exact: true }).click()
  const projectCode = page.getByRole('button', { name: '进入项目 PRJ-REAL-001' })
  await expect(projectCode).toBeVisible()
  const layout = await projectCode.evaluate(element => {
    const table = element.closest('table')
    const container = table?.closest('.pdm-project-number-scroll')
    return {
      codeClientWidth: element.clientWidth,
      codeScrollWidth: element.scrollWidth,
      tableScrollWidth: table?.scrollWidth ?? 0,
      containerClientWidth: container?.clientWidth ?? 0,
    }
  })
  expect(layout.codeScrollWidth).toBeLessThanOrEqual(layout.codeClientWidth)
  expect(layout.tableScrollWidth).toBeLessThanOrEqual(layout.containerClientWidth)
  await page.screenshot({ path: testInfo.outputPath('project-number-compact.png'), fullPage: false })
})

test('operator columns display the user name instead of the account name', async ({ page }, testInfo) => {
  const errors: string[] = []
  page.on('pageerror', error => errors.push(error.message))
  page.on('console', message => {
    if (message.type() === 'error' || message.type() === 'warning') errors.push(message.text())
  })
  await page.setViewportSize({ width: 1285, height: 1114 })
  await page.goto('/')
  const login = page.getByLabel('登录PLM')
  await login.getByRole('textbox', { name: '账号' }).fill('engineer')
  await login.getByRole('textbox', { name: '密码' }).fill('correct-password')
  await login.getByRole('button', { name: '登录', exact: true }).click()
  await enterProject(page)
  await page.locator('.pdm-tree-row').first().click({ button: 'right' })
  await page.getByRole('menuitem', { name: /查看\/对比历史版本/ }).click()

  const versionDrawer = page.locator('.el-drawer')
  await expect(versionDrawer).toContainText('真实工程师')
  await expect(versionDrawer).not.toContainText('engineer')
  await expect(page.locator('vite-error-overlay')).toHaveCount(0)
  expect(errors).toEqual([])
  await page.screenshot({ path: testInfo.outputPath('operator-display-name.png'), fullPage: false })
})

test('administrator switches independent company organization trees', async ({ page }, testInfo) => {
  const errors: string[] = []
  page.on('pageerror', error => errors.push(error.message))
  page.on('console', message => {
    if (message.type() !== 'error' && message.type() !== 'warning') return
    const location = message.location().url
    if (location.endsWith('/api/auth/resume') && message.text().includes('404')) return
    errors.push(`${message.text()} ${location}`.trim())
  })
  await page.setViewportSize({ width: 1536, height: 864 })
  await page.addInitScript(() => localStorage.setItem('pdm_active_organization', 'org-ks'))
  await page.goto('/')
  const loginForm = page.getByLabel('登录PLM')
  await loginForm.getByRole('textbox', { name: '账号' }).fill('admin')
  await loginForm.getByRole('textbox', { name: '密码' }).fill('correct-password')
  await loginForm.getByRole('button', { name: '登录', exact: true }).click()

  await expect(page.locator('.pdm-project-sidebar__summary').getByText('PRJ-REAL-001 · 真实装配项目', { exact: true })).toBeVisible()
  await page.getByRole('button', { name: '系统管理', exact: true }).click()
  await page.getByRole('button', { name: '用户设置', exact: true }).click()
  await page.getByLabel('用户设置功能').getByRole('button', { name: '组织关系', exact: true }).click()
  await expect(page.locator('.org-embedded-heading').getByText('组织关系', { exact: true })).toBeVisible()
  const companyTree = page.getByLabel('公司组织树')
  await expect(companyTree).toContainText('昆山自动化事业部')
  await expect(companyTree).not.toContainText('广州自动化事业部')
  await expect(page.getByLabel('组织详情')).toContainText('真实工程师')
  await expect(companyTree).toContainText('未分配人员2')

  await page.getByLabel('选择当前公司').selectOption('org-gz')
  await expect(page.getByRole('button', { name: '切换当前公司' })).toContainText('广州阿普顿自动化系统有限公司')
  await page.getByRole('button', { name: '系统管理', exact: true }).click()
  await page.getByRole('button', { name: '用户设置', exact: true }).click()
  await page.getByLabel('用户设置功能').getByRole('button', { name: '组织关系', exact: true }).click()
  const switchedCompanyTree = page.getByLabel('公司组织树')
  await expect(switchedCompanyTree).toContainText('广州自动化事业部')
  await expect(switchedCompanyTree).not.toContainText('昆山自动化事业部')
  await expect(page.getByLabel('组织详情')).toContainText('广州设计员')
  await expect.poll(() => page.evaluate(() => localStorage.getItem('pdm_active_organization'))).toBe('org-gz')

  await page.getByRole('button', { name: '公司管理', exact: true }).click()
  await expect(page.getByLabel('公司管理')).toContainText('昆山阿普顿自动化系统有限公司')
  await expect(page.getByLabel('公司管理')).toContainText('广州阿普顿自动化系统有限公司')
  await page.screenshot({ path: testInfo.outputPath('company-organization-management.png'), fullPage: false })
  await page.getByLabel('公司管理').getByRole('button', { name: '编辑', exact: true }).first().click()
  const companyDialog = page.getByRole('dialog', { name: '编辑公司' })
  const activeToggle = companyDialog.locator('.org-form-check').filter({ hasText: '启用' })
  await expect(activeToggle).toBeVisible()
  await expect(activeToggle.locator('input[type="checkbox"]')).toBeChecked()
  const companyAlignment = await activeToggle.evaluate(element => {
    const checkboxBox = element.querySelector('input[type="checkbox"]')!.getBoundingClientRect()
    const textBox = element.querySelector('span')!.getBoundingClientRect()
    return { checkboxX: checkboxBox.x, textX: textBox.x, centerDelta: Math.abs((checkboxBox.y + checkboxBox.height / 2) - (textBox.y + textBox.height / 2)) }
  })
  expect(companyAlignment.checkboxX).toBeLessThan(companyAlignment.textX)
  expect(companyAlignment.centerDelta).toBeLessThanOrEqual(1)
  await page.screenshot({ path: testInfo.outputPath('company-active-checkbox.png'), fullPage: false })
  await companyDialog.getByRole('button', { name: '取消', exact: true }).click()

  await page.getByLabel('用户设置功能').getByRole('button', { name: '组织关系', exact: true }).click()
  await page.getByRole('button', { name: '新建部门', exact: true }).click()
  const unitDialog = page.getByRole('dialog', { name: '新建部门' })
  for (const label of ['制造部门（可承接项目）', '启用']) {
    const toggle = unitDialog.locator('.org-form-check').filter({ hasText: label })
    const alignment = await toggle.evaluate(element => {
      const checkboxBox = element.querySelector('input[type="checkbox"]')!.getBoundingClientRect()
      const textBox = element.querySelector('span')!.getBoundingClientRect()
      return { checkboxX: checkboxBox.x, textX: textBox.x, centerDelta: Math.abs((checkboxBox.y + checkboxBox.height / 2) - (textBox.y + textBox.height / 2)) }
    })
    expect(alignment.checkboxX).toBeLessThan(alignment.textX)
    expect(alignment.centerDelta).toBeLessThanOrEqual(1)
  }
  await page.screenshot({ path: testInfo.outputPath('organization-checkbox-alignment.png'), fullPage: false })
  await unitDialog.getByRole('button', { name: '取消', exact: true }).click()
  await page.getByLabel('选择当前公司').selectOption('org-ks')
  await expect(page.getByRole('button', { name: '切换当前公司' })).toContainText('昆山阿普顿自动化系统有限公司')
  await page.getByRole('button', { name: '系统管理', exact: true }).click()
  await page.getByRole('button', { name: '用户设置', exact: true }).click()
  await page.getByLabel('用户设置功能').getByRole('button', { name: '组织关系', exact: true }).click()
  const returnedCompanyTree = page.getByLabel('公司组织树')
  await expect(returnedCompanyTree).toContainText('昆山设计部')
  await returnedCompanyTree.getByText('昆山设计部', { exact: true }).click()
  await page.getByLabel('组织详情').getByRole('button', { name: '编辑', exact: true }).click()
  const editUnitDialog = page.getByRole('dialog', { name: '编辑组织' })
  await expect(editUnitDialog.getByLabel('本级组织编码')).toHaveValue('DESIGN')
  await expect(editUnitDialog).toContainText('完整编码：KS-AUTO-DESIGN')
  await editUnitDialog.locator('label').filter({ hasText: '上级组织' }).locator('select').selectOption('ks-other')
  await expect(editUnitDialog).toContainText('完整编码：KS-OTHER-DESIGN')
  await page.screenshot({ path: testInfo.outputPath('organization-hierarchical-code.png'), fullPage: false })
  await editUnitDialog.getByRole('button', { name: '取消', exact: true }).click()
  await expect(page.locator('vite-error-overlay')).toHaveCount(0)
  expect(errors).toEqual([])
})

test('role permissions follow the system module directory and include future modules', async ({ page }, testInfo) => {
  const errors: string[] = []
  const permissionDirectory = {
    permissions: [
      { code: 'project.view', name: '查看项目清单', module: '项目管理', description: '查看当前公司范围内的项目。', sensitive: false },
      { code: 'document.edit', name: '编辑项目图档', module: '项目内容', description: '编辑图档并创建新版本。', sensitive: false },
      { code: 'future-module.view', name: '查看新增模块', module: '新增模块', description: '模拟后续发布的系统模块权限。', sensitive: false },
    ],
    roles: [
      { role: 'PlanningManager', name: '计划管理', description: '按所属公司分配项目执行事业部。', baseRole: 'PlanningManager', isSystem: true, isSystemAdministrator: false, permissions: ['project.view'], userCount: 2 },
    ],
  }
  page.on('pageerror', error => errors.push(error.message))
  page.on('console', message => {
    if (message.type() === 'error' || message.type() === 'warning') errors.push(`${message.text()} ${message.location().url}`.trim())
  })
  await page.route('**/api/role-permissions/PlanningManager', route => route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify(permissionDirectory),
  }))
  await page.route('**/api/role-permissions', route => route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify(permissionDirectory),
  }))

  await page.setViewportSize({ width: 1988, height: 1114 })
  await page.goto('/')
  const loginForm = page.getByLabel('登录PLM')
  await loginForm.getByRole('textbox', { name: '账号' }).fill('admin')
  await loginForm.getByRole('textbox', { name: '密码' }).fill('correct-password')
  await loginForm.getByRole('button', { name: '登录', exact: true }).click()
  await expect(page.locator('.pdm-project-sidebar__summary').getByText('PRJ-REAL-001 · 真实装配项目', { exact: true })).toBeVisible()
  await page.getByRole('button', { name: '系统管理', exact: true }).click()
  const systemManagement = page.getByRole('region', { name: '系统管理', exact: true })
  await expect(systemManagement).toBeVisible()
  await systemManagement.getByRole('button', { name: '用户设置', exact: true }).click()
  await page.getByLabel('用户设置功能').getByRole('button', { name: '角色权限', exact: true }).click()

  const roleList = page.getByLabel('角色列表')
  await expect(roleList).toContainText('计划管理')
  await expect(roleList.getByRole('button', { name: '权限设置', exact: true })).toHaveCount(1)
  await expect(roleList.getByText('基础权限', { exact: true })).toHaveCount(0)
  await expect(roleList.getByText('单据权限', { exact: true })).toHaveCount(0)
  await roleList.getByRole('button', { name: '权限设置', exact: true }).click()

  const dialog = page.getByRole('dialog', { name: '角色权限' })
  await expect(dialog).toBeVisible()
  await expect(dialog).toContainText('权限模块由系统功能目录自动生成')
  await expect(dialog).toContainText('3 个模块、3 项权限')
  const modules = dialog.getByRole('navigation', { name: '权限模块' })
  await expect(modules).toContainText('项目管理')
  await expect(dialog).toContainText('查看项目清单')
  await modules.getByRole('button', { name: /项目内容/ }).click()
  await expect(dialog).toContainText('编辑项目图档')
  await modules.getByRole('button', { name: /新增模块/ }).click()
  await expect(dialog).toContainText('查看新增模块')
  await dialog.getByRole('button', { name: '保存并立即生效', exact: true }).click()
  await expect(dialog).toBeVisible()
  await expect(dialog).toContainText('角色权限已保存并立即生效')
  await expect(page.locator('vite-error-overlay')).toHaveCount(0)
  expect(errors).toEqual([])
  await page.screenshot({ path: testInfo.outputPath('role-permission-system-modules.png'), fullPage: false })
})

for (const scale of [
  { name: '125-percent', width: 1536, height: 864 },
  { name: '150-percent', width: 1280, height: 720 },
]) {
  test(`workspace remains fixed and interactive at ${scale.name} logical viewport`, async ({ page }, testInfo) => {
    await page.setViewportSize({ width: scale.width, height: scale.height })
    await page.goto('/')
    const loginForm = page.getByLabel('登录PLM')
    await loginForm.getByRole('textbox', { name: '账号' }).fill('engineer')
    await loginForm.getByRole('textbox', { name: '密码' }).fill('correct-password')
    await loginForm.getByRole('button', { name: '登录', exact: true }).click()
    const shellLayout = await page.evaluate(() => {
      const sidebar = document.querySelector<HTMLElement>('.pdm-sidebar')?.getBoundingClientRect()
      const titlebar = document.querySelector<HTMLElement>('.pdm-titlebar')?.getBoundingClientRect()
      const labels = [...document.querySelectorAll<HTMLElement>('.pdm-nav-item span')]
      return {
        sidebarWidth: sidebar?.width ?? 0,
        titlebarHeight: titlebar?.height ?? 0,
        clippedLabels: labels.filter(label => label.scrollWidth > label.clientWidth).map(label => label.textContent),
      }
    })
    expect(shellLayout.sidebarWidth).toBe(155)
    expect(shellLayout.titlebarHeight).toBe(62)
    expect(shellLayout.clippedLabels).toEqual([])
    await enterProject(page)
    await page.getByRole('button', { name: '图档', exact: true }).click()
    await expect(page.getByLabel('项目设计树')).toBeVisible()

    const projectHeaderLayout = await page.evaluate(() => {
      const sidebar = document.querySelector<HTMLElement>('.pdm-project-sidebar')?.getBoundingClientRect()
      const context = document.querySelector<HTMLElement>('.pdm-project-sidebar__context')?.getBoundingClientRect()
      return { sidebarLeft: sidebar?.left ?? -1, contextLeft: context?.left ?? -1, sidebarTop: sidebar?.top ?? -1, contextTop: context?.top ?? -1, contextBottom: context?.bottom ?? -1 }
    })
    expect(projectHeaderLayout.contextLeft).toBe(projectHeaderLayout.sidebarLeft)
    expect(projectHeaderLayout.contextTop).toBeLessThan(projectHeaderLayout.sidebarTop)
    expect(projectHeaderLayout.contextBottom).toBeLessThanOrEqual(projectHeaderLayout.sidebarTop)

    const layout = await page.evaluate(() => {
      const tree = document.querySelector<HTMLElement>('[role="tree"]')
      const shell = document.querySelector<HTMLElement>('.pdm-app-shell')
      return {
        bodyClientHeight: document.body.clientHeight,
        bodyScrollHeight: document.body.scrollHeight,
        shellClientHeight: shell?.clientHeight ?? 0,
        shellScrollHeight: shell?.scrollHeight ?? 0,
        treeClientHeight: tree?.clientHeight ?? 0,
        treeScrollHeight: tree?.scrollHeight ?? 0,
      }
    })
    expect(layout.bodyScrollHeight).toBe(layout.bodyClientHeight)
    expect(layout.shellScrollHeight).toBe(layout.shellClientHeight)
    expect(layout.treeScrollHeight).toBeGreaterThan(layout.treeClientHeight)

    const navCases = [
      ['概览', '工作台主页面'],
      ['文件', '项目文件夹'],
      ['图档', '项目设计树'],
      ['BOM', 'BOM维护'],
      ['发布', '发布总览'],
      ['记录', '审计查询'],
    ] as const
    for (const [buttonName, panelName] of navCases) {
      const button = page.getByRole('button', { name: buttonName, exact: true })
      await expect(button).toBeEnabled()
      await button.click()
      await expect(page.getByLabel(panelName).or(page.getByText(panelName, { exact: true })).first()).toBeVisible()
    }

    await page.getByRole('button', { name: '图档', exact: true }).click()
    await page.locator('.pdm-tree-row').first().click({ button: 'right' })
    await page.getByRole('menuitem', { name: /查看\/对比历史版本/ }).click()
    await expect(page.getByText('图档历史版本对比')).toBeVisible()
    await expect(page.getByRole('button', { name: '只读打开左版' })).toHaveCount(0)
    await expect(page.getByRole('button', { name: '下载左版原始文件' })).toBeEnabled()
    const drawerLayout = await page.locator('.el-drawer__body').evaluate(element => ({ clientWidth: element.clientWidth, scrollWidth: element.scrollWidth }))
    expect(drawerLayout.scrollWidth).toBeLessThanOrEqual(drawerLayout.clientWidth)

    await page.screenshot({ path: testInfo.outputPath(`workspace-${scale.name}.png`), fullPage: false })
  })
}

test('administrator can select one archived material and open the reactivate confirmation', async ({ page }, testInfo) => {
  const consoleErrors: string[] = []
  const failedResponses: string[] = []
  page.on('console', message => {
    if (message.type() === 'error') consoleErrors.push(message.text())
  })
  page.on('response', response => {
    if (response.status() >= 400) failedResponses.push(`${response.status()} ${response.url()}`)
  })
  await page.setViewportSize({ width: 956, height: 1114 })
  await page.goto('/')
  const loginForm = page.getByLabel('登录PLM')
  await loginForm.getByRole('textbox', { name: '账号' }).fill('admin')
  await loginForm.getByRole('textbox', { name: '密码' }).fill('correct-password')
  await loginForm.getByRole('button', { name: '登录', exact: true }).click()
  await expect(page.locator('.pdm-project-detail')).toBeVisible({ timeout: 15_000 })
  await page.waitForLoadState('networkidle')
  const materialNav = page.getByRole('button', { name: '料品管理', exact: true })
  await expect(materialNav).toBeVisible()
  await materialNav.click()
  await expect(materialNav).toHaveAttribute('aria-current', 'page')
  await expect(page.getByRole('tab', { name: '料品主档', exact: true })).toBeVisible()

  await page.getByText('显示已停用', { exact: true }).click()
  const archivedRow = page.locator('.material-table tbody tr').filter({ hasText: '01021000001' })
  await expect(archivedRow).toHaveCount(1)
  await archivedRow.locator('label.el-checkbox').click()

  const toolbar = page.locator('.material-toolbar')
  await expect(toolbar.getByRole('button', { name: '启用', exact: true })).toBeEnabled()
  await expect(toolbar.getByRole('button', { name: '停用', exact: true })).toBeDisabled()
  const toolbarLayout = await toolbar.evaluate(element => ({
    clientWidth: element.clientWidth,
    scrollWidth: element.scrollWidth,
  }))
  expect(toolbarLayout.scrollWidth).toBeLessThanOrEqual(toolbarLayout.clientWidth)

  await toolbar.getByRole('button', { name: '启用', exact: true }).click()
  await expect(page.getByRole('dialog')).toContainText('启用料品')
  await expect(page.getByRole('dialog')).toContainText('保留原料号、审批状态和历史记录')
  await page.screenshot({ path: testInfo.outputPath('material-reactivation-confirmation.png'), fullPage: false })
  await page.getByRole('dialog').getByRole('button', { name: '取消', exact: true }).click()
  await expect(page.getByRole('dialog')).toHaveCount(0)
  expect({ consoleErrors, failedResponses }).toEqual({ consoleErrors: [], failedResponses: [] })
})

test('material relation editor keeps confirmed columns inside the dialog and saves selection advice', async ({ page }, testInfo) => {
  const mainMaterial = {
    id: 'relation-main', materialCode: '01021000002', name: '弹垫 Φ5', kind: 'Standard', supplyMode: 'Purchase', unitCode: '001',
    specification: '弹垫 Φ5', material: null, remark: '主物料备注', brand: 'MITSUBISHI', surfaceTreatment: null, weight: null, weightUnit: null,
    approvalStatus: 'Approved', approvedBy: 'admin', approvedAt: '2026-09-01T01:00:00Z', categoryCode: '0102', u9CategoryCode: '0102',
    u9ItemId: null, u9ItemCode: null, syncStatus: 'Succeeded', createdBy: 'admin', createdAt: '2026-09-01T01:00:00Z',
    updatedBy: 'admin', updatedAt: '2026-09-16T01:00:00Z', rowVersion: 3, isArchived: false, archivedBy: null,
    archivedAt: null, u9SyncConfirmed: true, sourceSystem: 'Pdm', masterOwner: 'Pdm', referenceCount: 0,
  }
  const optionMaterial = {
    ...mainMaterial,
    id: 'relation-option', materialCode: '01021000006', name: '弹垫 Φ6', specification: 'MR-J3-40A', brand: 'YASKAWA', remark: '候选备注', rowVersion: 2,
  }
  const relation = {
    id: 'relation-template', mainMaterialId: mainMaterial.id, mainMaterialCode: mainMaterial.materialCode, mainMaterialName: mainMaterial.name,
    name: `${mainMaterial.materialCode}关联物料`, isArchived: false, updatedBy: 'admin', updatedAt: '2026-09-16T01:00:00Z', rowVersion: 2,
    publishedRevision: null,
    draftRevision: {
      id: 'relation-revision', version: 1, state: 'Draft', changeNote: '', createdBy: 'admin', createdAt: '2026-09-16T01:00:00Z',
      publishedBy: null, publishedAt: null, rowVersion: 1,
      groups: [{
        id: 'relation-group', name: '安装件', isRequired: true, selectionMode: 'Single', minSelection: 1, maxSelection: 1,
        autoSelectUnique: false, sortOrder: 1,
        options: [{
          id: 'relation-option-row', materialId: optionMaterial.id, materialCode: optionMaterial.materialCode, materialName: optionMaterial.name,
          materialKind: 'Standard', unitCode: '001', quantityMode: 'PerMainQuantity', quantityPerSet: 1, isDefault: true, sortOrder: 1,
          selectionAdvice: '优先用于高速工位',
        }],
      }],
    },
  }
  let searchedByModel = false
  let savedBody: Record<string, unknown> | null = null
  await page.route('**/api/materials/page**', route => route.fulfill({ json: { items: [mainMaterial], total: 1, page: 1, pageSize: 50 } }))
  await page.route(/^http:\/\/127\.0\.0\.1:(?:5080|5173|519[3-5])\/api\/materials(?:\?.*)?$/, route => {
    const query = new URL(route.request().url()).searchParams.get('query') ?? ''
    if (query.includes('MR-J3')) searchedByModel = true
    return route.fulfill({ json: query && !optionMaterial.specification.includes(query) ? [] : [mainMaterial, optionMaterial] })
  })
  await page.route('**/api/material-relations/templates**', async route => {
    if (route.request().method() === 'GET') return route.fulfill({ json: [relation] })
    return route.fallback()
  })
  await page.route('**/api/material-relations/templates/relation-template/draft', async route => {
    savedBody = route.request().postDataJSON() as Record<string, unknown>
    return route.fulfill({ json: relation })
  })
  await page.route('**/api/materials/relation-main/attachments', route => route.fulfill({ json: [] }))

  const errors: string[] = []
  page.on('pageerror', error => errors.push(error.message))
  page.on('console', message => { if (message.type() === 'error') errors.push(message.text()) })
  await page.addInitScript(() => { window.setInterval = (() => 0) as unknown as typeof window.setInterval })
  await page.setViewportSize({ width: 1236, height: 1114 })
  await page.goto('/')
  const login = page.getByLabel('登录PLM')
  await login.getByRole('textbox', { name: '账号' }).fill('admin')
  await login.getByRole('textbox', { name: '密码' }).fill('correct-password')
  await login.getByRole('button', { name: '登录', exact: true }).click()
  await expect(page.locator('.pdm-project-detail')).toBeVisible({ timeout: 15_000 })
  await page.getByRole('button', { name: '料品管理', exact: true }).click()
  await page.getByRole('button', { name: `查看 ${mainMaterial.materialCode} 的关联物料` }).click()

  const dialog = page.getByRole('dialog', { name: '料品明细' })
  const editor = dialog.getByLabel('关联物料')
  await expect(editor).toBeVisible()
  await expect(editor.locator('.material-relation-summary')).toContainText('品牌MITSUBISHI')
  await expect(editor.locator('.material-relation-help')).toHaveCount(0)
  await expect(editor.getByText('分类名称 *', { exact: true })).toBeVisible()
  await expect(editor.locator('.material-relation-option-head span')).toHaveText([
    '料号', '名称', '型号', '品牌', '备注', '选型建议', '数量计算', '每套数量', '优先推荐', '操作',
  ])
  await expect(editor.locator('.material-relation-option-row')).toContainText('YASKAWA')
  const optionTableLayout = await editor.locator('.material-relation-option-table').evaluate(element => ({
    clientWidth: element.clientWidth,
    scrollWidth: element.scrollWidth,
  }))
  expect(optionTableLayout.scrollWidth).toBeLessThanOrEqual(optionTableLayout.clientWidth)

  const materialSearch = editor.locator('.material-relation-option-row .el-select input').first()
  await materialSearch.fill('MR-J3')
  await expect.poll(() => searchedByModel).toBe(true)
  await page.keyboard.press('Escape')
  await editor.getByRole('textbox', { name: '选型建议' }).fill('高速工位优先，低速工位可替代')
  await editor.getByRole('button', { name: '保存修改', exact: true }).click()
  await expect.poll(() => savedBody).not.toBeNull()
  expect(savedBody).toMatchObject({
    groups: [{ options: [{ selectionAdvice: '高速工位优先，低速工位可替代' }] }],
  })
  await expect(page.locator('vite-error-overlay')).toHaveCount(0)
  expect(errors).toEqual([])
  await page.screenshot({ path: testInfo.outputPath('material-relation-editor-confirmed-layout.png'), fullPage: false })
})

test('engineer can read a material rejection reason without approval controls', async ({ page }, testInfo) => {
  materialCodeApplications = [{
    id: 'application-rejected', projectId, bomItemId: 'bom-standard-1', bomHeaderKind: 'Standard', applicationType: 'StandardBomItem',
    status: 'Rejected', requestedBy: 'engineer', requestedAt: '2026-09-04T01:00:00Z', decidedBy: 'standardizer',
    decidedAt: '2026-09-04T02:00:00Z', decisionComment: '型号资料不完整', materialId: null, materialCode: null, rowVersion: 4,
    applicationName: '标准紧固件', projectCode: 'PRJ-REAL-001', projectName: '真实装配项目', categoryCode: '0102',
    specification: 'M8', brand: 'UPTON', remark: null, workflowState: 'Rejected',
  }]
  const consoleErrors: string[] = []
  const failedResponses: string[] = []
  page.on('console', message => { if (message.type() === 'error') consoleErrors.push(message.text()) })
  page.on('response', response => { if (response.status() >= 400) failedResponses.push(`${response.status()} ${response.url()}`) })

  await page.setViewportSize({ width: 1440, height: 900 })
  await page.goto('/')
  const loginForm = page.getByLabel('登录PLM')
  await loginForm.getByRole('textbox', { name: '账号' }).fill('engineer')
  await loginForm.getByRole('textbox', { name: '密码' }).fill('correct-password')
  await loginForm.getByRole('button', { name: '登录', exact: true }).click()
  await expect(page.locator('.pdm-project-detail')).toBeVisible({ timeout: 15_000 })

  await page.getByRole('button', { name: '料品管理', exact: true }).click()
  await page.getByRole('tab', { name: /料号审批/ }).click()
  await page.getByRole('tab', { name: /审批\/同步历史/ }).click()
  const history = page.getByLabel('第一步料号审批历史')
  await expect(history).toContainText('已驳回')
  await expect(history).toContainText('驳回原因')
  await expect(history).toContainText('型号资料不完整')
  await expect(history.getByRole('button', { name: '批准', exact: true })).toHaveCount(0)
  await expect(history.getByRole('button', { name: '驳回', exact: true })).toHaveCount(0)
  await page.screenshot({ path: testInfo.outputPath('engineer-material-rejection-history.png'), fullPage: false })
  expect({ consoleErrors, failedResponses }).toEqual({ consoleErrors: [], failedResponses: [] })
})

test('validation plan selector keeps selections across categories and skips completed category', async ({ page }) => {
  const categories = [
    { id: 'validation-category-complete', name: '定位工装', sortOrder: 10, isActive: true, itemCount: 1, referenceCount: 1, createdBy: 'system', createdAt: '2026-09-09T00:00:00Z', updatedBy: 'system', updatedAt: '2026-09-09T00:00:00Z', rowVersion: 1 },
    { id: 'validation-category-second', name: '独立工装', sortOrder: 20, isActive: true, itemCount: 1, referenceCount: 0, createdBy: 'system', createdAt: '2026-09-09T00:00:00Z', updatedBy: 'system', updatedAt: '2026-09-09T00:00:00Z', rowVersion: 1 },
    { id: 'validation-category-third', name: '安全相关', sortOrder: 30, isActive: true, itemCount: 1, referenceCount: 0, createdBy: 'system', createdAt: '2026-09-09T00:00:00Z', updatedBy: 'system', updatedAt: '2026-09-09T00:00:00Z', rowVersion: 1 },
  ]
  const items = [
    { id: 'validation-item-complete', categoryId: categories[0]!.id, content: '定位销检查', defaultInformationSource: '内部评审', sortOrder: 10, isActive: true, referenceCount: 1, createdBy: 'system', createdAt: '2026-09-09T00:00:00Z', updatedBy: 'system', updatedAt: '2026-09-09T00:00:00Z', rowVersion: 1 },
    { id: 'validation-item-second', categoryId: categories[1]!.id, content: '独立工装装配确认', defaultInformationSource: '内部评审', sortOrder: 10, isActive: true, referenceCount: 0, createdBy: 'system', createdAt: '2026-09-09T00:00:00Z', updatedBy: 'system', updatedAt: '2026-09-09T00:00:00Z', rowVersion: 1 },
    { id: 'validation-item-third', categoryId: categories[2]!.id, content: '安全门互锁确认', defaultInformationSource: '内部评审', sortOrder: 10, isActive: true, referenceCount: 0, createdBy: 'system', createdAt: '2026-09-09T00:00:00Z', updatedBy: 'system', updatedAt: '2026-09-09T00:00:00Z', rowVersion: 1 },
  ]
  const plan = {
    id: 'validation-plan-e2e', projectId, preparedBy: 'engineer', validationDate: '2026-09-14', revisionNumber: 1, state: 'Draft', approvalTasks: [], attachments: [],
    items: [{ id: 'validation-plan-row-existing', catalogCategoryId: categories[0]!.id, catalogItemId: items[0]!.id, categoryName: categories[0]!.name, validationContent: items[0]!.content, informationSource: '内部评审', validationDate: null, result: null, responsiblePerson: null, remark: null, sortOrder: 1 }],
    createdBy: 'engineer', createdAt: '2026-09-14T00:00:00Z', updatedBy: 'engineer', updatedAt: '2026-09-14T00:00:00Z', rowVersion: 1,
  }
  await page.route('**/api/auth/login', route => route.fulfill({ json: {
    accessToken: 'e2e-token', expiresAt: '2099-01-01T00:00:00Z', resumeToken: 'e2e-resume-token', username: 'engineer', displayName: '真实工程师', role: 'Engineer',
    permissions: ['project.view', 'project.content.view', 'validation-plan.edit'], primaryCompanyId: 'org-ks', activeCompanyId: 'org-ks', activeCompanyName: '昆山阿普顿自动化系统有限公司', crossCompanyView: false,
    accessibleCompanies: [{ id: 'org-ks', name: '昆山阿普顿自动化系统有限公司', code: '7' }],
  } }))
  await page.route('**/api/validation-check-catalog', route => route.fulfill({ json: { categories, items } }))
  await page.route(`**/api/projects/${projectId}/validation-plan`, route => route.fulfill({ json: plan }))
  await page.route('**/api/validation-plans/validation-plan-e2e/execution-records', route => route.fulfill({ json: [] }))
  const consoleErrors: string[] = []
  page.on('console', message => { if (message.type() === 'error') consoleErrors.push(message.text()) })

  await page.setViewportSize({ width: 1440, height: 900 })
  await page.goto('/')
  const loginForm = page.getByLabel('登录PLM')
  await loginForm.getByRole('textbox', { name: '账号' }).fill('engineer')
  await loginForm.getByRole('textbox', { name: '密码' }).fill('correct-password')
  await loginForm.getByRole('button', { name: '登录', exact: true }).click()
  await page.getByRole('button', { name: '验证计划', exact: true }).click()
  await page.locator('.validation-plan-summary__table tbody tr').click()
  await page.getByRole('button', { name: '选取内容' }).click()

  const dialog = page.getByRole('dialog', { name: '选择验证检查项' })
  const completeCategory = dialog.getByRole('button', { name: /定位工装/ })
  const secondCategory = dialog.getByRole('button', { name: /独立工装/ })
  const thirdCategory = dialog.getByRole('button', { name: /安全相关/ })
  await expect(completeCategory).toContainText('0')
  await expect(completeCategory).toHaveClass(/is-complete/)
  await expect(secondCategory).toHaveClass(/is-active/)
  await dialog.getByText('独立工装装配确认').click()
  await thirdCategory.click()
  await dialog.getByText('安全门互锁确认').click()
  await expect(dialog).toContainText('跨分类已选 2 项')
  await completeCategory.click()
  await expect(dialog).toContainText('该分类的 1 项已全部加入当前计划')
  await expect(dialog).toContainText('跨分类已选 2 项')
  await page.screenshot({ path: join(tmpdir(), 'uplm-validation-plan-multiselect-20260914.png'), fullPage: false })
  await dialog.getByRole('button', { name: '加入计划（2）' }).click()
  await expect(page.locator('.validation-plan__table')).toContainText('独立工装装配确认')
  await expect(page.locator('.validation-plan__table')).toContainText('安全门互锁确认')
  expect(consoleErrors).toEqual([])
})
