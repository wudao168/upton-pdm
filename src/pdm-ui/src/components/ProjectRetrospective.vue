<script setup lang="ts">
import { computed, onBeforeUnmount, ref, watch } from 'vue'
import { ElMessageBox } from 'element-plus'
import PersonName from './PersonName.vue'
import { createClientUuid } from '../clientId'
import { confirmQualityAdoption, qualityClosed, qualityDate, qualityFailure, qualityName, readProjectRetrospective, saveRetrospective, transitionRetrospective } from '../qualityReview'
import type { ProjectSummaryReview, RetrospectiveWorkspace, SaveSummary } from '../qualityReview'
import '../styles/quality-review.css'

const props = defineProps<{ token:string; projectId:string; projectCode?:string; projectName?:string }>()
const emit = defineEmits<{ updated:[] }>()
const data = ref<RetrospectiveWorkspace>(), loading = ref(false), busy = ref(false), error = ref(''), message = ref('')
const tab = ref('summary'), selectedId = ref(''), editingId = ref<string|null>(null)
const draft = ref<SaveSummary>({expectedRowVersion:0,kind:'阶段总结',title:'',achievements:'',lessons:'',remainingPlan:'',coverageFrom:null,coverageTo:null,targetResults:''})
const adoptionNotes = ref<Record<string,string>>({})
let generation=0
const selected = computed(()=>data.value?.summaries.find(item=>item.id===selectedId.value))
const outstanding = computed(()=>data.value?.issues.filter(item=>!qualityClosed(item.issue.status)) ?? [])
const history = computed(()=>[...(data.value?.summaries ?? [])].sort((a,b)=>b.createdAt.localeCompare(a.createdAt)))
const experienceTitle = (id:string)=>data.value?.experiences.find(item=>item.id===id)?.title || '经验条目'
async function load() {
  const run=++generation; loading.value=true; error.value=''; editingId.value=null
  try { const result=await readProjectRetrospective(props.projectId,props.token); if(run!==generation)return; data.value=result; if(!result.summaries.some(item=>item.id===selectedId.value)) selectedId.value=result.summaries.at(-1)?.id ?? ''; adoptionNotes.value=Object.fromEntries(result.adoptions.map(item=>[item.id,item.evidence])); }
  catch(reason){if(run===generation){data.value=undefined;error.value=qualityFailure(reason,'项目总结读取失败，请核对项目权限后重试。')}}
  finally{if(run===generation)loading.value=false}
}
watch(()=>[props.projectId,props.token],()=>{data.value=undefined;selectedId.value='';editingId.value=null;message.value='';void load()},{immediate:true})
onBeforeUnmount(()=>generation++)
async function perform(action:()=>Promise<unknown>,success:string) { if(busy.value)return; const run=generation;busy.value=true;error.value='';try{await action();if(run!==generation)return;message.value=success;emit('updated');await load()}catch(reason){if(run===generation)error.value=qualityFailure(reason)}finally{busy.value=false} }
function edit(item?:ProjectSummaryReview){editingId.value=item?.id ?? createClientUuid();draft.value={expectedRowVersion:data.value!.rowVersion,kind:item?.kind ?? '阶段总结',title:item?.title ?? `${props.projectCode || ''} 项目总结`.trim(),achievements:item?.achievements ?? '',lessons:item?.lessons ?? '',remainingPlan:item?.remainingPlan ?? '',coverageFrom:item?.coverageFrom??null,coverageTo:item?.coverageTo??null,targetResults:item?.targetResults??''}}
async function save(){if(!editingId.value||!data.value)return;if(!draft.value.title.trim()){error.value='请填写总结标题。';return}const id=editingId.value;await perform(()=>saveRetrospective(props.projectId,id,{...draft.value,coverageFrom:draft.value.coverageFrom||null,coverageTo:draft.value.coverageTo||null},props.token),'总结草稿已保存。');selectedId.value=id}
async function act(action:string,label:string){if(!selected.value||!data.value)return;let note='';try{const response=await ElMessageBox.prompt(`请填写${label}意见。${action==='publish'?'发布后保留本次复核的问题快照，后续处理不会改写本次总结。':''}`,label,{confirmButtonText:'确认',cancelButtonText:'取消',inputType:'textarea',inputValidator:value=>value.trim().length>0||'请填写处理意见。'});note=response.value}catch{return}const id=selected.value.id,version=data.value.rowVersion;await perform(()=>transitionRetrospective(props.projectId,id,version,action,note,props.token),`${label}已完成。`)}
async function confirmAdoption(id:string,state:string){const evidence=adoptionNotes.value[id]?.trim();if(!data.value||!evidence){error.value='请填写实施证据或不适用原因。';return}await perform(()=>confirmQualityAdoption(id,{state,evidence,expectedRowVersion:data.value!.rowVersion},props.token),'经验采用检查已更新。')}
</script>

