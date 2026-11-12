import { expect, test, type Page } from '@playwright/test'
import { join } from 'node:path'
import { defaultMobileOfficeLayout } from '../../src/mobileOfficeLayout'
const company = '11111111-1111-1111-1111-111111111111', project = '22222222-2222-2222-2222-222222222222'
const today = new Intl.DateTimeFormat('en-CA', { timeZone: 'Asia/Shanghai' }).format(new Date())
const permissions = ['project.view', 'system.project-center-permissions.v1', 'project.tab.overview.view', 'project.tab.opl.view', 'project.tab.project-plan.view', 'project.content.write', 'project.content.download', 'project.content.export', 'approval.decide']
const task = { id: 'approval-1', projectId: project, projectCode: 'P700014', projectName: '移动办公验证项目', releasePackageId: 'release-1', releasePackageNumber: 'REL-001', stage: '工程审核', packageState: 'PendingApproval', createdAt: today + 'T08:00:00Z' }
const projectData = { id: project, code: 'P700014', name: '移动办公验证项目', stage: '设计中', owner: 'employee', primaryProjectManager: 'employee', collaborativeProjectManagers: [], designers: ['employee'], quantity: 1, serialNumbers: [], responsibleUsers: ['employee'], canReadContent: true, isActive: true, organizationId: company, customerName: '测试客户', documentCount: 0, vaultName: '图档库', vaultLocation: '', releaseLocation: '' }
async function fixture(page: Page, settings: { unbound?: boolean; browser?: boolean; readonly?: boolean; noProjectView?: boolean; manageHours?: boolean; extraProjects?: boolean } = {}) {
  const catalogue = [projectData, ...(settings.extraProjects ? [2, 3, 4].map(index => ({ ...projectData, id: `work-hours-project-${index}`, code: `P70001${4 + index}`, name: `工时测试项目${index}` })) : [])]
  const state = { entries: [] as Array<{ id: string; date: string; projectId: string; location: string; hours: number; projectCode: string; projectName: string; rootProjectId: string; rootProjectCode: string; rootProjectName: string }>, rows: [] as unknown[], decisions: [] as unknown[], saves: [] as unknown[], conflict: false, rowVersion: 0, projectRequests: 0 }
  await page.addInitScript(({ browser }) => {
    Object.assign(window, { dd: { env: { platform: browser ? 'notInDingTalk' : 'ios' }, ready: (callback: () => void) => callback(), requestAuthCode: (input: { success: (value: { code: string }) => void }) => input.success({ code: 'verified-code' }) } })
  }, { browser: settings.browser })
  await page.route('**/api/**', async route => {
    const path = new URL(route.request().url()).pathname
    const method = route.request().method()
    let json: unknown = []
    if (path.endsWith('/office/bootstrap')) json = { enabled: true, corpId: 'corp', appKey: 'key', companyId: company }
    else if (path.endsWith('/office/layout')) json = { version: 0, layout: defaultMobileOfficeLayout() }
    else if (path.endsWith('/office/login')) {
      expect(route.request().postDataJSON()).toMatchObject({ companyId: company, code: 'verified-code' })
      if (settings.unbound) return route.fulfill({ status: 403, json: { message: '当前钉钉账号尚未绑定UPLM账号，请联系管理员。钉钉用户ID：employee-id' } })
      json = { accessToken: 'office-token', resumeToken: 'resume', expiresAt: today + 'T23:59:59Z', username: 'employee', displayName: '员工张工', role: 'Engineer', permissions: settings.noProjectView ? [] : settings.readonly ? permissions.filter(value => value !== 'project.content.write') : permissions, activeCompanyId: company, primaryCompanyId: company, activeCompanyName: '测试公司', accessibleCompanies: [] }
    } else if (path === '/api/projects') { state.projectRequests++; json = [projectData] }
    else if (path === '/api/approval-tasks/mine') json = state.decisions.length ? [] : [task]
    else if (path === '/api/notifications/mine') json = [{ id: 'notification-1', category: 'ProjectTodo', title: '项目交付核对', content: '请检查项目计划。', projectId: project, recipient: 'employee', sourceKey: 'todo:1', createdAt: today + 'T08:00:00Z' }, { id: 'inventory-reminder', category: 'Inventory', title: '库存核对', content: '请核对库存数量。', dueDate: today, createdAt: today + 'T08:00:00Z' }, { id: 'hours-reminder', category: 'work-hours', title: '不应混入待办的工时提醒', content: '工时缺填', createdAt: today + 'T08:00:00Z' }]
    else if (path === '/api/work-hours') {
      if (method === 'PUT') {
        const input = route.request().postDataJSON(); state.saves.push(input)
        if (state.conflict) return route.fulfill({ status: 409, json: { message: '工时已在其他页面修改，请刷新后重新填写；本次没有覆盖任何记录。' } })
        state.entries = input.entries.map((entry: object, index: number) => ({ ...entry, id: `hours-${index}`, projectCode: projectData.code, projectName: projectData.name, rootProjectId: project, rootProjectCode: projectData.code, rootProjectName: projectData.name })); state.rowVersion++
      }
      json = { today, startDate: today, username: 'employee', displayName: '员工张工', scope: '本人', people: [{ username: 'employee', displayName: '员工张工', department: '设计部', isActive: true }], projects: catalogue.map(item => ({ ...item, rootProjectId: item.id, rootProjectCode: item.code, rootProjectName: item.name })), sheets: [{ companyId: company, username: 'employee', month: today.slice(0, 7), rowVersion: state.rowVersion, entries: state.entries, history: [] }], compliance: [{ username: 'employee', days: [], reminderEnabled: true }], canManage: !!settings.manageHours, canManageSettings: !!settings.manageHours }
    } else if (path === `/api/projects/${project}/project-plan`) json = null
    else if (path.endsWith('/plan')) json = null
    else if (path === `/api/projects/${project}/release-packages`) json = [{ id: 'release-1', number: 'REL-001', projectId: project, state: 'ProcessReview', scope: 'Standard', approvalTasks: [{ id: task.id, assignee: 'employee', stage: 0, stepName: '工程审核', stepOrder: 1, decision: null }], versionBindings: [{ documentId: 'drawing-1', versionId: 'bound-version', revision: 'A.2', sha256: 'abc', publish: true, fileName: '测试工程图.SLDDRW' }], standardBomSnapshot: [], nonStandardBomSnapshot: [], electricalBomSnapshot: [] }]
    else if (path === `/api/approval-tasks/${task.id}/decision`) { state.decisions.push(route.request().postDataJSON()); json = {} }
    else if (path === `/api/projects/${project}/opl`) {
      if (method === 'POST') {
        const raw = route.request().postDataBuffer()!.toString()
        expect(raw).toContain('手机新增问题')
        state.rows.push({ issue: { id: 'opl-1', number: 1, description: '手机新增问题', solution: '', status: '待处理', priority: '普通', owner: 'employee', dueDate: null, createdBy: 'employee', createdAt: today, completedAt: null, attachments: [], rowVersion: 1, relatedProjectId: null }, canEdit: true, canComplete: true, isNew: false })
        json = (state.rows[0] as { issue: unknown }).issue
      } else json = { rootProject: projectData, subprojects: [], people: [{ username: 'employee', displayName: '员工张工' }], rows: state.rows }
    } else if (path.endsWith('/complete')) {
      const row = state.rows[0] as { issue: { status: string; completedAt: string; rowVersion: number } }; row.issue.status = '已完成'; row.issue.completedAt = today; row.issue.rowVersion++; json = row.issue
    } else if (path.includes('/auth/')) json = true
    return route.fulfill({ json })
  })
  return state
}
test('LAN HTTP mobile home, projects, OPL save and work-hours conflict preserve input', async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 })
  const errors: string[] = []; page.on('pageerror', error => errors.push(error.message))
  const state = await fixture(page)
  await page.goto(`/?dingtalkOffice=1&companyId=${company}`)
  await expect(page).toHaveTitle(/UPLM|PDM|PLM/)
  await expect(page.getByRole('region', { name: '移动办公首页' })).toBeVisible()
  await expect(page.getByRole('banner')).toHaveText('UPLM员工张工')
  await expect(page.getByRole('banner').locator('img')).toBeVisible()
  await expect(page.locator('[data-block="shortcuts"]')).toHaveCount(0)
  await expect(page.getByRole('heading', { name: '业务概况', exact: true })).toHaveCount(0)
  await expect(page.getByRole('navigation', { name: '移动办公导航' }).getByRole('button')).toHaveCount(5)
  await expect(page.getByText('不应混入待办的工时提醒')).toHaveCount(0)
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true)
  await page.screenshot({ path: join(process.env.TEMP!, 'uplm-mobile-home-390.png'), fullPage: true })
  await page.getByRole('navigation').getByRole('button', { name: /待办/ }).click()
  const inventory = page.locator('.mobile-record').filter({ hasText: '库存核对' })
  const date = await inventory.locator('small').boundingBox()
  const read = await inventory.getByRole('button', { name: '标记已读' }).boundingBox()
  expect(date!.x + date!.width <= read!.x || date!.y + date!.height <= read!.y).toBe(true)
  await inventory.getByRole('button', { name: '标记已读' }).click()
  await expect(inventory).toHaveCount(0)
  await page.getByRole('navigation').getByRole('button', { name: '项目', exact: true }).click()
  await page.getByRole('button', { name: /P700014.*移动办公验证项目/ }).click()
  await page.getByRole('button', { name: 'OPL开口问题', exact: true }).click()
  await expect(page.getByRole('button', { name: '看板', exact: true })).toHaveCount(0)
  await page.setViewportSize({ width: 320, height: 640 })
  const exportButton = page.getByRole('button', { name: '导出Excel', exact: true })
  const addButton = page.getByRole('button', { name: '新增问题', exact: true })
  await expect(exportButton).toBeVisible()
  expect((await exportButton.boundingBox())!.y).toBe((await addButton.boundingBox())!.y)
  await expect(page.locator('.opl-actions svg')).toHaveCount(0)
  await page.setViewportSize({ width: 390, height: 844 })
  const owner = page.getByRole('combobox', { name: '筛选负责人' })
  await owner.fill('张工')
  await expect(page.getByRole('option', { name: '员工张工' })).toBeVisible()
  expect(await owner.evaluate(element => getComputedStyle(element).borderTopWidth)).toBe('0px')
  await owner.press('Escape')
  await page.getByRole('button', { name: '新增问题', exact: true }).click()
  await page.getByRole('textbox', { name: '问题描述', exact: true }).fill('手机新增问题')
  await page.getByRole('button', { name: '保存问题', exact: true }).click()
  await expect(page.locator('.mobile-opl-list').getByText('手机新增问题', { exact: true })).toBeVisible()
  await expect(page.getByRole('dialog', { name: '新增开口问题' })).not.toBeVisible()
  await page.screenshot({ path: join(process.env.TEMP!, 'uplm-mobile-opl-390.png'), fullPage: true })
  await page.getByRole('navigation').getByRole('button', { name: '工时', exact: true }).click()
  await expect(page.getByLabel('填报日期')).toHaveValue(today)
  await page.getByRole('combobox', { name: '填报项目', exact: true }).click()
  await page.getByRole('option', { name: /P700014/ }).click()
  await page.getByLabel('工时数', { exact: true }).fill('4.5')
  state.conflict = true
  await page.getByRole('button', { name: '保存', exact: true }).click()
  await expect(page.getByRole('alert').filter({ hasText: '没有覆盖任何记录' })).toBeVisible()
  await expect(page.getByLabel('工时数', { exact: true })).toHaveValue('4.5')
  await page.getByRole('navigation').getByRole('button', { name: '首页', exact: true }).click()
  await expect(page.getByRole('region', { name: '移动办公首页' })).toBeVisible()
  await expect(page.getByRole('alert').filter({ hasText: '当前有未保存内容' })).toHaveCount(0)
  await page.getByRole('navigation').getByRole('button', { name: '工时', exact: true }).click()
  await expect(page.getByLabel('工时数', { exact: true })).toHaveValue('4.5')
  state.conflict = false
  await page.getByRole('button', { name: '保存', exact: true }).click()
  await expect.poll(() => state.entries[0]?.hours).toBe(4.5)
  await expect(page.getByLabel('工时数', { exact: true })).toHaveValue('4.5')
  expect(state.saves).toEqual(expect.arrayContaining([expect.objectContaining({ from: today, to: today, entries: [expect.objectContaining({ projectId: project, hours: 4.5, location: '厂内' })] })]))
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true)
  await expect(page.locator('.el-message')).toHaveCount(0, { timeout: 10_000 })
  await page.evaluate(() => window.scrollTo(0, 0))
  await page.screenshot({ path: join(process.env.TEMP!, 'uplm-mobile-hours-390.png'), fullPage: true })
  await page.getByRole('navigation').getByRole('button', { name: '首页', exact: true }).click()
  await expect(page.getByRole('region', { name: '移动办公首页' })).toBeVisible()
  await expect(page.locator('vite-error-overlay')).toHaveCount(0)
  expect(errors).toEqual([])
})
test('minimal mobile time entry has five fields, fits phone widths, and never requires saving to switch modules', async ({ page }) => {
  const errors: string[] = []; page.on('pageerror', error => errors.push(error.message))
  const state = await fixture(page, { manageHours: true })
  await page.goto(`/?dingtalkOffice=1&companyId=${company}&view=work-hours`)
  const entry = page.getByRole('region', { name: '填报工时', exact: true })
  await expect(entry).toBeVisible()
  await expect(entry.locator('fieldset > label')).toHaveCount(5)
  await expect(page.getByRole('group', { name: '项目 1 快速选择工时' }).getByRole('button')).toHaveText(['2小时', '4小时', '6小时', '8小时', '10小时'])
  await expect(entry).not.toContainText(/本月累计|未填工作日|复制|最近项目|修改记录|汇总工时|工时设置|当日记录/)
  await page.getByRole('combobox', { name: '填报项目', exact: true }).fill(projectData.name)
  await page.getByRole('option', { name: /P700014/ }).click()
  await expect(page.getByRole('listbox')).not.toBeVisible()
  await expect(page.getByLabel('自动带出项目名称')).toHaveValue(projectData.name)
  await page.getByLabel('所在地', { exact: true }).selectOption('厂外')
  await page.getByLabel('工时数', { exact: true }).fill('6.5')
  for (const [width, height] of [[320, 640], [390, 844], [430, 932]]) {
    await page.setViewportSize({ width: width!, height: height! })
    await expect.poll(() => page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true)
    const save = await entry.getByRole('button', { name: '保存', exact: true }).boundingBox()
    const nav = await page.getByRole('navigation').boundingBox()
    expect(save!.y + save!.height).toBeLessThanOrEqual(nav!.y)
    const dateControl = page.locator('.wh-mobile-date-control')
    const date = await dateControl.boundingBox()
    const dateText = await dateControl.locator('span').boundingBox()
    const location = await page.getByLabel('所在地', { exact: true }).boundingBox()
    expect(date!.width).toBeCloseTo(location!.width, 0)
    expect(date!.height).toBe(location!.height)
    expect(date!.y).toBe(location!.y)
    expect(date!.x + date!.width).toBeLessThan(location!.x)
    expect(dateText!.y + dateText!.height / 2).toBeCloseTo(date!.y + date!.height / 2, 0)
  }
  await page.setViewportSize({ width: 390, height: 844 })
  await page.screenshot({ path: join(process.env.UPLM_MOBILE_SCREENSHOT_DIR || process.env.TEMP!, 'mobile-hours-minimal-390.png'), fullPage: true })
  for (const label of ['首页', '待办', '项目', '我的']) {
    await page.getByRole('navigation').getByRole('button', { name: label }).click()
    await expect(entry).not.toBeVisible()
    await expect(page.getByRole('alert').filter({ hasText: '当前有未保存内容' })).toHaveCount(0)
    await page.getByRole('navigation').getByRole('button', { name: '工时', exact: true }).click()
    await expect(page.getByLabel('工时数', { exact: true })).toHaveValue('6.5')
    await expect(page.getByLabel('所在地', { exact: true })).toHaveValue('厂外')
    await expect(page.getByLabel('自动带出项目名称')).toHaveValue(projectData.name)
  }
  const yesterday = new Date(new Date(today + 'T00:00:00Z').getTime() - 86400000).toISOString().slice(0, 10)
  await page.getByLabel('填报日期').fill(yesterday)
  await page.getByLabel('填报日期').press('Tab')
  await expect(page.getByLabel('填报日期')).toHaveValue(yesterday)
  expect(state.saves).toHaveLength(0)
  await expect(page.locator('vite-error-overlay')).toHaveCount(0)
  expect(errors).toEqual([])
})

