import { flushPromises, mount } from '@vue/test-utils'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import ReleaseDrawingReviewPanel from '../src/components/ReleaseDrawingReviewPanel.vue'
import * as api from '../src/api'
import type { ReleasePackageSummary } from '../src/types'
vi.mock('../src/api', () => ({ readReleaseDrawingPreview: vi.fn(), decideReleaseDrawing: vi.fn(), decideReleaseDrawingNode: vi.fn(), generateReleaseDrawingPreview: vi.fn() }))
const fixture = (): ReleasePackageSummary => ({ id:'package',number:'RP-TEST',state:'工艺审核',scope:'NonStandardWithDrawing',steps:[{id:"main",stage:"主设审核",assignee:"admin",status:"current",detail:""}],workflowVersion:1,selectedBomItemIds:['bom'],createsManufacturingBaseline:false,locksDocuments:true,
  standardBomSnapshot:[],electricalBomSnapshot:[],wholeSetMultiplier:2,
  nonStandardBomSnapshot:[{id:'bom',kind:'NonStandard',sequence:1,drawingNumber:'M1',name:'测试支架',specification:'M1',quantity:3,unit:'件',revision:'W1',complete:true}],
  drawingReview:{revision:1,roundId:'round',items:[{documentId:'drawing',versionId:'v1',sha256:'hash',token:'token',fileName:'M1.SLDDRW',required:true,status:'Pending',bomItemIds:['bom'],notes:[],preview:{sourceSha256:'hash',format:'Pdf'}}]}
})
const render = (release=fixture(), ready=true, editable=true) => mount(ReleaseDrawingReviewPanel,{props:{release,token:'login',editable,username:'admin',embedded:true,selectedDocumentId:'drawing',externalPreviewReady:ready}})
beforeEach(()=>{vi.resetAllMocks()})
describe('原图与冻结BOM联合审核',()=>{
 it('无PDF时原图就绪可以提交冻结版本审核',async()=>{const release=fixture();release.drawingReview!.items[0]!.preview=null;const w=render(release);await flushPromises();expect(api.readReleaseDrawingPreview).not.toHaveBeenCalled();expect(api.generateReleaseDrawingPreview).not.toHaveBeenCalled();expect(w.text()).not.toContain('缺少预览');expect(w.text()).not.toContain('本节点全部必审图纸');expect(w.text()).not.toContain('生成预览');vi.mocked(api.decideReleaseDrawing).mockResolvedValue(release);await w.findAll('button').find(b=>b.text()==='通过')!.trigger('click');await flushPromises();expect(api.decideReleaseDrawing).toHaveBeenCalledWith('package',expect.objectContaining({documentId:'drawing',token:'token',status:'Confirmed',approvalTaskId:'main',roundId:'round'}),'login');w.unmount()});
 it('未加载原图不能确认，问题说明必填',async()=>{const w=render(fixture(),false);await flushPromises();expect(w.findAll('button').find(b=>b.text()==='通过')!.attributes('disabled')).toBeDefined();await w.findAll('button').find(b=>b.text()==='不通过')!.trigger('click');expect(w.get('[role="alert"]').text()).toContain('不通过时必须说明原因');expect(api.decideReleaseDrawing).not.toHaveBeenCalled();w.unmount()});
 it('只读与已完成审批不能提交',async()=>{const w=render(fixture(),true,false);expect(w.findAll('button').some(b=>b.text()==='通过')).toBe(false);await w.setProps({release:{...fixture(),state:'已发布'},editable:true});expect(w.findAll('button').some(b=>b.text()==='不通过')).toBe(false);w.unmount()});
 it('复用外部原图和树选择',async()=>{const release=fixture();release.drawingReview!.items.push({...release.drawingReview!.items[0]!,documentId:'drawing2',versionId:'v2',token:'token2',fileName:'M2.SLDDRW'});const w=render(release,false);await flushPromises();expect(w.find('.review-workspace>aside').exists()).toBe(false);expect(w.find('.review-pdf').exists()).toBe(false);await w.setProps({externalPreviewReady:true});expect(w.findAll('button').find(b=>b.text()==='通过')!.attributes('disabled')).toBeUndefined();await w.setProps({selectedDocumentId:'drawing2',externalPreviewReady:false});expect(w.text()).toContain('M2.SLDDRW');expect(w.findAll('button').find(b=>b.text()==='通过')!.attributes('disabled')).toBeDefined();w.unmount()});
 it('浏览器不能通过PDF替代原图审核',async()=>{const w=mount(ReleaseDrawingReviewPanel,{props:{release:fixture(),token:'login',editable:true,username:'admin'}});await flushPromises();expect(w.text()).toContain('Windows 客户端');expect(api.readReleaseDrawingPreview).not.toHaveBeenCalled();expect(w.findAll('button').find(b=>b.text()==='通过')!.attributes('disabled')).toBeDefined();w.unmount()});
 it('紧急代批使用明确入口且原因必填',async()=>{
   const release=fixture();release.steps[0]!.participants={policy:{requireOpinion:true}} as NonNullable<typeof release.steps[number]['participants']>;
   const w=render(release);await w.setProps({emergency:true});await flushPromises();
   expect(w.findAll('textarea')).toHaveLength(1);expect(w.find('[aria-label="图纸审核代批原因"]').exists()).toBe(false);expect(w.find('[aria-label="本节点审批意见"]').exists()).toBe(false);
   const confirm=w.findAll('button').find(b=>b.text()==='紧急代批')!;await confirm.trigger('click');expect(api.decideReleaseDrawing).not.toHaveBeenCalled();
   expect(w.get('[role="alert"]').text()).toContain('请填写紧急代批原因');
   expect(w.findAll('button').some(b=>b.text()==='通过')).toBe(false);
   vi.mocked(api.decideReleaseDrawing).mockResolvedValue(release);
   await w.get('[aria-label="紧急代批原因"]').setValue('负责人出差，受托核对尺寸无误');await confirm.trigger('click');await flushPromises();
   expect(api.decideReleaseDrawing).toHaveBeenCalledWith('package',expect.objectContaining({comment:'负责人出差，受托核对尺寸无误',emergencyReason:'负责人出差，受托核对尺寸无误'}),'login',true);w.unmount()
 });
 it('退回节点使用下方同一意见，失败后保留输入供修改',async()=>{
   const w=render();await flushPromises();const reject=w.findAll('button').find(b=>b.text()==='不通过')!;await reject.trigger('click');expect(api.decideReleaseDrawingNode).not.toHaveBeenCalled();
   await w.get('[aria-label="图纸审核意见"]').setValue('孔径与BOM不符，请修改');vi.mocked(api.decideReleaseDrawingNode).mockRejectedValue(new Error('审批状态已改变，请刷新后重试。'));
   await reject.trigger('click');await flushPromises();expect(api.decideReleaseDrawingNode).toHaveBeenCalledWith('package',expect.objectContaining({decision:'Rejected',comment:'孔径与BOM不符，请修改',approvalTaskId:'main',roundId:'round'}),'login');expect((w.get('textarea').element as HTMLTextAreaElement).value).toBe('孔径与BOM不符，请修改');w.unmount()
 });
 it('没有送审图纸的发布仍可填写意见并完成节点',async()=>{
   const release=fixture();release.drawingReview!.items=[];const w=render(release);await flushPromises();expect(w.findAll('textarea')).toHaveLength(1);
   vi.mocked(api.decideReleaseDrawingNode).mockResolvedValue(release);await w.get('textarea').setValue('仅移除旧BOM，已核对');await w.findAll('button').find(b=>b.text()==='通过')!.trigger('click');await flushPromises();expect(api.decideReleaseDrawingNode).toHaveBeenCalledWith('package',expect.objectContaining({decision:'Approved',comment:'仅移除旧BOM，已核对'}),'login');w.unmount()
 });
 it('BOM明细与图纸分别计数，同图多明细不合并或丢失数量',()=>{
   const release=fixture();release.nonStandardBomSnapshot.push({...release.nonStandardBomSnapshot[0]!,id:'bom2',quantity:4});release.drawingReview!.items[0]!.bomItemIds.push('bom2');
   const w=render(release);expect(w.text()).toContain('冻结BOM（2条明细／1张图纸）');
   for(const table of w.findAll('table')){expect(table.findAll('th').map(h=>h.text())).toEqual(['物料 / 型号','总量']);expect(table.findAll('tbody tr').map(r=>r.findAll('td')[1]!.text())).toEqual(['6','8'])}w.unmount()
 });
 it('变更审核按发布基线逐物料显示带符号的需求净变更', async () => {
   const release = fixture()
   release.scope = 'NonStandardSupplement'
   release.wholeSetMultiplier = 1
   const base = fixture()
   base.id = 'baseline'
   base.state = '已发布'
   base.publishedAt = '2026-11-17T08:00:00Z'
   base.wholeSetMultiplier = 1
   const plate = { ...release.nonStandardBomSnapshot[0]!, id: 'plate', drawingNumber: 'M2', name: '定位板', quantity: 1 }
   const removed = { ...plate, id: 'removed', drawingNumber: 'M3', quantity: 4 }
   base.nonStandardBomSnapshot = [{ ...base.nonStandardBomSnapshot[0]!, quantity: 1 }, plate, removed]
   release.nonStandardBomSnapshot.push(plate)
   release.selectedBomItemIds.push('plate')
   release.confirmedRemovedBomItemIds = ['removed']
   const w = render(release)
   expect(w.text()).toContain('暂无法核对需求净变更')
   await w.setProps({ history: [release, base] })
   const tables = w.findAll('table')
   expect(tables[0]!.findAll('td:last-child').map(cell => cell.text())).toEqual(['+2', '-4', '0'])
   expect(tables[1]!.get('th:last-child').text()).toBe('需求净变更')
   expect(tables[1]!.get('td:last-child').text()).toBe('+2')
   w.unmount()
 })
 it('刷新相同版本保留未提交意见，切换图纸清空避免误用',async()=>{
   const release=fixture();release.drawingReview!.items.push({...release.drawingReview!.items[0]!,documentId:'drawing2',token:'token2'});const w=render(release);await flushPromises();await w.get('textarea').setValue('只属于当前图纸');
   await w.setProps({release:JSON.parse(JSON.stringify(release))});expect((w.get('textarea').element as HTMLTextAreaElement).value).toBe('只属于当前图纸');await w.setProps({selectedDocumentId:'drawing2'});expect((w.get('textarea').element as HTMLTextAreaElement).value).toBe('');w.unmount()
 });

 it('通过和不通过配合翻图按钮，成功后自动定位下一张未通过图纸',async()=>{
   const release=fixture();release.drawingReview!.items.push({...release.drawingReview!.items[0]!,documentId:'drawing2',versionId:'v2',token:'token2',notes:[]});
   const w=render(release);await flushPromises();expect(w.findAll('.review-actions button').map(b=>b.text())).toEqual(['通过','不通过','上一张','下一张']);
   const saved=JSON.parse(JSON.stringify(release));saved.drawingReview.revision++;saved.drawingReview.items[0].notes=[{approvalTaskId:'main',roundId:'round',token:'token',actor:'admin',status:'Confirmed'}];vi.mocked(api.decideReleaseDrawing).mockResolvedValue(saved);
   await w.findAll('button').find(b=>b.text()==='通过')!.trigger('click');await flushPromises();expect(w.emitted('selectDocument')?.at(-1)).toEqual(['drawing2']);expect(api.decideReleaseDrawingNode).not.toHaveBeenCalled();w.unmount()
 });
 it('不通过一次提交退回流程并保留原因，不再单独登记问题',async()=>{
   const release=fixture(),w=render(release);await flushPromises();vi.mocked(api.decideReleaseDrawingNode).mockResolvedValue({...release,state:'已退回'});
   await w.get('textarea').setValue('孔径不符，请改为12');await w.findAll('button').find(b=>b.text()==='不通过')!.trigger('click');await flushPromises();expect(api.decideReleaseDrawing).not.toHaveBeenCalled();expect(api.decideReleaseDrawingNode).toHaveBeenCalledTimes(1);expect(api.decideReleaseDrawingNode).toHaveBeenCalledWith('package',expect.objectContaining({decision:'Rejected',comment:'孔径不符，请改为12'}),'login');expect(w.get('[role="status"]').text()).toContain('发布单已退回修改');w.unmount()
 });
 it('审核请求失败不自动跳图，也不清空原因',async()=>{
   const w=render();await flushPromises();vi.mocked(api.decideReleaseDrawing).mockRejectedValue(new Error('审批节点已变化，请刷新后重试。'));await w.get('textarea').setValue('尺寸无误');await w.findAll('button').find(b=>b.text()==='通过')!.trigger('click');await flushPromises();expect(w.emitted('selectDocument')).toBeUndefined();expect((w.get('textarea').element as HTMLTextAreaElement).value).toBe('尺寸无误');w.unmount()
 });

})
