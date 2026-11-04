import { flushPromises, mount } from '@vue/test-utils'
import ElementPlus from 'element-plus'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import ArchiveRecycleDialog from '../src/components/ArchiveRecycleDialog.vue'

const api = vi.hoisted(() => ({ listRecycleArchiveBatches: vi.fn(), previewRecycleArchive: vi.fn(), recycleArchiveSet: vi.fn(), listArchiveRecycleSets: vi.fn(), restoreArchiveSet: vi.fn() }))
vi.mock('../src/api', () => api)
const preview = { batchId: 'batch', canRecycle: true, scopeHash: 'reviewed-scope', members: [{ document: { id: 'part', fileName: 'PART.SLDPRT' }, versionId: 'v1' }], retainedFiles: ['OLD.SLDPRT：已有其他版本'], blockers: [] }
function render() { return mount(ArchiveRecycleDialog, { props: { projectId: 'project', token: 'token', open: false, onReload: vi.fn() }, global: { plugins: [ElementPlus], stubs: { teleport: true } } }) }
describe('存档批次回收', () => {
  beforeEach(() => { vi.clearAllMocks(); api.listRecycleArchiveBatches.mockResolvedValue([{ batchId: 'batch', committedAt: '2026-11-03T00:00:00Z', actor: 'admin', fileCount: 3 }]); api.listArchiveRecycleSets.mockResolvedValue([]); api.previewRecycleArchive.mockResolvedValue(preview); api.recycleArchiveSet.mockResolvedValue({ id: 'set' }) })
  it('打开后仅读取当前项目批次，预览后仍须填写原因及勾选确认', async () => {
    const wrapper = render(); expect(api.listRecycleArchiveBatches).not.toHaveBeenCalled(); await wrapper.setProps({ open: true }); await flushPromises()
    expect(api.listRecycleArchiveBatches).toHaveBeenCalledWith('project', 'token')
    await wrapper.find('select').setValue('batch'); await flushPromises()
    expect(wrapper.text()).toContain('本次回收 1 份；保留 1 份')
    const button = wrapper.findAll('button').find(item => item.text() === '整批移入回收站')!; expect(button.attributes('disabled')).toBeDefined()
    await wrapper.find('textarea').setValue('存错项目'); await wrapper.find('input[type="checkbox"]').setValue(true); await flushPromises()
    expect(button.attributes('disabled')).toBeUndefined(); await button.trigger('click'); await flushPromises()
    expect(api.recycleArchiveSet).toHaveBeenCalledWith('project', 'batch', 'reviewed-scope', '存错项目', 'token'); expect(wrapper.props('onReload')).toHaveBeenCalledTimes(1)
    wrapper.unmount()
  })
  it('存在范围外引用时展示具体原因并禁止回收', async () => {
    api.previewRecycleArchive.mockResolvedValue({ ...preview, canRecycle: false, blockers: ['仍被本次范围之外的图档引用：OTHER.SLDASM'] })
    const wrapper = render(); await wrapper.setProps({ open: true }); await flushPromises(); await wrapper.find('select').setValue('batch'); await flushPromises()
    expect(wrapper.text()).toContain('OTHER.SLDASM'); expect(wrapper.findAll('button').find(item => item.text() === '整批移入回收站')!.attributes('disabled')).toBeDefined(); expect(api.recycleArchiveSet).not.toHaveBeenCalled(); wrapper.unmount()
  })
  it('切换项目后丢弃旧项目迟到的预览', async () => {
    let finish!: (value: unknown) => void; api.previewRecycleArchive.mockImplementationOnce(() => new Promise(resolve => { finish = resolve }))
    const wrapper = render(); await wrapper.setProps({ open: true }); await flushPromises(); await wrapper.find('select').setValue('batch'); await flushPromises()
    await wrapper.setProps({ projectId: 'new-project' }); await flushPromises(); finish(preview); await flushPromises()
    expect(wrapper.text()).not.toContain('本次回收 1 份'); expect(api.listRecycleArchiveBatches).toHaveBeenLastCalledWith('new-project', 'token'); wrapper.unmount()
  })
})
