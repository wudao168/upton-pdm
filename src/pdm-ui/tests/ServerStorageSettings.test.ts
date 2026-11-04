import { flushPromises, mount } from '@vue/test-utils'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import ServerStorageSettings from '../src/components/ServerStorageSettings.vue'
import { readServerStorage, readStorageBackupPlan, saveServerStorage } from '../src/api'
import type { ServerStorageView } from '../src/serverStorage'

vi.mock('../src/api', () => ({ readServerStorage: vi.fn(), saveServerStorage: vi.fn(), readStorageBackupPlan: vi.fn() }))
const view: ServerStorageView = {
  settings: { version: 1, projectFileRoot: 'D:\\PDM\\Vault', oplAttachmentRoot: 'D:\\PDM\\Vault', legacyOplAttachmentRoot: 'D:\\PDM\\Vault',
    programTemplateRoot: 'H:\\archive\\templates', uploadTempRoot: 'H:\\archive\\uploads', browserCadArchiveRoot: 'H:\\archive\\browser',
    conversionWorkRoot: 'D:\\Temp\\conversion', clientLogRoot: 'D:\\Logs', backupRoot: '', databaseDataTarget: '', secretsTarget: '', previewAgentWorkTarget: '', historicalRoots: {} },
  databaseDataRoot: 'H:\\mysql\\data', secretsRoot: 'H:\\secrets', databaseBackupRoot: '', filesBackupRoot: '', previewAgentUrl: 'http://pc-07:5199', historyPolicy: '全部保留',
}
beforeEach(() => { vi.resetAllMocks(); vi.mocked(readServerStorage).mockResolvedValue(structuredClone(view)) })
describe('集中存储设置', () => {
  it('keeps backup unconfigured and saves migration targets without executing a backup', async () => {
    vi.mocked(saveServerStorage).mockImplementation(async settings => ({ ...view, settings }))
    const wrapper = mount(ServerStorageSettings, { props: { token: 'admin' } })
    await flushPromises()
    expect(wrapper.text()).toContain('所有历史文件保留')
    expect(wrapper.get<HTMLInputElement>('[aria-label="备份目标根目录"]').element.value).toBe('')
    await wrapper.get('[aria-label="程序模板根目录"]').setValue('E:\\ProgramTemplates')
    await wrapper.get('[aria-label="数据库数据迁移目标"]').setValue('E:\\Database')
    await wrapper.findAll('button').find(button => button.text() === '保存集中存储设置')!.trigger('click')
    await flushPromises()
    expect(saveServerStorage).toHaveBeenCalledWith(expect.objectContaining({ backupRoot: '', programTemplateRoot: 'E:\\ProgramTemplates', databaseDataTarget: 'E:\\Database' }), 'admin')
    expect(readStorageBackupPlan).not.toHaveBeenCalled()
    wrapper.unmount()
  })
  it('shows actual historical backup sources and the pending destination message', async () => {
    vi.mocked(readStorageBackupPlan).mockResolvedValue({ backupRoot: '', referenceCount: 24000, sources: [{ path: 'E:\\old-vault', categories: ['图档历史位置'], isDirectory: true, required: true }], warnings: [], message: '备份目标待确认；未执行备份。' })
    const wrapper = mount(ServerStorageSettings, { props: { token: 'admin' } })
    await flushPromises()
    await wrapper.findAll('button').find(button => button.text() === '核对备份覆盖范围')!.trigger('click')
    await flushPromises()
    expect(wrapper.text()).toContain('备份目标待确认')
    expect(wrapper.text()).toContain('24000')
    expect(wrapper.get('[aria-label="实际备份来源"]').text()).toContain('E:\\old-vault')
    expect(saveServerStorage).not.toHaveBeenCalled()
    wrapper.unmount()
  })
})
