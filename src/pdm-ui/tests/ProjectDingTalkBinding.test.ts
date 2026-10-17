import { mount, flushPromises } from '@vue/test-utils'
import ElementPlus from 'element-plus'
import { afterEach, expect, it, vi } from 'vitest'
import ProjectDingTalkBinding from '../src/components/ProjectDingTalkBinding.vue'
const api=vi.hoisted(()=>({readDingTalkBinding:vi.fn(),saveDingTalkBinding:vi.fn(),publishDingTalkEntry:vi.fn()}))
vi.mock('../src/api',()=>api)
const clipboardDescriptor=Object.getOwnPropertyDescriptor(navigator,'clipboard')
const commandDescriptor=Object.getOwnPropertyDescriptor(document,'execCommand')
let wrapper:ReturnType<typeof mount>
afterEach(()=>{wrapper?.unmount();document.body.innerHTML='';vi.clearAllMocks();if(clipboardDescriptor)Object.defineProperty(navigator,'clipboard',clipboardDescriptor);else Reflect.deleteProperty(navigator,'clipboard');if(commandDescriptor)Object.defineProperty(document,'execCommand',commandDescriptor);else Reflect.deleteProperty(document,'execCommand')})
it('shows bound project details, disables other projects and keeps the current root group available to its children',async()=>{
 api.readDingTalkBinding.mockResolvedValue({rootProjectId:'root',groupId:'own',canManage:true,enabled:true,rowVersion:2,groups:[
  {id:'own',name:'本项目群',registered:true,projectCount:1,boundProjects:[{id:'root',code:'P700014',name:'TEST 5'}]},
  {id:'other',name:'其他项目群',registered:true,projectCount:1,boundProjects:[{id:'other-root',code:'P700015',name:'TEST 6'}]},
  {id:'free',name:'未占用群',registered:true,projectCount:0,boundProjects:[]},
  {id:'pending',name:'待登记群',registered:false,projectCount:0,boundProjects:[]},
 ]})
 wrapper=mount(ProjectDingTalkBinding,{attachTo:document.body,props:{projectId:'child',token:'token'},global:{plugins:[ElementPlus],stubs:{PanelActions:{template:'<div><slot /></div>'}}}})
 await flushPromises()
 const options=wrapper.findAllComponents({name:'ElOption'})
 expect(options.map(option=>option.props('disabled'))).toEqual([false,true,false,true])
 expect(options[0]!.props('label')).toContain('P700014 · TEST 5')
 expect(options[1]!.props('label')).toContain('P700015 · TEST 6')
 expect(wrapper.text()).toContain('同一群只能关联一个主项目')
 wrapper.findComponent({name:'ElSelect'}).vm.$emit('update:modelValue','other');await flushPromises()
 await wrapper.findAll('button').find(button=>button.text()==='保存群绑定')!.trigger('click')
 expect(api.saveDingTalkBinding).not.toHaveBeenCalled()
 expect(wrapper.text()).toContain('该群已关联其他主项目或尚未登记')
})

const entry='http://192.168.2.8:5173/?dingtalkOpl=1&companyId=company&groupId=own'
async function bound() {
 api.readDingTalkBinding.mockResolvedValue({rootProjectId:'root',groupId:'own',canManage:true,enabled:true,rowVersion:2,entryUrl:entry,groups:[
  {id:'own',name:'本项目群',registered:true,projectCount:1,entryUrl:entry,boundProjects:[{id:'root',code:'P700014',name:'TEST 5'}]},
  {id:'free',name:'未占用群',registered:true,projectCount:0,entryUrl:entry.replace('own','free'),boundProjects:[]},
 ]})
 wrapper=mount(ProjectDingTalkBinding,{attachTo:document.body,props:{projectId:'root',token:'token'},global:{plugins:[ElementPlus],stubs:{PanelActions:{template:'<div><slot /></div>'}}}})
 await flushPromises()
}
it('copies the saved binding address with the Clipboard API',async()=>{
 const writeText=vi.fn().mockResolvedValue(undefined);Object.defineProperty(navigator,'clipboard',{configurable:true,value:{writeText}})
 await bound();await wrapper.get('.ding-entry-copy-row button').trigger('click');await flushPromises()
 expect(writeText).toHaveBeenCalledWith(entry)
 expect(wrapper.text()).toContain('群快捷入口地址已复制')
})
it('copies the full readonly address in an HTTP environment without the Clipboard API',async()=>{
 Object.defineProperty(navigator,'clipboard',{configurable:true,value:undefined});const copy=vi.fn().mockReturnValue(true);Object.defineProperty(document,'execCommand',{configurable:true,value:copy})
 await bound();await wrapper.get('.ding-entry-copy-row button').trigger('click');await flushPromises()
 expect(copy).toHaveBeenCalledWith('copy')
 const input=wrapper.get('.ding-entry-copy-row input').element as HTMLInputElement
 expect(input.selectionStart).toBe(0);expect(input.selectionEnd).toBe(entry.length)
 expect(wrapper.text()).toContain('群快捷入口地址已复制')
})
it('does not copy an unsaved group preview and provides Chinese help when copying fails',async()=>{
 Object.defineProperty(navigator,'clipboard',{configurable:true,value:{writeText:vi.fn().mockRejectedValue(new Error('NotAllowedError'))}});Object.defineProperty(document,'execCommand',{configurable:true,value:vi.fn().mockReturnValue(false)})
 await bound();await wrapper.get('.ding-entry-copy-row button').trigger('click');await flushPromises()
 expect(wrapper.text()).toContain('按 Ctrl+C 复制');expect(wrapper.text()).not.toContain('NotAllowedError')
 wrapper.findComponent({name:'ElSelect'}).vm.$emit('update:modelValue','free');await flushPromises()
 expect(wrapper.get('.ding-entry-copy-row button').attributes('disabled')).toBeDefined()
 expect(wrapper.text()).toContain('保存群绑定后生效并可复制')
})
