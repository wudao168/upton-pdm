import { expect, test } from '@playwright/test'

const projectId = '11111111-1111-1111-1111-111111111111'
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
    { id: 'ks-department', organizationId: 'org-ks', parentUnitId: 'ks-division', code: 'KS-DESIGN', name: '昆山设计部', kind: 'Department', isActive: true, sortOrder: 1 },
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
  let currentUsername = 'engineer'
  await page.route('http://127.0.0.1:5080/**', async (route) => {
    const path = new URL(route.request().url()).pathname
    const fulfill = (body: unknown, status = 200) => route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(body) })
    if (path === '/health') return fulfill({ status: 'ok' })
    if (path === '/api/auth/login') {
      const credentials = route.request().postDataJSON() as { username?: string }
      const administrator = credentials.username === 'admin'
      currentUsername = administrator ? 'admin' : 'engineer'
      return fulfill({ accessToken: 'e2e-token', expiresAt: '2099-01-01T00:00:00Z', resumeToken: 'e2e-resume-token', username: currentUsername, displayName: administrator ? '系统管理员' : '真实工程师', role: administrator ? 'Administrator' : 'Engineer', permissions: administrator ? ['project.view', 'project.create', 'project.child.create', 'project.content.view', 'document.edit', 'bom.edit', 'release.manage', 'settings.customer.manage', 'settings.organization.manage', 'settings.folder.manage', 'settings.storage.manage', 'system.role.view', 'system.role.edit', 'audit.view'] : ['project.view', 'project.create', 'project.child.create', 'project.content.view', 'document.edit', 'bom.edit', 'release.manage'] })
    }
    if (path === '/api/auth/me') return fulfill({ username: currentUsername, displayName: currentUsername === 'admin' ? '系统管理员' : '真实工程师', nickname: null, gender: 'unspecified', landline: null, mobilePhone: null, email: null })
    if (path === '/api/password-reset-requests') return fulfill([])
    if (path === '/api/approval-tasks/mine') return fulfill([])
    if (path === '/api/customers') return fulfill([{ id: 'customer-1', code: 'C00465', name: '中山比亚迪电子有限公司', isActive: true }])
    if (path === '/api/organization-directory') return fulfill(organizationDirectory)
    if (path === '/api/crm-integration') return fulfill({ baseUrl: '', username: '', passwordConfigured: false, autoSyncEnabled: false, autoSyncIntervalMinutes: 60, lastSyncAt: null, lastSyncCount: 0, lastAutoSyncAttemptAt: null, lastAutoSyncError: null })
    if (path === '/api/u9-material-integration') return fulfill({ baseUrl: '', enterpriseCode: '', organizationCode: '', userCode: '', clientId: '', clientSecretConfigured: false, itemCreatePath: '', itemQueryPath: '', itemModifyPath: '', itemDeletePath: '', unitCodeMappings: {}, writeEnabled: false, updatedBy: null, updatedAt: null })
    if (path === '/api/role-permissions') return fulfill({ permissions: [], roles: [] })
    if (path === '/api/system-settings') return fulfill({ vaultRoot: 'D:\\PDM\\Vault', releaseRoot: 'D:\\PDM\\Release', checkoutHeartbeatSeconds: 180, checkoutLeaseMinutes: 15, checkoutOfflineGraceMinutes: 60, checkoutReminderHours: 4, checkoutStrongReminderHours: 8, checkoutOverdueHours: 24, checkoutForceReleaseHours: 48 })
    if (path === '/api/system-settings/equipment-types') return fulfill([])
    if (path === '/api/folder-template') return fulfill([])
    if (path === '/api/edit-locks') return fulfill([])
    if (path === '/api/project-numbering/options') return fulfill({ organizations: [{ id: '70000000-0000-0000-0000-000000000001', name: '昆山阿普顿自动化系统有限公司', projectCompanyCode: '7', modelCompanyCode: 'AK', crmCompanyName: '昆山阿普顿自动化系统有限公司' }], projectTypes: [{ code: 'P', name: '标准项目' }], equipmentTypes: [{ code: 2, name: '类型02' }] })
    if (path === '/api/projects') return fulfill([{ id: projectId, code: 'PRJ-REAL-001', name: '真实装配项目', owner: '真实工程师', vaultLocation: 'D:\\PDM\\PRJ-REAL-001', releaseLocation: 'D:\\Release\\PRJ-REAL-001', isActive: true, quantity: 1, serialNumbers: ['70000001'], executionUnitName: '自动化事业部', primaryProjectManager: 'project-manager', collaborativeProjectManagers: ['project-manager-2'], designLead: 'design-lead', designers: [] }])
    if (path === `/api/projects/${projectId}`) return fulfill({ id: projectId, code: 'PRJ-REAL-001', name: '真实装配项目', owner: '真实工程师', vaultLocation: 'D:\\PDM\\PRJ-REAL-001', releaseLocation: 'D:\\Release\\PRJ-REAL-001', isActive: true, quantity: 1, serialNumbers: ['70000001'], executionUnitName: '自动化事业部', primaryProjectManager: 'project-manager', collaborativeProjectManagers: ['project-manager-2'], designLead: 'design-lead', designers: [] })
    if (path === `/api/projects/${projectId}/versions`) return fulfill(versions.map(version => ({ ...version, drawingNumber: 'REAL-ASM-001', documentName: '真实总装配', fileName: 'REAL-ASM-001.SLDASM' })))
    if (path === `/api/projects/${projectId}/audit`) return fulfill([])
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
    if (path.endsWith('/boms/Standard')) return fulfill([{ id: 'bom-standard-1', kind: 'Standard', sequence: 1, drawingNumber: 'REAL-STD-001', name: '标准紧固件', quantity: 4, unit: '件', material: null, specification: 'M8', revision: 'A', isComplete: true, source: 'Auto', isManuallyOverridden: false, isPendingRemoval: false }])
    if (path.endsWith('/boms/NonStandard')) return fulfill([{ id: 'bom-non-standard-1', kind: 'NonStandard', sequence: 1, drawingNumber: 'REAL-PRT-001', name: '真实底板', quantity: 2, unit: '件', material: 'Q235B', specification: '10mm', revision: 'A', isComplete: true, source: 'Auto', isManuallyOverridden: false, isPendingRemoval: false }])
    if (path.endsWith('/boms/Unclassified')) return fulfill([])
    if (path.endsWith('/boms/Electrical')) return fulfill([{ id: 'bom-electrical-1', kind: 'Electrical', sequence: 1, drawingNumber: 'REAL-EL-001', name: '真实传感器', quantity: 1, unit: '件', material: null, specification: 'PNP', revision: 'A', isComplete: false }])
    if (path.endsWith('/bom-source-data')) return fulfill([])
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

