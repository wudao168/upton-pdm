import { mkdir, writeFile } from 'node:fs/promises'
import { resolve } from 'node:path'
import { chromium } from '@playwright/test'

const port = Number(process.argv[2])
const scaleName = process.argv[3]
const outputDirectory = resolve(process.argv[4])
const openPreview = process.argv.includes('--open-preview')

if (!Number.isInteger(port) || !scaleName || !process.argv[4]) {
  throw new Error('Usage: node test-webview.mjs <port> <scale-name> <output-directory>')
}

await mkdir(outputDirectory, { recursive: true })
const errors = []
const browser = await chromium.connectOverCDP(`http://127.0.0.1:${port}`)

try {
  const pages = browser.contexts().flatMap(context => context.pages())
  const page = pages.find(candidate => candidate.url().includes('/ui/index.html'))
    ?? pages.find(candidate => candidate.url() !== 'about:blank')
  if (!page) throw new Error(`WebView2 PDM page was not found: ${pages.map(candidate => candidate.url()).join(', ')}`)

  page.on('pageerror', error => errors.push(error.message))
  page.on('console', message => {
    if (message.type() === 'error') errors.push(message.text())
  })

  const loginButton = page.getByRole('button', { name: '登录并加载数据' })
  if (await loginButton.count()) {
    await loginButton.waitFor({ state: 'visible', timeout: 15_000 })
    if (!(await loginButton.isEnabled())) throw new Error('Windows encrypted credentials were not restored into WebView2')
    await loginButton.click()
  }

  await page.locator('.pdm-app-body').waitFor({ state: 'visible', timeout: 20_000 })
  const navCases = [
    ['工作台', '工作台主页面'],
    ['项目图档', '项目图档结构'],
    ['BOM管理', 'BOM维护'],
    ['图纸审批', '审批与生产发包'],
    ['生产发包', '审批与生产发包'],
    ['审计查询', '审计查询'],
    ['系统设置', '项目存储设置'],
  ]

  const checkedButtons = []
  for (const [buttonName, panelName] of navCases) {
    const button = page.getByRole('button', { name: buttonName === '图纸审批' ? /^图纸审批/ : buttonName, exact: buttonName !== '图纸审批' })
    if (!(await button.isEnabled())) throw new Error(`Navigation button is disabled: ${buttonName}`)
    await button.click()
    await page.getByLabel(panelName).waitFor({ state: 'visible', timeout: 10_000 })
    checkedButtons.push(buttonName)
  }

  await page.getByRole('button', { name: '项目图档', exact: true }).click()
  await page.getByLabel('项目图档结构').waitFor({ state: 'visible', timeout: 10_000 })
  const metrics = await page.evaluate(() => {
    const shell = document.querySelector('.pdm-app-shell')
    const tree = document.querySelector('[role="tree"]')
    return {
      devicePixelRatio: window.devicePixelRatio,
      bodyClientHeight: document.body.clientHeight,
      bodyScrollHeight: document.body.scrollHeight,
      shellClientHeight: shell?.clientHeight ?? 0,
      shellScrollHeight: shell?.scrollHeight ?? 0,
      treeClientHeight: tree?.clientHeight ?? 0,
      treeScrollHeight: tree?.scrollHeight ?? 0,
      viewportWidth: window.innerWidth,
      viewportHeight: window.innerHeight,
    }
  })

  if (metrics.bodyClientHeight !== metrics.bodyScrollHeight) throw new Error('The WPF page scrolls vertically')
  if (metrics.shellClientHeight !== metrics.shellScrollHeight) throw new Error('The WPF application shell scrolls vertically')
  if (metrics.treeScrollHeight < metrics.treeClientHeight) throw new Error('The document tree has invalid scroll metrics')

  if (openPreview) {
    const previewButton = page.getByRole('button', { name: '打开真实预览' })
    if (!(await previewButton.isEnabled())) throw new Error('The real preview button is disabled')
    await previewButton.click()
    await page.waitForTimeout(3_000)
  }

  await page.screenshot({ path: resolve(outputDirectory, `webview-${scaleName}.png`) })
  const report = {
    status: errors.length === 0 ? 'passed' : 'failed',
    scale: scaleName,
    url: page.url(),
    metrics,
    checkedButtons,
    previewRequested: openPreview,
    consoleErrors: errors,
  }
  await writeFile(resolve(outputDirectory, `webview-${scaleName}.json`), `${JSON.stringify(report, null, 2)}\n`, 'utf8')
  process.stdout.write(JSON.stringify(report))
  if (errors.length) process.exitCode = 1
} finally {
  await browser.close()
}
