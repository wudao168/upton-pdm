import { flushPromises, mount } from '@vue/test-utils'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import Workspace from '../src/components/ReleaseDrawingWorkspace.vue'
import * as api from '../src/api'
import type { ReleasePackageSummary } from '../src/types'
vi.mock('../src/api',()=>({listReleaseDrawingReviews:vi.fn(),getApprovalEmergencyCapability:vi.fn()}))
const release: ReleasePackageSummary = {standardBomSnapshot:[],nonStandardBomSnapshot:[],electricalBomSnapshot:[],scope:'NonStandardWithDrawing',workflowVersion:1,selectedBomItemIds:[],createsManufacturingBaseline:false,locksDocuments:true,id:'p',number:'RP-1',state:'审批中',steps:[{id:'main',stage:'主设审核',assignee:'reviewer',status:'current',detail:''}],drawingReview:{revision:1,items:[]}}
const render = (permissions=['release.view','approval.decide']) => mount(Workspace,{props:{projectId:'project',token:'token',username:'reviewer',permissions,requestedId:'p',visible:true},slots:{default: '<template #default="state"><select :value="state.selectedId" @change="state.selectPackage($event.target.value)"><option value="">浏览图档</option><option v-for="item in state.packages" :value="item.id">{{item.state}}</option></select><div v-if="state.selected" data-review>{{state.editable}}</div></template>'}})
beforeEach(()=>{vi.resetAllMocks();vi.mocked(api.listReleaseDrawingReviews).mockResolvedValue([structuredClone(release)])})
describe('图档页发布审核入口',()=>{
  it('定位当前发布包并按实际节点提供审核操作',async()=>{const w=render();await flushPromises();expect(w.get('[data-review]').text()).toBe('true');expect(w.emitted('active')?.at(-1)).toEqual([true]);await w.get('select').setValue('');expect(w.find('[data-review]').exists()).toBe(false);w.unmount()})
  it('撤销审批权限后立即只读，撤销资料权限后停止展示',async()=>{const w=render();await flushPromises();await w.setProps({permissions:['release.view']});expect(w.get('[data-review]').text()).toBe('false');await w.setProps({permissions:[]});await flushPromises();expect(w.find('[data-review]').exists()).toBe(false);w.unmount()})
  it('跨项目切换时不把上一项目响应显示到当前项目',async()=>{let resolve!: (v:ReleasePackageSummary[])=>void;vi.mocked(api.listReleaseDrawingReviews).mockReturnValueOnce(new Promise(r=>{resolve=r}));const w=render();vi.mocked(api.listReleaseDrawingReviews).mockResolvedValueOnce([]);await w.setProps({projectId:'other',requestedId:''});await flushPromises();resolve([release]);await flushPromises();expect(w.find('[data-review]').exists()).toBe(false);expect(w.findAll('option')).toHaveLength(1);w.unmount()})
  it('发布中轮询到完成后仍保留该记录',async()=>{vi.useFakeTimers();vi.mocked(api.listReleaseDrawingReviews).mockResolvedValueOnce([{...release,state:'发布中',steps:[]}]);const w=render();await flushPromises();vi.mocked(api.listReleaseDrawingReviews).mockResolvedValue([{...release,state:'已发布',steps:[]}]);await vi.advanceTimersByTimeAsync(5000);await flushPromises();expect(w.get('select').text()).toContain('已发布');expect(w.get('[data-review]').text()).toBe('false');w.unmount();vi.useRealTimers()})
})