test('engineer logs in and reads the API-backed PDM workspace', async ({ page }, testInfo) => {
  await page.setViewportSize({ width: 1920, height: 1080 })
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
  await page.getByRole('banner').getByRole('button', { name: '登录', exact: true }).click()
  const loginForm = page.getByLabel('登录PDM')
  await loginForm.getByRole('textbox', { name: '账号' }).fill('engineer')
  await loginForm.getByRole('textbox', { name: '密码' }).fill('correct-password')
  await loginForm.getByRole('button', { name: '登录', exact: true }).click()
  await expect(page.getByLabel('项目中心')).toBeVisible()
  const titlebar = page.locator('.pdm-titlebar')
  await expect(titlebar).toContainText('昆山阿普顿自动化系统有限公司')
  await expect(titlebar).toContainText('工程师')
  await expect(titlebar.getByRole('button', { name: '消息' })).toBeVisible()
  await expect(titlebar.getByRole('button', { name: '主题' })).toBeVisible()
  await expect(titlebar.getByRole('button', { name: '退出' })).toBeVisible()
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
  expect(crmShellLayout.brandHeight).toBe(76)
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
  await expect(page.getByRole('button', { name: '进入项目' })).toBeVisible()
  await page.getByRole('button', { name: '进入项目' }).click()

  await expect(page.locator('.pdm-project-sidebar__summary').getByText('PRJ-REAL-001 · 真实装配项目', { exact: true })).toBeVisible()
  await expect(page.getByRole('banner').getByText('真实工程师', { exact: true })).toBeVisible()
  await page.getByRole('button', { name: '文件库', exact: true }).click()
  await expect(page.getByText('项目文件夹', { exact: true })).toBeVisible()
  await expect(page.getByText('机械图纸', { exact: true })).toBeVisible()
  await page.getByText('PRJ-REAL-001-0', { exact: true }).first().click()
  const fileDetails = page.getByRole('table', { name: '文件明细' })
  await expect(fileDetails).toBeVisible()
  await expect(fileDetails.getByRole('columnheader')).toHaveCount(7)
  const fileDetailWidths = await fileDetails.evaluate(table => {
    const headers = [...table.querySelectorAll<HTMLElement>('th')]
    return { table: table.getBoundingClientRect().width, number: headers[0]?.getBoundingClientRect().width ?? 0, name: headers[1]?.getBoundingClientRect().width ?? 0, updated: headers[6]?.getBoundingClientRect().width ?? 0 }
  })
  expect(fileDetailWidths.table).toBeGreaterThanOrEqual(1080)
  expect(fileDetailWidths.number).toBeGreaterThanOrEqual(200)
  expect(fileDetailWidths.name).toBeGreaterThanOrEqual(260)
  expect(fileDetailWidths.updated).toBeGreaterThanOrEqual(180)
  await page.getByRole('button', { name: '图档', exact: true }).click()
  await expect(page.getByLabel('项目设计树')).toContainText('REAL-PRT-001')
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
  expect(Math.abs(previewLayout.sidebarWidth - 200)).toBeLessThanOrEqual(2)
  expect(Math.abs(previewLayout.familyBottom - previewLayout.sidebarBottom)).toBeLessThanOrEqual(2)
  expect(previewLayout.sidebarBottom).toBeLessThanOrEqual(previewLayout.viewportHeight)
  expect(previewLayout.pageHeight).toBeLessThanOrEqual(previewLayout.viewportHeight)
  await expect(page.getByText('PRJ-2026-018')).toHaveCount(0)

  await page.getByRole('button', { name: '使用位置' }).click()
  await expect(page.getByRole('heading', { name: '使用位置' })).toBeVisible()
  await expect(page.getByText('REAL-TOP-001')).toBeVisible()
  await page.keyboard.press('Escape')

  await page.getByRole('button', { name: '作废图档' }).click()
  await expect(page.getByText('作废后该图档不能再获取编辑权限。请填写可追溯的作废原因。')).toBeVisible()
  await page.keyboard.press('Escape')

  await page.evaluate(() => window.dispatchEvent(new CustomEvent('pdm-solidworks-capability', { detail: { available: true } })))
  const solidWorksButton = page.getByRole('button', { name: '打开最新' })
  await expect(solidWorksButton).toBeEnabled()
  await page.screenshot({ path: testInfo.outputPath('controlled-open-actions.png'), fullPage: false })
  await solidWorksButton.click()
  await expect.poll(() => page.evaluate(() => (window as unknown as { pdmHostMessages: Array<{ type?: string; payload?: { mode?: string } }> }).pdmHostMessages?.filter(message => message.type === 'open-document').at(-1))).toMatchObject({ type: 'open-document', payload: { mode: 'LatestReadOnly' } })

  await page.getByRole('treeitem').first().click({ button: 'right' })
  await expect(page.getByRole('menuitem', { name: '获取最新版本并编辑' })).toHaveCount(0)
  const releasedButton = page.getByRole('menuitem', { name: '打开最新正式发布版（只读）' })
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
  await page.getByLabel('选择物料').check()
  await expect(page.getByText(/已选择 1 项/)).toBeVisible()
  await page.getByRole('button', { name: '编辑', exact: true }).click()
  const batchDialog = page.getByRole('dialog', { name: '批量编辑BOM属性' })
  await expect(batchDialog).toBeVisible()
  await expect(batchDialog.getByText('1 条物料 · 0 个属性')).toBeVisible()
  await batchDialog.getByLabel('修改品牌').check()
  await batchDialog.getByPlaceholder('勾选后留空即清空').nth(2).fill('UPTON')
  await expect(batchDialog.getByText('1 条物料 · 1 个属性')).toBeVisible()
  await page.screenshot({ path: testInfo.outputPath('bom-batch-editor.png'), fullPage: false })
  await batchDialog.getByRole('button', { name: '取消' }).click()
  await expect(batchDialog).toHaveCount(0)
})

