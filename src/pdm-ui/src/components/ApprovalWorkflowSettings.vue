<script setup lang="ts">
import { reactive, ref, watch } from 'vue'
import type { ApprovalAssigneeSource, ApprovalStage, ApprovalWorkflowStepTemplate, PdmSystemSettings, ReleaseApprovalSettings } from '../types'

const props = defineProps<{
  settings: PdmSystemSettings
  pending: boolean
  onSave: (settings: PdmSystemSettings) => Promise<PdmSystemSettings>
}>()

function fallback(): ReleaseApprovalSettings {
  return {
    mechanical: { code: 'mechanical-release', name: '机械发布审批', version: 1, steps: [
      { stage: 'MechanicalEngineer', name: '机械工程师自检', assigneeSource: 'Submitter' },
      { stage: 'MainDesigner', name: '主设审核', assigneeSource: 'ProjectDesignLead' },
      { stage: 'MechanicalSupervisor', name: '机械主管批准', assigneeSource: 'PrimaryUnitManager' },
    ] },
    electrical: { code: 'electrical-release', name: '电气发布审批', version: 1, steps: [
      { stage: 'HardwareEngineer', name: '硬件工程师自检', assigneeSource: 'Submitter' },
      { stage: 'HardwareSupervisor', name: '硬件主管审核', assigneeSource: 'PrimaryUnitManager' },
      { stage: 'StandardizationSupervisor', name: '标准化主管批准', assigneeSource: 'ParentUnitManager' },
    ] },
    emergencySubstituteRoleCode: 'BusinessUnitManager',
  }
}

const draft = reactive<ReleaseApprovalSettings>(fallback())
const reasonTypes = ref<string[]>([])
const materialApproverRoleCodes = ref<string[]>([])
const stageByValue: Record<number, ApprovalStage> = {
  1: 'ProcessReview', 2: 'Approval', 10: 'MechanicalEngineer', 20: 'MainDesigner',
  30: 'MechanicalSupervisor', 40: 'HardwareEngineer', 50: 'HardwareSupervisor', 60: 'StandardizationSupervisor',
}
const sourceByValue: Record<number, ApprovalAssigneeSource> = {
  0: 'Submitter', 1: 'ProjectDesignLead', 2: 'FixedUser', 3: 'PrimaryUnitManager', 4: 'ParentUnitManager',
}
function cloneStep(step: ApprovalWorkflowStepTemplate): ApprovalWorkflowStepTemplate {
  const stage = typeof step.stage === 'number' ? stageByValue[step.stage] : step.stage
  const assigneeSource = typeof step.assigneeSource === 'number' ? sourceByValue[step.assigneeSource] : step.assigneeSource
  return { ...step, stage, assigneeSource }
}
function cloneSettings(settings: ReleaseApprovalSettings): ReleaseApprovalSettings {
  return {
    mechanical: { ...settings.mechanical, steps: settings.mechanical.steps.map(cloneStep) },
    electrical: { ...settings.electrical, steps: settings.electrical.steps.map(cloneStep) },
    emergencySubstituteRoleCode: settings.emergencySubstituteRoleCode,
  }
}
function copyFrom(settings?: ReleaseApprovalSettings) {
  Object.assign(draft, cloneSettings(settings ?? fallback()))
}
watch(() => props.settings.approvalWorkflows, copyFrom, { immediate: true, deep: true })
watch(() => props.settings.releaseChangeReasonTypes, values => {
  reasonTypes.value = [...(values?.length ? values : ['设计变更', '客户需求', '物料替代', '质量整改', '生产反馈', '其他'])]
}, { immediate: true, deep: true })
watch(() => props.settings.materialCodeApproval, value => {
  materialApproverRoleCodes.value = [...(value?.approverRoleCodes?.length ? value.approverRoleCodes : ['ProcessReviewer', 'Approver'])]
}, { immediate: true, deep: true })

const sourceLabel = (source: ApprovalAssigneeSource) => ({
  Submitter: '当前提交人',
  ProjectDesignLead: '项目主设',
  FixedUser: '固定账号（旧模板）',
  PrimaryUnitManager: '提交人主部门负责人',
  ParentUnitManager: '上级部门负责人',
})[source]

async function save() {
  await props.onSave({ ...props.settings, approvalWorkflows: cloneSettings(draft), materialCodeApproval: { version: props.settings.materialCodeApproval?.version ?? 1, approverRoleCodes: materialApproverRoleCodes.value }, releaseChangeReasonTypes: reasonTypes.value })
}
</script>

