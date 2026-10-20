<script setup lang="ts">
import { computed, reactive, ref, watch } from 'vue'
import { Plus, RefreshCw, Search } from '@lucide/vue'
import { ElMessage } from '../statusMessage'
import ProjectApplicationSnapshot from './ProjectApplicationSnapshot.vue'
import { PdmApiError, readProjectApplications, readProjectApplication, saveProjectApplication, actProjectApplication, approveProjectApplication } from '../api'
import type { ProjectApplication, ProjectApplicationInput, ProjectApplicationDetail, ProjectApplicationView, ProjectApplicationApprovalInput } from '../projectApplications'

const props = defineProps<{ token: string; active: boolean; requestedId?: string }>()
const emit = defineEmits<{ open: [projectId: string]; changed: [] }>()
const view = ref<ProjectApplicationView | null>(null)
const detail = ref<ProjectApplicationDetail | null>(null)
const loading = ref(false), pending = ref(false), error = ref('')
const query = ref(''), status = ref(''), kind = ref('')
const formOpen = ref(false), detailOpen = ref(false), approvalOpen = ref(false)
const editing = ref<ProjectApplication | null>(null)
const customerChoice = ref('')
const blank = (): ProjectApplicationInput => ({ customerId: null, customerName: '', projectName: '', orderDate: null, deliveryDate: null, salesUsername: '', presalesUsernames: [], remarks: '', isSpecial: false })
const form = reactive(blank())
const approvalData = reactive(blank())
const approval = reactive<ProjectApplicationApprovalInput>({ expectedRowVersion: 0, customerId: '', projectTypeCode: '', equipmentTypeCode: 0, quantity: 1, bomItemCategoryCode: '0301', projectAlias: '', comment: '' })
const returnReason = ref('')
const approvedData = computed(() => detail.value?.history.find(entry => entry.approvedData)?.approvedData)
const filtered = computed(() => (view.value?.applications ?? []).filter(x => {
  const needle = query.value.trim().toLowerCase()
  return (!status.value || x.status === status.value) && (!kind.value || x.data.input.isSpecial === (kind.value === '特殊'))
    && (!needle || [x.number, x.data.input.customerName, x.data.input.projectName, x.applicantName, x.data.salesName, x.projectCode].some(v => v?.toLowerCase().includes(needle)))
}))
let loadGeneration = 0
async function load() {
  const generation = ++loadGeneration
  loading.value = true; error.value = ''
  try { const value = await readProjectApplications(props.token); if (generation === loadGeneration) view.value = value }
  catch (cause) { if (generation === loadGeneration) error.value = failure(cause, '立项申请读取失败，请刷新重试。') }
  finally { if (generation === loadGeneration) loading.value = false }
}
function failure(cause: unknown, fallback: string) { return cause instanceof PdmApiError ? cause.message : fallback }
async function show(id: string) {
  try { detail.value = await readProjectApplication(id, props.token); returnReason.value = ''; approvalOpen.value = false; detailOpen.value = true }
  catch (cause) { ElMessage.error(failure(cause, '申请详情读取失败，请刷新重试。')) }
}
function edit(value?: ProjectApplication) {
  editing.value = value ?? null
  Object.assign(form, value ? { ...value.data.input, presalesUsernames: [...value.data.input.presalesUsernames] } : blank())
  customerChoice.value = value?.data.input.customerId || value?.data.input.customerName || ''
  formOpen.value = true
}
async function save(submit: boolean) {
  if (pending.value) return
  submit ||= editing.value?.status === '待审批'
  const customer = view.value?.customers.find(x => x.id === customerChoice.value)
  const data: ProjectApplicationInput = { ...form, customerId: customer?.id ?? null, customerName: customer?.name ?? customerChoice.value.trim(),
    orderDate: form.orderDate || null, deliveryDate: form.deliveryDate || null, presalesUsernames: [...form.presalesUsernames] }
  if (submit && (!data.customerName || !data.projectName.trim() || !data.deliveryDate || !data.salesUsername || !data.presalesUsernames.length))
    return ElMessage.warning('请填写客户、项目名称、交货日期、销售人员和售前工程师；订单日期可不填。')
  pending.value = true
  try {
    const saved = await saveProjectApplication(editing.value?.id, { data, expectedRowVersion: editing.value?.rowVersion ?? 0, submit }, props.token)
    formOpen.value = false
    ElMessage.success(submit ? '立项申请已提交，等待计划管理审批。' : '立项申请草稿已保存。')
    await load(); await show(saved.id); emit('changed')
  } catch (cause) { ElMessage.error(failure(cause, '申请保存失败，请稍后重试。')) }
  finally { pending.value = false }
}
function prepareApproval() {
  const app = detail.value!.application, options = view.value!.numberingOptions
  Object.assign(approval, { expectedRowVersion: app.rowVersion, customerId: app.data.input.customerId || '', projectTypeCode: options.projectTypes.find(x => x.isActive)?.code ?? '',
    equipmentTypeCode: options.equipmentTypes.find(x => x.isActive)?.code ?? 0, quantity: 1, bomItemCategoryCode: '0301', projectAlias: '', comment: '' })
  Object.assign(approvalData, { ...app.data.input, presalesUsernames: [...app.data.input.presalesUsernames] })
  approvalOpen.value = true
}
async function act(action: 'withdraw' | 'return' | 'approve') {
  if (!detail.value || pending.value) return
  if (action === 'return' && !returnReason.value.trim()) return ElMessage.warning('请填写退回原因。')
  if (action === 'approve' && !approval.customerId) return ElMessage.warning('请关联从U9C同步的正式客户档案。')
  pending.value = true
  try {
    const app = detail.value.application
    if (action === 'approve') await approveProjectApplication(app.id, { ...approval, projectData: { ...approvalData, customerId: approval.customerId,
      orderDate: approvalData.orderDate || null, deliveryDate: approvalData.deliveryDate || null, presalesUsernames: [...approvalData.presalesUsernames] } }, props.token)
    else await actProjectApplication(app.id, action, { expectedRowVersion: app.rowVersion, comment: returnReason.value }, props.token)
    ElMessage.success(action === 'approve' ? '申请已批准，主项目已自动创建。' : action === 'return' ? '申请已退回，原提交内容已保留。' : '申请已撤回，原提交记录已保留。')
    await load(); await show(app.id); emit('changed')
  } catch (cause) { ElMessage.error(failure(cause, '操作失败，请刷新申请状态后重试。')) }
  finally { pending.value = false }
}
function formatTime(value: string) { return new Date(value).toLocaleString('zh-CN', { hour12: false }) }
watch(() => [props.active, props.token] as const, ([active]) => { if (active) void load() }, { immediate: true })
watch(() => [props.active, props.requestedId] as const, ([active, id]) => { if (active && id) void show(id) }, { immediate: true })
</script>