test('mobile time entry saves three projects together and preserves earlier entries', async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 })
  const state = await fixture(page, { extraProjects: true })
  state.entries.push({ id: 'outside', date: today, projectId: project, location: '厂外', hours: 2, projectCode: projectData.code, projectName: projectData.name, rootProjectId: project, rootProjectCode: projectData.code, rootProjectName: projectData.name })
  await page.goto(`/?dingtalkOffice=1&companyId=${company}&view=work-hours`)
  await expect(page.getByLabel('所在地', { exact: true })).toHaveValue('厂内')
  const add = page.getByRole('button', { name: '添加项目', exact: true })
  async function chooseProject(name: string, code: string) {
    const select = page.getByRole('combobox', { name, exact: true })
    await select.click()
    const listId = await select.getAttribute('aria-controls')
    await page.locator(`[id="${listId}"]`).getByRole('option', { name: new RegExp(code) }).click()
  }
  for (const [index, code, hours] of [[1, 'P700014', 2], [2, 'P700016', 6], [3, 'P700017', 8]] as const) {
    if (index > 1) await add.click()
    await chooseProject(index === 1 ? '填报项目' : `填报项目 ${index}`, code)
    await page.getByRole('group', { name: `项目 ${index} 快速选择工时` }).getByRole('button', { name: `${hours}小时`, exact: true }).click()
  }
  await expect(add).toBeDisabled()
  await page.getByRole('button', { name: '保存', exact: true }).click()
  await expect.poll(() => state.entries.map(row => [row.location, row.hours])).toEqual([['厂外', 2], ['厂内', 2], ['厂内', 6], ['厂内', 8]])
  await page.getByRole('button', { name: '移除项目 3' }).click()
  await chooseProject('填报项目 2', 'P700018')
  await page.getByRole('button', { name: '保存', exact: true }).click()
  await expect(page.getByRole('alert').filter({ hasText: '最多填报3个不同项目' })).toBeVisible()
  expect(state.saves).toHaveLength(1)
})

