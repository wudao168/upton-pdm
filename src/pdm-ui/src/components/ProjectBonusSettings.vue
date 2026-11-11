<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { getProjectBonusSettings, saveProjectBonusSettings } from '../api'
import { bonusId, bonusPhase } from '../projectBonus'
import type { BonusSettingsView, BonusSettings } from '../projectBonus'
import BonusIndicatorEditor from './BonusIndicatorEditor.vue'
import ProjectBonusApprovalEditor from './ProjectBonusApprovalEditor.vue'
import { ElMessage } from '../statusMessage'
import '../styles/project-bonus.css'
const props = defineProps<{ token?: string; companyId?: string }>()
const view = ref<BonusSettingsView | null>(null), draft = ref<BonusSettings | null>(null)
const error = ref(''), busy = ref(false), selectedRole = ref(''), section = ref('roles')
const role = computed(() => draft.value?.roles.find(r => r.id === selectedRole.value))
let sequence = 0
async function load() {
  const seq = ++sequence; if (!props.token) return
  busy.value = true; error.value = ''; view.value = null; draft.value = null
  try { const result = await getProjectBonusSettings(props.token); if (seq !== sequence) return; view.value = result; draft.value = structuredClone(result.settings); selectedRole.value = result.settings.roles[0]?.id ?? '' }
  catch (e) { if (seq === sequence) error.value = e instanceof Error ? e.message : '奖金规则加载失败。' }
  finally { if (seq === sequence) busy.value = false }
}
async function save() {
  if (!props.token || !draft.value || busy.value) return
  busy.value = true; error.value = ''
  try { const result = await saveProjectBonusSettings(draft.value, props.token); view.value = result; draft.value = structuredClone(result.settings); ElMessage.success('奖金规则已保存，已提交的申请继续使用原版本。') }
  catch (e) { error.value = e instanceof Error ? e.message : '奖金规则保存失败。' }
  finally { busy.value = false }
}
function addRole() { if (!draft.value) return; const id = bonusId(); draft.value.roles.push({ id, name: '新岗位', phase: 'Shipment', suggestedWeight: 10, indicators: [] }); selectedRole.value = id }
function copyRole() { if (!draft.value || !role.value) return; const copy = JSON.parse(JSON.stringify(role.value)); copy.id = bonusId(); copy.name += '（副本）'; copy.indicators.forEach((i: { id: string }) => { i.id = bonusId() }); draft.value.roles.push(copy); selectedRole.value = copy.id }
function removeRole() { if (!draft.value || !role.value) return; draft.value.roles = draft.value.roles.filter(r => r.id !== selectedRole.value); selectedRole.value = draft.value.roles[0]?.id ?? '' }
watch([() => Boolean(props.token), () => props.companyId], load, { immediate: true })
</script>
<template>
  <section class="project-bonus bonus-settings" v-loading="busy" aria-label="项目奖金规则设置">
    <header class="bonus-heading"><div><h2>项目奖金规则</h2><p>岗位分阶段考核 · 评分部门独立配置 · 历史申请冻结模板</p></div><button class="pdm-primary-action" :disabled="busy || !draft" @click="save">保存规则</button></header>
    <p v-if="error" role="alert" class="bonus-error">{{ error }}</p>
    <template v-if="draft && view">
      <div class="bonus-policy"><span>规则版本 v{{ draft.version }}</span><label><input v-model="draft.enabled" type="checkbox" />启用奖金申请</label><span>奖金池沿用项目结算；发货 / 终验收各50%上限，扣减不转分</span></div>
      <p class="bonus-muted">预设参考多维项目评价、质量与证据管理原则，具体分值为可编辑的初始建议。首次启用前，请绑定本公司实际部门及负责人；保存不改变已发起的评分与审批。</p>
      <nav class="pdm-project-subtabs pdm-segmented bonus-tabs" role="tablist" aria-label="奖金规则分类"><button v-for="tab in [{ id: 'roles', name: '岗位考核' }, { id: 'project', name: '节点与预算' }, { id: 'departments', name: '评分部门' }, { id: 'flow', name: '审批与发放' }]" :key="tab.id" role="tab" :aria-selected="section === tab.id" :class="{ 'is-active': section === tab.id }" @click="section = tab.id">{{ tab.name }}</button></nav>
      <fieldset :disabled="busy">
        <div v-if="section === 'roles'" class="bonus-settings-layout">
          <aside><button class="bonus-role-option" v-for="r in draft.roles" :key="r.id" type="button" :class="{ selected: r.id === selectedRole }" @click="selectedRole = r.id"><strong>{{ r.name }}</strong><small>{{ bonusPhase(r.phase) }} · {{ r.indicators.length }}项指标</small></button><button type="button" class="pdm-secondary-action" @click="addRole">新增岗位模板</button></aside>
          <article v-if="role" class="bonus-card"><div class="bonus-form-grid"><label>岗位名称<input v-model="role.name" maxlength="80" /></label><label>适用阶段<select v-model="role.phase"><option value="Shipment">发货</option><option value="Acceptance">终验收</option></select></label><label>建议分配权重<el-input-number v-model="role.suggestedWeight" :min="0.01" :max="1000" :controls="false" :precision="2" /></label></div>
            <div class="bonus-actions"><button type="button" class="pdm-secondary-action" @click="copyRole">复制模板</button><button type="button" class="pdm-secondary-action" @click="removeRole">删除模板</button></div>
            <BonusIndicatorEditor v-model="role.indicators" :departments="draft.departments" />
          </article><p v-else>请添加岗位模板。</p>
        </div>
        <article v-if="section === 'project'" class="bonus-card"><h3>项目级评价</h3><p>初始权重：节点控制60%、预算控制40%。后续可编辑指标、分值、权重和评分部门。个人岗位评价在项目系数之外独立计算。</p><BonusIndicatorEditor v-model="draft.projectIndicators" :departments="draft.departments" /></article>
        <article v-if="section === 'departments'" class="bonus-card"><h3>评分 / 审批部门映射</h3><p>每个指标可以由多个部门按不同权重评分；部门内任一指定评分人可提交该部门意见。本人不参与自己的评分。</p><div class="bonus-table-scroll"><table><thead><tr><th>部门名称</th><th>实际组织部门</th><th>评分 / 审批人员</th><th>操作</th></tr></thead><tbody><tr v-for="(d, index) in draft.departments" :key="d.key"><td><input v-model="d.name" maxlength="80" /></td><td><select v-model="d.unitId"><option :value="null">请选择部门</option><option v-for="unit in view.departments" :key="unit.id" :value="unit.id">{{ unit.name }}</option></select></td><td><el-select v-model="d.reviewers" multiple filterable placeholder="选择该部门人员"><el-option v-for="user in view.users" :key="user.username" :value="user.username" :label="user.name" /></el-select></td><td><button class="pdm-secondary-action" @click="draft.departments.splice(index, 1)">移除</button></td></tr></tbody></table></div><button class="pdm-secondary-action" @click="draft.departments.push({ key: bonusId(), name: '新部门', unitId: null, reviewers: [] })">添加部门</button></article>
        <article v-if="section === 'flow'" class="bonus-card"><h3>项目奖金审批</h3><p>点击流程节点编辑属性，通过节点间的“＋”添加审核节点；可拖动节点或使用上移、下移调整顺序。至少保留一个审核节点，最多12个。</p>
          <ProjectBonusApprovalEditor v-model="draft.approvalNodes" :departments="draft.departments" :version="draft.version" :disabled="busy || !view.canEdit" />
          <label class="bonus-payment-department">发放登记部门<select v-model="draft.paymentDepartmentKey"><option v-for="d in draft.departments" :key="d.key" :value="d.key">{{ d.name }}</option></select></label>
        </article>
      </fieldset>
    </template>
  </section>
</template>
