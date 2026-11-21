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
  it('收到退回提醒后定位对应图纸', async () => {
    const drawing = (id: string) => ({
      documentId: id, versionId: id + '-version', sha256: '', fileName: id + '.SLDDRW',
      token: id, bomItemIds: [], required: true, status: 'Pending' as const, notes: [],
    })
    vi.mocked(api.listReleaseDrawingReviews).mockResolvedValue([{ ...release, drawingReview: {
      revision: 1, items: [drawing('first'), drawing('returned')],
    } }])
    const wrapper = mount(Workspace, {
      props: { projectId: 'project', token: 'token', username: 'reviewer', permissions: ['release.view'],
        requestedId: 'p', requestedDrawingId: 'returned', visible: true },
      slots: { default: '<template #default="state"><div data-drawing>{{state.drawingId}}</div></template>' },
    })
    try {
      await flushPromises()
      expect(wrapper.get('[data-drawing]').text()).toBe('returned')
      await wrapper.setProps({ requestedDrawingId: 'first' })
      await flushPromises()
      expect(wrapper.get('[data-drawing]').text()).toBe('first')
    } finally { wrapper.unmount() }
  })
  it('主设复审只标记新版待确认，保留未升版结论并显示主管退回尚待存档', async () => {
    const drawing = (id: string) => ({
      documentId: id, versionId: id + '-version', sha256: '', fileName: id + '.SLDDRW',
      token: id, bomItemIds: [], required: true, status: 'Pending' as const, notes: [],
    })
    const unchanged = drawing('unchanged')
    const revised = drawing('revised')
    const waiting = { ...drawing('waiting'), notes: [{ status: 'Issue', actor: 'supervisor', actorName: '主管',
      comment: '需修改', at: '2026-11-17T06:00:00Z', versionId: 'waiting-version',
      approvalTaskId: 'supervisor', roundId: 'round', token: 'waiting' }] }
    vi.mocked(api.listReleaseDrawingReviews).mockResolvedValue([{ ...release,
      steps: [...release.steps, { id: 'supervisor', stage: '机械主管批准', assignee: 'supervisor', status: 'waiting', detail: '' }],
      drawingReview: {
        revision: 2, roundId: 'round', items: [unchanged, revised, waiting],
        preservedConfirmations: { main: [unchanged.token, waiting.token, 'old-revised-token'] },
      },
    }])
    const wrapper = mount(Workspace, {
      props: { projectId: 'project', token: 'token', username: 'reviewer', permissions: ['release.view'],
        requestedId: 'p', visible: true },
      slots: { default: '<template #default="state"><div v-for="(badge, id) of state.badges" :data-drawing="id">{{badge.label}}</div></template>' },
    })
    try {
      await flushPromises()
      expect(wrapper.get('[data-drawing="unchanged"]').text()).toBe('已确认')
      expect(wrapper.get('[data-drawing="revised"]').text()).toBe('待确认')
      expect(wrapper.get('[data-drawing="waiting"]').text()).toBe('待修改')
    } finally { wrapper.unmount() }
  })
  it('跨项目切换时不把上一项目响应显示到当前项目',async()=>{let resolve!: (v:ReleasePackageSummary[])=>void;vi.mocked(api.listReleaseDrawingReviews).mockReturnValueOnce(new Promise(r=>{resolve=r}));const w=render();vi.mocked(api.listReleaseDrawingReviews).mockResolvedValueOnce([]);await w.setProps({projectId:'other',requestedId:''});await flushPromises();resolve([release]);await flushPromises();expect(w.find('[data-review]').exists()).toBe(false);expect(w.findAll('option')).toHaveLength(1);w.unmount()})
  it('发布中轮询到完成后仍保留该记录',async()=>{vi.useFakeTimers();vi.mocked(api.listReleaseDrawingReviews).mockResolvedValueOnce([{...release,state:'发布中',steps:[]}]);const w=render();await flushPromises();vi.mocked(api.listReleaseDrawingReviews).mockResolvedValue([{...release,state:'已发布',steps:[]}]);await vi.advanceTimersByTimeAsync(5000);await flushPromises();expect(w.get('select').text()).toContain('已发布');expect(w.get('[data-review]').text()).toBe('false');w.unmount();vi.useRealTimers()})
})
