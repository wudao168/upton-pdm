import { mount, flushPromises } from '@vue/test-utils'
import ElementPlus, { ElMessageBox } from 'element-plus'
import { afterEach, expect, it, vi } from 'vitest'
import ProjectDingTalkBinding from '../src/components/ProjectDingTalkBinding.vue'
import type { DingTalkBinding } from '../src/dingtalk'
const api=vi.hoisted(()=>({readDingTalkBinding:vi.fn(),saveDingTalkBinding:vi.fn(),publishDingTalkEntry:vi.fn(),createDingTalkBindingCommand:vi.fn()}))
vi.mock('../src/api',()=>api)
const clipboardDescriptor=Object.getOwnPropertyDescriptor(navigator,'clipboard')
const commandDescriptor=Object.getOwnPropertyDescriptor(document,'execCommand')
const entry='http://192.168.2.8:5173/?dingtalkOpl=1&companyId=company&groupId=own'
const command='绑定项目 P700014 A1B2C3D4E5F60718'
const binding={rootProjectId:'root',groupId:'own',canManage:true,enabled:true,rowVersion:2,entryUrl:entry,groups:[{id:'own',name:'本项目群',registered:true,projectCount:1,entryUrl:entry,boundProjects:[{id:'root',code:'P700014',name:'TEST 5'}]},{id:'other',name:'其他项目群',registered:true,projectCount:1,entryUrl:''}]}
let wrapper:ReturnType<typeof mount>
afterEach(()=>{wrapper?.unmount();document.body.innerHTML='';vi.restoreAllMocks();vi.clearAllMocks();if(clipboardDescriptor)Object.defineProperty(navigator,'clipboard',clipboardDescriptor);else Reflect.deleteProperty(navigator,'clipboard');if(commandDescriptor)Object.defineProperty(document,'execCommand',commandDescriptor);else Reflect.deleteProperty(document,'execCommand')})
async function bound(data:DingTalkBinding=binding){
 api.readDingTalkBinding.mockResolvedValue(data)
 api.createDingTalkBindingCommand.mockResolvedValue({command,expiresAt:'2026-10-26T12:00:00+08:00'})
 wrapper=mount(ProjectDingTalkBinding,{attachTo:document.body,props:{projectId:'child',token:'token'},global:{plugins:[ElementPlus],stubs:{PanelActions:{template:'<div><slot /></div>'}}}});await flushPromises()
}
it('shows only the current project group without a company-wide group selector',async()=>{
 await bound();expect(wrapper.get('input[aria-label="当前项目群"]').element).toHaveProperty('value','本项目群');expect(wrapper.text()).not.toContain('其他项目群');expect(wrapper.findComponent({name:'ElSelect'}).exists()).toBe(false)
 expect(wrapper.findAll('.ding-binding-row>span').map(label=>label.text())).toEqual(['当前项目群','项目绑定码','快捷入口地址'])
 expect(wrapper.text()).toContain('同一群只能关联一个主项目');expect(wrapper.text()).toContain('无需再选择群或点击保存')
})
it('generates and copies a project-scoped one-time command without saving a binding',async()=>{
 const writeText=vi.fn().mockResolvedValue(undefined);Object.defineProperty(navigator,'clipboard',{configurable:true,value:{writeText}})
 await bound();await wrapper.get('[aria-label="生成并复制项目绑定码"]').trigger('click');await flushPromises()
 expect(api.createDingTalkBindingCommand).toHaveBeenCalledWith('child','token');expect(writeText).toHaveBeenCalledWith(command)
 expect(wrapper.get('input[aria-label="项目绑定码"]').element).toHaveProperty('value',command)
 expect(wrapper.text()).toContain('本项目绑定码已复制');expect(wrapper.text()).toContain('重新生成后旧指令失效')
 expect(api.saveDingTalkBinding).not.toHaveBeenCalled()
})
it('copies the full command and entry over HTTP without the Clipboard API',async()=>{
 Object.defineProperty(navigator,'clipboard',{configurable:true,value:undefined});const copy=vi.fn().mockReturnValue(true);Object.defineProperty(document,'execCommand',{configurable:true,value:copy})
 await bound();await wrapper.get('[aria-label="生成并复制项目绑定码"]').trigger('click');await flushPromises()
 const input=wrapper.get('input[aria-label="项目绑定码"]').element as HTMLInputElement
 expect(copy).toHaveBeenCalledWith('copy');expect(input.selectionStart).toBe(0);expect(input.selectionEnd).toBe(command.length)
 await wrapper.get('[aria-label="复制快捷入口地址"]').trigger('click');await flushPromises()
 const address=wrapper.get('input[aria-label="快捷入口地址"]').element as HTMLInputElement
 expect(address.selectionStart).toBe(0);expect(address.selectionEnd).toBe(entry.length);expect(wrapper.text()).toContain('快捷入口地址已复制')
})
it('refreshes a command-created binding on focus without an extra save',async()=>{
 await bound({...binding,groupId:null,entryUrl:'',groups:[]})
 expect(wrapper.get('input[aria-label="当前项目群"]').attributes('placeholder')).toBe('尚未绑定')
 api.readDingTalkBinding.mockResolvedValue({...binding,bindingMessage:'已将项目 P700014 直接绑定到钉钉群“本项目群”。'})
 window.dispatchEvent(new Event('focus'));await flushPromises()
 expect(wrapper.get('input[aria-label="当前项目群"]').element).toHaveProperty('value','本项目群');expect(wrapper.text()).toContain('直接绑定')
 expect(api.saveDingTalkBinding).not.toHaveBeenCalled()
})
it('requires confirmation to unlink and clears the generated command after unlinking',async()=>{
 const confirm=vi.spyOn(ElMessageBox,'confirm').mockRejectedValueOnce('cancel').mockResolvedValueOnce('confirm' as never)
 Object.defineProperty(navigator,'clipboard',{configurable:true,value:{writeText:vi.fn().mockResolvedValue(undefined)}})
 await bound();await wrapper.get('[aria-label="生成并复制项目绑定码"]').trigger('click');await flushPromises()
 const unlink=wrapper.findAll('button').find(button=>button.text()==='解除群绑定')!
 await unlink.trigger('click');await flushPromises();expect(api.saveDingTalkBinding).not.toHaveBeenCalled()
 api.saveDingTalkBinding.mockResolvedValue({...binding,groupId:null,groups:[],entryUrl:'',rowVersion:3})
 await unlink.trigger('click');await flushPromises()
 expect(confirm).toHaveBeenCalled();expect(api.saveDingTalkBinding).toHaveBeenCalledWith('child',{groupId:null,expectedRowVersion:2},'token')
 expect(wrapper.text()).toContain('已解除当前主项目的群绑定');expect(wrapper.get('input[aria-label="项目绑定码"]').element).toHaveProperty('value','')
})
it('disables command generation for readers and translates raw failures into Chinese',async()=>{
 await bound({...binding,canManage:false});expect(wrapper.get('[aria-label="生成并复制项目绑定码"]').attributes('disabled')).toBeDefined()
 await wrapper.get('[aria-label="生成并复制项目绑定码"]').trigger('click');expect(api.createDingTalkBindingCommand).not.toHaveBeenCalled()
 wrapper.unmount();await bound();api.createDingTalkBindingCommand.mockRejectedValue(new Error('Forbidden'))
 await wrapper.get('[aria-label="生成并复制项目绑定码"]').trigger('click');await flushPromises()
 expect(wrapper.text()).toContain('项目群绑定操作失败');expect(wrapper.text()).not.toContain('Forbidden')
})
