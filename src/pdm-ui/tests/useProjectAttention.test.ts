import { afterEach, expect, it, vi } from 'vitest'
import { defineComponent, ref } from 'vue'
import { flushPromises, mount } from '@vue/test-utils'
import { useProjectAttention } from '../src/composables/useProjectAttention'
const api=vi.hoisted(()=>({readProjectWorkbench:vi.fn(),listReleasePackages:vi.fn()}))
vi.mock('../src/api',()=>api)
afterEach(()=>{vi.useRealTimers();vi.clearAllMocks()})
it('隐藏页面停止风险轮询，返回页面恢复读取并保留已知状态',async()=>{
 vi.useFakeTimers();const active=ref(true);api.readProjectWorkbench.mockResolvedValue({projects:[{project:{id:'page-performance',code:'P1'}}]});let attention!:ReturnType<typeof useProjectAttention>
 const wrapper=mount(defineComponent({setup(){attention=useProjectAttention(()=> 'performance-test-token',()=>['page-performance'],()=>active.value);return()=>null}}))
 await flushPromises();expect(api.readProjectWorkbench).toHaveBeenCalledTimes(1)
 active.value=false;await flushPromises();await vi.advanceTimersByTimeAsync(120000);expect(api.readProjectWorkbench).toHaveBeenCalledTimes(1);expect(attention.entries.value['page-performance']).toBeTruthy()
 active.value=true;await flushPromises();expect(api.readProjectWorkbench).toHaveBeenCalledTimes(2);wrapper.unmount()
})
it('切走页面后不再启动剩余项目的风险请求',async()=>{
 const active=ref(true);const completions:Array<()=>void>=[];api.readProjectWorkbench.mockImplementation(()=>new Promise(resolve=>{completions.push(()=>resolve({projects:[]}))}))
 const wrapper=mount(defineComponent({setup(){useProjectAttention(()=> 'queue-test-token',()=>['q1','q2','q3','q4'],()=>active.value);return()=>null}}))
 await flushPromises();expect(api.readProjectWorkbench).toHaveBeenCalledTimes(2)
 active.value=false;await flushPromises();completions.forEach(complete=>complete());await flushPromises();expect(api.readProjectWorkbench).toHaveBeenCalledTimes(2);wrapper.unmount()
})
