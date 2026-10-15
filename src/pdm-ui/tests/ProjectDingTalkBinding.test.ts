import { mount, flushPromises } from '@vue/test-utils'
import ElementPlus from 'element-plus'
import { afterEach, expect, it, vi } from 'vitest'
import ProjectDingTalkBinding from '../src/components/ProjectDingTalkBinding.vue'
const api=vi.hoisted(()=>({readDingTalkBinding:vi.fn(),saveDingTalkBinding:vi.fn(),publishDingTalkEntry:vi.fn()}))
vi.mock('../src/api',()=>api)
let wrapper:ReturnType<typeof mount>
afterEach(()=>{wrapper?.unmount();document.body.innerHTML='';vi.clearAllMocks()})
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
