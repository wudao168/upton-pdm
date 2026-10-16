<script setup lang="ts">
import { computed, reactive, ref, watch } from 'vue'
import type { ApprovalAssigneeSource, ApprovalStage, ApprovalWorkflowStepTemplate, ApprovalWorkflowTemplate, FormalSupplementPolicies, OrganizationDirectory, OrganizationUnit, PdmSystemSettings, ReleaseApprovalSettings, RolePermissionSettings } from '../types'

const props = defineProps<{
  settings: PdmSystemSettings
  roles?: RolePermissionSettings[]
  directory?: OrganizationDirectory
  pending: boolean
  onSave: (settings: PdmSystemSettings) => Promise<PdmSystemSettings>
}>()

function materialWorkflowDefault(): ApprovalWorkflowTemplate {
  return { code: 'material-code-application', name: '料号申请审批', version: props.settings.materialCodeApproval?.version ?? 1, steps: [
    { stage: 'MechanicalEngineer', name: '申请人自检', assigneeSource: 'Submitter' },
    { stage: 'StandardizationSupervisor', name: '标准化审核', assigneeSource: 'Role', roleCodes: [...(props.settings.materialCodeApproval?.approverRoleCodes ?? ['ProcessReviewer', 'Approver'])] },
  ] }
}
const roleOptions = computed(() => props.roles?.length ? props.roles : [
  { role: 'Administrator', name: '系统管理员' }, { role: 'Engineer', name: '工程师' },
  { role: 'ProcessReviewer', name: '标准化工程师' }, { role: 'Approver', name: '标准化主管' }, { role: 'BusinessUnitManager', name: '事业部经理' },
])
const roleName = (code: string) => roleOptions.value.find(role => role.role === code)?.name ?? code
function emergencyValue(flow: ApprovalWorkflowTemplate) {
  return flow.emergencyRoleCodes == null ? ['standard-release', 'nonstandard-release', 'electrical-release'].includes(flow.code) ? '__legacy__' : '' : flow.emergencyRoleCodes[0] || ''
}
function changeEmergency(flow: ApprovalWorkflowTemplate, value: string) { flow.emergencyRoleCodes = value === '__legacy__' ? null : value ? [value] : [] }
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
    materialCodeApplication: materialWorkflowDefault(),
    emergencySubstituteRoleCode: 'BusinessUnitManager',
  }
}