<template>
  <section class="pdm-project-manager approval-workflow-settings" aria-label="审批流程设置">
    <header class="workflow-heading">
      <div>
        <div class="pdm-breadcrumb">系统管理 <span>/</span> 审批流程</div>
        <h1>发布审批流程</h1>
        <p>审批人按提交人的主部门和上级部门负责人动态解析；发布包创建后固化账号，组织调整不影响在途审批。</p>
      </div>
      <button type="button" class="pdm-primary-action" :disabled="pending" @click="save">保存发布设置</button>
    </header>

    <div class="workflow-grid">
      <section v-for="flow in [draft.mechanical, draft.electrical]" :key="flow.code" class="pdm-panel workflow-card">
        <div class="workflow-card-title">
          <div><h2>{{ flow.name }}</h2><p>当前模板 v{{ flow.version }}</p></div>
          <span>{{ flow.code === 'mechanical-release' ? '标准件 / 非标件与图纸' : '电气BOM' }}</span>
        </div>
        <ol class="workflow-steps">
          <li v-for="(step, index) in flow.steps" :key="step.stage">
            <b>{{ index + 1 }}</b>
            <label>节点名称<input v-model.trim="step.name" maxlength="120"></label>
            <label>审批人来源<input :value="sourceLabel(step.assigneeSource)" disabled></label>
          </li>
        </ol>
      </section>
    </div>

    <section class="pdm-panel emergency-rule">
      <div><h2>紧急代批</h2><p>事业部经理仅可替代当前节点，必须填写原因；代批后仍进入下一审批节点，不会跳过整条流程。</p></div>
      <strong>事业部经理</strong>
    </section>

    <section class="pdm-panel material-approval-rule">
      <div><h2>料号申请审批</h2><p>标准件由申请工程师发起；所选标准化角色共享待办池，任意一人处理后立即关闭其他人的待办。非标件不走本流程，在图纸+BOM终审完成时自动生成料号。</p></div>
      <div class="material-approval-roles">
        <label><input v-model="materialApproverRoleCodes" type="checkbox" value="ProcessReviewer"> 标准化工程师</label>
        <label><input v-model="materialApproverRoleCodes" type="checkbox" value="Approver"> 标准化主管</label>
      </div>
    </section>

    <section class="pdm-panel change-reason-settings">
      <header><div><h2>增补/变更原因</h2><p>创建增补发布包时可多选；此处维护可选原因类型。</p></div><button type="button" class="pdm-secondary-action" @click="reasonTypes.push('')">新增原因</button></header>
      <div class="change-reason-list">
        <label v-for="(_, index) in reasonTypes" :key="index"><span>原因 {{ index + 1 }}</span><input v-model.trim="reasonTypes[index]" maxlength="50"><button type="button" class="pdm-text-action is-danger" :disabled="reasonTypes.length === 1" @click="reasonTypes.splice(index, 1)">删除</button></label>
      </div>
    </section>
  </section>
</template>

<style scoped>
.approval-workflow-settings{min-height:0;overflow:auto}.workflow-heading{display:flex;align-items:flex-start;justify-content:space-between;gap:20px;margin-bottom:12px}.workflow-heading h1{margin:4px 0;font-size:18px}.workflow-heading p,.workflow-card p,.emergency-rule p,.change-reason-settings p{margin:0;color:var(--pdm-muted)}.workflow-grid{display:grid;grid-template-columns:repeat(2,minmax(0,1fr));gap:12px}.workflow-card{padding:14px}.workflow-card-title,.emergency-rule,.change-reason-settings>header{display:flex;align-items:center;justify-content:space-between;gap:16px}.workflow-card-title h2,.emergency-rule h2,.change-reason-settings h2{margin:0 0 4px;font-size:14px}.workflow-card-title>span,.emergency-rule>strong{padding:4px 8px;border-radius:999px;background:var(--pdm-blue-soft);color:var(--pdm-blue)}.workflow-steps{display:grid;gap:8px;margin:14px 0 0;padding:0;list-style:none}.workflow-steps li{display:grid;grid-template-columns:24px minmax(140px,1fr) minmax(180px,1fr);align-items:end;gap:8px;padding:9px;border:1px solid var(--pdm-border);border-radius:7px;background:var(--pdm-surface-soft)}.workflow-steps b{align-self:center;display:grid;place-items:center;width:22px;height:22px;border-radius:50%;background:var(--pdm-blue);color:#fff}.workflow-steps label{display:grid;gap:4px;color:var(--pdm-muted)}.workflow-steps input{min-width:0;height:30px;border:1px solid var(--pdm-border);border-radius:5px;background:#fff;padding:0 8px;color:var(--pdm-text)}.workflow-steps input:disabled{background:#f4f6f8}.emergency-rule,.change-reason-settings{margin-top:12px;padding:14px}.change-reason-list{display:grid;grid-template-columns:repeat(3,minmax(0,1fr));gap:8px;margin-top:12px}.change-reason-list label{display:grid;grid-template-columns:minmax(0,1fr) auto;gap:4px;color:var(--pdm-muted)}.change-reason-list label>span{grid-column:1}.change-reason-list input{grid-column:1;height:30px;border:1px solid var(--pdm-border);border-radius:5px;padding:0 8px}.change-reason-list button{grid-column:2;grid-row:2}@media(max-width:1000px){.workflow-grid{grid-template-columns:1fr}.workflow-steps li{grid-template-columns:24px 1fr}.workflow-steps li label{grid-column:2}.change-reason-list{grid-template-columns:1fr}}
</style>
<style scoped>
.material-approval-rule{display:flex;align-items:center;justify-content:space-between;gap:18px;margin-top:12px;padding:14px}.material-approval-rule h2{margin:0 0 4px;font-size:14px}.material-approval-rule p{margin:0;color:var(--pdm-muted)}.material-approval-roles{display:flex;gap:14px;white-space:nowrap}.material-approval-roles label{display:flex;align-items:center;gap:5px}
</style>