test('mobile time entry keeps half-hour buttons and keys, decimal validation and daily limits', async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 })
  const state = await fixture(page)
  await page.goto(`/?dingtalkOffice=1&companyId=${company}&view=work-hours`)
  await page.getByRole('combobox', { name: '填报项目', exact: true }).click()
  await page.getByRole('option', { name: /P700014/ }).click()
  const hours = page.getByLabel('工时数', { exact: true })
  await hours.fill('4'); await hours.press('ArrowUp'); await expect(hours).toHaveValue('4.5')
  await hours.press('ArrowDown'); await expect(hours).toHaveValue('4')
  await hours.hover(); const box = await hours.boundingBox()
  const padding = await hours.evaluate(element => parseFloat(getComputedStyle(element).paddingRight))
  await page.mouse.click(box!.x + box!.width - padding - 7, box!.y + box!.height / 2 - 4); await expect(hours).toHaveValue('4.5')
  await page.mouse.click(box!.x + box!.width - padding - 7, box!.y + box!.height / 2 + 4); await expect(hours).toHaveValue('4')
  await hours.fill('24'); await hours.press('ArrowUp'); await expect(hours).toHaveValue('24')
  await hours.fill('0.5'); await hours.press('ArrowDown'); await expect(hours).toHaveValue('0.5')
  await hours.fill('0.25'); await page.getByRole('button', { name: '保存', exact: true }).click()
  await expect(page.getByRole('alert').filter({ hasText: '按0.5小时填写' })).toBeVisible()
  expect(state.saves).toHaveLength(0)
  await hours.fill('4.5'); await page.getByRole('button', { name: '保存', exact: true }).click()
  await expect.poll(() => state.entries[0]?.hours).toBe(4.5)
  await hours.fill('5.5'); await page.getByRole('button', { name: '保存', exact: true }).click()
  await expect.poll(() => state.entries[0]?.hours).toBe(5.5)
  expect(state.entries).toHaveLength(1)
})