<template>
  <section class="pdm-panel project-applications" aria-label="立项申请" :aria-busy="loading">
    <div class="application-toolbar">
      <button v-if="view?.canApply" class="pdm-primary-action" :disabled="pending" @click="edit()"><Plus :size="15" />新建申请</button>
      <label class="application-search"><Search :size="14" /><input v-model="query" aria-label="搜索立项申请" placeholder="申请单号、客户、项目或人员"></label>
      <select v-model="status" aria-label="筛选申请状态"><option value="">全部状态</option><option v-for="item in ['草稿','待审批','已退回','已撤回','已批准']" :key="item">{{ item }}</option></select>
      <select v-model="kind" aria-label="筛选立项方式"><option value="">全部立项方式</option><option value="正常">正常立项</option><option value="特殊">特殊立项</option></select>
      <button class="pdm-secondary-action" :disabled="loading" @click="load"><RefreshCw :size="14" />刷新</button>
      <span class="application-count">共 {{ filtered.length }} 条</span>
    </div>
    <p class="application-hint">销售助理发起，计划管理审批后自动创建主项目。原申请及历次审批记录长期保留。</p>
    <p v-if="error" role="alert" class="application-error">{{ error }}</p>
    <div class="application-table-wrap">
      <table class="application-table"><thead><tr><th>申请单号</th><th>立项方式</th><th>客户名称</th><th>项目名称</th><th>申请人</th><th>销售人员</th><th>订单日期</th><th>交货日期</th><th>状态</th><th>关联项目</th><th>操作</th></tr></thead>
        <tbody><tr v-for="app in filtered" :key="app.id"><td><button class="pdm-text-action" @click="show(app.id)">{{ app.number }}</button></td><td><span :class="{ 'application-special': app.data.input.isSpecial }">{{ app.data.input.isSpecial ? '特殊立项' : '正常立项' }}</span></td><td>{{ app.data.input.customerName || '—' }}</td><td>{{ app.data.input.projectName || '未命名草稿' }}</td><td>{{ app.applicantName }}</td><td>{{ app.data.salesName || '—' }}</td><td>{{ app.data.input.orderDate || '未填写' }}</td><td>{{ app.data.input.deliveryDate || '—' }}</td><td><span class="pdm-status" :class="app.status === '已批准' ? 'is-ok' : 'is-warn'">{{ app.status }}</span></td><td><button v-if="app.projectId" class="pdm-text-action" @click="emit('open', app.projectId)">{{ app.projectCode }}</button><span v-else>—</span></td><td><button class="pdm-text-action" @click="show(app.id)">{{ view?.canApprove && app.status === '待审批' ? '引用并审批' : '查看详情' }}</button></td></tr>
          <tr v-if="!loading && !filtered.length"><td colspan="11" class="application-empty">{{ query || status || kind ? '没有符合条件的申请，请调整筛选。' : '暂无立项申请。' }}</td></tr></tbody>
      </table>
    </div>

    <el-dialog v-model="formOpen" :title="editing ? '修改立项申请' : '新建立项申请'" width="min(760px, 94vw)" :close-on-click-modal="false" :close-on-press-escape="!pending" :show-close="!pending" append-to-body>
      <form class="application-form" aria-label="立项申请表单" @submit.prevent="save(true)">
        <p v-if="editing?.status === '待审批'" class="application-wide application-hint">保存修改后将生成新的提交版本，原申请及历史记录保留，审批人需重新引用最新版本。</p>
        <label class="application-wide">立项方式<el-radio-group v-model="form.isSpecial" aria-label="立项方式"><el-radio :value="false">正常立项</el-radio><el-radio :value="true">特殊立项（合同未签，提前启动）</el-radio></el-radio-group></label>
        <p v-if="form.isSpecial" class="application-wide application-special-note">合同未签也可提交。建议在备注中说明提前启动原因；订单日期可留空。</p>
        <label class="application-wide">客户名称 <em>*</em><el-select v-model="customerChoice" aria-label="客户名称" filterable allow-create default-first-option placeholder="搜索已有客户，或输入客户全称" style="width:100%"><el-option v-for="item in view?.customers" :key="item.id" :value="item.id" :label="`${item.name}（${item.code}）`" /></el-select><small>新录入客户将在审批时关联正式客户档案。</small></label>
        <label class="application-wide">项目名称 <em>*</em><input v-model="form.projectName" aria-label="项目名称" maxlength="200"></label>
        <label>订单日期（选填）<input v-model="form.orderDate" aria-label="订单日期" type="date"></label>
        <label>交货日期 <em>*</em><input v-model="form.deliveryDate" aria-label="交货日期" type="date"></label>
        <label>销售人员 <em>*</em><el-select v-model="form.salesUsername" aria-label="销售人员" filterable placeholder="请选择销售人员"><el-option v-for="person in view?.people" :key="person.username" :value="person.username" :label="person.displayName" /></el-select></label>
        <label>售前工程师 <em>*</em><el-select v-model="form.presalesUsernames" aria-label="售前工程师" multiple filterable placeholder="可选择多位工程师"><el-option v-for="person in view?.people" :key="person.username" :value="person.username" :label="person.displayName" /></el-select></label>
        <label class="application-wide">备注<textarea v-model="form.remarks" aria-label="备注" maxlength="2000" rows="3" placeholder="特殊立项可在此说明提前启动原因"></textarea></label>
      </form>
      <template #footer><button class="pdm-secondary-action" :disabled="pending" @click="formOpen = false">取消</button><button v-if="editing?.status !== '待审批'" class="pdm-secondary-action" :disabled="pending" @click="save(false)">保存草稿</button><button class="pdm-primary-action" :disabled="pending" @click="save(true)">{{ pending ? '正在保存…' : editing?.status === '待审批' ? '保存修改并重新提交' : '提交申请' }}</button></template>
    </el-dialog>

    <el-dialog v-model="detailOpen" title="立项申请详情" width="min(920px, 94vw)" :close-on-click-modal="false" :close-on-press-escape="!pending" :show-close="!pending" append-to-body>
      <template v-if="detail">
        <div class="application-detail-title"><strong>{{ detail.application.number }}</strong><span class="pdm-status is-warn">{{ detail.application.status }}</span><span v-if="detail.application.data.input.isSpecial" class="application-special">特殊立项</span><span>第 {{ detail.application.revision }} 次提交</span></div>
        <h3>原申请资料</h3>
        <dl class="application-data"><dt>客户名称</dt><dd>{{ detail.application.data.input.customerName || '—' }}</dd><dt>项目名称</dt><dd>{{ detail.application.data.input.projectName || '—' }}</dd><dt>申请人</dt><dd>{{ detail.application.applicantName }}</dd><dt>立项方式</dt><dd>{{ detail.application.data.input.isSpecial ? '特殊立项（合同未签，提前启动）' : '正常立项' }}</dd><dt>订单日期</dt><dd>{{ detail.application.data.input.orderDate || '未填写' }}</dd><dt>交货日期</dt><dd>{{ detail.application.data.input.deliveryDate || '未填写' }}</dd><dt>销售人员</dt><dd>{{ detail.application.data.salesName || '—' }}</dd><dt>售前工程师</dt><dd>{{ detail.application.data.presalesPeople.map(x => x.displayName).join('、') || '—' }}</dd><dt>备注</dt><dd class="application-wide">{{ detail.application.data.input.remarks || '—' }}</dd></dl>
        <p v-if="detail.application.projectId">已生成主项目：<button class="pdm-text-action" @click="emit('open', detail.application.projectId); detailOpen = false">{{ detail.application.projectCode }}</button></p>
        <section v-if="approvedData" aria-label="最终建项资料"><h3>最终建项资料</h3><ProjectApplicationSnapshot :data="approvedData" /></section>
        <template v-if="detail.canApprove">
          <button v-if="!approvalOpen" class="pdm-primary-action" :disabled="pending" @click="prepareApproval">引用申请并编辑建项资料</button>
          <template v-else><h3>审批建项资料（可修改）</h3><p class="application-hint">已引用第 {{ detail.application.revision }} 次申请。项目名称、日期、人员等资料均可调整，批准后按以下内容建项，原申请及修改记录保留。</p>
            <div class="application-form">
              <label class="application-wide">正式客户档案 <em>*</em><el-select v-model="approval.customerId" aria-label="正式客户档案" filterable placeholder="请选择正式客户"><el-option v-for="item in view?.customers" :key="item.id" :value="item.id" :label="`${item.name}（${item.code}）`" /></el-select></label>
              <label class="application-wide">立项方式<el-radio-group v-model="approvalData.isSpecial" aria-label="审批立项方式"><el-radio :value="false">正常立项</el-radio><el-radio :value="true">特殊立项（合同未签，提前启动）</el-radio></el-radio-group></label>
              <label class="application-wide">项目名称 <em>*</em><input v-model="approvalData.projectName" aria-label="审批项目名称" maxlength="200"></label>
              <label>订单日期（选填）<input v-model="approvalData.orderDate" aria-label="审批订单日期" type="date"></label>
              <label>交货日期 <em>*</em><input v-model="approvalData.deliveryDate" aria-label="审批交货日期" type="date"></label>
              <label>销售人员 <em>*</em><el-select v-model="approvalData.salesUsername" aria-label="审批销售人员" filterable placeholder="请选择销售人员"><el-option v-for="person in view?.people" :key="person.username" :value="person.username" :label="person.displayName" /></el-select></label>
              <label>售前工程师 <em>*</em><el-select v-model="approvalData.presalesUsernames" aria-label="审批售前工程师" multiple filterable placeholder="可选择多位工程师"><el-option v-for="person in view?.people" :key="person.username" :value="person.username" :label="person.displayName" /></el-select></label>
              <label class="application-wide">备注<textarea v-model="approvalData.remarks" aria-label="审批备注" maxlength="2000" rows="2"></textarea></label>
              <label>项目类型<select v-model="approval.projectTypeCode" aria-label="项目类型"><option v-for="item in view?.numberingOptions.projectTypes.filter(x => x.isActive)" :key="item.code" :value="item.code">{{ item.code }} · {{ item.name }}</option></select></label>
              <label>设备类型<select v-model.number="approval.equipmentTypeCode" aria-label="设备类型"><option v-for="item in view?.numberingOptions.equipmentTypes.filter(x => x.isActive)" :key="item.code" :value="item.code">{{ item.code }} · {{ item.name }}</option></select></label>
              <label>BOM根节点类型<select v-model="approval.bomItemCategoryCode" aria-label="BOM根节点类型"><option value="0301">0301 · 产线</option><option value="0302">0302 · 设备</option></select></label>
              <label>数量<input v-model.number="approval.quantity" aria-label="数量" type="number" min="1" max="10000"></label>
              <label class="application-wide">项目别名<input v-model="approval.projectAlias" aria-label="项目别名" maxlength="200"></label>
              <label class="application-wide">审批意见<textarea v-model="approval.comment" aria-label="审批意见" rows="2" maxlength="2000"></textarea></label>
            </div><button class="pdm-primary-action" :disabled="pending" @click="act('approve')">{{ pending ? '正在处理…' : '批准并创建主项目' }}</button>
          </template>
          <div class="application-return"><label>退回原因（退回时必填）<textarea v-model="returnReason" aria-label="退回原因" rows="2" maxlength="2000"></textarea></label><button class="pdm-secondary-action" :disabled="pending" @click="act('return')">退回修改</button></div>
        </template>
        <h3>提交及审批记录 <small>按发生顺序保留，可展开查看每次内容</small></h3>
        <div class="application-history"><details v-for="entry in detail.history" :key="entry.id"><summary>{{ entry.action }} · {{ entry.actorName }} · {{ formatTime(entry.at) }} <span>第 {{ entry.revision }} 次提交</span></summary><dl class="application-data"><dt>客户名称</dt><dd>{{ entry.data.input.customerName || '—' }}</dd><dt>项目名称</dt><dd>{{ entry.data.input.projectName || '—' }}</dd><dt>立项方式</dt><dd>{{ entry.data.input.isSpecial ? '特殊立项' : '正常立项' }}</dd><dt>订单日期</dt><dd>{{ entry.data.input.orderDate || '未填写' }}</dd><dt>交货日期</dt><dd>{{ entry.data.input.deliveryDate || '—' }}</dd><dt>销售人员</dt><dd>{{ entry.data.salesName || '—' }}</dd><dt>售前工程师</dt><dd>{{ entry.data.presalesPeople.map(x => x.displayName).join('、') || '—' }}</dd><dt>备注</dt><dd>{{ entry.data.input.remarks || '—' }}</dd></dl><p v-if="entry.comment">意见：{{ entry.comment }}</p><p v-if="entry.approval">建项资料：客户 {{ entry.approvedCustomerName || entry.approval.customerId }}；项目类型 {{ entry.approval.projectTypeCode }}；设备类型 {{ entry.approval.equipmentTypeCode }}；BOM {{ entry.approval.bomItemCategoryCode }}；数量 {{ entry.approval.quantity }}；别名 {{ entry.approval.projectAlias || '—' }}</p><section v-if="entry.approvedData" aria-label="历史最终建项资料"><h3>最终建项资料</h3><ProjectApplicationSnapshot :data="entry.approvedData" /></section><p v-if="entry.projectCode">生成主项目：{{ entry.projectCode }}</p></details></div>
      </template>
      <template #footer><button class="pdm-secondary-action" :disabled="pending" @click="detailOpen = false">关闭</button><button v-if="detail?.canWithdraw" class="pdm-secondary-action" :disabled="pending" @click="act('withdraw')">撤回申请</button><button v-if="detail?.canEdit" class="pdm-primary-action" :disabled="pending" @click="detailOpen = false; edit(detail.application)">修改申请</button></template>
    </el-dialog>
  </section>