test('project numbers remain fully visible at the compact adaptive width', async ({ page }, testInfo) => {
  await page.setViewportSize({ width: 982, height: 994 })
  await page.goto('/')
  await page.getByRole('banner').getByRole('button', { name: '登录', exact: true }).click()
  const loginForm = page.getByLabel('登录PDM')
  await loginForm.getByRole('textbox', { name: '账号' }).fill('engineer')
  await loginForm.getByRole('textbox', { name: '密码' }).fill('correct-password')
  await loginForm.getByRole('button', { name: '登录', exact: true }).click()

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

test('administrator switches independent company organization trees', async ({ page }, testInfo) => {
  await page.setViewportSize({ width: 1536, height: 864 })
  await page.addInitScript(() => localStorage.setItem('pdm_active_organization', 'org-ks'))
  await page.goto('/')
  await page.getByRole('banner').getByRole('button', { name: '登录', exact: true }).click()
  const loginForm = page.getByLabel('登录PDM')
  await loginForm.getByRole('textbox', { name: '账号' }).fill('admin')
  await loginForm.getByRole('textbox', { name: '密码' }).fill('correct-password')
  await loginForm.getByRole('button', { name: '登录', exact: true }).click()

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
  await expect(companyTree).toContainText('广州自动化事业部')
  await expect(companyTree).not.toContainText('昆山自动化事业部')
  await expect(page.getByLabel('组织详情')).toContainText('广州设计员')
  await expect.poll(() => page.evaluate(() => localStorage.getItem('pdm_active_organization'))).toBe('org-gz')

  await page.getByRole('button', { name: '公司管理', exact: true }).click()
  await expect(page.getByLabel('公司管理')).toContainText('昆山阿普顿自动化系统有限公司')
  await expect(page.getByLabel('公司管理')).toContainText('广州阿普顿自动化系统有限公司')
  await page.screenshot({ path: testInfo.outputPath('company-organization-management.png'), fullPage: false })
})

for (const scale of [
  { name: '125-percent', width: 1536, height: 864 },
  { name: '150-percent', width: 1280, height: 720 },
]) {
  test(`workspace remains fixed and interactive at ${scale.name} logical viewport`, async ({ page }, testInfo) => {
    await page.setViewportSize({ width: scale.width, height: scale.height })
    await page.goto('/')
    await page.getByRole('banner').getByRole('button', { name: '登录', exact: true }).click()
    const loginForm = page.getByLabel('登录PDM')
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
    await page.getByRole('button', { name: '进入项目' }).click()
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
      ['文件库', '项目文件夹'],
      ['图档', '项目设计树'],
      ['BOM', 'BOM维护'],
      ['审批发布', '审批与生产发包'],
      ['项目记录', '审计查询'],
    ] as const
    for (const [buttonName, panelName] of navCases) {
      const button = page.getByRole('button', { name: buttonName, exact: true })
      await expect(button).toBeEnabled()
      await button.click()
      await expect(page.getByLabel(panelName).or(page.getByText(panelName, { exact: true })).first()).toBeVisible()
    }

    await page.getByRole('button', { name: '版本', exact: true }).click()
    await page.getByRole('button', { name: '查看与对比', exact: true }).first().click()
    await expect(page.getByText('图档历史版本对比')).toBeVisible()
    await expect(page.getByRole('button', { name: '只读预览左侧' })).toHaveCount(0)
    await expect(page.getByRole('button', { name: 'SolidWorks只读打开左侧' })).toHaveCount(0)
    await expect(page.getByRole('button', { name: '下载左侧' })).toBeEnabled()
    const drawerLayout = await page.locator('.el-drawer__body').evaluate(element => ({ clientWidth: element.clientWidth, scrollWidth: element.scrollWidth }))
    expect(drawerLayout.scrollWidth).toBeLessThanOrEqual(drawerLayout.clientWidth)

    await page.screenshot({ path: testInfo.outputPath(`workspace-${scale.name}.png`), fullPage: false })
  })
}
