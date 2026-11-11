<script setup lang="ts">
import UserSelect from './UserSelect'
import { computed, reactive, ref, watch } from 'vue'
import { ElTabs, ElTabPane } from 'element-plus'
import WorkflowNotificationSettings from './WorkflowNotificationSettings.vue'
import ApprovalFlowCanvas from './ApprovalFlowCanvas.vue'
import ApprovalConditionEditor from './ApprovalConditionEditor.vue'
import ApprovalCopyRecipientsEditor from './ApprovalCopyRecipientsEditor.vue'
import ProjectBonusSettings from './ProjectBonusSettings.vue'
import { incompleteCopy } from '../workflowNotifications'
import { unifiedExpense, expenseNodeDefaults } from '../expenseWorkflow'
import { normalizeGraph, graphNodes, findBranch, locateNode, makeCondition, graphId, moveGraphNode, removeGraphNode, syncGraphSteps } from '../approvalFlowGraph'
import type { ApprovalFlowNode, ApprovalFlowBranch } from '../types'
import type { ApprovalAssigneeSource, ApprovalStage, ApprovalWorkflowStepTemplate, ApprovalWorkflowTemplate, FormalSupplementPolicies, OrganizationDirectory, OrganizationUnit, PdmSystemSettings, ReleaseApprovalSettings, RolePermissionSettings } from '../types'

const props = defineProps<{
  token?: string
  companyId?: string
  settings: PdmSystemSettings
  roles?: RolePermissionSettings[]
  directory?: OrganizationDirectory
  pending: boolean
  onSave: (settings: PdmSystemSettings) => Promise<PdmSystemSettings>
}>()

const activeTab = ref('standard-release')
const isExpense = (flow: ApprovalWorkflowTemplate) => flow.code.startsWith('expense-')
const selectedNodes = reactive<Record<string, string>>({})

function selectedNode(flow: ApprovalWorkflowTemplate) {
  const key = selectedNodes[flow.code]
  if (key && (findBranch(flow,key) || locateNode(flow.layout??[],key)?.node.kind==='Condition')) return key
  if (key?.startsWith('copy:') && flow.steps.some(step => step.copyOnApproval && 'copy:' + step.stage === key)) return key
  if (key === 'notification' && props.token && props.companyId) return key
  return flow.steps.find(step => step.stage === key)?.stage ?? flow.steps[1]?.stage ?? flow.steps[0]?.stage ?? ''
}