</template>

<style scoped>
.project-applications{display:flex;flex-direction:column;min-height:0;flex:1;padding:12px;gap:10px;overflow:hidden}.application-toolbar{display:flex;align-items:center;gap:8px;flex-wrap:wrap}.application-search{display:flex;align-items:center;gap:6px;border:1px solid var(--pdm-border);border-radius:4px;padding:0 8px;min-width:250px}.application-search input{border:0!important;min-width:240px}.application-count{margin-left:auto;color:var(--pdm-muted);font-size:12px}.application-hint{margin:0;font-size:12px;color:var(--pdm-muted)}.application-error{color:var(--el-color-danger)}.application-table-wrap{flex:1;min-height:0;overflow:auto}.application-table{width:100%;min-width:1150px;border-collapse:collapse;font-size:12px}.application-table th,.application-table td{padding:9px 8px;border-bottom:1px solid var(--pdm-border-soft);text-align:center;vertical-align:middle;white-space:nowrap;max-width:220px;overflow:hidden;text-overflow:ellipsis}.application-table th{position:sticky;top:0;background:var(--pdm-table-header);z-index:1}.application-table tbody tr:nth-child(even){background:var(--pdm-row-stripe)}.application-empty{text-align:center!important;height:120px;color:var(--pdm-muted)}.application-special{display:inline-block;color:var(--el-color-warning-dark-2);background:var(--el-color-warning-light-9);border:1px solid var(--el-color-warning-light-5);padding:2px 5px;border-radius:3px;font-size:11px}.application-form{display:grid;grid-template-columns:1fr 1fr;gap:14px 18px;margin:12px 0}.application-form>label,.application-return label{display:flex;flex-direction:column;gap:6px;align-items:stretch;font-size:13px;min-width:0}.application-form em{display:contents;color:var(--el-color-danger);font-style:normal}.application-wide{grid-column:1/-1}.application-form small,h3 small{font-size:12px;color:var(--pdm-muted);font-weight:400}.application-form>label>input,.application-form>label>select,.application-form>label>textarea,.application-return textarea,.application-toolbar select,.application-toolbar input{padding:7px 9px;font:inherit;color:var(--pdm-text);background:var(--pdm-surface);border:1px solid var(--pdm-border);border-radius:4px;min-width:0}.application-special-note{margin:0;padding:10px;background:var(--el-color-warning-light-9);font-size:12px}.application-detail-title{display:flex;gap:12px;align-items:center;flex-wrap:wrap;font-size:12px}.application-data{display:grid;grid-template-columns:95px 1fr 95px 1fr;border-top:1px solid var(--pdm-border-soft);border-left:1px solid var(--pdm-border-soft);font-size:12px}.application-data dt,.application-data dd{margin:0;padding:9px;border-right:1px solid var(--pdm-border-soft);border-bottom:1px solid var(--pdm-border-soft);white-space:pre-wrap;overflow-wrap:anywhere}.application-data dt{background:var(--pdm-table-header);color:var(--pdm-muted)}.application-data .application-wide{grid-column:2/-1}.application-return{display:flex;gap:12px;align-items:flex-end;margin-top:16px;padding-top:12px;border-top:1px solid var(--pdm-border)}.application-return label{flex:1}.application-history details{border:1px solid var(--pdm-border);border-radius:4px;margin-bottom:8px;padding:10px;font-size:12px}.application-history summary{cursor:pointer}.application-history summary span{float:right;color:var(--pdm-muted)}.application-history p{white-space:pre-wrap;overflow-wrap:anywhere}h3{font-size:14px;margin:20px 0 10px}button{display:inline-flex;align-items:center;gap:4px} :deep(.el-select){width:100%}
@media(max-width:700px){.application-form{grid-template-columns:1fr}.application-data{grid-template-columns:90px 1fr}.application-toolbar{align-items:stretch}.application-search{flex:1}.application-history summary span{float:none;display:block;margin-top:5px}}
</style>
