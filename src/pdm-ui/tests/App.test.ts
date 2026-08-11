import { flushPromises, mount } from '@vue/test-utils'
import ElementPlus from 'element-plus'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import App from '../src/App.vue'

const projectId = '11111111-1111-1111-1111-111111111111'

function json(value: unknown, status = 200) {
  return new Response(JSON.stringify(value), { status, headers: { 'Content-Type': 'application/json' } })
}

function installApiMock() {
  vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = String(input)
    if (url.endsWith('/health')) return json({ status: 'ok' })
    if (url.endsWith('/api/auth/login')) {
      const credentials = JSON.parse(String(init?.body)) as { password: string }
      return credentials.password === 'correct-password'
        ? json({ accessToken: 'test-token', expiresAt: '2099-01-01T00:00:00Z', username: 'engineer', displayName: '真实工程师', role: 'Engineer' })
        : json({ title: 'Unauthorized' }, 401)
    }
    if (url.endsWith('/api/projects')) {
      return json([{ id: projectId, code: 'PRJ-REAL-001', name: '真实装配项目', owner: '真实工程师', vaultLocation: 'D:\\PDM\\PRJ-REAL-001', releaseLocation: 'D:\\Release\\PRJ-REAL-001', isActive: true }])
    }
    if (url.endsWith('/documents')) {
      return json([
        { id: 'doc-root', drawingNumber: 'REAL-ASM-001', name: '真实总装配', fileName: 'REAL-ASM-001.SLDASM', kind: 0, revision: { display: 'W2' }, checkedOutBy: 'engineer' },
        { id: 'doc-part', drawingNumber: 'REAL-PRT-001', name: '真实底板', fileName: 'REAL-PRT-001.SLDPRT', kind: 1, revision: { display: 'A' }, checkedOutBy: null },
      ])
    }
    if (url.endsWith('/reference-tree')) {
      return json({ nodeId: 'node-root', documentId: 'doc-root', instancePath: 'REAL-ASM-001', fileName: 'REAL-ASM-001.SLDASM', displayName: '真实总装配', kind: 0, configuration: '默认', quantity: 1, status: 0, revision: null, checkedOutBy: 'engineer', children: [{ nodeId: 'node-part', documentId: 'doc-part', instancePath: 'REAL-ASM-001/REAL-PRT-001', fileName: 'REAL-PRT-001.SLDPRT', displayName: '真实底板', kind: 1, configuration: '默认', quantity: 2, status: 0, revision: null, checkedOutBy: null, children: [] }] })
    }
    if (url.endsWith('/boms/Mechanical')) {
      return json([{ sequence: 1, drawingNumber: 'REAL-PRT-001', name: '真实底板', quantity: 2, unit: '件', material: 'Q235B', specification: '10mm', revision: 'A', isComplete: true }])
    }
    if (url.endsWith('/boms/Electrical')) {
      return json([{ sequence: 1, drawingNumber: 'REAL-EL-001', name: '真实传感器', quantity: 1, unit: '件', material: null, specification: 'PNP', revision: 'A', isComplete: false }])
    }
    if (url.endsWith('/release-packages')) {
      return json([{ id: 'package-1', number: 'RP-REAL-001', state: 2, approvalTasks: [{ stage: 1, assignee: '工艺工程师', decisionBy: '工艺工程师', decision: 0, decidedAt: '2026-08-11T01:00:00Z' }, { stage: 2, assignee: '批准人', decisionBy: null, decision: null, decidedAt: null }], publishedAt: null }])
    }
    return json({ title: `Unexpected URL: ${url}` }, 404)
  }))
}

async function login(wrapper: ReturnType<typeof mount>) {
  await wrapper.get('input[name="username"]').setValue('engineer')
  await wrapper.get('input[name="password"]').setValue('correct-password')
  await wrapper.get('form[aria-label="登录PDM"]').trigger('submit')
  await flushPromises()
}

function buttonByText(wrapper: ReturnType<typeof mount>, label: string) {
  const button = wrapper.findAll('button').find((candidate) => candidate.text().includes(label))
  if (!button) throw new Error(`Button not found: ${label}`)
  return button
}

describe('PDM client workspace', () => {
  beforeEach(() => {
    window.sessionStorage.clear()
    installApiMock()
  })

  it('logs in and renders project, tree, BOM and release data returned by the API', async () => {
    const wrapper = mount(App, { attachTo: document.body, global: { plugins: [ElementPlus] } })
    expect(wrapper.text()).toContain('登录 UPTON PDM')

    await login(wrapper)

    expect(wrapper.text()).toContain('PRJ-REAL-001 · 真实装配项目')
    expect(wrapper.text()).toContain('REAL-ASM-001')
    expect(wrapper.text()).toContain('工作版本 W2')
    expect(wrapper.text()).toContain('正在编辑 · engineer')
    expect(wrapper.text()).toContain('1 项完整')
    expect(wrapper.text()).toContain('1 项待确认')
    expect(wrapper.text()).toContain('RP-REAL-001')
    expect(wrapper.text()).not.toContain('PRJ-2026-018')
  })

  it('filters the real reference tree and shows a clear login error', async () => {
    const wrapper = mount(App, { attachTo: document.body, global: { plugins: [ElementPlus] } })
    await wrapper.get('input[name="username"]').setValue('engineer')
    await wrapper.get('input[name="password"]').setValue('wrong-password')
    await wrapper.get('form[aria-label="登录PDM"]').trigger('submit')
    await flushPromises()
    expect(wrapper.text()).toContain('用户名或密码错误')

    await wrapper.get('input[name="password"]').setValue('correct-password')
    await wrapper.get('form[aria-label="登录PDM"]').trigger('submit')
    await flushPromises()
    await wrapper.get('input[type="search"]').setValue('REAL-PRT-001')
    await flushPromises()
    expect(wrapper.text()).toContain('REAL-PRT-001')
    expect(wrapper.text()).not.toContain('PRJ-2026-018')
  })

  it('switches the workbench and document pages and makes navigation buttons respond', async () => {
    const wrapper = mount(App, { attachTo: document.body, global: { plugins: [ElementPlus] } })
    await login(wrapper)

    await buttonByText(wrapper, '工作台').trigger('click')
    expect(wrapper.get('[aria-label="工作台主页面"]').text()).toContain('当前工作图档')

    await buttonByText(wrapper, '进入项目图档').trigger('click')
    expect(wrapper.find('[aria-label="项目图档结构"]').exists()).toBe(true)

    await buttonByText(wrapper, 'BOM管理').trigger('click')
    expect(wrapper.get('button[role="tab"][aria-selected="true"]').text()).toBe('机械BOM')

    await wrapper.get('button[aria-label="适合窗口"]').trigger('click')
    await wrapper.get('button[aria-label="通知"]').trigger('click')
    await buttonByText(wrapper, '生产发包').trigger('click')
    await flushPromises()
    expect(document.body.textContent).toContain('预览已适合当前窗口')
    expect(document.body.textContent).toContain('当前没有新的系统通知')
    expect(document.body.textContent).toContain('生产发包将在后续阶段开放')
  })
})