test('mobile navigation stays available while saving time in the background', async ({ page }) => {
  const state = await fixture(page)
  await page.goto(`/?dingtalkOffice=1&companyId=${company}&view=work-hours`)
  await page.getByRole('combobox', { name: '填报项目', exact: true }).click()
  await page.getByRole('option', { name: /P700014/ }).click()
  await page.getByLabel('工时数', { exact: true }).fill('6.5')
  let release!: () => void, requested!: () => void
  const blocked = new Promise<void>(resolve => { release = resolve }), started = new Promise<void>(resolve => { requested = resolve })
  await page.route('**/api/work-hours', async route => { if (route.request().method() === 'PUT') { requested(); await blocked }; await route.fallback() })
  try {
    await page.getByRole('button', { name: '保存', exact: true }).click(); await started
    await page.getByRole('navigation').getByRole('button', { name: '首页', exact: true }).click()
    await expect(page.getByRole('region', { name: '移动办公首页' })).toBeVisible()
    await expect(page.getByRole('alert').filter({ hasText: '当前有未保存内容' })).toHaveCount(0)
  } finally { release() }
  await expect.poll(() => state.entries[0]?.hours).toBe(6.5)
  await page.getByRole('navigation').getByRole('button', { name: '工时', exact: true }).click()
  await expect(page.getByLabel('工时数', { exact: true })).toHaveValue('6.5')
})

