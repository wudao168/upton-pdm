<script setup lang="ts">
import DepartmentName from './DepartmentName.vue'
import PersonName from './PersonName.vue'
import { useUserDisplayName as usePersonDisplayName } from '../userDisplay'
const displayUserName = usePersonDisplayName()
import { computed, ref, watch } from 'vue'
import { getProjectBonus, saveProjectBonus, scoreProjectBonus, actProjectBonus, payProjectBonus, getProjectBonusHistory } from '../api'
import { bonusId, bonusMoney as money, bonusPhase, bonusStatus } from '../projectBonus'
import type { BonusView, BonusPerson, BonusEvidence, BonusScore, BonusIndicator, BonusBook, BonusTask } from '../projectBonus'
import { uppercaseMoney } from '../projectBudget'
import { ElMessage } from '../statusMessage'
import '../styles/project-bonus.css'
const props = defineProps<{ projectId: string; token?: string }>()
const emit = defineEmits<{ dirty: [boolean]; updated: [] }>()
const data = ref<BonusView | null>(null), busy = ref(false), error = ref(''), phase = ref('Shipment'), editing = ref(false)
const people = ref<BonusPerson[]>([]), evidence = ref<BonusEvidence[]>([]), milestone = ref(''), scoreDraft = ref<BonusScore[]>([]), comment = ref('')
const paymentDate = ref(''), voucher = ref(''), baseline = ref(''), history = ref<BonusBook[]>([]), showHistory = ref(false)
const current = computed(() => data.value?.applications.find(v => v.application.phase === phase.value))
const app = computed(() => current.value?.application)
const settings = computed(() => editing.value ? data.value?.settings : app.value?.template ?? data.value?.settings)
const roles = computed(() => settings.value?.roles.filter(r => r.phase === phase.value) ?? [])
const draftState = () => JSON.stringify([people.value, evidence.value, milestone.value, scoreDraft.value])
const dirty = computed(() => !!baseline.value && baseline.value !== draftState())
watch(dirty, v => emit('dirty', v))
const username = (name: string) => data.value?.users.find(u => u.username === name)?.name ?? name
const roleName = (id: string) => settings.value?.roles.find(r => r.id === id)?.name ?? '岗位'
const personName = (id: string) => { const p = (editing.value ? people.value : app.value?.people ?? []).find(p => p.id === id); return p ? `${username(p.username)} · ${roleName(p.roleId)}` : '项目级评价' }
const department = (key: string) => settings.value?.departments.find(d => d.key === key)?.name ?? key
const totalAward = computed(() => app.value?.awards.reduce((sum, award) => sum + award.amount, 0) ?? 0)
const approved = computed(() => data.value?.applications.filter(v => ['Approved', 'Paid'].includes(v.application.status)).reduce((sum, v) => sum + v.application.awards.reduce((s, a) => s + a.amount, 0), 0) ?? 0)
const paid = computed(() => data.value?.applications.reduce((sum, v) => sum + (v.application.payment?.amount ?? 0), 0) ?? 0)
const evidenceRows = computed(() => [
  ...(settings.value?.projectIndicators ?? []).map(i => ({ personId: '', indicator: i })),
  ...people.value.flatMap(p => (settings.value?.roles.find(r => r.id === p.roleId)?.indicators ?? []).map(i => ({ personId: p.id, indicator: i }))),
])
function evidenceText(person: string, id: string) { return evidence.value.find(e => e.personId === person && e.indicatorId === id)?.text ?? '' }
function setEvidence(person: string, id: string, text: string) { const row = evidence.value.find(e => e.personId === person && e.indicatorId === id); if (row) row.text = text; else evidence.value.push({ personId: person, indicatorId: id, text }) }
function indicator(task: { personId: string; indicatorId: string }): BonusIndicator | undefined {
  if (!task.personId) return app.value?.template?.projectIndicators.find(i => i.id === task.indicatorId)
  const person = app.value?.people.find(p => p.id === task.personId)
  return app.value?.template?.roles.find(r => r.id === person?.roleId)?.indicators.find(i => i.id === task.indicatorId)
}
function resetDraft() {
  editing.value = false; people.value = []; evidence.value = []; milestone.value = ''; comment.value = ''
  scoreDraft.value = (current.value?.myScoringTasks ?? []).map(t => ({ indicatorId: t.indicatorId, personId: t.personId, departmentKey: t.departmentKey,
    score: app.value?.scores.find(s => sameScore(s, t))?.score ?? null, comment: app.value?.scores.find(s => sameScore(s, t))?.comment ?? '' }))
  baseline.value = draftState()
}
const sameScore = (s: BonusScore, t: BonusTask) => s.indicatorId === t.indicatorId && s.personId === t.personId && s.departmentKey === t.departmentKey
let sequence = 0
async function load() {
  if (!props.token) return
  const seq = ++sequence; busy.value = true; error.value = ''; data.value = null
  try { const result = await getProjectBonus(props.projectId, props.token); if (seq === sequence) { data.value = result; resetDraft() } }
  catch (e) { if (seq === sequence) error.value = e instanceof Error ? e.message : '项目奖金加载失败。' }
  finally { if (seq === sequence) busy.value = false }
}
async function run(action: () => Promise<BonusView>, message: string) {
  if (busy.value) return; busy.value = true; error.value = ''; const seq = sequence
  try { const result = await action(); if (seq !== sequence) return; data.value = result; resetDraft(); emit('updated'); ElMessage.success(message) }
  catch (e) { if (seq === sequence) error.value = e instanceof Error ? e.message : '操作失败，请刷新核对后重试。' }
  finally { if (seq === sequence) busy.value = false }
}
function edit() {
  people.value = structuredClone(app.value?.people ? JSON.parse(JSON.stringify(app.value.people)) : [])
  evidence.value = structuredClone(app.value?.evidence ? JSON.parse(JSON.stringify(app.value.evidence)) : [])
  milestone.value = app.value?.milestoneEvidence ?? ''; editing.value = true; baseline.value = draftState()
}
function addPerson() { const role = roles.value[0]; if (role) people.value.push({ id: bonusId(), username: '', roleId: role.id, weight: role.suggestedWeight, reason: '' }) }
function changeRole(person: BonusPerson) { person.weight = roles.value.find(r => r.id === person.roleId)?.suggestedWeight ?? 10 }
function save(startScoring: boolean) {
  if (!data.value || !props.token) return
  const validEvidence = evidence.value.filter(e => evidenceRows.value.some(r => r.personId === e.personId && r.indicator.id === e.indicatorId))
  void run(() => saveProjectBonus(data.value!.projectId, phase.value, { expectedRowVersion: data.value!.rowVersion, people: people.value, evidence: validEvidence, milestoneEvidence: milestone.value, startScoring }, props.token!), startScoring ? '已发起部门评分，奖金池与本次规则已冻结。' : '奖金草稿已保存。')
}
function submitScores() {
  if (!data.value || !props.token) return
  const filled = scoreDraft.value.filter(s => s.score != null)
  void run(() => scoreProjectBonus(data.value!.projectId, phase.value, data.value!.rowVersion, filled, props.token!), '部门评分已保存。')
}
function act(action: string) {
  if (!data.value || !props.token) return
  void run(() => actProjectBonus(data.value!.projectId, phase.value, action, data.value!.rowVersion, comment.value, props.token!), '申请处理结果已保存。')
}
function pay() {
  if (!data.value || !props.token) return
  void run(() => payProjectBonus(data.value!.projectId, phase.value, { expectedRowVersion: data.value!.rowVersion, amount: totalAward.value, date: paymentDate.value, voucher: voucher.value }, props.token!), '奖金发放记录已保存。')
}
async function openHistory() {
  if (!data.value || !props.token || busy.value) return; busy.value = true
  try { history.value = await getProjectBonusHistory(data.value.projectId, props.token); showHistory.value = true }
  catch (e) { error.value = e instanceof Error ? e.message : '历史记录加载失败。' } finally { busy.value = false }
}
watch(phase, resetDraft)
watch([() => props.projectId, () => Boolean(props.token)], () => { phase.value = 'Shipment'; load() }, { immediate: true })
</script>
<template>
  <section class="project-bonus" v-loading="busy" aria-label="项目奖金">
    <header class="bonus-heading"><div class="bonus-actions"><button v-if="data?.canViewHistory" :disabled="busy || dirty" class="pdm-secondary-action" @click="openHistory">历史版本</button><button class="pdm-secondary-action" :disabled="busy || dirty" @click="load">刷新</button></div></header>
    <p v-if="error" role="alert" class="bonus-error">{{ error }}</p>
    <template v-if="data">
      <div v-if="data.pool != null" class="bonus-stats"><article class="pdm-stat-card"><span>{{ data.frozen ? '冻结奖金池' : '当前奖金池' }}</span><strong>{{ money(data.pool) }}</strong><small>{{ uppercaseMoney(data.pool) }}</small></article><article class="pdm-stat-card"><span>每阶段申请上限 · 50%</span><strong>{{ money(data.pool / 2) }}</strong><small>发货与终验收各一次</small></article><article class="pdm-stat-card"><span>已批准 / 已登记发放</span><strong>{{ money(approved) }}</strong><small>已登记发放 {{ money(paid) }}</small></article><article class="pdm-stat-card"><span>未批准金额</span><strong>{{ money(data.pool - approved) }}</strong><small>含未申请及考核扣减，不自动转分</small></article></div>
      <p v-else-if="data.canCreate" class="bonus-muted">请先完善项目结算与奖金比例。</p>
      <div class="bonus-phases"><button v-for="p in ['Shipment', 'Acceptance']" :key="p" class="bonus-phase" :class="{ selected: phase === p }" :disabled="busy || dirty" @click="phase = p"><span>{{ p === 'Shipment' ? '01' : '02' }}</span><div><strong>{{ bonusPhase(p) }}阶段</strong><small>{{ bonusStatus(data.applications.find(v => v.application.phase === p)?.application.status) }}</small></div><b>50%</b></button></div>

      <div v-if="!editing" class="bonus-actions"><button v-if="data.canCreate && (!current || current.canEdit)" class="pdm-primary-action bonus-application-action" :disabled="busy" @click="edit">{{ current ? '编辑申请' : '发起' + bonusPhase(phase) + '奖金申请' }}</button><span v-if="app?.template" class="bonus-muted">申请模板 v{{ app.template.version }} · {{ bonusStatus(app.status) }}</span></div>
      <form v-if="editing" @submit.prevent="save(false)">
        <fieldset :disabled="busy"><article class="bonus-card"><header><h3>阶段依据与人员分配</h3><span>按岗位贡献设置权重，系统自动换算占比</span></header>
          <p v-if="!data.settings?.enabled" class="bonus-error">项目奖金规则尚未启用，请先在系统设置 → 审批流程 → 项目奖金规则中绑定部门和人员。</p>
          <label>{{ bonusPhase(phase) }}凭证及完成说明<textarea v-model="milestone" rows="2" maxlength="2000" placeholder="填写发货单 / 终验收报告编号、实际日期及完成情况；批准延期请注明审批记录。" /></label>
          <div class="bonus-table-scroll"><table><thead><tr><th>人员</th><th>岗位</th><th>分配权重</th><th>权重占比</th><th>贡献及调整说明</th><th>操作</th></tr></thead><tbody><tr v-for="(p, i) in people" :key="p.id"><td><el-select v-model="p.username" filterable placeholder="选择人员"><el-option v-for="u in data.users" :key="u.username" :value="u.username" :label="u.name" /></el-select></td><td><select v-model="p.roleId" @change="changeRole(p)"><option v-for="r in roles" :key="r.id" :value="r.id">{{ r.name }}</option></select></td><td><el-input-number v-model="p.weight" :min="0.01" :max="1000" :controls="false" :precision="2" /></td><td>{{ (p.weight / (people.reduce((s, r) => s + r.weight, 0) || 1) * 100).toFixed(2) }}%</td><td><input v-model="p.reason" maxlength="2000" placeholder="说明贡献及权重调整原因" /></td><td><button type="button" class="pdm-secondary-action" @click="people.splice(i, 1)">移除</button></td></tr><tr v-if="!people.length"><td colspan="6">请添加参与本阶段的人员和岗位。</td></tr></tbody></table></div><button type="button" class="pdm-secondary-action" @click="addPerson">添加参与人员</button>
        </article>
        <article class="bonus-card"><h3>考核依据</h3><p>项目级与各岗位分别提供证据，评分由设置的部门独立完成。一个人可承担不同岗位，同一岗位不可重复添加。</p><details v-for="row in evidenceRows" :key="row.personId + row.indicator.id" class="bonus-indicator"><summary><PersonName :name="personName(row.personId)" /> · {{ row.indicator.name }} <span>{{ row.indicator.weight }}%</span></summary><p>{{ row.indicator.guidance }}</p><label>{{ row.indicator.evidence }}<textarea :value="evidenceText(row.personId, row.indicator.id)" rows="2" maxlength="2000" @input="setEvidence(row.personId, row.indicator.id, ($event.target as HTMLTextAreaElement).value)" /></label></details></article>
        <footer class="bonus-actions"><button class="pdm-primary-action" type="button" @click="save(true)">提交部门评分</button><button class="pdm-secondary-action" type="submit">保存草稿</button><button class="pdm-secondary-action" type="button" @click="resetDraft">取消编辑</button></footer></fieldset>
      </form>
      <template v-else-if="app && current">
        <article v-if="current.myScoringTasks.length" class="bonus-card"><header><h3>我的部门评分</h3><span>可逐项保存，全部部门完成后自动计算分配</span></header><fieldset :disabled="busy"><div v-for="s in scoreDraft" :key="s.personId + s.indicatorId + s.departmentKey" class="bonus-score-item"><h4><PersonName :name="personName(s.personId)" /> · {{ indicator(s)?.name }} <small><DepartmentName :name="department(s.departmentKey)" /></small></h4><p>{{ indicator(s)?.guidance }}</p><p class="bonus-muted">依据：{{ app.evidence.find(e => e.personId === s.personId && e.indicatorId === s.indicatorId)?.text || '未提供' }}</p><div class="bonus-form-grid"><label>评分 / {{ indicator(s)?.maxScore }}<el-input-number v-model="s.score" :min="0" :max="indicator(s)?.maxScore ?? 100" :controls="false" :precision="2" /></label><label class="bonus-wide">评分说明<input v-model="s.comment" maxlength="2000" placeholder="说明评分事实、扣分事项与责任归属" /></label></div></div><button type="button" class="pdm-primary-action" @click="submitScores">保存部门评分</button></fieldset></article>
        <article class="bonus-card"><header><h3>分配结果</h3><strong>{{ app.awards.length ? money(totalAward) : '评分完成后计算' }}</strong></header><p v-if="app.projectScore != null">项目评价 {{ app.projectScore.toFixed(2) }} / 100 · 本阶段保留 {{ data.pool == null ? '—' : money(data.pool / 2 - totalAward) }}</p><div class="bonus-table-scroll"><table><thead><tr><th>人员</th><th>岗位</th><th>分配权重</th><th>岗位得分</th><th>分配金额</th><th>贡献说明</th></tr></thead><tbody><tr v-for="p in app.people" :key="p.id"><td><PersonName :name="username(p.username)" /></td><td>{{ roleName(p.roleId) }}</td><td>{{ p.weight }}</td><td>{{ app.awards.find(a => a.personId === p.id)?.roleScore.toFixed(2) ?? '待评分' }}</td><td>{{ money(app.awards.find(a => a.personId === p.id)?.amount) }}</td><td>{{ p.reason }}</td></tr><tr v-if="!app.people.length"><td colspan="6">暂无可查看的分配信息。</td></tr></tbody></table></div></article>
        <article class="bonus-card"><details><summary>评分依据与结果（{{ app.scores.length }}项已评分）</summary><p>{{ app.milestoneEvidence }}</p><div class="bonus-table-scroll"><table><thead><tr><th>对象</th><th>指标</th><th>评分部门</th><th>得分</th><th>评分说明</th><th>评分人 / 时间</th></tr></thead><tbody><tr v-for="s in app.scores" :key="s.personId + s.indicatorId + s.departmentKey"><td><PersonName :name="personName(s.personId)" /></td><td>{{ indicator(s)?.name }}</td><td><DepartmentName :name="department(s.departmentKey)" /></td><td>{{ s.score }} / {{ indicator(s)?.maxScore }}</td><td>{{ s.comment }}</td><td><PersonName :name="displayUserName(s.actor)" /><br />{{ s.at ? new Date(s.at).toLocaleString('zh-CN') : '' }}</td></tr><tr v-if="!app.scores.length"><td colspan="6">尚无部门评分。</td></tr></tbody></table></div></details></article>
        <article v-if="app.steps.length" class="bonus-card"><h3>审批进度</h3><div class="bonus-flow"><div v-for="step in app.steps" :key="step.id" class="bonus-flow-node" :class="{ complete: step.decision === 'Approved', rejected: step.decision === 'Rejected' }"><strong>{{ step.name }}</strong><small>{{ step.decision === 'Approved' ? '已通过' : step.decision === 'Rejected' ? '已退回' : '待处理' }}</small><div v-for="d in step.participants.departments ?? []" :key="d.unitId" class="bonus-dept-state"><b>{{ d.name }} · {{ d.rejectedBy ? '已退回' : d.complete ? '已通过' : '待处理' }}</b><small>{{ d.members.map(username).join('、') }}</small><p v-for="(signature, index) in d.signatures" :key="index"><PersonName :name="username(signature.actor)" />：{{ signature.comment }} · {{ new Date(signature.signedAt).toLocaleString('zh-CN') }}</p><p v-if="d.rejectedBy">{{ d.rejectedBy }}：{{ d.rejection }}</p></div></div></div></article>
        <article v-if="current.canSubmit || current.canApprove || data.canCreate && ['Scoring', 'Scored', 'Pending'].includes(app.status)" class="bonus-card"><label>处理意见<textarea v-model="comment" rows="2" maxlength="2000" placeholder="审批、退回与撤回须填写意见；重复负责多个并行部门时，逐部门确认。" /></label><div class="bonus-actions"><button v-if="current.canSubmit" class="pdm-primary-action" :disabled="busy || dirty" @click="act('submit')">确认分配并提交审批</button><button v-if="current.canApprove" class="pdm-primary-action" :disabled="busy || dirty" @click="act('approve')">同意当前部门 / 节点</button><button v-if="current.canApprove" class="pdm-secondary-action" :disabled="busy || dirty" @click="act('reject')">退回申请</button><button v-if="data.canCreate && ['Scoring', 'Scored', 'Pending'].includes(app.status)" class="pdm-secondary-action" :disabled="busy || dirty" @click="act('withdraw')">撤回修改</button></div></article>
        <article v-if="current.canPay" class="bonus-card"><h3>财务发放登记</h3><p>本次登记 {{ money(totalAward) }} · {{ uppercaseMoney(totalAward) }}</p><div class="bonus-form-grid"><label>发放日期<input v-model="paymentDate" type="date" /></label><label class="bonus-wide">凭证编号<input v-model="voucher" maxlength="500" /></label></div><button class="pdm-primary-action" :disabled="busy" @click="pay">登记已发放</button></article>
        <article v-if="app.payment" class="bonus-card"><h3>已登记发放</h3><p>{{ money(app.payment.amount) }} · {{ app.payment.date }} · 凭证 {{ app.payment.voucher }}</p></article>
        <details v-if="app.audit.length" class="bonus-card"><summary>操作记录</summary><div class="bonus-table-scroll"><table><thead><tr><th>时间</th><th>人员</th><th>操作</th><th>说明</th></tr></thead><tbody><tr v-for="(row, i) in app.audit" :key="i"><td>{{ new Date(row.at).toLocaleString('zh-CN') }}</td><td><PersonName :name="username(row.actor)" /></td><td>{{ row.action }}</td><td>{{ row.comment }}</td></tr></tbody></table></div></details>
      </template>
      <p v-else-if="!editing" class="bonus-empty">本阶段尚无可查看的奖金申请。</p>
    </template>
    <el-dialog v-model="showHistory" title="奖金申请历史版本" width="90%" destroy-on-close><p>保留最近100个版本，历史金额和评分不会随规则设置改变。</p><div class="bonus-table-scroll"><table><thead><tr><th>版本</th><th>阶段</th><th>规则版本</th><th>状态</th><th>分配总额</th><th>最后操作</th></tr></thead><tbody><template v-for="book in history" :key="book.rowVersion"><tr v-for="row in book.applications" :key="row.phase"><td>v{{ book.rowVersion }}</td><td>{{ bonusPhase(row.phase) }}</td><td>v{{ row.template?.version }}</td><td>{{ bonusStatus(row.status) }}</td><td>{{ row.awards.length ? money(row.awards.reduce((s, a) => s + a.amount, 0)) : '待评分' }}</td><td>{{ row.audit.at(-1)?.action }} · <PersonName :name="displayUserName(row.audit.at(-1)?.actor)" /><details><summary>人员与评分</summary><p v-for="p in row.people" :key="p.id"><PersonName :name="username(p.username)" /> · {{ row.template?.roles.find(r => r.id === p.roleId)?.name }} · 权重 {{ p.weight }} · {{ money(row.awards.find(a => a.personId === p.id)?.amount) }}</p><p v-for="(s, i) in row.scores" :key="i"><PersonName :name="displayUserName(s.actor)" /> · {{ s.score }} · {{ s.comment }}</p></details></td></tr></template><tr v-if="!history.length"><td colspan="6">暂无历史版本。</td></tr></tbody></table></div></el-dialog>
  </section>
</template>
