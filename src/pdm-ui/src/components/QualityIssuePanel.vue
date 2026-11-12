<script setup lang="ts">
import { onBeforeUnmount, ref, watch } from 'vue'
import PersonName from './PersonName.vue'
import { qualityDate, qualityFailure, qualityName, readQualityIssue, saveQualityIssue } from '../qualityReview'
import type { QualityIssueWorkspace, SaveIssueProfile } from '../qualityReview'
import '../styles/quality-review.css'

const props=defineProps<{token:string;projectId:string;issueId:string}>()
const emit=defineEmits<{updated:[]}>()
const data=ref<QualityIssueWorkspace>(), loading=ref(false), busy=ref(false), error=ref(''), message=ref('')
const empty=():SaveIssueProfile=>({expectedRowVersion:0,domain:'',category:'',severity:'普通',occurredOn:null,themeId:null,duplicateOfIssueId:null,recurrenceOfIssueId:null,immediateAction:'',rootCause:'',correctiveAction:'',verificationResult:'未验证',evidenceNote:'',evidenceAttachmentIds:[]})
const draft=ref(empty());let generation=0
async function load(){const run=++generation;loading.value=true;error.value='';try{const result=await readQualityIssue(props.projectId,props.issueId,props.token);if(run!==generation)return;data.value=result;const profile=result.row.profile;draft.value={...empty(),...(profile?{domain:profile.domain,category:profile.category,severity:profile.severity,occurredOn:profile.occurredOn,themeId:profile.themeId,duplicateOfIssueId:profile.duplicateOfIssueId,recurrenceOfIssueId:profile.recurrenceOfIssueId,immediateAction:profile.immediateAction,rootCause:profile.rootCause,correctiveAction:profile.correctiveAction,verificationResult:profile.verificationResult,evidenceNote:profile.evidenceNote,evidenceAttachmentIds:[...profile.evidenceAttachmentIds]}:{}),expectedRowVersion:result.rowVersion}}catch(reason){if(run===generation){data.value=undefined;error.value=qualityFailure(reason,'问题闭环资料读取失败，请核对项目权限后重试。')}}finally{if(run===generation)loading.value=false}}
watch(()=>[props.token,props.projectId,props.issueId],()=>{data.value=undefined;draft.value=empty();message.value='';void load()},{immediate:true})
onBeforeUnmount(()=>generation++)
async function save(){if(!data.value||busy.value)return;if(draft.value.duplicateOfIssueId&&draft.value.recurrenceOfIssueId){error.value='同一事件重复登记与独立复发只能选择一项。';return}const run=generation;busy.value=true;error.value='';try{await saveQualityIssue(props.projectId,props.issueId,{...draft.value,occurredOn:draft.value.occurredOn||null},props.token);if(run!==generation)return;message.value='问题闭环资料已保存。';emit('updated');await load()}catch(reason){if(run===generation)error.value=qualityFailure(reason)}finally{busy.value=false}}
</script>

<template>
  <section class="quality-review" style="height:auto" aria-label="问题分类与质量闭环">
    <header class="quality-review__toolbar"><h3>问题分类与质量闭环</h3><button :disabled="busy||loading" @click="load">刷新</button></header>
    <p v-if="error" role="alert" class="quality-review__error">{{ error }}</p><p v-if="message" role="status" class="quality-review__success">{{ message }}</p><p v-if="loading" role="status">正在读取闭环资料…</p>
    <form v-else-if="data" @submit.prevent="save">
      <fieldset :disabled="busy||!data.canManageIssues" class="quality-review__form">
        <label>问题领域<select v-model="draft.domain" aria-label="问题领域" :disabled="data.row.profile?.domain==='质量'"><option value="">待分类</option><option>质量</option><option>设计</option><option>采购</option><option>生产</option><option>进度</option><option>成本</option><option>其他</option></select></label>
        <label>分类<input v-model="draft.category" maxlength="100" placeholder="如：机械设计、来料检验"></label>
        <label>严重程度<select v-model="draft.severity"><option>普通</option><option>重大</option><option>严重</option></select></label>
        <label>实际发生日期<input v-model="draft.occurredOn" type="date"></label>
        <label class="quality-review__wide">共性问题主题<select v-model="draft.themeId"><option :value="null">未归类</option><option v-for="theme in data.themes.filter(item=>item.isActive||item.id===draft.themeId)" :key="theme.id" :value="theme.id">{{ theme.name }}{{ theme.isActive?'':'（已停用）' }}</option></select></label>
        <label>同一事件的重复登记<select v-model="draft.duplicateOfIssueId" :disabled="!!draft.recurrenceOfIssueId"><option :value="null">独立事件</option><option v-for="row in data.relatedIssues" :key="row.issue.id" :value="row.issue.id">{{ row.projectCode }} #{{ row.issue.number }} {{ row.issue.description }}</option></select></label>
        <label>独立复发的原问题<select v-model="draft.recurrenceOfIssueId" :disabled="!!draft.duplicateOfIssueId"><option :value="null">非复发 / 未关联</option><option v-for="row in data.relatedIssues" :key="row.issue.id" :value="row.issue.id">{{ row.projectCode }} #{{ row.issue.number }} {{ row.issue.description }}</option></select></label>
        <p class="quality-review__hint quality-review__wide">重复登记不增加事件次数；再次独立发生作为新事件统计。历史发生日期未知时保留空白。</p>
        <label class="quality-review__wide">临时处置<textarea v-model="draft.immediateAction" maxlength="4000" placeholder="隔离、止损和临时防护措施" /></label><label class="quality-review__wide">原因分析<textarea v-model="draft.rootCause" maxlength="4000" /></label><label class="quality-review__wide">纠正措施<textarea v-model="draft.correctiveAction" maxlength="4000" /></label>
      </fieldset>
      <fieldset :disabled="busy||!data.canVerifyIssues" class="quality-review__form" style="margin-top:12px">
        <label>效果验证<select v-model="draft.verificationResult" aria-label="效果验证"><option>未验证</option><option>未通过</option><option>通过</option></select></label><p class="quality-review__hint">措施完成后由获授权人员确认效果；发布图纸本身不表示验证通过。</p>
        <label class="quality-review__wide">验证依据<textarea v-model="draft.evidenceNote" maxlength="4000" placeholder="填写验证方法、实测结果及证据位置" /></label>
        <div class="quality-review__wide"><span>关联现有问题附件</span><div class="quality-review__checklist"><label v-for="file in data.row.issue.attachments" :key="file.id"><input v-model="draft.evidenceAttachmentIds" type="checkbox" :value="file.id">{{ file.name }}</label><p v-if="!data.row.issue.attachments.length" class="quality-review__hint">暂无附件，可在问题详情中上传后刷新。</p></div></div>
      </fieldset>
      <p v-if="data.row.profile?.verifiedBy" class="quality-review__hint">最近验证：<PersonName :name="qualityName(data.people,data.row.profile.verifiedBy)" /> · {{ qualityDate(data.row.profile.verifiedAt) }}</p>
      <div class="quality-review__actions" style="margin-top:12px"><button v-if="data.canManageIssues||data.canVerifyIssues" type="submit" class="pdm-primary-action" :disabled="busy">保存闭环</button></div>
    </form>
  </section>
</template>
