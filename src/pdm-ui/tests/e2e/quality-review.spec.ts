import { test, expect } from '@playwright/test'
import { join } from 'node:path'
import { tmpdir } from 'node:os'

test('company review supports classification, improvement, empty state and narrow layout over LAN HTTP', async ({ page }) => {
  test.setTimeout(120_000)
  page.setDefaultTimeout(15_000)
  const errors: string[] = [], writes: string[] = []
  let reviewLoads = 0
  let themeDeleteRequests = 0
  page.on('pageerror', error => errors.push(error.message))
  page.on('console', message => {
    if (message.type() !== 'error') return
    const expectedRejection = message.text().includes('400') && message.location().url.includes(`/api/quality-review/themes/${themeId}`)
      || message.text().includes('[质量与改善]') && message.text().includes('该主题已被问题记录引用，无法删除')
    if (!expectedRejection) errors.push(message.text())
  })
  const projectId = '11111111-1111-1111-1111-111111111111', issueId = '22222222-2222-2222-2222-222222222222'
  const themeId = '33333333-3333-3333-3333-333333333333'
  const permissions = ['quality.review.view', 'quality.review.detail', 'quality.review.manage', 'quality.improvement.manage']
  const session = { accessToken: 'quality-browser-fixture', expiresAt: '2099-01-01T00:00:00Z', username: 'review-reader', displayName: '复盘验收员', role: 'Engineer', permissions,
    primaryCompanyId: 'company-test', activeCompanyId: 'company-test', activeCompanyName: '复盘验收公司', accessibleCompanies: [{ id: 'company-test', name: '复盘验收公司', code: 'TEST' }] }
  const issue = { id: issueId, projectId, number: 1, description: '定位基准偏差导致装配返工', priority: '普通', status: '待处理', owner: 'engineer', dueDate: '2026-11-30', solution: '', createdBy: 'reporter', createdAt: '2026-11-10T00:00:00Z', completedAt: null, updatedBy: 'reporter', updatedAt: '2026-11-10T00:00:00Z', rowVersion: 1, attachments: [] }
  const row = { issue, profile: null as any, rootProjectId: projectId, projectCode: 'QA-001', projectName: '质量复盘试点', ownerName: '张工', createdByName: '李工', canOpenProject: true }
  const people = [{ username: 'engineer', displayName: '张工' }, { username: 'reporter', displayName: '李工' }]
  const catalog = { companyId: 'company-test', rowVersion: 0, profiles: [] as any[], themes: [{ id: themeId, name: '定位基准不一致', description: '不同工序定位基准未统一', isActive: true }], summaries: [], improvements: [] as any[], experiences: [], adoptions: [], settings: { windowDays: 90, eventThreshold: 3, projectThreshold: 2 } }
  const payload = { catalog, issues: [row], metrics: [{ themeId, name: '定位基准不一致', eventCount: 3, projectCount: 2, majorCount: 0, isCandidate: true, activeImprovementId: null, unknownDateCount: 0 }],
    projects: [{ id: projectId, code: 'QA-001', name: '质量复盘试点', rootProjectId: projectId }], people,
    capabilities: { canViewDetails: true, canManageThemes: true, canManageImprovements: true, canExecuteImprovements: true, canManageSettings: true }, windowFrom: '2026-08-14', windowTo: '2026-11-11' }
  await page.route('**/api/**', async route => {
    const path = new URL(route.request().url()).pathname, method = route.request().method()
    const fulfill = (json: unknown) => route.fulfill({ json })
    if (path === '/api/auth/login' || path === '/api/auth/resume') return fulfill(session)
    if (path === '/api/auth/me' || path === '/api/auth/profile') return fulfill({ username: session.username, displayName: session.displayName })
    if (path === '/api/me/table-preferences') return fulfill({ preferences: {} })
    if (path === '/api/organization-directory') return fulfill({ organizations: [{ id: 'company-test', code: 'TEST', name: '复盘验收公司', isActive: true }], units: [], departments: [] })
    if (path === '/api/system-connections') return fulfill({ pdf: { state: 'unconfigured', message: '未配置' }, u9c: { state: 'unconfigured', message: '未配置' } })
    if (path === '/api/materials/notification-permission') return fulfill({ enabled: false })
    if (path === '/api/workbench') return fulfill({ projects: [], notes: [], risks: [], scope: 'company', companyName: '复盘验收公司' })
    if (path === '/api/quality-review') { reviewLoads++; return fulfill(payload) }
    if (path === `/api/projects/${projectId}/quality-review/issues/${issueId}`) {
      if (method === 'PUT') { const input = route.request().postDataJSON(); row.profile = { ...input, issueId, updatedBy: 'engineer', updatedAt: '2026-11-11T01:00:00Z' }; catalog.profiles = [row.profile]; catalog.rowVersion++; writes.push('issue') }
      return fulfill(method === 'PUT' ? { rowVersion: catalog.rowVersion } : { rowVersion: catalog.rowVersion, row, themes: catalog.themes, people, relatedIssues: [], canManageIssues: true, canVerifyIssues: true, canCloseIssues: false, canReopenIssues: false, canVoidIssues: false })
    }
    if (path.startsWith('/api/quality-review/themes/') && method === 'PUT') { const input = route.request().postDataJSON(); catalog.themes.push({ ...input, id: path.split('/').at(-1) }); catalog.rowVersion++; writes.push('theme'); return fulfill({ rowVersion: catalog.rowVersion }) }
    if (path.startsWith('/api/quality-review/themes/') && method === 'DELETE') {
      themeDeleteRequests++
      expect(new URL(route.request().url()).searchParams.get('expectedRowVersion')).toBe(String(catalog.rowVersion))
      const id = path.split('/').at(-1)
      if (id === themeId) return route.fulfill({ status: 400, json: { detail: '该主题已被问题记录引用，无法删除。请在维护中将主题停用。' } })
      catalog.themes = catalog.themes.filter(theme => theme.id !== id); catalog.rowVersion++; writes.push('theme-delete')
      return fulfill({ rowVersion: catalog.rowVersion })
    }
    if (path.startsWith('/api/quality-review/improvements/') && method === 'PUT') { const input = route.request().postDataJSON(); catalog.improvements.push({ ...input, id: path.split('/').at(-1), state: '草稿', observations: [], history: [], createdBy: 'engineer', createdAt: '2026-11-11T01:00:00Z', observationStartedAt: null }); catalog.rowVersion++; writes.push('improvement'); return fulfill({ rowVersion: catalog.rowVersion }) }
    if (path === '/api/quality-review/settings' && method === 'PUT') { const input = route.request().postDataJSON(); catalog.settings = input; catalog.rowVersion++; writes.push('settings'); return fulfill({ rowVersion: catalog.rowVersion }) }
    return fulfill([])
  })
  await page.route('**/health', route => route.fulfill({ json: { status: 'ok' } }))
  await page.route('**/*jelly*.html*', route => new URL(route.request().url()).searchParams.has('import') ? route.fallback() : route.fulfill({ contentType: 'text/html', body: '<html></html>' }))
  await page.setViewportSize({ width: 1440, height: 1000 })
  await page.goto('/')
  expect(new URL(page.url()).hostname).toBe(process.env.PDM_E2E_HOST || '127.0.0.1')
  await expect(page).toHaveTitle(/PLM/)
  const login = page.getByLabel('登录PLM')
  await login.getByRole('textbox', { name: '账号' }).fill('review-reader')
  await login.getByRole('textbox', { name: '密码' }).fill('fixture-password')
  await login.getByRole('button', { name: '登录', exact: true }).click()
  await page.locator('.pdm-sidebar').getByRole('button', { name: '经验总结', exact: true }).click()
  const panel = page.getByRole('region', { name: '公司问题与改善', exact: true })
  await expect(panel.getByText(issue.description, { exact: true })).toBeVisible()
  await expect(panel.getByRole('heading', { name: '公司问题与改善', exact: true })).toHaveCount(0)
  const tabs = panel.getByRole('tablist', { name: '公司问题视图' })
  await expect(tabs).toHaveClass(/\bwh-tabs\b/)
  const refresh = panel.getByRole('button', { name: '刷新', exact: true })
  const refreshBox = (await refresh.boundingBox())!
  const keywordBox = (await panel.getByLabel('关键词').boundingBox())!
  expect(Math.abs(refreshBox.y + refreshBox.height - keywordBox.y - keywordBox.height)).toBeLessThan(2)
  expect(refreshBox.x).toBeGreaterThanOrEqual(keywordBox.x + keywordBox.width)
  const cards = panel.locator('.quality-review__stats > article')
  await expect(cards).toHaveCount(3)
  await expect(cards.nth(1)).toHaveClass(/is-warning/)
  await refresh.click()
  await expect.poll(() => reviewLoads).toBe(2)

  for (const [tabName, addName] of [
    ['共性问题', '新增主题'], ['改善事项', '新增改善'],
    ['经验复用', ''], ['项目总结', ''], ['统计设置', ''],
  ]) {
    await tabs.getByRole('tab', { name: tabName, exact: true }).click()
    await expect(refresh).toHaveCount(1)
    const button = (await refresh.boundingBox())!
    const card = (await cards.first().boundingBox())!
    expect(button.y).toBeGreaterThan(card.y + card.height)
    if (addName) {
      const add = (await panel.getByRole('button', { name: addName, exact: true }).boundingBox())!
      expect(Math.abs(button.y - add.y)).toBeLessThan(2)
      expect(button.x + button.width).toBeLessThanOrEqual(add.x)
    }
    const before = reviewLoads
    await refresh.click()
    await expect.poll(() => reviewLoads).toBe(before + 1)
  }
  await tabs.getByRole('tab', { name: '问题总表', exact: true }).click()
  await expect(cards.first()).toHaveCSS('height', '100px')
  await expect(panel.getByText('张工', { exact: true })).toBeVisible()
  await panel.getByLabel('关键词').fill('无结果')
  await expect(panel.getByText('没有符合条件的问题。')).toBeVisible()
  await panel.getByLabel('关键词').clear()
  await panel.getByRole('button', { name: '闭环资料', exact: true }).click()
  const dialog = page.getByRole('dialog')
  await dialog.getByLabel('问题类型', { exact: true }).selectOption('质量')
  await expect(dialog.getByLabel('分类', { exact: true })).toHaveCount(0)
  await expect(dialog.getByText('同一事件的重复登记', { exact: true })).toHaveCount(0)
  await expect(dialog.getByText('独立复发的原问题', { exact: true })).toHaveCount(0)
  await dialog.getByLabel('实际发生日期').fill('2026-11-10')
  await dialog.getByLabel('纠正措施').fill('维护主题期间保留的措施草稿')
  await expect(dialog.locator('details')).not.toHaveAttribute('open', '')
  await expect(page.locator('[data-pdm-genie-layer]')).toHaveCount(0)
  await page.screenshot({ path: join(tmpdir(), 'pdm-quality-simplified-daily.png'), fullPage: true })
  await page.setViewportSize({ width: 400, height: 900 })
  await expect(dialog.getByLabel('问题类型', { exact: true })).toBeVisible()
  expect(await dialog.locator('.quality-review__issue-form').evaluate(element => element.scrollWidth <= element.clientWidth + 2)).toBe(true)
  await page.screenshot({ path: join(tmpdir(), 'pdm-quality-simplified-daily-narrow.png'), fullPage: true })
  await page.setViewportSize({ width: 1440, height: 1000 })
  await dialog.locator('details > summary').click()
  await dialog.getByRole('button', { name: '维护共性问题', exact: true }).click()
  const directory = page.getByRole('dialog', { name: '公司共性问题目录', exact: true })
  await directory.getByRole('button', { name: '新增主题', exact: true }).click()
  const themeDialog = page.getByRole('dialog', { name: '共性问题主题', exact: true })
  await themeDialog.getByLabel('主题名称', { exact: true }).fill('新建共性问题验收')
  await themeDialog.getByLabel('定义及归类边界', { exact: true }).fill('同一类定位偏差按主项目归集')
  await themeDialog.getByRole('button', { name: '保存主题', exact: true }).click()
  await expect(directory.getByText('共性问题主题已保存。')).toBeVisible()
  await expect(directory.getByText('新建共性问题验收', { exact: true })).toBeVisible()
  await directory.getByRole('button', { name: /关闭/ }).click()
  await expect(directory).not.toBeVisible()
  await expect(page.locator('[data-pdm-genie-layer]')).toHaveCount(0)
  await dialog.getByRole('combobox', { name: '归入公司共性问题', exact: true }).selectOption(catalog.themes.find(theme => theme.name === '新建共性问题验收')!.id)
  await dialog.getByRole('button', { name: '维护共性问题', exact: true }).click()
  await expect(directory).toBeVisible()
  const newThemeRow = directory.getByRole('row').filter({ hasText: '新建共性问题验收' })
  await newThemeRow.getByRole('button', { name: '删除', exact: true }).click()
  const deleteConfirmation = page.getByRole('dialog', { name: '删除共性主题', exact: true })
  await expect(deleteConfirmation).toContainText('新建共性问题验收')
  await deleteConfirmation.getByRole('button', { name: '取消', exact: true }).click()
  await expect(deleteConfirmation).not.toBeVisible()
  expect(themeDeleteRequests).toBe(0)
  await expect(newThemeRow).toBeVisible()
  await newThemeRow.getByRole('button', { name: '删除', exact: true }).click()
  await deleteConfirmation.getByRole('button', { name: '确认删除', exact: true }).click()
  await expect(deleteConfirmation).not.toBeVisible()
  await expect(newThemeRow).toHaveCount(0)
  await expect(directory.getByText('共性问题主题已删除。', { exact: true })).toBeVisible()
  await expect(page.locator('[data-pdm-genie-layer]')).toHaveCount(0)
  await page.screenshot({ path: join(tmpdir(), 'pdm-quality-company-directory.png'), fullPage: true })
  await directory.getByRole('button', { name: /关闭/ }).click()
  await expect(dialog.getByLabel('纠正措施')).toHaveValue('维护主题期间保留的措施草稿')
  await expect(dialog.getByRole('combobox', { name: '归入公司共性问题', exact: true }).locator('option:checked')).toHaveText('未归类')
  await expect(dialog.getByRole('combobox', { name: '归入公司共性问题', exact: true }).locator('option')).toContainText(['未归类', '定位基准不一致'])
  await expect(dialog.getByRole('combobox', { name: '归入公司共性问题', exact: true }).locator('option')).toHaveCount(2)
  await dialog.getByRole('combobox', { name: '归入公司共性问题', exact: true }).selectOption(themeId)
  await dialog.getByLabel('临时处置').fill('隔离待装配部件')
  await dialog.getByLabel('原因分析').fill('图纸定位基准未统一')
  await dialog.getByLabel('纠正措施').fill('统一工艺基准并复测')
  await dialog.getByRole('button', { name: '保存闭环', exact: true }).click()
  await expect(dialog.getByText('问题闭环资料已保存。')).toBeVisible()
  await dialog.getByRole('button', { name: /关闭/ }).click()
  await panel.getByRole('tab', { name: '共性问题', exact: true }).click()
  await panel.getByRole('row').filter({ hasText: '定位基准不一致' }).first().getByRole('button', { name: '删除', exact: true }).click()
  await deleteConfirmation.getByRole('button', { name: '确认删除', exact: true }).click()
  await expect(deleteConfirmation).not.toBeVisible()
  await expect(panel.getByRole('alert')).toContainText('该主题已被问题记录引用，无法删除')
  await expect(panel.getByRole('row').filter({ hasText: '定位基准不一致' }).first()).toBeVisible()
  await expect(page.locator('[data-pdm-genie-layer]')).toHaveCount(0)
  await page.screenshot({ path: join(tmpdir(), 'pdm-quality-theme-delete-protection.png'), fullPage: true })
  await panel.getByRole('button', { name: '发起改善', exact: true }).click()
  await dialog.getByRole('combobox', { name: /^负责人/ }).click()
  await page.getByRole('option', { name: /张工/ }).click()
  await dialog.getByLabel('完成日期', { exact: true }).fill('2026-12-31')
  await dialog.getByLabel('共同原因').fill('标准未明确基准')
  await dialog.getByLabel('目标及适用样本定义').fill('后续三个项目使用统一定位基准')
  await dialog.getByRole('button', { name: '新增措施', exact: true }).click()
  await dialog.getByLabel('措施内容').fill('修订装配检验标准')
  await dialog.getByRole('button', { name: '保存改善', exact: true }).click()
  await expect(panel.getByText('改善事项已保存。')).toBeVisible()
  await expect(panel.getByText('修订装配检验标准', { exact: true })).toBeVisible()
  await expect(page.locator('[data-pdm-genie-layer]')).toHaveCount(0)
  await page.screenshot({ path: join(tmpdir(), 'pdm-quality-review-desktop.png'), fullPage: true })
  await panel.getByRole('tab', { name: '统计设置', exact: true }).click()
  const settingBoxes = await Promise.all(['统计窗口（天）', '事件次数阈值', '涉及项目阈值'].map(name => panel.getByLabel(name, { exact: true }).boundingBox()))
  expect(settingBoxes.every(box => box && Math.abs(box.y - settingBoxes[0]!.y) < 2)).toBe(true)
  await panel.locator('form').screenshot({ path: join(tmpdir(), 'pdm-quality-settings-one-row.png') })
  const threshold = panel.getByLabel('事件次数阈值')
  await threshold.focus(); await threshold.press('ArrowUp'); await expect(threshold).toHaveValue('4')
  await threshold.press('ArrowDown'); await expect(threshold).toHaveValue('3')
  await panel.getByRole('button', { name: '保存设置', exact: true }).click()
  await expect(panel.getByText('高频识别设置已保存。')).toBeVisible()
  await page.setViewportSize({ width: 760, height: 900 })
  await expect(panel.getByLabel('统计窗口（天）')).toBeVisible()
  await page.setViewportSize({ width: 400, height: 900 })
  expect(await cards.evaluateAll(elements => elements.every(card => {
    const bounds = card.getBoundingClientRect()
    const note = card.querySelector('em')!.getBoundingClientRect()
    return bounds.height === 100 && note.bottom <= bounds.bottom && note.right <= bounds.right
  }))).toBe(true)
  await page.screenshot({ path: join(tmpdir(), 'pdm-quality-review-narrow.png'), fullPage: true })
  expect(await panel.evaluate(element => element.scrollWidth <= element.clientWidth + 2)).toBe(true)
  expect(writes).toEqual(['theme', 'theme-delete', 'issue', 'improvement', 'settings'])
  expect(themeDeleteRequests).toBe(2)
  await expect(page.locator('vite-error-overlay')).toHaveCount(0)
  expect(errors).toEqual([])
})