test('approval detail binds the selected release and saves decisions through the existing endpoint', async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 })
  const state = await fixture(page)
  await page.goto(`/?dingtalkOffice=1&companyId=${company}&view=tasks&taskId=${task.id}`)
  await expect(page.getByRole('region', { name: '审批详情' })).toBeVisible()
  await expect(page.getByText('版本 A.2')).toBeVisible()
  let previewVersion = ''
  await page.route('**/api/documents/drawing-1/versions/*/preview', route => { previewVersion = new URL(route.request().url()).pathname; return route.fulfill({ status: 404, json: { message: '本次审批版本尚未生成预览。' } }) })
  await page.getByRole('button', { name: '查看此版本图纸', exact: true }).click()
  await expect(page.getByRole('alert')).toContainText('预览')
  expect(previewVersion).toBe('/api/documents/drawing-1/versions/bound-version/preview')
  await page.getByRole('button', { name: '退回修改', exact: true }).click()
  await expect(page.getByRole('alert')).toContainText('退回时请填写处理意见')
  expect(state.decisions).toHaveLength(0)
  await page.getByLabel('审批处理意见').fill('需要补充说明')
  await page.getByRole('button', { name: '退回修改', exact: true }).click()
  await expect(page.getByRole('status')).toContainText('审批操作已保存')
  expect(state.decisions).toEqual([{ decision: 1, comment: '需要补充说明' }])
  await expect(page.getByRole('heading', { name: '待审批（0）' })).toBeVisible()
})
test('unbound identities and ordinary browsers never load employee business data', async ({ page }) => {
  const state = await fixture(page, { unbound: true })
  await page.goto(`/?dingtalkOffice=1&companyId=${company}`)
  await expect(page.getByRole('alert')).toContainText('employee-id')
  expect(state.projectRequests).toBe(0)
  await expect(page.getByRole('navigation')).toHaveCount(0)
})
test('read-only roles cannot start approvals or add OPL', async ({ page }) => {
  await fixture(page, { readonly: true })
  await page.goto(`/?dingtalkOffice=1&companyId=${company}&projectId=${project}&tab=opl`)
  await expect(page.getByRole('region', { name: '开口问题清单' })).toBeVisible()
  await expect(page.getByRole('button', { name: '新增问题', exact: true })).toHaveCount(0)
  await page.getByRole('navigation').getByRole('button', { name: /^待办/ }).click()
  await page.getByRole('button', { name: /P700014.*移动办公验证项目/ }).click()
  await expect(page.getByRole('button', { name: '通过审批', exact: true })).toHaveCount(0)
})
test('ordinary browser is stopped before employee login', async ({ page }) => {
  const state = await fixture(page, { browser: true })
  await page.goto(`/?dingtalkOffice=1&companyId=${company}`)
  await expect(page.getByRole('alert')).toContainText('请在钉钉工作台中打开')
  expect(state.projectRequests).toBe(0)
})

