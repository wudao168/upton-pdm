import { expect, test } from '@playwright/test'

const projectId = '11111111-1111-1111-1111-111111111111'

test.beforeEach(async ({ page }) => {
  await page.route('http://127.0.0.1:5080/**', async (route) => {
    const path = new URL(route.request().url()).pathname
    const fulfill = (body: unknown, status = 200) => route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(body) })
    if (path === '/health') return fulfill({ status: 'ok' })
    if (path === '/api/auth/login') return fulfill({ accessToken: 'e2e-token', expiresAt: '2099-01-01T00:00:00Z', username: 'engineer', displayName: '真实工程师', role: 'Engineer' })
    if (path === '/api/projects') return fulfill([{ id: projectId, code: 'PRJ-REAL-001', name: '真实装配项目', owner: '真实工程师', vaultLocation: 'D:\\PDM\\PRJ-REAL-001', releaseLocation: 'D:\\Release\\PRJ-REAL-001', isActive: true }])
    if (path.endsWith('/documents')) return fulfill([{ id: 'doc-root', drawingNumber: 'REAL-ASM-001', name: '真实总装配', fileName: 'REAL-ASM-001.SLDASM', kind: 0, revision: { display: 'W2' }, checkedOutBy: 'engineer' }, { id: 'doc-part', drawingNumber: 'REAL-PRT-001', name: '真实底板', fileName: 'REAL-PRT-001.SLDPRT', kind: 1, revision: { display: 'A' }, checkedOutBy: null }])
    if (path.endsWith('/reference-tree')) return fulfill({ nodeId: 'node-root', documentId: 'doc-root', instancePath: 'REAL-ASM-001', fileName: 'REAL-ASM-001.SLDASM', displayName: '真实总装配', kind: 0, configuration: '默认', quantity: 1, status: 0, revision: null, checkedOutBy: 'engineer', children: [{ nodeId: 'node-part', documentId: 'doc-part', instancePath: 'REAL-ASM-001/REAL-PRT-001', fileName: 'REAL-PRT-001.SLDPRT', displayName: '真实底板', kind: 1, configuration: '默认', quantity: 2, status: 0, revision: null, checkedOutBy: null, children: [] }] })
    if (path.endsWith('/boms/Mechanical')) return fulfill([{ sequence: 1, drawingNumber: 'REAL-PRT-001', name: '真实底板', quantity: 2, unit: '件', material: 'Q235B', specification: '10mm', revision: 'A', isComplete: true }])
    if (path.endsWith('/boms/Electrical')) return fulfill([{ sequence: 1, drawingNumber: 'REAL-EL-001', name: '真实传感器', quantity: 1, unit: '件', material: null, specification: 'PNP', revision: 'A', isComplete: false }])
    if (path.endsWith('/release-packages')) return fulfill([{ id: 'package-1', number: 'RP-REAL-001', state: 2, approvalTasks: [{ stage: 1, assignee: '工艺工程师', decisionBy: '工艺工程师', decision: 0, decidedAt: '2026-08-11T01:00:00Z' }, { stage: 2, assignee: '批准人', decisionBy: null, decision: null, decidedAt: null }], publishedAt: null }])
    return fulfill({ title: `Unexpected route: ${path}` }, 404)
  })
})

test('engineer logs in and reads the API-backed PDM workspace', async ({ page }) => {
  await page.goto('/')

  await expect(page.getByRole('main').getByText('登录 UPTON PDM')).toBeVisible()
  await page.getByLabel('登录PDM').getByLabel('用户名').fill('engineer')
  await page.getByLabel('登录PDM').getByLabel('密码').fill('correct-password')
  await page.getByRole('button', { name: '登录并加载数据' }).click()

  await expect(page.getByText('PRJ-REAL-001 · 真实装配项目')).toBeVisible()
  await expect(page.getByText('真实工程师', { exact: true })).toBeVisible()
  await expect(page.getByLabel('项目图档结构')).toContainText('REAL-PRT-001')
  await expect(page.getByLabel('图档预览')).toContainText('工作版本 W2')
  await expect(page.getByLabel('图档预览')).toContainText('正在编辑 · engineer')
  await expect(page.getByLabel('BOM完整性')).toContainText('1 项完整')
  await expect(page.getByLabel('BOM完整性')).toContainText('1 项待确认')
  await expect(page.getByLabel('当前发布包')).toContainText('RP-REAL-001')
  await expect(page.getByText('PRJ-2026-018')).toHaveCount(0)

  await page.getByRole('tab', { name: '机械BOM' }).click()
  await expect(page.getByRole('row', { name: /1 REAL-PRT-001 真实底板 2 Q235B A/ })).toBeVisible()
})
