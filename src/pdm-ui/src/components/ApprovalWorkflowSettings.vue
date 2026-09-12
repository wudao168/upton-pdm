<script setup lang="ts">
import { reactive, ref, watch } from 'vue'
import type { ApprovalAssigneeSource, ApprovalStage, ApprovalWorkflowStepTemplate, FormalSupplementPolicies, PdmSystemSettings, ReleaseApprovalSettings } from '../types'

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
    validationPlan: { code: 'validation-plan', name: '验证计划审批', version: 1, steps: [
      { stage: 'MechanicalEngineer', name: '编制人自检', assigneeSource: 'Submitter' },
      { stage: 'MainDesigner', name: '主设审核', assigneeSource: 'ProjectDesignLead' },
      { stage: 'MechanicalSupervisor', name: '机械主管批准', assigneeSource: 'PrimaryUnitManager' },
    ] },
    emergencySubstituteRoleCode: 'BusinessUnitManager',
  }
}

const draft = reactive<ReleaseApprovalSettings>(fallback())
const formalSupplementPolicies = reactive<FormalSupplementPolicies>({ standard: { maximumCount: 2, validDays: null }, electrical: { maximumCount: 2, validDays: null } })
const reasonGroups = [
  { category: '正式补充', reasons: ['正式补充'] },
  { category: '物料问题', reasons: ['交期不满足', '物料下单晚', '买错物料', '物料漏买'] },
  { category: '图纸问题', reasons: ['图纸漏下', '图纸错误'] },
  { category: '设计问题', reasons: ['设计变更', '设计错误'] },
  { category: '客户原因', reasons: ['客户需求变更', '客户信息输入错误', '客户未及时确认', '客户未及时提供产品'] },
  { category: '其他', reasons: ['填写具体原因'] },
]
const fixedReasonTypes = reasonGroups.flatMap(group => group.category === '正式补充'
  ? ['正式补充']
  : group.category === '其他' ? ['其他'] : group.reasons.map(reason => `${group.category} / ${reason}`))
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
    validationPlan: { ...(settings.validationPlan ?? fallback().validationPlan!), steps: (settings.validationPlan ?? fallback().validationPlan!).steps.map(cloneStep) },
    emergencySubstituteRoleCode: settings.emergencySubstituteRoleCode,
  }
}
function copyFrom(settings?: ReleaseApprovalSettings) {
  Object.assign(draft, cloneSettings(settings ?? fallback()))
}
watch(() => props.settings.approvalWorkflows, copyFrom, { immediate: true, deep: true })
watch(() => props.settings.formalSupplementPolicies, value => {
  const source = value ?? { standard: { maximumCount: 2, validDays: null }, electrical: { maximumCount: 2, validDays: null } }
  formalSupplementPolicies.standard = { maximumCount: source.standard.maximumCount ?? null, validDays: source.standard.validDays ?? null }
  formalSupplementPolicies.electrical = { maximumCount: source.electrical.maximumCount ?? null, validDays: source.electrical.validDays ?? null }
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
  await props.onSave({
    ...props.settings,
    approvalWorkflows: cloneSettings(draft),
    materialCodeApproval: { version: props.settings.materialCodeApproval?.version ?? 1, approverRoleCodes: materialApproverRoleCodes.value },
    releaseChangeReasonTypes: fixedReasonTypes,
    formalSupplementPolicies: {
      standard: { ...formalSupplementPolicies.standard },
      electrical: { ...formalSupplementPolicies.electrical },
    },
  })
}

function toggleUnlimited(kind: keyof FormalSupplementPolicies, field: 'maximumCount' | 'validDays', unlimited: boolean) {
  formalSupplementPolicies[kind][field] = unlimited ? null : field === 'maximumCount' ? 2 : 30
}
</script>

<template>
  <section class="pdm-project-manager approval-workflow-settings" aria-label="审批流程设置">
    <header class="workflow-heading">
      <div>
        <div class="pdm-breadcrumb">系统管理 <span>/</span> 审批流程</div>
        <h1>审批流程</h1>
        <p>审批人按提交人的主部门和上级部门负责人动态解析；提交后固化账号和模板版本，组织调整不影响在途审批。</p>
      </div>
      <button type="button" class="pdm-primary-action" :disabled="pending" @click="save">保存审批设置</button>
    </header>

    <div class="workflow-grid">
      <section v-for="flow in [draft.mechanical, draft.electrical, draft.validationPlan!]" :key="flow.code" class="pdm-panel workflow-card">
        <div class="workflow-card-title">
          <div><h2>{{ flow.name }}</h2><p>当前模板 v{{ flow.version }}</p></div>
          <span>{{ flow.code === 'mechanical-release' ? '标准件 / 非标件与图纸' : flow.code === 'electrical-release' ? '电气BOM' : '项目验证计划' }}</span>
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
      <header><div><h2>增补/变更原因与正式补充规则</h2><p>工程师必须选择到具体原因；“其他”必须填写说明。原因编码固定，确保历史统计口径一致。</p></div></header>
      <div class="formal-supplement-policy-grid">
        <section v-for="item in [{ key: 'standard', label: '标准件BOM' }, { key: 'electrical', label: '电气BOM' }]" :key="item.key" class="formal-supplement-policy-card">
          <strong>{{ item.label }}</strong>
          <label>正式补充次数<input v-model.number="formalSupplementPolicies[item.key as keyof FormalSupplementPolicies].maximumCount" type="number" min="0" max="99" :disabled="formalSupplementPolicies[item.key as keyof FormalSupplementPolicies].maximumCount == null"><span>次</span></label>
          <label class="policy-unlimited"><input type="checkbox" :checked="formalSupplementPolicies[item.key as keyof FormalSupplementPolicies].maximumCount == null" @change="toggleUnlimited(item.key as keyof FormalSupplementPolicies, 'maximumCount', ($event.target as HTMLInputElement).checked)">不限次数</label>
          <label>首次正式发布后<input v-model.number="formalSupplementPolicies[item.key as keyof FormalSupplementPolicies].validDays" type="number" min="1" max="3650" :disabled="formalSupplementPolicies[item.key as keyof FormalSupplementPolicies].validDays == null"><span>天内</span></label>
          <label class="policy-unlimited"><input type="checkbox" :checked="formalSupplementPolicies[item.key as keyof FormalSupplementPolicies].validDays == null" @change="toggleUnlimited(item.key as keyof FormalSupplementPolicies, 'validDays', ($event.target as HTMLInputElement).checked)">不限时间</label>
        </section>
      </div>
      <div class="change-reason-list">
        <section v-for="group in reasonGroups" :key="group.category"><strong>{{ group.category }}</strong><span v-for="reason in group.reasons" :key="reason">{{ reason }}</span></section>
      </div>
    </section>
  </section>
</template>

<style scoped>
.approval-workflow-settings{min-height:0;overflow:auto}.workflow-heading{display:flex;align-items:flex-start;justify-content:space-between;gap:20px;margin-bottom:12px}.workflow-heading h1{margin:4px 0;font-size:18px}.workflow-heading p,.workflow-card p,.emergency-rule p,.change-reason-settings p{margin:0;color:var(--pdm-muted)}.workflow-grid{display:grid;grid-template-columns:repeat(2,minmax(0,1fr));gap:12px}.workflow-card{padding:14px}.workflow-card-title,.emergency-rule,.change-reason-settings>header{display:flex;align-items:center;justify-content:space-between;gap:16px}.workflow-card-title h2,.emergency-rule h2,.change-reason-settings h2{margin:0 0 4px;font-size:14px}.workflow-card-title>span,.emergency-rule>strong{padding:4px 8px;border-radius:999px;background:var(--pdm-blue-soft);color:var(--pdm-blue)}.workflow-steps{display:grid;gap:8px;margin:14px 0 0;padding:0;list-style:none}.workflow-steps li{display:grid;grid-template-columns:24px minmax(140px,1fr) minmax(180px,1fr);align-items:end;gap:8px;padding:9px;border:1px solid var(--pdm-border);border-radius:7px;background:var(--pdm-surface-soft)}.workflow-steps b{align-self:center;display:grid;place-items:center;width:22px;height:22px;border-radius:50%;background:var(--pdm-blue);color:#fff}.workflow-steps label{display:grid;gap:4px;color:var(--pdm-muted)}.workflow-steps input{min-width:0;height:30px;border:1px solid var(--pdm-border);border-radius:5px;background:#fff;padding:0 8px;color:var(--pdm-text)}.workflow-steps input:disabled{background:#f4f6f8}.emergency-rule,.change-reason-settings{margin-top:12px;padding:14px}.formal-supplement-policy-grid{display:grid;grid-template-columns:repeat(2,minmax(0,1fr));gap:10px;margin-top:12px}.formal-supplement-policy-card{display:grid;grid-template-columns:auto minmax(170px,1fr) auto;align-items:center;gap:8px 12px;padding:10px;border:1px solid var(--pdm-border);border-radius:7px;background:var(--pdm-surface-soft)}.formal-supplement-policy-card>strong{grid-row:1/3}.formal-supplement-policy-card label{display:flex;align-items:center;gap:6px;color:var(--pdm-muted)}.formal-supplement-policy-card label>input[type=number]{width:82px;height:30px;border:1px solid var(--pdm-border);border-radius:5px;padding:0 7px}.formal-supplement-policy-card .policy-unlimited{white-space:nowrap}.change-reason-list{display:grid;grid-template-columns:repeat(3,minmax(0,1fr));gap:8px;margin-top:12px}.change-reason-list section{display:flex;align-items:center;flex-wrap:wrap;gap:6px;padding:9px;border:1px solid var(--pdm-border);border-radius:7px}.change-reason-list section>strong{width:100%}.change-reason-list section>span{padding:3px 7px;border-radius:999px;background:var(--pdm-blue-soft);color:var(--pdm-blue)}@media(max-width:1000px){.workflow-grid,.formal-supplement-policy-grid{grid-template-columns:1fr}.workflow-steps li{grid-template-columns:24px 1fr}.workflow-steps li label{grid-column:2}.change-reason-list{grid-template-columns:1fr}}
</style>
<style scoped>
.material-approval-rule{display:flex;align-items:center;justify-content:space-between;gap:18px;margin-top:12px;padding:14px}.material-approval-rule h2{margin:0 0 4px;font-size:14px}.material-approval-rule p{margin:0;color:var(--pdm-muted)}.material-approval-roles{display:flex;gap:14px;white-space:nowrap}.material-approval-roles label{display:flex;align-items:center;gap:5px}
</style>
