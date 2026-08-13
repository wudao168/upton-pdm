import { flushPromises, mount } from '@vue/test-utils'
import ElementPlus from 'element-plus'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import App from '../src/App.vue'

const projectId = '11111111-1111-1111-1111-111111111111'

function json(value: unknown, status = 200) {
  return new Response(JSON.stringify(value), { status, headers: { 'Content-Type': 'application/json' } })
}

function installApiMock() {
  const projects: Array<Record<string, unknown>> = [{ id: projectId, code: 'PRJ-REAL-001', name: '真实装配项目', owner: 'engineer', responsibleUsers: ['engineer'], vaultLocation: 'D:\\PDM\\PRJ-REAL-001', releaseLocation: 'D:\\Release\\PRJ-REAL-001', isActive: true, quantity: 1, serialNumbers: [] }]
  const customers = [{ id: 'customer-1', code: 'C00465', name: '中山比亚迪电子有限公司', isActive: true }]
  vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = String(input)
    if (url.endsWith('/health')) return json({ status: 'ok' })
    if (url.endsWith('/api/auth/login')) {
      const credentials = JSON.parse(String(init?.body)) as { username: string; password: string }
      return credentials.password === 'correct-password'
        ? json({ accessToken: 'test-token', expiresAt: '2099-01-01T00:00:00Z', username: credentials.username, displayName: credentials.username === 'admin' ? '系统管理员' : '真实工程师', role: credentials.username === 'admin' ? 'Administrator' : 'Engineer' })
        : json({ title: 'Unauthorized' }, 401)
    }
    if (url.endsWith('/api/customers')) {
      if (init?.method === 'POST') {
        const request = JSON.parse(String(init.body)) as { code: string; name: string; isActive: boolean }
        const created = { id: 'customer-created', ...request }
        customers.push(created)
        return json(created, 201)
      }
      return json(customers)
    }
    if (url.endsWith('/api/customers/customer-1') && init?.method === 'PUT') {
      const request = JSON.parse(String(init.body)) as { code: string; name: string; isActive: boolean }
      Object.assign(customers[0], request)
      return json(customers[0])
    }
    if (url.endsWith('/api/users')) return json([{ username: 'admin', displayName: '系统管理员', role: 'Administrator', isActive: true }, { username: 'engineer', displayName: '真实工程师', role: 'Engineer', isActive: true }])
    if (url.endsWith('/api/system-settings')) return json(init?.method === 'PUT' ? JSON.parse(String(init.body)) : { vaultRoot: 'D:\\PDM\\Vault', releaseRoot: 'D:\\PDM\\Release' })
    if (url.endsWith('/api/system-settings/equipment-types')) return json([{ code: 0, name: '标准设备', isActive: true }, { code: 2, name: '测试设备', isActive: true }])
    if (url.includes('/api/system-settings/equipment-types/') && init?.method === 'PUT') return json({ code: Number(url.split('/').at(-1)), ...JSON.parse(String(init.body)) })
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
    if (url.endsWith(`/api/projects/${projectId}/responsibles`) && init?.method === 'PUT') {
      const request = JSON.parse(String(init.body)) as { usernames: string[] }
      Object.assign(projects[0], { responsibleUsers: request.usernames, owner: request.usernames[0] })
      return json(projects[0])
    }
    if (url.includes('/api/projects/project-created/')) {
      if (url.endsWith('/reference-tree')) return json({ title: 'Not Found' }, 404)
      return json([])
    }
    if (url.endsWith(`/api/projects/${projectId}`)) return json(projects[0])
    if (url.endsWith('/documents')) {
      return json([
        { id: 'doc-root', drawingNumber: 'REAL-ASM-001', name: '真实总装配', fileName: 'REAL-ASM-001.SLDASM', kind: 0, revision: { display: 'W2' }, checkedOutBy: 'engineer' },
        { id: 'doc-part', drawingNumber: 'REAL-PRT-001', name: '真实底板', fileName: 'REAL-PRT-001.SLDPRT', kind: 1, revision: { display: 'A' }, checkedOutBy: null },
      ])
    }
    if (url.endsWith('/reference-tree')) {
      return json({
        nodeId: 'node-root', documentId: 'doc-root', instancePath: 'REAL-ASM-001', fileName: 'REAL-ASM-001.SLDASM', displayName: '真实总装配', kind: 0, configuration: '默认', quantity: 1, status: 0, revision: null, checkedOutBy: 'engineer',
        children: [
          { nodeId: 'node-part-1', documentId: 'doc-part', instancePath: 'REAL-ASM-001/REAL-PRT-001-1', fileName: 'REAL-PRT-001.SLDPRT', displayName: '真实底板-1', kind: 1, configuration: '默认', quantity: 1, status: 0, revision: null, checkedOutBy: null, children: [] },
          { nodeId: 'node-part-2', documentId: 'doc-part', instancePath: 'REAL-ASM-001/REAL-PRT-001-2', fileName: 'REAL-PRT-001.SLDPRT', displayName: '真实底板-2', kind: 1, configuration: '默认', quantity: 1, status: 0, revision: null, checkedOutBy: null, children: [] },
          { nodeId: 'node-stale-duplicate', documentId: 'doc-part', instancePath: 'REAL-ASM-001/REAL-PRT-001-2', fileName: 'REAL-PRT-001.SLDPRT', displayName: '旧快照重复项', kind: 1, configuration: '默认', quantity: 1, status: 0, revision: null, checkedOutBy: null, children: [] },
          { nodeId: 'node-unregistered', documentId: null, instancePath: 'REAL-ASM-001/3-1', fileName: '3.SLDPRT', displayName: '3-1', kind: 1, configuration: '默认', quantity: 1, status: 0, revision: null, checkedOutBy: null, children: [] },
        ],
      })
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

async function login(wrapper: ReturnType<typeof mount>, openProject = true) {
  await wrapper.get('input[name="username"]').setValue('engineer')
  await wrapper.get('input[name="password"]').setValue('correct-password')
  await wrapper.get('form[aria-label="登录PDM"]').trigger('submit')
  await flushPromises()
  if (openProject) {
    await buttonByText(wrapper, '进入项目').trigger('click')
    await flushPromises()
  }
}

function buttonByText(wrapper: ReturnType<typeof mount>, label: string) {
  const button = wrapper.findAll('button').find((candidate) => candidate.text().includes(label))
  if (!button) throw new Error(`Button not found: ${label}`)
  return button
}

describe('PDM client workspace', () => {
  beforeEach(() => {
    window.sessionStorage.clear()
    window.localStorage.clear()
    Object.defineProperty(window, 'chrome', { configurable: true, value: undefined })
    installApiMock()
  })

  it('restores credentials from the Windows client and asks the host to save them only after login succeeds', async () => {
    const postMessage = vi.fn()
    Object.defineProperty(window, 'chrome', {
      configurable: true,
      value: { webview: { postMessage, addEventListener: vi.fn() } },
    })
    const wrapper = mount(App, { attachTo: document.body, global: { plugins: [ElementPlus] } })

    expect(postMessage).toHaveBeenCalledWith({ type: 'credentials-request', payload: undefined })
    window.dispatchEvent(new CustomEvent('pdm-remembered-credentials', {
      detail: { username: 'engineer', password: 'correct-password', remember: true },
    }))
    await flushPromises()

    expect((wrapper.get('input[name="username"]').element as HTMLInputElement).value).toBe('engineer')
    expect((wrapper.get('input[name="password"]').element as HTMLInputElement).value).toBe('correct-password')
    expect((wrapper.get('input[name="rememberCredentials"]').element as HTMLInputElement).checked).toBe(true)

    await wrapper.get('form[aria-label="登录PDM"]').trigger('submit')
    await flushPromises()

    expect(postMessage).toHaveBeenCalledWith({
      type: 'credentials-save',
      payload: { username: 'engineer', password: 'correct-password' },
    })
    expect(window.localStorage.length).toBe(0)
    expect(window.sessionStorage.getItem('upton-pdm-session')).not.toContain('correct-password')
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

  it('creates and manages projects before SolidWorks drawings are associated', async () => {
    const wrapper = mount(App, { attachTo: document.body, global: { plugins: [ElementPlus] } })
    await login(wrapper, false)

    expect(wrapper.get('[aria-label="项目管理"]').text()).toContain('项目中心')
    expect(wrapper.text()).toContain('PRJ-REAL-001')
    await buttonByText(wrapper, '创建主项目').trigger('click')
    await wrapper.get('select[name="projectTypeCode"]').setValue('P')
    await wrapper.get('select[name="equipmentTypeCode"]').setValue('2')
    await flushPromises()
    await wrapper.get('select[name="customerId"]').setValue('customer-1')
    await wrapper.get('input[name="projectName"]').setValue('新建装配项目')
    await wrapper.get('input[name="projectAlias"]').setValue('测试别名')
    await wrapper.get('input[name="signedDate"]').setValue('2026-08-13')
    await wrapper.get('input[name="quantity"]').setValue('2')
    await wrapper.get('form[aria-label="创建PDM项目"]').trigger('submit')
    await flushPromises()

    const fetchMock = vi.mocked(fetch)
    expect(fetchMock).toHaveBeenCalledWith(expect.stringMatching(/\/api\/projects$/), expect.objectContaining({
      method: 'POST',
      body: JSON.stringify({
        organizationId: '70000000-0000-0000-0000-000000000001',
        projectTypeCode: 'P',
        equipmentTypeCode: 2,
        customerId: 'customer-1',
        name: '新建装配项目',
        projectAlias: '测试别名',
        signedDate: '2026-08-13',
        quantity: 2,
      }),
    }))
    expect(wrapper.text()).toContain('P700001')
    expect(wrapper.text()).toContain('AK-2-C00465-001-00')
    expect(wrapper.text()).toContain('70000002')

    const createdRow = wrapper.findAll('tbody tr').find(row => row.text().includes('P700001'))
    expect(createdRow?.text()).toContain('当前')
    const childButton = createdRow?.findAll('button').find(button => button.text().includes('子项目'))
    await childButton?.trigger('click')
    await wrapper.get('input[name="childProjectName"]').setValue('子项目一')
    await wrapper.get('input[name="childQuantity"]').setValue('2')
    await wrapper.get('form[aria-label="创建PDM子项目"]').trigger('submit')
    await flushPromises()
    expect(wrapper.text()).toContain('P700001-1')
    expect(wrapper.text()).toContain('AK-2-C00465-001-01')
    expect(wrapper.text()).toContain('70000004')
  })

  it('maintains customers, project responsibles and system settings outside the create form', async () => {
    const wrapper = mount(App, { attachTo: document.body, global: { plugins: [ElementPlus] } })
    await wrapper.get('input[name="username"]').setValue('admin')
    await wrapper.get('input[name="password"]').setValue('correct-password')
    await wrapper.get('form[aria-label="登录PDM"]').trigger('submit')
    await flushPromises()

    await buttonByText(wrapper, '创建主项目').trigger('click')
    expect(wrapper.find('select[name="customerId"]').exists()).toBe(true)
    expect(wrapper.find('input[name="customerCode"]').exists()).toBe(false)
    expect(wrapper.find('input[name="projectOwner"]').exists()).toBe(false)
    expect(wrapper.find('input[name="vaultLocation"]').exists()).toBe(false)
    await buttonByText(wrapper, '取消').trigger('click')

    await buttonByText(wrapper, '客户维护').trigger('click')
    expect(wrapper.get('[aria-label="客户维护"]').text()).toContain('C00465')
    await buttonByText(wrapper, '新增客户').trigger('click')
    const dialogInputs = wrapper.findAll('.pdm-project-form input')
    await dialogInputs[0].setValue('C00888')
    await dialogInputs[1].setValue('新增客户验收')
    await buttonByText(wrapper, '保存').trigger('click')
    await flushPromises()
    expect(vi.mocked(fetch)).toHaveBeenCalledWith(expect.stringMatching(/\/api\/customers$/), expect.objectContaining({ method: 'POST' }))

    await buttonByText(wrapper, '项目管理').trigger('click')
    const maintainButton = wrapper.findAll('button').find(button => button.text().trim() === '维护')
    expect(maintainButton).toBeDefined()
    await maintainButton!.trigger('click')
    expect(document.body.textContent).toContain('被选账号可以看到并进入该项目')
    expect(document.body.textContent).toContain('真实工程师（engineer）')

    await buttonByText(wrapper, '系统设置').trigger('click')
    expect(wrapper.get('[aria-label="系统设置"]').text()).toContain('根目录\\项目号')
    await wrapper.get('.el-tabs__item#tab-equipment').trigger('click')
    await flushPromises()
    expect(wrapper.text()).toContain('设备类型设置')
    expect(wrapper.text()).toContain('00–99')
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
    await buttonByText(wrapper, '进入项目').trigger('click')
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
    expect(wrapper.get('button[role="tab"][aria-selected="true"]').text()).toContain('机械BOM')
    expect(wrapper.text()).toContain('保存BOM')

    await buttonByText(wrapper, '项目图档').trigger('click')
    expect(wrapper.text()).toContain('正在加载 eDrawings…')
    await wrapper.get('button[aria-label="适合窗口"]').trigger('click')
    await wrapper.get('button[aria-label="通知"]').trigger('click')
    await buttonByText(wrapper, '生产发包').trigger('click')
    await flushPromises()
    expect(document.body.textContent).toContain('当前没有新的系统通知')
    expect(document.body.textContent).toContain('审批与生产发包')
    expect(document.body.textContent).not.toContain('将在后续阶段开放')
  })

  it('uses occurrence ids, removes only exact duplicate instances, and marks unregistered references', async () => {
    const wrapper = mount(App, { attachTo: document.body, global: { plugins: [ElementPlus] } })
    await login(wrapper)

    const tree = wrapper.get('[aria-label="项目图档结构"]')
    const partRows = tree.findAll('.pdm-tree-row').filter(row => row.text().includes('REAL-PRT-001'))
    expect(partRows).toHaveLength(2)
    expect(tree.text()).not.toContain('旧快照重复项')
    expect(tree.text()).toContain('3-1')
    expect(tree.text()).toContain('未入库')

    await partRows[1].trigger('click')
    expect(tree.findAll('.pdm-tree-row.is-selected')).toHaveLength(1)
    expect(tree.get('.pdm-tree-row.is-selected').text()).toContain('真实底板-2')
  })

  it('automatically previews the selected document in the embedded eDrawings host without opening a separate web window', async () => {
    const postMessage = vi.fn()
    Object.defineProperty(window, 'chrome', {
      configurable: true,
      value: { webview: { postMessage, addEventListener: vi.fn() } },
    })
    const wrapper = mount(App, { attachTo: document.body, global: { plugins: [ElementPlus] } })
    await login(wrapper)

    const slot = wrapper.get('[aria-label="客户端内嵌eDrawings预览区"]')
    vi.spyOn(slot.element, 'getBoundingClientRect').mockReturnValue({
      x: 260, y: 180, left: 260, top: 180, right: 960, bottom: 700,
      width: 700, height: 520, toJSON: () => ({}),
    })
    window.dispatchEvent(new Event('resize'))
    await flushPromises()

    expect(postMessage).toHaveBeenCalledWith(expect.objectContaining({
      type: 'preview-host-bounds',
      payload: expect.objectContaining({ left: 260, top: 180, width: 700, height: 520, visible: true }),
    }))
    expect(postMessage).toHaveBeenCalledWith({
      type: 'preview-document',
      payload: { documentId: 'doc-root', fileName: 'REAL-ASM-001.SLDASM', revision: 'W2' },
    })
    expect(wrapper.text()).not.toContain('在客户端内预览')

    window.dispatchEvent(new CustomEvent('pdm-preview-status', { detail: { state: 'ready', fileName: 'REAL-ASM-001.SLDASM' } }))
    await flushPromises()
    expect(slot.attributes('data-preview-state')).toBe('ready')
    await wrapper.get('button[aria-label="适合窗口"]').trigger('click')
    expect(postMessage).toHaveBeenCalledWith({ type: 'preview-host-fit', payload: undefined })

    await wrapper.get('button[role="tab"]:last-of-type').trigger('click')
    expect(postMessage).toHaveBeenCalledWith({ type: 'preview-host-hide', payload: undefined })

    await wrapper.get('button[role="tab"]:first-of-type').trigger('click')
    await flushPromises()
    expect(postMessage.mock.calls.filter(([message]) => message.type === 'preview-document')).toHaveLength(2)
  })

  it('opens only PDM-controlled document identities in SolidWorks from the entity button and tree menu', async () => {
    const postMessage = vi.fn()
    Object.defineProperty(window, 'chrome', {
      configurable: true,
      value: { webview: { postMessage, addEventListener: vi.fn() } },
    })
    const wrapper = mount(App, { attachTo: document.body, global: { plugins: [ElementPlus] } })
    await login(wrapper)
    window.dispatchEvent(new CustomEvent('pdm-solidworks-capability', { detail: { available: true } }))
    await flushPromises()

    await buttonByText(wrapper, 'SolidWorks打开最新').trigger('click')
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
    await buttonByText(wrapper, '打开最新正式发布版（只读）').trigger('click')
    expect(postMessage).toHaveBeenCalledWith({
      type: 'open-document',
      payload: expect.objectContaining({ projectId, documentId: 'doc-root', mode: 'LatestReleased' }),
    })

    await buttonByText(wrapper, '版本对比').trigger('click')
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
    await buttonByText(wrapper, '版本对比').trigger('click')
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
})
