import { flushPromises, mount } from '@vue/test-utils'
import ElementPlus from 'element-plus'
import { expect, it, vi } from 'vitest'
import BomFilesDialog from '../src/components/BomFilesDialog.vue'
const api = vi.hoisted(() => ({ readBomFiles: vi.fn(), readProcurementFiles: vi.fn(), readBomFileBlob: vi.fn(), readProcurementFileBlob: vi.fn(), adoptBomLibraryFiles: vi.fn(), removeBomFile: vi.fn(), uploadBomFile: vi.fn() }))
vi.mock('../src/api', () => api)
it('标准库物料依据服务端维护权限显示只读，既有附件仍可查看',async()=>{
  const file={id:'file',fileName:'已上传配图.jpg',kind:'Document',source:'项目上传',purpose:'配图'}
  api.readBomFiles.mockResolvedValue({files:[file],libraryUpdateAvailable:true,rowVersion:1,canEditFiles:false,isStandardLibraryMaterial:true})
  const wrapper=mount(BomFilesDialog,{attachTo:document.body,props:{token:'token',projectId:'project',row:{id:'standard',drawingNumber:'STD-001'},canEdit:true},global:{plugins:[ElementPlus]}})
  expect(document.querySelector('[aria-label="上传项目附件"]')).toBeNull()
  await flushPromises()
  expect(document.body.textContent).toContain('当前为只读')
  expect(document.querySelector('[aria-label="上传项目附件"]')).toBeNull()
  expect([...document.querySelectorAll('button')].some(button=>button.textContent==='移除')).toBe(false)
  expect([...document.querySelectorAll('button')].some(button=>button.textContent==='采用当前启用版本及资料')).toBe(false)
  expect(document.body.textContent).toContain('已上传配图.jpg')
  await wrapper.setProps({row:{id:'manager-row',drawingNumber:'STD-001'}})
  api.readBomFiles.mockResolvedValue({files:[file],libraryUpdateAvailable:true,rowVersion:1,canEditFiles:true,isStandardLibraryMaterial:true})
  await wrapper.setProps({row:{id:'authorized',drawingNumber:'STD-001'}});await flushPromises()
  expect(document.querySelector('[aria-label="上传项目附件"]')).not.toBeNull()
  expect([...document.querySelectorAll('button')].some(button=>button.textContent==='移除')).toBe(true)
  await wrapper.setProps({bomVersionId:'frozen-version'});await flushPromises()
  expect(document.querySelector('[aria-label="上传项目附件"]')).toBeNull()
  wrapper.unmount()
})
it('切换行和关闭后，旧附件请求不能回写到新行', async () => {
  let finishOld!: (value: any) => void
  api.readBomFiles.mockImplementation((_project: string, id: string) => id === 'old' ? new Promise(resolve => { finishOld = resolve }) : Promise.resolve({ files: [{ id: 'new-file', fileName: '新行图纸.pdf' }], libraryUpdateAvailable: false, rowVersion: 1 }))
  const wrapper = mount(BomFilesDialog, { props: { token: 'token', projectId: 'project', row: { id: 'old', drawingNumber: 'OLD' }, canEdit: false }, global: { plugins: [ElementPlus] } })
  await wrapper.setProps({ row: { id: 'new', drawingNumber: 'NEW' } }); await flushPromises()
  finishOld({ files: [], libraryUpdateAvailable: false, rowVersion: 1 }); await flushPromises()
  expect(wrapper.emitted('changed')).toHaveLength(1)
  expect((wrapper.emitted('changed')![0]![0] as any[])[0].id).toBe('new-file')
  wrapper.unmount()
})
