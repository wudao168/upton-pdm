import { expect, test, type BrowserContext, type Page } from '@playwright/test'
import { join } from 'node:path'
import { defaultMobileOfficeLayout, type MobileOfficeLayoutView } from '../../src/mobileOfficeLayout'
const company = '11111111-1111-1111-1111-111111111111'
const base = '/api/settings/dingtalk/office-layout'
const screenshotRoot = process.env.UPLM_LAYOUT_SCREENSHOT_DIR || process.env.TEMP!
async function fixture(context: BrowserContext) {
  let state: MobileOfficeLayoutView = { companyId: company, rowVersion: 0, publishedVersion: 0, draft: defaultMobileOfficeLayout(), history: [{ version: 0, publishedAt: null, publishedBy: '系统', action: 'default', sourceVersion: null, layout: defaultMobileOfficeLayout() }] }
  let conflict = false, layoutOffline = false
  await context.addInitScript(() => {
    Object.assign(window, { dd: { env: { platform: 'ios' }, ready: (callback: () => void) => callback(), requestAuthCode: (input: { success: (value: { code: string }) => void }) => input.success({ code: 'verified-code' }) } })
  })
  await context.route('**/api/**', async route => {
    const path = new URL(route.request().url()).pathname, method = route.request().method()
    let json: unknown = []
    if (path.startsWith(base)) {
      if (method !== 'GET') {
        const input = route.request().postDataJSON()
        if (conflict || input.expectedRowVersion !== state.rowVersion) return route.fulfill({ status: 409, json: { message: '移动端页面已被其他管理员修改，请保留当前修改并重新读取后核对。' } })
        if (path.endsWith('/draft')) state.draft = input.layout
        else {
          const rollback = path.endsWith('/rollback'), layout = rollback ? state.history.find(item => item.version === input.version)!.layout : state.draft
          state.publishedVersion++; state.history.unshift({ version: state.publishedVersion, publishedAt: new Date().toISOString(), publishedBy: 'admin', action: rollback ? 'rollback' : 'publish', sourceVersion: rollback ? input.version : null, layout: structuredClone(layout) })
        }
        state.rowVersion++
      }
      json = state
    } else if (path.endsWith('/office/layout')) {
      if (layoutOffline) return route.fulfill({ status: 503, json: { message: '移动端布局暂时无法读取，请稍后刷新。' } })
      json = { version: state.publishedVersion, layout: state.history.find(item => item.version === state.publishedVersion)!.layout }
    } else if (path.endsWith('/office/bootstrap')) json = { enabled: true, corpId: 'corp', appKey: 'key', companyId: company }
    else if (path.endsWith('/office/login')) json = { accessToken: 'employee-token', username: 'employee', displayName: '员工张工', role: 'Engineer', permissions: [], activeCompanyId: company, activeCompanyName: '测试公司', accessibleCompanies: [] }
    else if (path === '/api/work-hours') json = { today: '2026-11-05', sheets: [], compliance: [], people: [], projects: [] }
    return route.fulfill({ json })
  })
  return { state: () => state, conflict: (value: boolean) => { conflict = value }, offline: () => { layoutOffline = true } }
}
function errorsFor(page: Page) { const errors: string[] = []; page.on('pageerror', error => errors.push(error.message)); page.on('console', message => { if (message.type() === 'error' && !message.text().includes('status of 409') && !message.text().includes('status of 503')) errors.push(message.text()) }); return errors }
async function editor(page: Page) {
  const failures: string[] = []
  page.on('pageerror', error => failures.push(error.message))
  page.on('console', message => { if (message.type() === 'error') failures.push(message.text()) })
  await page.goto('/tests/fixtures/mobile-office-layout.html')
  await expect(page.getByRole('region', { name: '移动端页面维护', exact: true }), `编辑器初始化失败：${failures.join('；')}`).toBeVisible()
  await expect(page.getByText('当前发布：系统默认 V0')).toBeVisible()
}
test('LAN HTTP visual editing, phone preview, draft isolation, publish and rollback', async ({ page, context }) => {
  test.setTimeout(60_000)
  const state = await fixture(context), errors = errorsFor(page)
  await page.setViewportSize({ width: 1536, height: 1000 }); await editor(page)
  await expect(page).toHaveTitle('UPLM移动端页面维护测试')
  await page.getByLabel('模块标题', { exact: true }).fill('工程移动工作台')
  await page.getByLabel('显示项目列表', { exact: true }).uncheck()
  await page.getByLabel('待办导航名称').fill('审批中心')
  await page.getByLabel('显示项目导航', { exact: true }).uncheck()
  await page.getByLabel('移动端主题').selectOption('blue')
  expect(await page.getByLabel('移动端主题').evaluate(element => element.clientHeight >= element.scrollHeight)).toBe(true)
  await page.getByLabel('上移审批列表', { exact: true }).click()
  await expect(page.locator('.layout-phone [data-block]').nth(1)).toHaveAttribute('data-block', 'approvals')
  const source = page.locator('.layout-item').filter({ has: page.getByRole('button', { name: '审批列表', exact: true }) })
  const target = page.locator('.layout-item').filter({ has: page.getByRole('button', { name: '业务概况', exact: true }) })
  await source.dragTo(target)
  await expect(page.locator('.layout-phone [data-block]').nth(1)).toHaveAttribute('data-block', 'metrics')
  await expect(page.getByLabel('手机页面预览')).toContainText('工程移动工作台')
  await expect(page.getByLabel('手机页面预览').getByRole('button', { name: /审批中心/ })).toBeVisible()
  for (const width of ['320', '390', '430']) {
    await page.getByLabel('手机预览宽度').selectOption(width)
    expect(await page.locator('.layout-phone').evaluate(element => element.scrollWidth <= element.clientWidth)).toBe(true)
  }
  await page.getByLabel('手机预览宽度').selectOption('390')
  await expect(page.getByRole('button', { name: '发布草稿', exact: true })).toBeDisabled()
  await page.getByRole('button', { name: '保存草稿', exact: true }).click()
  await expect(page.getByRole('alert')).toContainText('草稿已保存')
  const phone = await context.newPage(); const phoneErrors = errorsFor(phone); await phone.setViewportSize({ width: 390, height: 844 })
  await phone.goto(`/?dingtalkOffice=1&companyId=${company}`)
  await expect(phone.getByRole('region', { name: '移动办公首页' })).toContainText('移动办公', { timeout: 15_000 })
  await expect(phone.getByText('工程移动工作台')).toHaveCount(0)
  await page.getByRole('button', { name: '发布草稿', exact: true }).click(); await page.getByRole('button', { name: '确认发布', exact: true }).click()
  await expect(page.getByRole('alert')).toContainText('已发布 V1')
  await phone.reload()
  await expect(phone.getByRole('region', { name: '移动办公首页' })).toContainText('工程移动工作台')
  await expect(phone.getByRole('navigation').getByRole('button')).toHaveCount(4)
  await expect(phone.locator('[data-block="projects"]')).toHaveCount(0)
  expect(await phone.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true)
  await phone.screenshot({ path: join(screenshotRoot, 'mobile-office-layout-published-390.png'), fullPage: true })
  await page.screenshot({ path: join(screenshotRoot, 'mobile-office-layout-editor-desktop.png'), fullPage: true })
  await phone.getByRole('navigation').getByRole('button', { name: '我的', exact: true }).click(); await expect(phone.getByRole('region', { name: '我的账号' })).toContainText('V1')
  await page.getByRole('button', { name: '回滚到V0', exact: true }).click(); await page.getByRole('button', { name: '确认回滚', exact: true }).click()
  await expect(page.getByRole('alert')).toContainText('已回滚并发布为 V2')
  await phone.reload(); await expect(phone.getByRole('region', { name: '移动办公首页' })).toContainText('移动办公')
  await expect(phone.getByText('工程移动工作台')).toHaveCount(0)
  expect(state.state().draft.blocks[0]!.title).toBe('工程移动工作台')
  expect(state.state().history).toHaveLength(3)
  await page.reload(); await expect(page.getByLabel('模块标题', { exact: true })).toHaveValue('工程移动工作台')
  expect(errors).toEqual([]); expect(phoneErrors).toEqual([])
})
test('save conflict preserves input, historical preview is isolated, and controls work at narrow widths', async ({ page, context }) => {
  const state = await fixture(context), errors = errorsFor(page); await editor(page)
  await page.getByLabel('模块标题', { exact: true }).fill('冲突时保留的标题'); state.conflict(true)
  await page.getByRole('button', { name: '保存草稿', exact: true }).click()
  await expect(page.getByRole('alert')).toContainText('其他管理员修改')
  await expect(page.getByLabel('模块标题', { exact: true })).toHaveValue('冲突时保留的标题')
  await page.getByRole('button', { name: '预览版本V0', exact: true }).click(); await expect(page.locator('.layout-phone h1')).toHaveText('移动办公')
  await page.getByRole('button', { name: '预览当前草稿', exact: true }).click(); await expect(page.locator('.layout-phone h1')).toHaveText('冲突时保留的标题')
  await page.setViewportSize({ width: 390, height: 844 }); expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true)
  await page.screenshot({ path: join(screenshotRoot, 'mobile-office-layout-editor-narrow.png'), fullPage: true })
  expect(errors).toEqual([])
})
test('layout request failure retains the working default and displays a Chinese retry message', async ({ page, context }) => {
  const state = await fixture(context); state.offline(); await page.setViewportSize({ width: 390, height: 844 })
  await page.goto(`/?dingtalkOffice=1&companyId=${company}`)
  await expect(page.getByRole('region', { name: '移动办公首页' })).toBeVisible()
  await expect(page.getByRole('status').filter({ hasText: '布局暂时无法读取' })).toBeVisible()
  await expect(page.getByRole('navigation').getByRole('button', { name: '首页', exact: true })).toBeVisible()
})
