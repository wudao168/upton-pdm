import { expect, test } from '@playwright/test'

const projectId = '11111111-1111-1111-1111-111111111111'
const versions = [
  { id: 'version-w1', documentId: 'doc-root', revision: { display: 'W1' }, status: 0, fileLength: 1024, sha256: 'A'.repeat(64), createdBy: 'engineer', createdAt: '2026-08-10T01:00:00Z', changeNote: '首次存档' },
  { id: 'version-w2', documentId: 'doc-root', revision: { display: 'W2' }, status: 0, fileLength: 2048, sha256: 'B'.repeat(64), createdBy: 'engineer', createdAt: '2026-08-11T01:00:00Z', changeNote: '完善结构' },
]

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
  await page.route('http://127.0.0.1:5080/**', async (route) => {
    const path = new URL(route.request().url()).pathname
    const fulfill = (body: unknown, status = 200) => route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(body) })
    if (path === '/health') return fulfill({ status: 'ok' })
    if (path === '/api/auth/login') return fulfill({ accessToken: 'e2e-token', expiresAt: '2099-01-01T00:00:00Z', username: 'engineer', displayName: '真实工程师', role: 'Engineer' })
    if (path === '/api/projects') return fulfill([{ id: projectId, code: 'PRJ-REAL-001', name: '真实装配项目', owner: '真实工程师', vaultLocation: 'D:\\PDM\\PRJ-REAL-001', releaseLocation: 'D:\\Release\\PRJ-REAL-001', isActive: true }])
    if (path.endsWith('/documents')) return fulfill([{ id: 'doc-root', drawingNumber: 'REAL-ASM-001', name: '真实总装配', fileName: 'REAL-ASM-001.SLDASM', kind: 0, revision: { display: 'W2' }, checkedOutBy: 'engineer' }, { id: 'doc-part', drawingNumber: 'REAL-PRT-001', name: '真实底板', fileName: 'REAL-PRT-001.SLDPRT', kind: 1, revision: { display: 'A' }, checkedOutBy: null }])
    if (path.endsWith('/reference-tree')) return fulfill({ nodeId: 'node-root', documentId: 'doc-root', instancePath: 'REAL-ASM-001', fileName: 'REAL-ASM-001.SLDASM', displayName: '真实总装配', kind: 0, configuration: '默认', quantity: 1, status: 0, revision: null, checkedOutBy: 'engineer', children: referenceChildren })
    if (path.endsWith('/boms/Mechanical')) return fulfill([{ sequence: 1, drawingNumber: 'REAL-PRT-001', name: '真实底板', quantity: 2, unit: '件', material: 'Q235B', specification: '10mm', revision: 'A', isComplete: true }])
    if (path.endsWith('/boms/Electrical')) return fulfill([{ sequence: 1, drawingNumber: 'REAL-EL-001', name: '真实传感器', quantity: 1, unit: '件', material: null, specification: 'PNP', revision: 'A', isComplete: false }])
    if (path.endsWith('/release-packages')) return fulfill([{ id: 'package-1', number: 'RP-REAL-001', state: 2, approvalTasks: [{ stage: 1, assignee: '工艺工程师', decisionBy: '工艺工程师', decision: 0, decidedAt: '2026-08-11T01:00:00Z' }, { stage: 2, assignee: '批准人', decisionBy: null, decision: null, decidedAt: null }], publishedAt: null }])
    if (path === '/api/documents/doc-root/versions') return fulfill(versions)
    if (path === '/api/documents/doc-root/versions/compare') return fulfill({ documentId: 'doc-root', left: versions[0], right: versions[1], propertyChanges: [], referenceChanges: [], bomChanges: [] })
    if (path === '/api/audit') return fulfill([{ id: 'audit-1', occurredAt: '2026-08-11T01:00:00Z', actor: 'engineer', action: 'VersionViewed', entityType: 'Document', entityId: 'doc-root', detail: '查看 W2' }])
    if (path.endsWith('/storage-status')) return fulfill({ vaultAvailable: true, releaseAvailable: true })
    if (path.endsWith('/boms/Mechanical') && route.request().method() === 'PUT') return fulfill([{ sequence: 1, drawingNumber: 'REAL-PRT-001', name: '真实底板', quantity: 2, unit: '件', material: 'Q235B', specification: '10mm', revision: 'A', isComplete: true }])
    return fulfill({ title: `Unexpected route: ${path}` }, 404)
  })
})

test('engineer logs in and reads the API-backed PDM workspace', async ({ page }) => {
  await page.goto('/')

  await expect(page.getByRole('main').getByText('登录 UPTON PDM')).toBeVisible()
  await page.getByLabel('登录PDM').getByLabel('用户名').fill('engineer')
  await page.getByRole('textbox', { name: '密码' }).fill('correct-password')
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

for (const scale of [
  { name: '125-percent', width: 1536, height: 864 },
  { name: '150-percent', width: 1280, height: 720 },
]) {
  test(`workspace remains fixed and interactive at ${scale.name} logical viewport`, async ({ page }, testInfo) => {
    await page.setViewportSize({ width: scale.width, height: scale.height })
    await page.goto('/')
    await page.getByRole('textbox', { name: '用户名' }).fill('engineer')
    await page.getByRole('textbox', { name: '密码' }).fill('correct-password')
    await page.getByRole('button', { name: '登录并加载数据' }).click()
    await expect(page.getByLabel('项目图档结构')).toBeVisible()

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
      ['工作台', '工作台主页面'],
      ['项目图档', '项目图档结构'],
      ['BOM管理', 'BOM维护'],
      ['图纸审批', '审批与生产发包'],
      ['生产发包', '审批与生产发包'],
      ['审计查询', '审计查询'],
      ['系统设置', '项目存储设置'],
    ] as const
    for (const [buttonName, panelName] of navCases) {
      const button = page.getByRole('button', { name: buttonName === '图纸审批' ? /^图纸审批/ : buttonName, exact: buttonName !== '图纸审批' })
      await expect(button).toBeEnabled()
      await button.click()
      await expect(page.getByLabel(panelName)).toBeVisible()
    }

    await page.getByRole('button', { name: '变更管理', exact: true }).click()
    await expect(page.getByText('图档历史版本对比')).toBeVisible()
    await expect(page.getByRole('button', { name: '只读预览左侧' })).toBeEnabled()

    await page.screenshot({ path: testInfo.outputPath(`workspace-${scale.name}.png`), fullPage: false })
  })
}