function describeStep(step: ApprovalWorkflowStepTemplate) {
  if (step.assigneeSource === 'ParallelDepartments') return (step.parallelDepartments ?? []).map(d => activeUnits.value.find(u => u.id === d.unitId)?.name ?? '请选择部门').join(' / ') + ' · 并行全部通过'
  if (step.assigneeSource === 'Role') return step.roleCodes?.map(roleName).join('、') || '请选择角色'
  if (step.assigneeSource === 'FixedUser') return activeUsers.value.find(user => user.username === step.fixedAssignee)?.displayName || '请选择人员'
  if (step.assigneeSource === 'DepartmentMembers') return activeUnits.value.find(unit => unit.id === step.fixedUnitId)?.name || '请选择部门'
  if (step.assigneeSource === 'FixedUnitManager') {
    const unit = activeUnits.value.find(unit => unit.id === step.fixedUnitId)
    return unit ? unit.name + '负责人' : '请选择部门'
  }
  return sourceLabel(step.assigneeSource)
}

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
      { stage: 'MainDesigner', name: '主设计审核', assigneeSource: 'ProjectDesignLead' },
      { stage: 'MechanicalSupervisor', name: '机械主管批准', assigneeSource: 'PrimaryUnitManager' },
    ] },
    electrical: { code: 'electrical-release', name: '电气发布审批', version: 1, steps: [
      { stage: 'HardwareEngineer', name: '硬件工程师自检', assigneeSource: 'Submitter' },
      { stage: 'HardwareSupervisor', name: '硬件主管审核', assigneeSource: 'PrimaryUnitManager' },
      { stage: 'StandardizationSupervisor', name: '标准化主管批准', assigneeSource: 'ParentUnitManager' },
    ] },
    validationPlan: { code: 'validation-plan', name: '验证计划审批', version: 1, steps: [
      { stage: 'MechanicalEngineer', name: '编制人自检', assigneeSource: 'Submitter' },
      { stage: 'MainDesigner', name: '主设计审核', assigneeSource: 'ProjectDesignLead' },
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
const flows = computed(() => [draft.standard!, draft.nonStandard!, draft.electrical, draft.validationPlan!, draft.materialCodeApplication!, draft.expense!])
const nodeDefaults: ApprovalWorkflowStepTemplate[] = [
  { stage: 'MainDesigner', name: '主设计审核', assigneeSource: 'ProjectDesignLead' },
  { stage: 'MechanicalSupervisor', name: '机械主管批准', assigneeSource: 'PrimaryUnitManager' },
  { stage: 'HardwareSupervisor', name: '硬件主管审核', assigneeSource: 'PrimaryUnitManager' },
  { stage: 'StandardizationSupervisor', name: '标准化主管批准', assigneeSource: 'ParentUnitManager' },
  { stage: 'MechanicalEngineer', name: '机械工程师审核', assigneeSource: 'ProjectDesignLead' },
  { stage: 'HardwareEngineer', name: '硬件工程师审核', assigneeSource: 'PrimaryUnitManager' },
]
const formalSupplementPolicies = reactive<FormalSupplementPolicies>({ standard: { maximumCount: 2, validDays: null }, electrical: { maximumCount: 2, validDays: null } })
const defaultReasonGroups = [
  { category: '正式补充', reasons: ['正式补充'] },
  { category: '物料问题', reasons: ['交期不满足', '物料下单晚', '买错物料', '物料漏买'] },
  { category: '图纸问题', reasons: ['图纸漏下', '图纸错误'] },
  { category: '设计问题', reasons: ['设计变更', '设计错误'] },
  { category: '客户原因', reasons: ['客户需求变更', '客户信息输入错误', '客户未及时确认', '客户未及时提供产品'] },
  { category: '其他', reasons: ['填写具体原因'] },
]
const reasonGroups = reactive(defaultReasonGroups.map(group => ({ ...group, reasons: [...group.reasons] })))
const reasonLabels = reactive<Record<string, string>>({})
const reasonKey = (category: string, reason: string) => category === '正式补充' || category === '其他' ? category : `${category} / ${reason}`
watch(() => [props.settings.releaseChangeReasonTypes, props.settings.releaseChangeReasonLabels] as const, ([types, value]) => {
  if (saving.value) return
  for (const key of Object.keys(reasonLabels)) delete reasonLabels[key]
  for (const group of reasonGroups) {
    group.reasons = types == null ? [...defaultReasonGroups.find(item => item.category === group.category)!.reasons]
      : types.filter(key => key === group.category || key.startsWith(group.category + ' / ')).map(key => key === group.category ? (group.category === '其他' ? '填写具体原因' : group.category) : key.slice(group.category.length + 3))
  }
  for (const group of reasonGroups) for (const reason of group.reasons) {
    const key = reasonKey(group.category, reason)
    reasonLabels[key] = value?.[key] ?? reason
  }
}, { immediate: true, deep: true })
function addReason(group: { category: string; reasons: string[] }) {
  const special = group.category === '正式补充' || group.category === '其他'
  if (special && group.reasons.length) return
  const reason = special ? group.category === '其他' ? '填写具体原因' : group.category : `custom-${Date.now()}-${Math.random().toString(36).slice(2, 8)}`
  group.reasons.push(reason)
  reasonLabels[reasonKey(group.category, reason)] = special ? reason : '新增原因'
}
function removeReason(group: { category: string; reasons: string[] }, reason: string) {
  group.reasons.splice(group.reasons.indexOf(reason), 1)
  delete reasonLabels[reasonKey(group.category, reason)]
}
function moveReason(group: { reasons: string[] }, index: number, offset: -1 | 1) {
  const target = index + offset
  if (target < 0 || target >= group.reasons.length) return
  const [reason] = group.reasons.splice(index, 1)
  group.reasons.splice(target, 0, reason!)
}
const stageByValue: Record<number, ApprovalStage> = {
  1: 'ProcessReview', 2: 'Approval', 10: 'MechanicalEngineer', 20: 'MainDesigner',
  30: 'MechanicalSupervisor', 40: 'HardwareEngineer', 50: 'HardwareSupervisor', 60: 'StandardizationSupervisor',
}
const sourceByValue: Record<number, ApprovalAssigneeSource> = {
  0: 'Submitter', 1: 'ProjectDesignLead', 2: 'FixedUser', 3: 'PrimaryUnitManager', 4: 'ParentUnitManager', 5: 'FixedUnitManager', 6: 'Role', 7: 'DepartmentMembers', 8: 'ParallelDepartments',
}
function cloneStep(step: ApprovalWorkflowStepTemplate): ApprovalWorkflowStepTemplate {
  const stage = typeof step.stage === 'number' ? stageByValue[step.stage] : step.stage
  const assigneeSource = typeof step.assigneeSource === 'number' ? sourceByValue[step.assigneeSource] : step.assigneeSource
  const copy = { ...step, emptyFallbackUser: step.emptyFallbackUser || null, copyOnApproval: step.copyOnApproval ? { ...step.copyOnApproval } : null, policy: step.policy ? { ...step.policy } : null, stage, assigneeSource, conditionRole: step.conditionRole || null, unlessRole: !!step.unlessRole }
  copy.parallelDepartments = step.parallelDepartments?.map(d => ({ ...d }))
  if (copy.copyOnApproval?.recipients) copy.copyOnApproval.recipients=copy.copyOnApproval.recipients.map(row=>({...row}))
  copy.conditions = step.conditions ? { match: step.conditions.match, rules: step.conditions.rules.map(rule => ({ ...rule })) }
    : null
  if (assigneeSource !== 'FixedUser') delete copy.fixedAssignee
  if (assigneeSource !== 'FixedUnitManager' && assigneeSource !== 'DepartmentMembers') delete copy.fixedUnitId
  if (assigneeSource === 'Role') copy.roleCodes = [...(step.roleCodes ?? [])]
  else delete copy.roleCodes
  return copy
}
function cloneSettings(settings: ReleaseApprovalSettings): ReleaseApprovalSettings {
  const standard = settings.standard ?? { ...settings.mechanical, code: 'standard-release', name: '标准件BOM审批' }
  const nonStandard = settings.nonStandard ?? { ...settings.mechanical, code: 'nonstandard-release', name: '非标件BOM与图纸审批' }
  const expense = unifiedExpense(settings)
  const result: ReleaseApprovalSettings = {
    mechanical: { ...settings.mechanical, steps: settings.mechanical.steps.map(cloneStep) },
    standard: { ...standard, steps: standard.steps.map(cloneStep) },
    nonStandard: { ...nonStandard, steps: nonStandard.steps.map(cloneStep) },
    electrical: { ...settings.electrical, steps: settings.electrical.steps.map(cloneStep) },
    validationPlan: { ...(settings.validationPlan ?? fallback().validationPlan!), steps: (settings.validationPlan ?? fallback().validationPlan!).steps.map(cloneStep) },
    materialCodeApplication: { ...(settings.materialCodeApplication ?? materialWorkflowDefault()), steps: (settings.materialCodeApplication ?? materialWorkflowDefault()).steps.map(cloneStep) },
    expense: { ...expense, steps: expense.steps.map(cloneStep) },
    emergencySubstituteRoleCode: settings.emergencySubstituteRoleCode,
  }
  for (const key of ['standard','nonStandard','electrical','validationPlan','materialCodeApplication','expense'] as const) result[key] = normalizeGraph(result[key]!)
  return result
}
function copyFrom(settings?: ReleaseApprovalSettings) {
  if (saving.value) return
  validationError.value = ''
  Object.assign(draft, cloneSettings(settings ?? fallback()))
}
watch(() => props.settings.approvalWorkflows, copyFrom, { immediate: true, deep: true })
watch(() => props.settings.formalSupplementPolicies, value => {
  if (saving.value) return
  const source = value ?? { standard: { maximumCount: 2, validDays: null }, electrical: { maximumCount: 2, validDays: null } }
  formalSupplementPolicies.standard = { maximumCount: source.standard.maximumCount ?? null, validDays: source.standard.validDays ?? null }
  formalSupplementPolicies.electrical = { maximumCount: source.electrical.maximumCount ?? null, validDays: source.electrical.validDays ?? null }
}, { immediate: true, deep: true })


const sourceLabel = (source: ApprovalAssigneeSource) => ({
  Submitter: '当前提交人',
  ProjectDesignLead: '项目主设计',
  FixedUser: '指定人员',
  PrimaryUnitManager: '提交人主部门负责人',
  ParentUnitManager: '上级部门负责人',
  FixedUnitManager: '指定部门负责人',
  Role: '指定角色',
  DepartmentMembers: '指定部门成员',
  ParallelDepartments: '多部门并行审批',
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
  step.parallelDepartments = step.assigneeSource === 'ParallelDepartments' ? [] : undefined
  step.requireAllApprovers = false
  step.emptyFallbackUser = null
  step.fixedAssignee = null
  step.fixedUnitId = null
  step.roleCodes = step.assigneeSource === 'Role' ? [] : undefined
  validationError.value = ''
}

async function save(code: string) {
  if (props.pending || saving.value) return
  validationError.value = ''
  for (const flow of flows.value.filter(item => item.code === code)) {
    if (flow.steps.length<2) { validationError.value='流程至少需要一个审核节点。'; return }
    for (const node of graphNodes(flow.layout??[])) for (const branch of node.branches??[]) {
      if (branch.conditions && (!branch.conditions.rules.length || branch.conditions.rules.some(rule=>!['Empty','NotEmpty'].includes(rule.operator)&&!String(rule.value??'').trim()))) {
        validationError.value='请为每个条件分支填写完整的判断规则。'; selectedNodes[code]=branch.id; return
      }
    }
    for (const step of flow.steps) {
      if (step.conditions?.rules.some(rule => !['Empty','NotEmpty'].includes(rule.operator) && !String(rule.value ?? '').trim())) validationError.value = '请填写每条流转条件的比较值。'
      if (step.assigneeSource === 'Role' && !step.roleCodes?.length) validationError.value = `${flow.name}的“${step.name}”请至少选择一个审批角色。`
      if (step.assigneeSource === 'FixedUser' && !activeUsers.value.some(user => user.username === step.fixedAssignee))
        validationError.value = `${flow.name}的“${step.name}”请选择有效的指定人员。`
      if (step.assigneeSource === 'FixedUnitManager' && (!activeUnits.value.some(unit => unit.id === step.fixedUnitId) || !unitManager(step.fixedUnitId!)))
        validationError.value = `${flow.name}的“${step.name}”请选择已配置有效负责人的部门。`
      if (step.assigneeSource === 'DepartmentMembers' && !activeUnits.value.some(unit => unit.id === step.fixedUnitId)) validationError.value = `请为“${step.name}”选择有效部门。`
      const cc = step.copyOnApproval
      if (cc && (!cc.message.trim() || (cc.recipients ? !cc.recipients.length || cc.recipients.some(incompleteCopy) : ['Role','Department','User'].includes(cc.source) && !cc.target))) {
        validationError.value = '请填写抄送消息并选择有效的接收对象。'
        selectedNodes[code] = 'copy:' + step.stage
        return
      }
      if (validationError.value) { selectedNodes[code] = step.stage; return }
    }
  }
  saving.value = true
  try {
    const workflowKey = ({ 'standard-release': 'standard', 'nonstandard-release': 'nonStandard', 'electrical-release': 'electrical', 'validation-plan': 'validationPlan', 'material-code-application': 'materialCodeApplication', 'expense-approval': 'expense' } as Record<string, 'standard' | 'nonStandard' | 'electrical' | 'validationPlan' | 'materialCodeApplication' | 'expense'>)[code]
    const settings = { ...props.settings }
    if (workflowKey) {
      settings.approvalWorkflows = { ...(props.settings.approvalWorkflows ?? fallback()), [workflowKey]: cloneSettings(draft)[workflowKey] }
    } else {
      if (Object.values(reasonLabels).some(label => !label.trim() || label.trim().length > 60)) {
        validationError.value = '具体原因文字不能为空，且不能超过60个字符。'
        return
      }
      settings.releaseChangeReasonTypes = reasonGroups.flatMap(group => group.reasons.map(reason => reasonKey(group.category, reason)))
      settings.releaseChangeReasonLabels = Object.fromEntries(Object.entries(reasonLabels).map(([key, label]) => [key, label.trim()]))
      settings.formalSupplementPolicies = { standard: { ...formalSupplementPolicies.standard }, electrical: { ...formalSupplementPolicies.electrical } }
    }
    const saved = await props.onSave(settings)
    if (workflowKey) draft[workflowKey] = cloneSettings(saved.approvalWorkflows ?? fallback())[workflowKey]!

  } finally { saving.value = false }
}

function addStep(flow: ApprovalWorkflowTemplate, nodes:ApprovalFlowNode[]=flow.layout!, index=nodes.length, kind:'Approval'|'Condition'='Approval') {
  if (kind==='Condition') { const node=makeCondition();nodes.splice(index,0,node);selectedNodes[flow.code]=node.branches![0]!.id;return }
  if (flow.steps.length >= (isExpense(flow) ? 21 : 6)) return
  const step = (isExpense(flow) ? expenseNodeDefaults : nodeDefaults).find(item => !flow.steps.some(current => current.stage === item.stage))
  if (step) {
    flow.steps.push({ ...step })
    nodes.splice(index,0,{id:step.stage,kind:'Approval',name:step.name,stage:step.stage})
    syncGraphSteps(flow)
    selectedNodes[flow.code] = step.stage
  }
}
function moveStep(flow: ApprovalWorkflowTemplate, index: number, offset: number) {
  shiftNode(flow,flow.steps[index]!.stage,offset)
}
function shiftNode(flow:ApprovalWorkflowTemplate,id:string,offset:number) {
  id=nodeId(flow,id)
  const found=locateNode(flow.layout??[],id);if(!found)return
  const target=found.index+offset;if(target<0||target>=found.nodes.length)return
  moveGraphNode(flow,id,found.nodes,target+(offset>0?1:0))
}
const selectedBranch=(flow:ApprovalWorkflowTemplate)=>findBranch(flow,selectedNodes[flow.code]??'')
const selectedCondition=(flow:ApprovalWorkflowTemplate)=>{ const node=locateNode(flow.layout??[],selectedNodes[flow.code]??'')?.node;return node?.kind==='Condition'?node:undefined }
function addBranch(flow:ApprovalWorkflowTemplate,node:ApprovalFlowNode) { const branch:ApprovalFlowBranch={id:graphId('branch'),name:'条件'+node.branches!.length,conditions:{match:'All',rules:[]},nodes:[]};node.branches!.splice(node.branches!.length-1,0,branch);selectedNodes[flow.code]=branch.id }
function shiftBranch(node:ApprovalFlowNode,index:number,offset:number) { const target=index+offset;if(target<0||target>=node.branches!.length-1)return;const [branch]=node.branches!.splice(index,1);node.branches!.splice(target,0,branch!) }
function removeBranch(flow:ApprovalWorkflowTemplate,branch:ApprovalFlowBranch) { const parent=graphNodes(flow.layout??[]).find(node=>node.branches?.includes(branch));if(!parent||!branch.conditions||parent.branches!.length<=2)return;const stages=new Set(graphNodes(branch.nodes).map(node=>node.stage));flow.steps=flow.steps.filter(step=>!stages.has(step.stage));parent.branches=parent.branches!.filter(item=>item!==branch);selectedNodes[flow.code]=parent.id }
function nodeId(flow:ApprovalWorkflowTemplate,key:string) { return graphNodes(flow.layout??[]).find(node=>node.stage===key)?.id??key }
function removeNode(flow:ApprovalWorkflowTemplate,id:string) { removeGraphNode(flow,nodeId(flow,id));selectedNodes[flow.code]=flow.steps[1]?.stage??flow.steps[0]!.stage }
function destinations(flow:ApprovalWorkflowTemplate) {return [{id:'root',name:'主流程（分支汇合后）'},...graphNodes(flow.layout??[]).flatMap(node=>(node.branches??[]).map(branch=>({id:branch.id,name:node.name+' / '+branch.name})))]}
function moveTo(flow:ApprovalWorkflowTemplate,id:string,target:string) {const nodes=target==='root'?flow.layout:findBranch(flow,target)?.nodes;if(nodes)moveGraphNode(flow,nodeId(flow,id),nodes,nodes.length)}

function toggleUnlimited(kind: keyof FormalSupplementPolicies, field: 'maximumCount' | 'validDays', unlimited: boolean) {
  formalSupplementPolicies[kind][field] = unlimited ? null : field === 'maximumCount' ? 2 : 30
}
</script>

<template>
  <section class="pdm-project-manager approval-workflow-settings" aria-label="审批流程设置">
    <p v-if="validationError" class="pdm-inline-error" role="alert">{{ validationError }}</p>

    <el-tabs v-model="activeTab" class="pdm-settings-tabs workflow-tabs">
      <el-tab-pane name="project-bonus" label="项目奖金规则" lazy><ProjectBonusSettings v-if="activeTab === 'project-bonus'" :token="token" :company-id="companyId" /></el-tab-pane>
      <el-tab-pane v-for="flow in flows" :key="flow.code" :name="flow.code" :label="({ 'standard-release': '标准件审批', 'nonstandard-release': '非标件审批', 'electrical-release': '电气件审批' } as Record<string, string>)[flow.code] || flow.name">
      <div class="workflow-layout">
      <section class="pdm-panel workflow-card" :aria-label="flow.name">
        <div class="workflow-card-title">
          <div><h2>{{ flow.name }}</h2><p>当前模板 v{{ flow.version }}</p></div>
          <button v-if="token && companyId" type="button" class="pdm-secondary-action completion-copy-action" :aria-pressed="selectedNode(flow)==='notification'" @click="selectedNodes[flow.code]='notification'">完成时抄送</button>
          <button type="button" class="pdm-primary-action" :disabled="pending || saving" @click="save(flow.code)">保存审批设置</button>
          <span>{{ isExpense(flow) ? '现场采购 / 维修补订' : flow.code === 'standard-release' ? '标准件BOM' : flow.code === 'nonstandard-release' ? '非标件BOM与图纸' : flow.code === 'electrical-release' ? '电气件BOM' : flow.code === 'material-code-application' ? '物料料号申请' : '项目验证计划' }}</span>
        </div>
        <p v-if="flow.code === 'material-code-application'" class="workflow-material-note">用于标准件和电气件BOM物料申请，末节点通过后才生成料号并进入同步；角色节点由该节点任一指定角色的人员处理。</p>
        <div class="designer-body">
        <ApprovalFlowCanvas :flow="flow" :selected="selectedNode(flow)" :descriptions="flow.steps.map(describeStep)" :role-name="roleName"
          :disabled="pending || saving" :show-notification="!isExpense(flow) && !!token && !!companyId"
          @select="selectedNodes[flow.code] = $event" @insert="(nodes,index,kind)=>addStep(flow,nodes,index,kind)" @move-node="(id,nodes,index)=>moveGraphNode(flow,id,nodes,index)" @add-branch="addBranch(flow,$event)" @shift-branch="(node,index,offset)=>shiftBranch(node,index,offset)" />
        <section class="designer-inspector" aria-label="节点属性">
        <WorkflowNotificationSettings v-if="token && companyId" v-show="selectedNode(flow) === 'notification'"
          :token="token" :company-id="companyId" :flow-code="flow.code" :directory="directory" :roles="roles" :active="activeTab === flow.code" />
        <h3 v-if="selectedNode(flow) !== 'notification'">{{ selectedBranch(flow)?'分支条件设置':selectedCondition(flow)?'条件节点设置':selectedNode(flow).startsWith('copy:') ? '抄送设置' : '审核节点设置' }}</h3>
        <fieldset v-if="selectedBranch(flow)" class="graph-inspector" :disabled="pending||saving">
          <label>分支名称<input v-model="selectedBranch(flow)!.name" aria-label="分支名称" maxlength="120" /></label>
          <ApprovalConditionEditor v-if="selectedBranch(flow)!.conditions" :step="selectedBranch(flow)!" :flow-code="flow.code" :disabled="pending||saving" :directory="directory" :roles="roleOptions" />
          <p v-else>其他分支都不满足时执行本分支，可在其下方的“＋”中添加审核节点。</p>
          <button v-if="selectedBranch(flow)!.conditions" type="button" class="pdm-secondary-action" :disabled="(graphNodes(flow.layout??[]).find(node=>node.branches?.includes(selectedBranch(flow)!))?.branches?.length??0)<=2" title="至少保留一个条件分支和其他情况；可在条件节点设置中删除整个分支节点" @click="removeBranch(flow,selectedBranch(flow)!)">删除本分支及其中节点</button>
        </fieldset>
        <fieldset v-if="selectedCondition(flow)" class="graph-inspector" :disabled="pending||saving">
          <label>条件节点名称<input v-model="selectedCondition(flow)!.name" aria-label="条件节点名称" maxlength="120" /></label>
          <p>从左到右判断，只执行首个符合条件的分支；均不符合则执行其他情况。分支结束后继续汇合点之后的流程。</p>
          <div class="workflow-node-actions"><button type="button" class="pdm-secondary-action" @click="shiftNode(flow,selectedNode(flow),-1)">上移条件节点</button><button type="button" class="pdm-secondary-action" @click="shiftNode(flow,selectedNode(flow),1)">下移条件节点</button><button type="button" class="pdm-secondary-action" @click="removeNode(flow,selectedNode(flow))">删除条件及其中节点</button></div>
          <label>移动到<select aria-label="条件节点移动到" value="" @change="moveTo(flow,selectedNode(flow),($event.target as HTMLSelectElement).value);($event.target as HTMLSelectElement).value='' "><option value="" disabled>请选择位置</option><option v-for="target in destinations(flow)" :key="target.id" :value="target.id">{{target.name}}</option></select></label>
        </fieldset>
        <template v-for="step in flow.steps" :key="'copy:' + step.stage">
          <fieldset v-if="step.copyOnApproval && selectedNode(flow) === 'copy:' + step.stage" class="copy-options" :disabled="pending || saving">
            <legend>{{ step.name }}通过后</legend>
            <ApprovalCopyRecipientsEditor :model-value="step.copyOnApproval.recipients??[{source:step.copyOnApproval.source,target:step.copyOnApproval.target}]" :directory="directory" :roles="roleOptions" :company-id="companyId" :disabled="pending||saving" @update:model-value="step.copyOnApproval!.recipients=$event" />
            <label>站内消息<textarea v-model.trim="step.copyOnApproval.message" rows="3" maxlength="500" /></label>
            <button type="button" class="pdm-secondary-action" @click="step.copyOnApproval = null; selectedNodes[flow.code] = step.stage">移除抄送</button>
          </fieldset>
        </template>
        <ol class="workflow-steps">
          <li v-for="(step, index) in flow.steps" v-show="selectedNode(flow) === step.stage" :key="step.stage">
            <b>{{ index + 1 }}</b>
            <label>节点名称<input v-model.trim="step.name" maxlength="120" required :disabled="pending || saving"></label>
            <div class="workflow-node-assignee">
            <label>审批人来源<input v-if="index === 0" :value="sourceLabel(step.assigneeSource)" disabled>
              <select v-else v-model="step.assigneeSource" aria-label="审批人来源" :disabled="pending || saving" @change="changeSource(step)">
                <option value="Role">指定角色</option>
                <option value="DepartmentMembers">指定部门成员</option>
                <option value="ParallelDepartments">多部门并行审批</option>
                <option value="ProjectDesignLead">项目主设计</option>
                <option value="PrimaryUnitManager">提交人主部门负责人</option>
                <option value="ParentUnitManager">上级部门负责人</option>
                <option value="FixedUser">指定人员</option>
                <option value="FixedUnitManager">指定部门负责人</option>
              </select>
            </label>
            <label v-if="step.assigneeSource === 'FixedUser'" class="workflow-target">指定人员
              <UserSelect v-model="step.fixedAssignee" aria-label="指定人员" :disabled="pending || saving">
                <option :value="null" disabled>请选择人员</option>
                <option v-if="step.fixedAssignee && !activeUsers.some(user => user.username === step.fixedAssignee)" :value="step.fixedAssignee" disabled>原指定人员已停用或不存在，请重新选择</option>
                <option v-for="user in activeUsers" :key="user.username" :value="user.username">{{ user.displayName }}</option>
              </UserSelect>
            </label>
            <div v-if="step.assigneeSource === 'ParallelDepartments'" class="workflow-target">
              <p>每个部门独立处理，全部部门通过后流转；同一人负责多个部门时逐部门确认。</p>
              <label v-for="(branch, b) in step.parallelDepartments ?? []" :key="b">并行部门 {{ b + 1 }}
                <select v-model="branch.unitId" aria-label="并行审批部门"><option value="">请选择部门</option><option v-for="unit in activeUnits" :key="unit.id" :value="unit.id">{{ unit.name }}</option></select>
                <label><input type="checkbox" v-model="branch.requireAll" />该部门全员通过</label>
                <button type="button" class="pdm-secondary-action" @click="step.parallelDepartments!.splice(b, 1)">移除部门</button>
              </label>
              <button type="button" class="pdm-secondary-action" @click="(step.parallelDepartments ??= []).push({ unitId: '', requireAll: false })">添加并行部门</button>
            </div>
            <label v-if="step.assigneeSource === 'FixedUnitManager' || step.assigneeSource === 'DepartmentMembers'" class="workflow-target">指定部门
              <select v-model="step.fixedUnitId" aria-label="指定部门" :disabled="pending || saving">
                <option :value="null" disabled>请选择部门</option>
                <option v-if="step.fixedUnitId && !activeUnits.some(unit => unit.id === step.fixedUnitId)" :value="step.fixedUnitId" disabled>原指定部门已停用或不存在，请重新选择</option>
                <option v-for="unit in activeUnits" :key="unit.id" :value="unit.id" :disabled="step.assigneeSource === 'FixedUnitManager' && !unitManager(unit.id)">{{ step.assigneeSource === 'DepartmentMembers' ? unit.name : unitLabel(unit) }}</option>
              </select>
            </label>
            <details v-if="step.assigneeSource === 'Role'" class="workflow-target workflow-role-choices">
              <summary>审批角色：{{ step.roleCodes?.map(roleName).join('、') || '请选择角色' }}</summary>
              <div><label v-for="role in roleOptions" :key="role.role"><input v-model="step.roleCodes" type="checkbox" :value="role.role" :disabled="pending || saving">{{ role.name }}</label></div>
            </details>
            <label v-if="step.assigneeSource === 'Role' || step.assigneeSource === 'DepartmentMembers'" class="workflow-target">多人审批方式
              <select :value="step.requireAllApprovers ? 'all' : 'any'" aria-label="多人审批方式" :disabled="pending || saving"
                @change="step.requireAllApprovers = ($event.target as HTMLSelectElement).value === 'all'">
                <option value="any">或签（任一人通过）</option><option value="all">会签（所有人通过）</option>
              </select>
            </label>
            <details v-if="index > 0" class="policy-settings">
              <summary>审批操作与字段权限</summary>
              <label><input type="checkbox" :checked="step.policy?.requireOpinion" :disabled="pending || saving"
                @change="step.policy = { ...step.policy, requireOpinion: ($event.target as HTMLInputElement).checked }">同意时必须填写意见</label>
              <label v-if="!isExpense(flow) && flow.code !== 'material-code-application' && !['Role','DepartmentMembers'].includes(step.assigneeSource)"><input type="checkbox" :checked="step.policy?.disableTransfer" :disabled="pending || saving"
                @change="step.policy = { ...step.policy, disableTransfer: ($event.target as HTMLInputElement).checked }">禁止转交</label>
              <label><input type="checkbox" :checked="step.policy?.editBusinessNote" :disabled="pending || saving"
                @change="step.policy = { ...step.policy, editBusinessNote: ($event.target as HTMLInputElement).checked }">可修改{{ isExpense(flow) ? '申请原因' : flow.code === 'validation-plan' ? '计划备注' : flow.code === 'material-code-application' ? '料号申请说明' : '发布备注' }}</label>
            </details>
            <details v-if="step.assigneeSource === 'Role' || step.assigneeSource === 'DepartmentMembers'" class="workflow-target">
              <summary>高级设置 · 无有效处理人时</summary>
              <label>替补处理人<select v-model="step.emptyFallbackUser" aria-label="无处理人时" :disabled="pending || saving">
                <option :value="null">阻止提交并提示原因</option>
                <option v-for="user in activeUsers" :key="user.username" :value="user.username">转给{{ user.displayName }}</option>
              </select></label>
            </details>
            <div v-if="index > 0" class="workflow-node-actions">
              <button type="button" class="pdm-secondary-action" :disabled="pending || saving" @click="step.copyOnApproval ??= { source: 'Submitter', target: '', message: step.name + '已通过。' }; selectedNodes[flow.code] = 'copy:' + step.stage">{{ step.copyOnApproval ? '编辑抄送' : '添加抄送' }}</button>
              <button type="button" class="pdm-secondary-action" :disabled="pending || saving" aria-label="上移节点" @click="moveStep(flow, index, -1)">上移</button>
              <button type="button" class="pdm-secondary-action" :disabled="pending || saving" aria-label="下移节点" @click="moveStep(flow, index, 1)">下移</button>
              <button type="button" class="pdm-secondary-action" :disabled="pending || saving || flow.steps.length <= 2" aria-label="删除节点" @click="removeNode(flow,step.stage)">删除</button>
            </div>
            <label v-if="index>0">移动到<select aria-label="审核节点移动到" value="" :disabled="pending||saving" @change="moveTo(flow,step.stage,($event.target as HTMLSelectElement).value);($event.target as HTMLSelectElement).value='' "><option value="" disabled>请选择位置</option><option v-for="target in destinations(flow)" :key="target.id" :value="target.id">{{target.name}}</option></select></label>
            </div>
          </li>
        </ol>
        </section></div>
        <div v-if="!isExpense(flow)" class="workflow-emergency">
          <label>紧急代批角色<select :value="emergencyValue(flow)" :aria-label="`${flow.name}紧急代批角色`" :disabled="pending || saving" @change="changeEmergency(flow, ($event.target as HTMLSelectElement).value)">
            <option value="">未启用</option><option v-if="flow.emergencyRoleCodes == null && ['standard-release','nonstandard-release','electrical-release'].includes(flow.code)" value="__legacy__">默认（事业部经理）</option>
            <option v-for="role in roleOptions" :key="role.role" :value="role.role">{{ role.name }}</option>
          </select></label><small>只处理当前节点，必须填写原因。</small>
        </div>
        <footer v-if="!isExpense(flow)" class="workflow-card-footer">
          <small>首节点为提交人自检，至少保留一个后续审批节点。</small>
          <button type="button" class="pdm-secondary-action" :disabled="pending || saving || flow.steps.length >= 6" @click="addStep(flow)">添加审批节点</button>
        </footer>
      </section>
      </div>
      </el-tab-pane>
      <el-tab-pane name="change-rules" label="增补/变更规则">
    <section class="pdm-panel change-reason-settings">
      <header><div><h2>增补/变更原因与正式补充规则</h2><p>工程师必须选择到具体原因；“其他”必须填写说明。具体原因文字可编辑，保存后用于新操作的原因选择；业务编码和历史记录保持原值。</p></div><button type="button" class="pdm-primary-action" :disabled="pending || saving" @click="save('change-rules')">保存规则</button></header>
      <div class="formal-supplement-policy-grid">
        <section v-for="item in [{ key: 'standard', label: '标准件BOM' }, { key: 'electrical', label: '电气件BOM' }]" :key="item.key" class="formal-supplement-policy-card">
          <strong>{{ item.label }}</strong>
          <label class="policy-value"><span>正式补充次数</span><input v-model.number="formalSupplementPolicies[item.key as keyof FormalSupplementPolicies].maximumCount" type="number" min="0" max="99" :disabled="formalSupplementPolicies[item.key as keyof FormalSupplementPolicies].maximumCount == null"><span>次</span></label>
          <label class="policy-unlimited"><input type="checkbox" :checked="formalSupplementPolicies[item.key as keyof FormalSupplementPolicies].maximumCount == null" @change="toggleUnlimited(item.key as keyof FormalSupplementPolicies, 'maximumCount', ($event.target as HTMLInputElement).checked)">不限次数</label>
          <label class="policy-value"><span>首次正式发布后</span><input v-model.number="formalSupplementPolicies[item.key as keyof FormalSupplementPolicies].validDays" type="number" min="1" max="3650" :disabled="formalSupplementPolicies[item.key as keyof FormalSupplementPolicies].validDays == null"><span>天内</span></label>
          <label class="policy-unlimited"><input type="checkbox" :checked="formalSupplementPolicies[item.key as keyof FormalSupplementPolicies].validDays == null" @change="toggleUnlimited(item.key as keyof FormalSupplementPolicies, 'validDays', ($event.target as HTMLInputElement).checked)">不限时间</label>
        </section>
      </div>
      <div class="change-reason-list">
        <section v-for="group in reasonGroups" :key="group.category"><strong>{{ group.category }}</strong><div class="change-reason-rows"><div v-for="(reason, index) in group.reasons" :key="reason" class="change-reason-item"><input v-model="reasonLabels[reasonKey(group.category, reason)]" :aria-label="`${group.category}具体原因 ${reason}`" maxlength="60" :disabled="pending || saving" /><div class="change-reason-actions"><button type="button" class="pdm-secondary-action" :aria-label="`上移${group.category}原因 ${reasonLabels[reasonKey(group.category, reason)]}`" :disabled="pending || saving || index === 0" @click="moveReason(group, index, -1)">上移</button><button type="button" class="pdm-secondary-action" :aria-label="`下移${group.category}原因 ${reasonLabels[reasonKey(group.category, reason)]}`" :disabled="pending || saving || index === group.reasons.length - 1" @click="moveReason(group, index, 1)">下移</button><button type="button" class="pdm-text-danger" :aria-label="`删除${group.category}原因 ${reasonLabels[reasonKey(group.category, reason)]}`" :disabled="pending || saving" @click="removeReason(group, reason)">删除</button></div></div></div><div class="change-reason-footer"><button v-if="!['正式补充','其他'].includes(group.category) || !group.reasons.length" type="button" class="pdm-primary-action" :disabled="pending || saving" @click="addReason(group)">新增原因</button></div></section>
      </div>
    </section>
      </el-tab-pane>
    </el-tabs>
  </section>
</template>

<style scoped>
.graph-inspector{display:grid;gap:14px;border:0;padding:14px 0;min-width:0}.graph-inspector>label{display:grid;gap:6px}.graph-inspector input,.graph-inspector select{max-width:100%;min-width:0;height:30px}
.approval-workflow-settings{min-height:0;overflow:auto}.workflow-card p,.change-reason-settings p{margin:0;color:var(--pdm-muted)}.workflow-tabs{flex:none}.workflow-tabs :deep(.el-tabs__content){overflow:visible}.workflow-tabs :deep(.el-tab-pane){height:auto}.workflow-card{padding:14px}.workflow-card-title,.change-reason-settings>header{display:flex;align-items:center;justify-content:space-between;gap:16px}.workflow-card-title h2,.change-reason-settings h2{margin:0 0 4px;font-size:14px}.workflow-card-title>button{margin-left:auto}.workflow-card-title>span{padding:4px 8px;border-radius:999px;background:var(--pdm-blue-soft); color:var(--pdm-text)}.workflow-steps{display:grid;gap:8px;margin:14px 0 0;padding:0;list-style:none}.workflow-steps li{display:grid;grid-template-columns:24px minmax(0,1fr) minmax(0,1.8fr);align-items:end;gap:8px;padding:9px;border:1px solid var(--pdm-border);border-radius:7px;background:var(--pdm-surface-soft)}.workflow-steps b{align-self:center;display:grid;place-items:center;width:22px;height:22px;border-radius:50%;background:var(--pdm-blue);color:var(--pdm-surface)}.workflow-steps label{display:grid;gap:4px;color:var(--pdm-muted)}.workflow-steps input,.workflow-steps select{min-width:0;height:30px;border:1px solid var(--pdm-border);border-radius:5px;background:var(--pdm-surface);padding:0 8px;color:var(--pdm-text)}.workflow-steps input:disabled{background:var(--pdm-surface-muted)}.change-reason-settings{padding:14px}.formal-supplement-policy-grid{display:grid;grid-template-columns:repeat(2,minmax(0,1fr));gap:10px;margin-top:12px}.formal-supplement-policy-card{display:grid;grid-template-columns:auto minmax(170px,1fr) auto;align-items:center;gap:8px 12px;padding:10px;border:1px solid var(--pdm-border);border-radius:7px;background:var(--pdm-surface-soft)}.formal-supplement-policy-card>strong{grid-row:1/3}.formal-supplement-policy-card label{display:flex;align-items:center;gap:6px;color:var(--pdm-muted)}.formal-supplement-policy-card label>input[type=number]{width:82px;height:30px;border:1px solid var(--pdm-border);border-radius:5px;padding:0 7px}.formal-supplement-policy-card .policy-value{display:grid;grid-template-columns:100px 82px auto;gap:12px}.formal-supplement-policy-card .policy-unlimited{white-space:nowrap}.change-reason-list{display:grid;grid-template-columns:repeat(3,minmax(0,1fr));gap:8px;margin-top:12px}.change-reason-list section{display:flex;flex-direction:column;align-items:stretch;gap:8px;height:230px;min-width:0;box-sizing:border-box;padding:9px;border:1px solid var(--pdm-border);border-radius:7px}.change-reason-list section>strong{width:100%}.change-reason-list{align-items:start}.change-reason-rows{display:flex;flex:1;min-height:0;flex-direction:column;gap:8px;overflow:auto}.change-reason-rows>.change-reason-item{flex-shrink:0}.change-reason-footer{display:flex;align-items:center;min-height:30px;flex-shrink:0}.change-reason-item{display:grid;grid-template-columns:minmax(0,1fr) auto;align-items:center;gap:8px;width:100%;min-width:0}.change-reason-actions{display:flex;align-items:center;gap:6px;white-space:nowrap}.change-reason-actions button{padding:0 6px;min-height:26px}.change-reason-item>input{min-width:0;width:100%;max-width:100%;height:30px;box-sizing:border-box;padding:3px 7px;border:1px solid var(--pdm-border);border-radius:5px;background:var(--pdm-surface);color:var(--pdm-text)}@media(max-width:1000px){.formal-supplement-policy-grid{grid-template-columns:1fr}.workflow-steps li{grid-template-columns:24px 1fr}.workflow-steps li label{grid-column:2}.change-reason-list{grid-template-columns:1fr}}
.workflow-emergency{display:flex;align-items:flex-end;flex-wrap:wrap;gap:8px;margin-top:10px;padding-top:10px;border-top:1px solid var(--pdm-border)}.workflow-emergency label{display:grid;gap:4px;color:var(--pdm-muted);min-width:160px;flex:1}.workflow-emergency select{height:30px;min-width:0;border:1px solid var(--pdm-border);border-radius:5px;background:var(--pdm-surface);color:var(--pdm-text);padding:0 8px}.workflow-emergency small,.workflow-material-note{color:var(--pdm-muted);font-size:11px}.workflow-role-choices{width:100%;font-size:12px;color:var(--pdm-muted)}.workflow-role-choices>div{display:flex;flex-wrap:wrap;gap:8px 14px;padding-top:8px;max-height:120px;overflow:auto}.workflow-role-choices>div label{display:flex;align-items:center;gap:4px}.workflow-role-choices input[type=checkbox]{height:auto}.workflow-material-note{margin-top:8px!important;line-height:1.6}
</style>

<style scoped>
 .copy-options{display:grid;gap:12px;border:0;margin:12px 0 0;padding:0}.copy-options label{display:grid;gap:4px}.copy-options select,.copy-options textarea{max-width:100%;padding:6px;border:1px solid var(--pdm-border);background:var(--pdm-surface);color:var(--pdm-text);border-radius:5px}.copy-options legend{color:var(--pdm-muted);padding-bottom:8px}
.approval-workflow-settings .workflow-layout{display:block}
.designer-body{display:grid;grid-template-columns:minmax(220px,0.9fr) minmax(300px,1.1fr);gap:16px;margin-top:14px;align-items:start}
.designer-inspector{min-width:0;padding:16px;border:1px solid var(--pdm-border);border-radius:8px;background:var(--pdm-surface)}
.designer-inspector .policy-settings label{display:flex;align-items:center;gap:6px;margin-top:7px}.designer-inspector .policy-settings input{height:auto}.route-settings label{margin-top:8px}.designer-inspector h3{margin:0;font-size:14px}.designer-inspector .workflow-steps{margin-top:10px}
.designer-inspector .workflow-steps li{display:flex;flex-direction:column;align-items:stretch;background:transparent;border:0;padding:0}
.designer-inspector .workflow-steps li>b{display:none}.designer-inspector .workflow-node-assignee{display:grid;grid-template-columns:minmax(0,1fr);align-items:stretch;gap:10px}
.designer-inspector .workflow-node-assignee>*{grid-column:1;max-width:100%;min-width:0}.designer-inspector .workflow-node-actions{flex-wrap:wrap}
.designer-inspector .workflow-node-actions{order:2;justify-content:flex-start;margin-top:8px}
.designer-inspector .workflow-notifications{margin:0;padding:0;border:0}
@container(max-width:650px){.designer-body{grid-template-columns:minmax(0,1fr)}}
</style>

<style scoped>
.workflow-layout{display:grid;grid-template-columns:repeat(2,minmax(0,1fr));align-items:stretch;gap:5px;min-width:0}
.workflow-layout>.workflow-card{container-type:inline-size}
.workflow-layout>.workflow-notification-card{min-width:0;margin:0;padding:14px;border:1px solid var(--pdm-border);border-radius:7px;background:var(--pdm-surface)}
.workflow-layout .workflow-card-title{flex-wrap:wrap;gap:8px}
.workflow-layout .workflow-card-title>div{min-width:0}
@container(max-width:650px){.workflow-steps li{grid-template-columns:24px minmax(0,1fr)}.workflow-steps li>label,.workflow-node-assignee{grid-column:2}.workflow-node-assignee>label{flex-basis:100%}.workflow-node-actions{margin-left:auto}.workflow-emergency{flex-wrap:wrap}}
.workflow-card{min-width:0}.workflow-card-title>button{margin-left:auto}.workflow-card-title>span{flex-shrink:0}.workflow-steps select{width:100%}.workflow-node-assignee{display:flex;flex-wrap:wrap;min-width:0;align-items:flex-end;gap:6px}.workflow-node-assignee>label{min-width:0;flex:1}.workflow-node-assignee>.workflow-target{flex-basis:100%;order:1}.workflow-node-actions{display:flex;flex:0 0 auto;justify-content:flex-end;gap:6px}.workflow-node-actions button{min-height:26px;padding:0 8px}.workflow-card-footer{display:flex;align-items:center;justify-content:space-between;gap:8px;margin-top:10px}.workflow-card-footer small{color:var(--pdm-muted)}
@media(max-width:1000px){.workflow-node-assignee{grid-column:2}}
.workflow-steps li:has(.workflow-target)>label{align-self:start}
</style>