const draft = reactive<ReleaseApprovalSettings>(fallback())
const saving = ref(false)
const validationError = ref('')
const activeUsers = computed(() => (props.directory?.users ?? []).filter(user => user.isActive))
const activeUnits = computed(() => (props.directory?.units ?? []).filter(unit => unit.isActive))
const flows = computed(() => [draft.standard!, draft.nonStandard!, draft.electrical, draft.validationPlan!, draft.materialCodeApplication!])
const nodeDefaults: ApprovalWorkflowStepTemplate[] = [
  { stage: 'MainDesigner', name: '主设审核', assigneeSource: 'ProjectDesignLead' },
  { stage: 'MechanicalSupervisor', name: '机械主管批准', assigneeSource: 'PrimaryUnitManager' },
  { stage: 'HardwareSupervisor', name: '硬件主管审核', assigneeSource: 'PrimaryUnitManager' },
  { stage: 'StandardizationSupervisor', name: '标准化主管批准', assigneeSource: 'ParentUnitManager' },
  { stage: 'MechanicalEngineer', name: '机械工程师审核', assigneeSource: 'ProjectDesignLead' },
  { stage: 'HardwareEngineer', name: '硬件工程师审核', assigneeSource: 'PrimaryUnitManager' },
]
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
  0: 'Submitter', 1: 'ProjectDesignLead', 2: 'FixedUser', 3: 'PrimaryUnitManager', 4: 'ParentUnitManager', 5: 'FixedUnitManager', 6: 'Role',
}
function cloneStep(step: ApprovalWorkflowStepTemplate): ApprovalWorkflowStepTemplate {
  const stage = typeof step.stage === 'number' ? stageByValue[step.stage] : step.stage
  const assigneeSource = typeof step.assigneeSource === 'number' ? sourceByValue[step.assigneeSource] : step.assigneeSource
  const copy = { ...step, stage, assigneeSource }
  if (assigneeSource !== 'FixedUser') delete copy.fixedAssignee
  if (assigneeSource !== 'FixedUnitManager') delete copy.fixedUnitId
  if (assigneeSource === 'Role') copy.roleCodes = [...(step.roleCodes ?? [])]
  else delete copy.roleCodes
  return copy
}
function cloneSettings(settings: ReleaseApprovalSettings): ReleaseApprovalSettings {
  const standard = settings.standard ?? { ...settings.mechanical, code: 'standard-release', name: '标准件BOM审批' }
  const nonStandard = settings.nonStandard ?? { ...settings.mechanical, code: 'nonstandard-release', name: '非标件BOM与图纸审批' }
  return {
    mechanical: { ...settings.mechanical, steps: settings.mechanical.steps.map(cloneStep) },
    standard: { ...standard, steps: standard.steps.map(cloneStep) },
    nonStandard: { ...nonStandard, steps: nonStandard.steps.map(cloneStep) },
    electrical: { ...settings.electrical, steps: settings.electrical.steps.map(cloneStep) },
    validationPlan: { ...(settings.validationPlan ?? fallback().validationPlan!), steps: (settings.validationPlan ?? fallback().validationPlan!).steps.map(cloneStep) },
    materialCodeApplication: { ...(settings.materialCodeApplication ?? materialWorkflowDefault()), steps: (settings.materialCodeApplication ?? materialWorkflowDefault()).steps.map(cloneStep) },
    emergencySubstituteRoleCode: settings.emergencySubstituteRoleCode,
  }
}
function copyFrom(settings?: ReleaseApprovalSettings) {
  validationError.value = ''
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
  FixedUser: '指定人员',
  PrimaryUnitManager: '提交人主部门负责人',
  ParentUnitManager: '上级部门负责人',
  FixedUnitManager: '指定部门负责人',
  Role: '指定角色',
})[source]

function unitManager(unitId: string) {
  const username = props.directory?.managers.find(item => item.unitId === unitId)?.primaryManager
  return activeUsers.value.find(user => user.username.toLowerCase() === username?.toLowerCase())
}
function unitLabel(unit: OrganizationUnit) {
  const names = [unit.name]
  let parent = props.directory?.units.find(item => item.id === unit.parentUnitId)
  while (parent) { names.unshift(parent.name); parent = props.directory?.units.find(item => item.id === parent!.parentUnitId) }
  const company = props.directory?.organizations.find(item => item.id === unit.organizationId)?.name
  const manager = unitManager(unit.id)
  const context = [company, ...names.slice(0, -1)].filter(Boolean).join(' / ')
  return `${unit.name}（${manager ? '负责人：' + manager.displayName : '未配置有效负责人'}）${context ? ' · ' + context : ''}`
}
function changeSource(step: ApprovalWorkflowStepTemplate) {
  step.fixedAssignee = null
  step.fixedUnitId = null
  step.roleCodes = step.assigneeSource === 'Role' ? [] : undefined
  validationError.value = ''
}

async function save() {
  if (props.pending || saving.value) return
  validationError.value = ''
  for (const flow of flows.value) {
    for (const step of flow.steps) {
      if (step.assigneeSource === 'Role' && !step.roleCodes?.length) validationError.value = `${flow.name}的“${step.name}”请至少选择一个审批角色。`
      if (step.assigneeSource === 'FixedUser' && !activeUsers.value.some(user => user.username === step.fixedAssignee))
        validationError.value = `${flow.name}的“${step.name}”请选择有效的指定人员。`
      if (step.assigneeSource === 'FixedUnitManager' && (!activeUnits.value.some(unit => unit.id === step.fixedUnitId) || !unitManager(step.fixedUnitId!)))
        validationError.value = `${flow.name}的“${step.name}”请选择已配置有效负责人的部门。`
      if (validationError.value) return
    }
  }
  saving.value = true
  try {
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
  } finally { saving.value = false }
}