test('without project view permission mobile navigation and project shortcuts stay hidden', async ({ page }) => {
  const state = await fixture(page, { noProjectView: true })
  await page.goto(`/?dingtalkOffice=1&companyId=${company}&view=projects&projectId=${project}`)
  await expect(page.getByRole('region', { name: '移动办公首页' })).toBeVisible()
  await expect(page.getByRole('navigation').getByRole('button', { name: '项目', exact: true })).toHaveCount(0)
  await expect(page.getByRole('button', { name: '查看项目 / 提交OPL', exact: true })).toHaveCount(0)
  expect(state.projectRequests).toBe(0)
})
test('project shortcut preselects the project and landscape layout stays within the viewport', async ({ page }) => {
  await page.setViewportSize({ width: 844, height: 390 })
  await fixture(page)
  await page.goto(`/?dingtalkOffice=1&companyId=${company}&projectId=${project}`)
  await page.getByRole('button', { name: '填写项目工时', exact: true }).click()
  await expect(page.getByLabel('自动带出项目名称')).toHaveValue(projectData.name)
  await expect(page.getByLabel('填报日期')).toHaveValue(today)
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true)
  await page.getByRole('navigation').getByRole('button', { name: '首页', exact: true }).click()
  await expect(page.getByRole('region', { name: '移动办公首页' })).toBeVisible()
  await page.getByRole('navigation').getByRole('button', { name: '项目', exact: true }).click()
  await page.getByRole('button', { name: '填写项目工时', exact: true }).click()
  await expect(page.getByLabel('自动带出项目名称')).toHaveValue(projectData.name)
})
test('logout during a background refresh does not block the next verified login', async ({ page }) => {
  await fixture(page)
  await page.goto(`/?dingtalkOffice=1&companyId=${company}`)
  await expect(page.getByRole('region', { name: '移动办公首页' })).toBeVisible()
  await expect(page.getByRole('button', { name: /P700014.*移动办公验证项目/ }).first()).toBeVisible()
  let release!: () => void, requested!: () => void
  const blocked = new Promise<void>(resolve => { release = resolve })
  const requestStarted = new Promise<void>(resolve => { requested = resolve })
  await page.route('**/api/projects', async route => { requested(); await blocked; await route.fulfill({ json: [projectData] }) }, { times: 1 })
  await page.getByRole('navigation').getByRole('button', { name: '项目', exact: true }).click()
  await page.getByRole('button', { name: /P700014.*移动办公验证项目/ }).click()
  await page.getByRole('button', { name: 'OPL开口问题', exact: true }).click()
  await page.getByRole('button', { name: '新增问题', exact: true }).click()
  await page.getByRole('textbox', { name: '问题描述', exact: true }).fill('手机新增问题')
  await page.getByRole('button', { name: '保存问题', exact: true }).click()
  await requestStarted
  await page.getByRole('navigation').getByRole('button', { name: '我的', exact: true }).click()
  await page.getByRole('button', { name: '退出当前登录', exact: true }).click()
  await expect(page.getByRole('button', { name: '重新进入', exact: true })).toBeVisible()
  release()
  await page.getByRole('button', { name: '重新进入', exact: true }).click()
  await expect(page.getByRole('region', { name: '移动办公首页' })).toBeVisible()
  await expect(page.getByRole('button', { name: /P700014.*移动办公验证项目/ }).first()).toBeVisible()
})