<template>
  <section class="quality-review project-retrospective" aria-label="项目总结">
    <header class="quality-review__toolbar"><h2>{{ projectCode }} 项目总结</h2><div class="quality-review__actions"><button v-if="data?.canEdit" :disabled="busy||loading" @click="edit()">新增总结</button><button :disabled="busy||loading" @click="load">刷新</button></div></header>
    <p v-if="error" role="alert" class="quality-review__error">{{ error }}</p><p v-if="message" role="status" class="quality-review__success">{{ message }}</p>
    <nav class="quality-review__tabs" role="tablist" aria-label="总结视图"><button role="tab" :aria-selected="tab==='summary'" @click="tab='summary'">总结与历史</button><button role="tab" :aria-selected="tab==='remaining'" @click="tab='remaining'">遗留跟踪</button><button role="tab" :aria-selected="tab==='experience'" @click="tab='experience'">经验采用</button></nav>
    <p v-if="loading" role="status">正在读取项目总结…</p>
    <template v-else-if="data">
      <template v-if="tab==='summary'">
        <form v-if="editingId" class="quality-review__card project-summary-editor" @submit.prevent="save">
          <h3>{{ data.summaries.some(item=>item.id===editingId)?'编辑总结草稿':'新增总结草稿' }}</h3><fieldset :disabled="busy" class="quality-review__form">
            <label>总结类型<select v-model="draft.kind"><option>阶段总结</option><option>最终总结</option></select></label><label>标题<input v-model="draft.title" aria-label="总结标题" maxlength="200" required></label>
            <label>总结开始日期<input v-model="draft.coverageFrom" type="date"></label><label>总结结束日期<input v-model="draft.coverageTo" type="date"></label><label class="quality-review__wide">进度、预算与质量目标达成<textarea v-model="draft.targetResults" maxlength="12000" /></label><label class="quality-review__wide">成果与做得好的方面<textarea v-model="draft.achievements" maxlength="10000" /></label><label class="quality-review__wide">主要问题与经验教训<textarea v-model="draft.lessons" maxlength="10000" /></label><label class="quality-review__wide">遗留事项、责任安排及后续计划<textarea v-model="draft.remainingPlan" maxlength="10000" /></label>
          </fieldset><div class="quality-review__actions"><button type="button" :disabled="busy" @click="editingId=null">取消</button><button class="pdm-primary-action" :disabled="busy" type="submit">保存草稿</button></div>
        </form>
        <div class="quality-review__scroll"><table><thead><tr><th>标题</th><th>类型</th><th>版本</th><th>状态</th><th>编制日期</th><th>发布日期</th><th>操作</th></tr></thead><tbody><tr v-for="item in history" :key="item.id" :aria-selected="selectedId===item.id"><td>{{ item.title }}</td><td>{{ item.kind }}</td><td>R{{ item.revision }}</td><td>{{ item.state }}</td><td>{{ qualityDate(item.createdAt) }}</td><td>{{ qualityDate(item.publishedAt) }}</td><td><button @click="selectedId=item.id;editingId=null">查看</button></td></tr><tr v-if="!history.length"><td colspan="7" class="quality-review__empty">暂无项目总结。</td></tr></tbody></table></div>
        <article v-if="selected" class="quality-review__card"><header class="quality-review__toolbar"><h3>{{ selected.title }} · R{{ selected.revision }}</h3><div class="quality-review__actions">
          <template v-if="data.canEdit&&selected.state==='草稿'"><button :disabled="busy" @click="edit(selected)">编辑</button><button :disabled="busy" @click="act('submit','提交复核')">提交复核</button></template>
          <template v-if="data.canReview&&selected.state==='待复核'"><button :disabled="busy" @click="act('review','复核通过')">复核通过</button><button :disabled="busy" @click="act('reject','退回修改')">退回修改</button></template>
          <button v-if="data.canReview&&selected.state==='已复核'" :disabled="busy" @click="act('reject','退回修改')">退回修改</button><button v-if="data.canPublish&&selected.state==='已复核'" :disabled="busy" @click="act('publish','发布总结')">发布总结</button><button v-if="data.canEdit&&selected.state==='已发布'" :disabled="busy" @click="act('revise','创建修订')">创建修订</button>
        </div></header><div class="quality-review__detail"><p><strong>总结期间</strong>{{ selected.coverageFrom||'未设置' }} 至 {{ selected.coverageTo||'未设置' }}</p><p><strong>目标达成</strong>{{ selected.targetResults||'未填写' }}</p><p><strong>成果</strong>{{ selected.achievements||'未填写' }}</p><p><strong>经验教训</strong>{{ selected.lessons||'未填写' }}</p><p class="quality-review__wide"><strong>后续计划</strong>{{ selected.remainingPlan||'未填写' }}</p></div>
          <h3>总结关联问题{{ selected.state==='已发布'?'（发布快照）':'' }}</h3><div class="quality-review__scroll"><table><thead><tr><th>项目</th><th>问题编号</th><th>问题</th><th>记录时状态</th><th>当前状态</th><th>负责人</th></tr></thead><tbody><tr v-for="item in selected.issues" :key="item.issueId"><td>{{ item.projectCode }}</td><td>{{ item.number }}</td><td>{{ item.description }}</td><td>{{ item.status }}</td><td>{{ data.issues.find(row=>row.issue.id===item.issueId)?.issue.status||'当前状态不可用' }}</td><td><PersonName :name="item.ownerName||'姓名待补充'" /></td></tr><tr v-if="!selected.issues.length"><td colspan="6">暂无关联问题快照</td></tr></tbody></table></div>
        </article>
      </template>
      <p v-if="tab==='remaining'&&!data.canViewIssues" class="quality-review__hint">当前账号未获授权查看项目问题明细，无法判断遗留问题数量。</p><template v-if="tab==='remaining'&&data.canViewIssues"><div class="quality-review__scroll"><table><thead><tr><th>项目</th><th>问题编号</th><th>遗留问题</th><th>状态</th><th>负责人</th><th>计划完成</th><th>措施</th></tr></thead><tbody><tr v-for="row in outstanding" :key="row.issue.id"><td>{{ row.projectCode }}</td><td>{{ row.issue.number }}</td><td>{{ row.issue.description }}</td><td>{{ row.issue.status }}</td><td><PersonName :name="row.ownerName||'姓名待补充'" /></td><td>{{ row.issue.dueDate||'未设置' }}</td><td>{{ row.profile?.correctiveAction||row.issue.solution||'待补充' }}</td></tr><tr v-if="!outstanding.length"><td colspan="7" class="quality-review__empty">当前没有未关闭的项目问题。</td></tr></tbody></table></div></template>
      <template v-if="tab==='experience'">
        <article v-for="item in data.adoptions" :key="item.id" class="quality-review__card"><header class="quality-review__toolbar"><h3>{{ experienceTitle(item.experienceId) }}</h3><span>{{ item.state }}</span></header><p class="quality-review__hint">落实人：<PersonName :name="qualityName(data.people,item.owner)" /> · 计划日期：{{ item.dueOn }}</p><div class="quality-review__detail"><p><strong>适用范围</strong>{{ data.experiences.find(exp=>exp.id===item.experienceId)?.applicability||'—' }}</p><p><strong>复用内容</strong>{{ data.experiences.find(exp=>exp.id===item.experienceId)?.content||'—' }}</p></div><label>实施证据或不适用原因<textarea v-model="adoptionNotes[item.id]" :disabled="busy||!data.canAdopt||item.state!=='待检查'" maxlength="4000" /></label><div v-if="data.canAdopt&&item.state==='待检查'" class="quality-review__actions"><button :disabled="busy" @click="confirmAdoption(item.id,'不适用')">不适用</button><button :disabled="busy" class="pdm-primary-action" @click="confirmAdoption(item.id,'已采用')">确认落实</button></div></article><p v-if="!data.adoptions.length" class="quality-review__empty">当前项目没有经验采用任务。</p>
      </template>
    </template>
  </section>
</template>

<style scoped>
.project-retrospective { container: retrospective / inline-size; gap:8px; padding-block:6px 12px; }
.project-summary-editor { flex-shrink:0; padding-block:8px 12px; }
.project-summary-editor > h3 { margin:0; font-size:14px; line-height:20px; }
.project-summary-editor .quality-review__form { grid-template-columns:repeat(4,minmax(0,1fr)); gap:8px 12px; }
.project-summary-editor .quality-review__wide { grid-column:span 2; }
.project-summary-editor textarea { height:80px; min-height:80px; }
@container retrospective (max-width:760px) {
  .project-summary-editor .quality-review__form { grid-template-columns:repeat(2,minmax(0,1fr)); }
  .project-summary-editor .quality-review__wide { grid-column:span 1; }
}
@container retrospective (max-width:480px) {
  .project-summary-editor .quality-review__form { grid-template-columns:minmax(0,1fr); }
}
</style>
