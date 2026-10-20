import { flushPromises, mount } from '@vue/test-utils'
import ElementPlus from 'element-plus'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import BrowserCadArchiveButton from '../src/components/BrowserCadArchiveButton.vue'
import BrowserCadArchiveSettings from '../src/components/BrowserCadArchiveSettings.vue'
const api = vi.hoisted(() => ({ readBrowserCadCapability: vi.fn(), readBrowserCadTicket: vi.fn(), stageBrowserCadArchive: vi.fn(), startBrowserCadArchive: vi.fn(), setBrowserCadEnabled: vi.fn() }))
vi.mock('../src/api', () => api)
vi.mock('../src/statusMessage', () => ({ ElMessage: { success: vi.fn() } }))
let wrapper: ReturnType<typeof mount> | undefined
beforeEach(() => { vi.clearAllMocks(); sessionStorage.clear(); api.readBrowserCadCapability.mockResolvedValue({ enabled: true, canArchive: true, canConfigure: true }) })
afterEach(() => { wrapper?.unmount(); wrapper = undefined; document.body.innerHTML = '' })
async function open() { wrapper = mount(BrowserCadArchiveButton, { attachTo: document.body, props: { token: 'token', projectId: 'project' }, global: { plugins: [ElementPlus] } }); await flushPromises(); return wrapper }
describe('网页存档入口', () => {
  it('only shows the requested label and needs no local SolidWorks capability', async () => {
    await open(); expect(wrapper!.get('.browser-cad-entry').text()).toBe('存档')
    expect(wrapper!.get('.browser-cad-entry').attributes('disabled')).toBeUndefined()
    await wrapper!.get('.browser-cad-entry').trigger('click'); await flushPromises()
    expect(document.body.textContent).toContain('选择文件夹'); expect(document.body.textContent).toContain('选择文件')
  })
  it('hides the entry when the administrator disables it and disables unauthorized users', async () => {
    api.readBrowserCadCapability.mockResolvedValue({ enabled: false, canArchive: false, canConfigure: false })
    await open(); expect(wrapper!.find('.browser-cad-entry').exists()).toBe(false)
    api.readBrowserCadCapability.mockResolvedValue({ enabled: true, canArchive: false, reason: '当前账号没有存档权限' })
    window.dispatchEvent(new Event('pdm-browser-archive-settings')); await flushPromises()
    expect(wrapper!.get('.browser-cad-entry').attributes('disabled')).toBeDefined()
    expect(wrapper!.get('.browser-cad-entry').attributes('title')).toContain('没有存档权限')
  })
  it('receives files, confirms the root, starts a durable job and refreshes only after success', async () => {
    const ready = { id: 'job', state: 'Ready', stage: '文件已接收', files: [{ relativePath: 'ROOT.SLDASM', length: 100 }, { relativePath: 'PART.SLDPRT', length: 50 }], archivedFiles: 0, newVersions: 0 }
    api.stageBrowserCadArchive.mockResolvedValue(ready); api.startBrowserCadArchive.mockResolvedValue({ ...ready, state: 'Queued' })
    api.readBrowserCadTicket.mockResolvedValue({ ...ready, state: 'Succeeded', stage: '存档完成', archivedFiles: 2, newVersions: 1 })
    await open(); await wrapper!.get('.browser-cad-entry').trigger('click'); await flushPromises()
    const input = document.querySelector<HTMLInputElement>('input[aria-label="选择存档文件"]')!; const file = new File(['cad'], 'ROOT.SLDASM')
    Object.defineProperty(input, 'files', { value: [file] }); input.dispatchEvent(new Event('change', { bubbles: true })); await flushPromises()
    expect(api.stageBrowserCadArchive).toHaveBeenCalledWith(expect.any(String), expect.objectContaining({ projectId: 'project' }), [file], 'token')
    expect(wrapper!.emitted('archived')).toBeUndefined()
    const submit = document.querySelector<HTMLButtonElement>('.el-dialog__footer .pdm-primary-action')!
    expect(submit.textContent).toBe('存档'); submit.click(); await flushPromises()
    expect(api.startBrowserCadArchive).toHaveBeenCalledWith('job', 'ROOT.SLDASM', 'token')
    expect(wrapper!.emitted('archived')).toHaveLength(1); expect(document.body.textContent).toContain('新增 1 个版本')
  })
  it('administrator setting controls both entries and is absent for ordinary users', async () => {
    api.setBrowserCadEnabled.mockResolvedValue({ enabled: false })
    wrapper = mount(BrowserCadArchiveSettings, { props: { token: 'token' } }); await flushPromises()
    expect(wrapper.text()).toContain('不影响SolidWorks插件存档')
    await wrapper.get('input[type="checkbox"]').setValue(false); await wrapper.get('button').trigger('click'); await flushPromises()
    expect(api.setBrowserCadEnabled).toHaveBeenCalledWith(false, 'token')
    wrapper.unmount(); api.readBrowserCadCapability.mockResolvedValue({ enabled: true, canArchive: true, canConfigure: false })
    wrapper = mount(BrowserCadArchiveSettings, { props: { token: 'token' } }); await flushPromises()
    expect(wrapper.find('input').exists()).toBe(false)
  })
})
