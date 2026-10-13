import { afterEach, expect, it, vi } from 'vitest'
import { defineComponent, reactive } from 'vue'
import { flushPromises, mount } from '@vue/test-utils'
import { useProjectTabUpdates, bomNeedsHandling, needsHandling, procurementContent, fingerprint } from '../src/projectTabUpdates'
import type { ProjectSummary } from '../src/types'
let wrapper: ReturnType<typeof mount>
let state: ReturnType<typeof useProjectTabUpdates>
const context = reactive({ project: {id:'p1',canReadContent:true} as ProjectSummary, currentUsername:'user1', token:'token', activeTab:'information' })
let revision = 1, fail = false
function setup(clear = true) {
  if (clear) localStorage.clear()
  vi.useFakeTimers()
  Object.defineProperty(document, 'visibilityState', {configurable:true,value:'visible'})
  vi.stubGlobal('fetch', vi.fn(async (url: string) => {
    if(url.endsWith('/opl')) { if(fail)throw Error('offline');return {ok:true,json:async()=>({rows:[{issue:{id:'task1', status:'已完成', rowVersion:revision,updatedAt:`2026-10-13T00:0${revision}:00Z`,updatedBy:'engineer'}}]})} }
    return {ok:true,json:async()=>null}
  }))
  wrapper=mount(defineComponent({setup(){ state=useProjectTabUpdates(context);return ()=>null }}))
}
afterEach(()=>{wrapper?.unmount();vi.useRealTimers();vi.unstubAllGlobals();revision=1;fail=false;context.currentUsername='user1';context.activeTab='information'})
it('baselines first use, detects changed content, and does not clear on tab navigation', async()=>{
  setup();await flushPromises();expect(Object.keys(state.pending.value)).toHaveLength(0)
  revision=2;await vi.advanceTimersByTimeAsync(60000);await flushPromises()
  expect(state.pending.value.opl?.actor).toBe('engineer');expect(state.pending.value.opl?.updatedAt).toBe('2026-10-13T00:02:00Z')
  context.activeTab='opl';await flushPromises();expect(state.pending.value.opl).toBeDefined()
  revision=3;await vi.advanceTimersByTimeAsync(60000);await flushPromises();expect(state.pending.value.opl).toBeDefined()
})
it('persists unread updates across reloads and isolates accounts', async()=>{
  setup();await flushPromises();revision=2;await vi.advanceTimersByTimeAsync(60000);await flushPromises();wrapper.unmount()
  setup(false);await flushPromises();expect(state.pending.value.opl).toBeDefined()
  context.currentUsername='user2';await flushPromises();expect(state.pending.value.opl).toBeUndefined()
})
it('failed detection does not invent an update or clear an existing update', async()=>{
  setup();await flushPromises();fail=true;await vi.advanceTimersByTimeAsync(60000);await flushPromises();expect(state.pending.value.opl).toBeUndefined()
  fail=false;revision=2;await vi.advanceTimersByTimeAsync(60000);await flushPromises();expect(state.pending.value.opl).toBeDefined()
  fail=true;await vi.advanceTimersByTimeAsync(60000);await flushPromises();expect(state.pending.value.opl).toBeDefined()
})
 it('keeps unfinished work after viewing and only clears completed work',()=>{
 expect(needsHandling('opl',[{rows:[{issue:{status:'待处理'}}]}])).toBe(true)
 expect(needsHandling('opl',[{rows:[{issue:{status:'已完成'}}]}])).toBe(false)
})
it('requires successful publication of identical BOM content, not approval or navigation',()=>{
 const item={drawingNumber:'A',name:'零件',quantity:1,revision:'W1'}
 const current=[[item],[],[],[]]
 const versions=[{id:'v1',state:'Released',items:[item]}]
 expect(bomNeedsHandling(current,versions,[])).toBe(true)
 expect(bomNeedsHandling(current,versions,[{standardBomVersionId:'v1',state:'Approved'}])).toBe(true)
 const published=[{standardBomVersionId:'v1',publishedAt:'2026-10-13T01:00:00Z'}]
 expect(bomNeedsHandling(current,versions,published)).toBe(false)
 expect(bomNeedsHandling([[{...item,quantity:2}],[],[],[]],versions,published)).toBe(true)
 expect(bomNeedsHandling([[item,{...item,drawingNumber:'B'}],[],[],[]],versions,published)).toBe(true)
 expect(bomNeedsHandling([[],[],[],[]],versions,published)).toBe(true)
})
it('ignores procurement refresh metadata but detects real item changes',()=>{
 const first={items:[],lastSuccessfulRefreshAt:'2026-10-13T01:00:00Z',lastRefreshError:null}
 const refreshed={...first,lastSuccessfulRefreshAt:'2026-10-13T09:00:00Z',lastRefreshError:'刷新失败'}
 expect(fingerprint(procurementContent(first))).toBe(fingerprint(procurementContent(refreshed)))
 expect(fingerprint(procurementContent({items:[{id:'1',stock:0}]}))).not.toBe(fingerprint(procurementContent({items:[{id:'1',stock:2}]})))
})
