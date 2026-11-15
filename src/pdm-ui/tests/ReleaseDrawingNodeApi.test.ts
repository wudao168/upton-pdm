import { afterEach, expect, it, vi } from 'vitest'
import { decideReleaseDrawingNode, listReleaseDrawingReviews } from '../src/api'
afterEach(()=>vi.unstubAllGlobals())
it('图档节点决定沿用服务端审批枚举并携带本轮与节点标识',async()=>{
  const fetch=vi.fn().mockResolvedValue(new Response(JSON.stringify({id:'p',state:'Rejected',approvalTasks:[],scope:'NonStandardWithDrawing',standardBomSnapshot:[],nonStandardBomSnapshot:[],electricalBomSnapshot:[]})))
  vi.stubGlobal('fetch',fetch)
  await decideReleaseDrawingNode('p',{approvalTaskId:'task',roundId:'round',decision:'Rejected',comment:'尺寸需修改'},'token')
  expect(JSON.parse(fetch.mock.calls[0]![1].body)).toEqual({approvalTaskId:'task',roundId:'round',decision:1,comment:'尺寸需修改'})
})

it.each([1,2,'ProcessReview','Approval'])('发布状态%s仅表示审批中，实际节点名称来自审批流',async(state)=>{
 const fetch=vi.fn().mockResolvedValue(new Response(JSON.stringify([{id:'p',state,approvalTasks:[{id:'task',stage:1,stepName:'装配方案复核',stepOrder:1,assignee:'qa'}],scope:'NonStandardWithDrawing',standardBomSnapshot:[],nonStandardBomSnapshot:[],electricalBomSnapshot:[]}])));vi.stubGlobal('fetch',fetch);
 const [result]=await listReleaseDrawingReviews('project','token');expect(result!.state).toBe('审批中');expect(result!.steps.find(s=>s.status==='current')?.stage).toBe('装配方案复核');expect(JSON.stringify(result)).not.toContain('工艺审核');
});
it('没有保存节点名称时不按旧阶段代码另造节点名称',async()=>{
 vi.stubGlobal('fetch',vi.fn().mockResolvedValue(new Response(JSON.stringify([{id:'p',state:1,approvalTasks:[{id:'task',stage:1,stepOrder:1,assignee:'qa'}],scope:'NonStandardWithDrawing',standardBomSnapshot:[],nonStandardBomSnapshot:[],electricalBomSnapshot:[]}]))));
 const [result]=await listReleaseDrawingReviews('project','token');expect(result!.steps.find(s=>s.status==='current')?.stage).toBe('审批节点（名称待补充）');
});