function addStep(flow: ApprovalWorkflowTemplate) {
  const step = nodeDefaults.find(item => !flow.steps.some(current => current.stage === item.stage))
  if (step) flow.steps.push({ ...step })
}
function moveStep(flow: ApprovalWorkflowTemplate, index: number, offset: number) {
  const target = index + offset
  if (index < 1 || target < 1 || target >= flow.steps.length) return
  const [step] = flow.steps.splice(index, 1)
  flow.steps.splice(target, 0, step!)
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
        <p>三类BOM、验证计划及料号申请分别设置审批节点、顺序和审批人来源，并在各自流程内设置紧急代批角色。流程启动时固化模板及审批人，组织调整不影响在途审批。</p>
      </div>
      <button type="button" class="pdm-primary-action" :disabled="pending || saving" @click="save">保存审批设置</button>
    </header>
    <p v-if="validationError" class="pdm-inline-error" role="alert">{{ validationError }}</p>

    <div class="workflow-grid">
      <section v-for="flow in flows" :key="flow.code" class="pdm-panel workflow-card" :aria-label="flow.name">
        <div class="workflow-card-title">
          <div><h2>{{ flow.name }}</h2><p>当前模板 v{{ flow.version }}</p></div>
          <span>{{ flow.code === 'standard-release' ? '标准件BOM' : flow.code === 'nonstandard-release' ? '非标件BOM与图纸' : flow.code === 'electrical-release' ? '电气BOM' : flow.code === 'material-code-application' ? '物料料号申请' : '项目验证计划' }}</span>
        </div>
        <p v-if="flow.code === 'material-code-application'" class="workflow-material-note">用于标准件和电气BOM物料申请，末节点通过后才生成料号并进入同步；角色节点由该节点任一指定角色的人员处理。</p>
        <ol class="workflow-steps">
          <li v-for="(step, index) in flow.steps" :key="step.stage">
            <b>{{ index + 1 }}</b>
            <label>节点名称<input v-model.trim="step.name" maxlength="120" required :disabled="pending || saving"></label>
            <div class="workflow-node-assignee">
            <label>审批人来源<input v-if="index === 0" :value="sourceLabel(step.assigneeSource)" disabled>
              <select v-else v-model="step.assigneeSource" aria-label="审批人来源" :disabled="pending || saving" @change="changeSource(step)">
                <option v-if="flow.code === 'material-code-application'" value="Role">指定角色</option>
                <option value="ProjectDesignLead">项目主设</option>
                <option value="PrimaryUnitManager">提交人主部门负责人</option>
                <option value="ParentUnitManager">上级部门负责人</option>
                <option value="FixedUser">指定人员</option>
                <option value="FixedUnitManager">指定部门负责人</option>
              </select>
            </label>
            <label v-if="step.assigneeSource === 'FixedUser'" class="workflow-target">指定人员
              <select v-model="step.fixedAssignee" aria-label="指定人员" :disabled="pending || saving">
                <option :value="null" disabled>请选择人员</option>
                <option v-if="step.fixedAssignee && !activeUsers.some(user => user.username === step.fixedAssignee)" :value="step.fixedAssignee" disabled>原指定人员已停用或不存在，请重新选择</option>
                <option v-for="user in activeUsers" :key="user.username" :value="user.username">{{ user.displayName }}（{{ user.username }}）</option>
              </select>
            </label>
            <label v-if="step.assigneeSource === 'FixedUnitManager'" class="workflow-target">指定部门
              <select v-model="step.fixedUnitId" aria-label="指定部门" :disabled="pending || saving">
                <option :value="null" disabled>请选择部门</option>
                <option v-if="step.fixedUnitId && !activeUnits.some(unit => unit.id === step.fixedUnitId)" :value="step.fixedUnitId" disabled>原指定部门已停用或不存在，请重新选择</option>
                <option v-for="unit in activeUnits" :key="unit.id" :value="unit.id" :disabled="!unitManager(unit.id)">{{ unitLabel(unit) }}</option>
              </select>
            </label>
            <details v-if="step.assigneeSource === 'Role'" class="workflow-target workflow-role-choices">
              <summary>审批角色：{{ step.roleCodes?.map(roleName).join('、') || '请选择角色' }}</summary>
              <div><label v-for="role in roleOptions" :key="role.role"><input v-model="step.roleCodes" type="checkbox" :value="role.role" :disabled="pending || saving">{{ role.name }}</label></div>
            </details>
            <div v-if="index > 0" class="workflow-node-actions">
              <button type="button" class="pdm-secondary-action" :disabled="pending || saving || index === 1" aria-label="上移节点" @click="moveStep(flow, index, -1)">上移</button>
              <button type="button" class="pdm-secondary-action" :disabled="pending || saving || index === flow.steps.length - 1" aria-label="下移节点" @click="moveStep(flow, index, 1)">下移</button>
              <button type="button" class="pdm-secondary-action" :disabled="pending || saving || flow.steps.length <= 2" aria-label="删除节点" @click="flow.steps.splice(index, 1)">删除</button>
            </div>
            </div>
          </li>
        </ol>
        <div class="workflow-emergency">
          <label>紧急代批角色<select :value="emergencyValue(flow)" :aria-label="`${flow.name}紧急代批角色`" :disabled="pending || saving" @change="changeEmergency(flow, ($event.target as HTMLSelectElement).value)">
            <option value="">未启用</option><option v-if="flow.emergencyRoleCodes == null && ['standard-release','nonstandard-release','electrical-release'].includes(flow.code)" value="__legacy__">默认（事业部经理）</option>
            <option v-for="role in roleOptions" :key="role.role" :value="role.role">{{ role.name }}</option>
          </select></label><small>只处理当前节点，必须填写原因。</small>
        </div>
        <footer class="workflow-card-footer">
          <small>首节点为提交人自检，至少保留一个后续审批节点。</small>
          <button type="button" class="pdm-secondary-action" :disabled="pending || saving || flow.steps.length >= 6" @click="addStep(flow)">添加审批节点</button>
        </footer>
      </section>
    </div>

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
.approval-workflow-settings{min-height:0;overflow:auto}.workflow-heading{display:flex;align-items:flex-start;justify-content:space-between;gap:20px;margin-bottom:12px}.workflow-heading h1{margin:4px 0;font-size:var(--pdm-font-size-heading)}.workflow-heading p,.workflow-card p,.change-reason-settings p{margin:0;color:var(--pdm-muted)}.workflow-grid{display:grid;grid-template-columns:repeat(2,minmax(0,1fr));gap:12px}.workflow-card{padding:14px}.workflow-card-title,.change-reason-settings>header{display:flex;align-items:center;justify-content:space-between;gap:16px}.workflow-card-title h2,.change-reason-settings h2{margin:0 0 4px;font-size:14px}.workflow-card-title>span{padding:4px 8px;border-radius:999px;background:var(--pdm-blue-soft);color:var(--pdm-blue)}.workflow-steps{display:grid;gap:8px;margin:14px 0 0;padding:0;list-style:none}.workflow-steps li{display:grid;grid-template-columns:24px minmax(0,1fr) minmax(0,1.8fr);align-items:end;gap:8px;padding:9px;border:1px solid var(--pdm-border);border-radius:7px;background:var(--pdm-surface-soft)}.workflow-steps b{align-self:center;display:grid;place-items:center;width:22px;height:22px;border-radius:50%;background:var(--pdm-blue);color:var(--pdm-surface)}.workflow-steps label{display:grid;gap:4px;color:var(--pdm-muted)}.workflow-steps input,.workflow-steps select{min-width:0;height:30px;border:1px solid var(--pdm-border);border-radius:5px;background:var(--pdm-surface);padding:0 8px;color:var(--pdm-text)}.workflow-steps input:disabled{background:var(--pdm-surface-muted)}.change-reason-settings{margin-top:12px;padding:14px}.formal-supplement-policy-grid{display:grid;grid-template-columns:repeat(2,minmax(0,1fr));gap:10px;margin-top:12px}.formal-supplement-policy-card{display:grid;grid-template-columns:auto minmax(170px,1fr) auto;align-items:center;gap:8px 12px;padding:10px;border:1px solid var(--pdm-border);border-radius:7px;background:var(--pdm-surface-soft)}.formal-supplement-policy-card>strong{grid-row:1/3}.formal-supplement-policy-card label{display:flex;align-items:center;gap:6px;color:var(--pdm-muted)}.formal-supplement-policy-card label>input[type=number]{width:82px;height:30px;border:1px solid var(--pdm-border);border-radius:5px;padding:0 7px}.formal-supplement-policy-card .policy-unlimited{white-space:nowrap}.change-reason-list{display:grid;grid-template-columns:repeat(3,minmax(0,1fr));gap:8px;margin-top:12px}.change-reason-list section{display:flex;align-items:center;flex-wrap:wrap;gap:6px;padding:9px;border:1px solid var(--pdm-border);border-radius:7px}.change-reason-list section>strong{width:100%}.change-reason-list section>span{padding:3px 7px;border-radius:999px;background:var(--pdm-blue-soft);color:var(--pdm-blue)}@media(max-width:1000px){.workflow-grid,.formal-supplement-policy-grid{grid-template-columns:1fr}.workflow-steps li{grid-template-columns:24px 1fr}.workflow-steps li label{grid-column:2}.change-reason-list{grid-template-columns:1fr}}
.workflow-emergency{display:flex;align-items:flex-end;flex-wrap:wrap;gap:8px;margin-top:10px;padding-top:10px;border-top:1px solid var(--pdm-border)}.workflow-emergency label{display:grid;gap:4px;color:var(--pdm-muted);min-width:160px;flex:1}.workflow-emergency select{height:30px;min-width:0;border:1px solid var(--pdm-border);border-radius:5px;background:var(--pdm-surface);color:var(--pdm-text);padding:0 8px}.workflow-emergency small,.workflow-material-note{color:var(--pdm-muted);font-size:11px}.workflow-role-choices{width:100%;font-size:12px;color:var(--pdm-muted)}.workflow-role-choices>div{display:flex;flex-wrap:wrap;gap:8px 14px;padding-top:8px;max-height:120px;overflow:auto}.workflow-role-choices>div label{display:flex;align-items:center;gap:4px}.workflow-role-choices input[type=checkbox]{height:auto}.workflow-material-note{margin-top:8px!important;line-height:1.6}
</style>

<style scoped>
.workflow-card{min-width:0}.workflow-card-title>span{flex-shrink:0}.workflow-steps select{width:100%}.workflow-node-assignee{display:flex;flex-wrap:wrap;min-width:0;align-items:flex-end;gap:6px}.workflow-node-assignee>label{min-width:0;flex:1}.workflow-node-assignee>.workflow-target{flex-basis:100%;order:1}.workflow-node-actions{display:flex;flex:0 0 auto;justify-content:flex-end;gap:6px}.workflow-node-actions button{min-height:26px;padding:0 8px}.workflow-card-footer{display:flex;align-items:center;justify-content:space-between;gap:8px;margin-top:10px}.workflow-card-footer small{color:var(--pdm-muted)}
@media(max-width:1000px){.workflow-node-assignee{grid-column:2}}
.workflow-steps li:has(.workflow-target)>label{align-self:start}
</style>
