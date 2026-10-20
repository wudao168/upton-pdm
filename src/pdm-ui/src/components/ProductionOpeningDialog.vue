<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue'
import { includeExistingProductionRelease, listProjects, listReleasePackages,
  previewExistingProductionRelease } from '../api'
import type { ProductionLine, ProductionOpeningLot, ProductionOpeningStage,
  ProductionReleaseState, ProductionRoute } from '../productionTracking'
import type { ProjectSummary, ReleasePackageSummary } from '../types'
import { userErrorMessage } from '../userErrorMessage'

const props = defineProps<{ token: string; initialProjectId: string; trackedIds: string[] }>()
const emit = defineEmits<{ close: []; included: [projectId: string, releaseId: string] }>()
type OpeningRow = Omit<ProductionOpeningLot, 'route'> & { id: number; route: ProductionRoute | '' }
const projects = ref<ProjectSummary[]>([]), packages = ref<ReleasePackageSummary[]>([])
const projectId = ref(props.initialProjectId), packageId = ref(''), projectQuery = ref('')
const preview = ref<ProductionReleaseState | null>(null), rows = ref<OpeningRow[]>([])
const checkedKeys = ref<string[]>([]), received = ref(false), note = ref('')
const pending = ref(false), error = ref('')
let nextRowId = 0
const visibleProjects = computed(() => projects.value.filter(item =>
  !projectQuery.value || `${item.code} ${item.name}`.toLowerCase().includes(projectQuery.value.trim().toLowerCase())).slice(0, 100))
const availablePackages = computed(() => packages.value.filter(item => item.state === '已发布'
  && ['NonStandardWithDrawing', 'NonStandardLongLead', 'NonStandardSupplement'].includes(item.scope)
  && !props.trackedIds.includes(item.id)))
const stages = (route: ProductionRoute | ''): { value: ProductionOpeningStage; label: string }[] => route === 'Self'
  ? [{ value: 'Unstarted', label: '未开工' }, { value: 'Started', label: '已开工' },
    { value: 'MachiningCompleted', label: '加工完成' }, { value: 'Packed', label: '已包装交检' },
    { value: 'QualityReceived', label: '质量已收料' }, { value: 'Passed', label: '检验合格' },
    { value: 'Issued', label: '已领用' }]
  : [{ value: 'Unstarted', label: '未交货' }, { value: 'ExternalDelivered', label: '外协已交货' },
    { value: 'QualityReceived', label: '质量已收料' }, { value: 'Passed', label: '检验合格' },
    { value: 'Issued', label: '已领用' }]
const line = (key: string) => preview.value?.lines.find(item => item.materialKey === key)
const lineRows = (key: string) => rows.value.filter(item => item.materialKey === key)
function addRow(item: ProductionLine) {
  rows.value.push({ id: ++nextRowId, materialKey: item.materialKey, route: '',
    quantity: 0, stage: 'Unstarted', supplier: '', assignee: '', promisedOn: null, recipient: '' })
}
function chooseRoute(row: OpeningRow, value: string) {
  row.route = value === 'Self' || value === 'External' ? value : ''
  row.stage = 'Unstarted'
  if (row.route && row.quantity === 0) row.quantity = Math.max(0,
    (line(row.materialKey)?.requiredQuantity ?? 0) - lineRows(row.materialKey)
      .filter(item => item.id !== row.id && item.route).reduce((total, item) => total + item.quantity, 0))
  if (!row.route) row.quantity = 0
}
async function loadPackages() {
  packages.value = []; packageId.value = ''; preview.value = null; rows.value = []; checkedKeys.value = []
  if (!projectId.value) return
  pending.value = true; error.value = ''
  try { packages.value = await listReleasePackages(projectId.value, props.token) }
  catch (cause) { error.value = userErrorMessage(cause instanceof Error ? cause.message : '') }
  finally { pending.value = false }
}
async function loadPreview() {
  preview.value = null; rows.value = []; checkedKeys.value = []; received.value = false
  if (!packageId.value) return
  pending.value = true; error.value = ''
  try {
    preview.value = await previewExistingProductionRelease(packageId.value, props.token)
    preview.value.lines.forEach(addRow)
  } catch (cause) { error.value = userErrorMessage(cause instanceof Error ? cause.message : '') }
  finally { pending.value = false }
}
async function submit() {
  if (!preview.value || pending.value) return
  const allKeys = preview.value.lines.map(item => item.materialKey)
  if (checkedKeys.value.length !== allKeys.length) { error.value = '请逐项核对并勾选全部物料。'; return }
  if (!note.value.trim()) { error.value = '请填写供应链与质量部门的期初核对说明。'; return }
  const lots: ProductionOpeningLot[] = []
  for (const item of preview.value.lines) {
    const entries = lineRows(item.materialKey).filter(row => row.route)
    if (entries.some(row => !Number.isFinite(row.quantity) || row.quantity <= 0)
      || entries.reduce((total, row) => total + row.quantity, 0) > item.requiredQuantity) {
      error.value = `${item.drawingNumber} 的期初批次数量不正确，请核对。`; return
    }
    for (const row of entries) {
      if (row.route === 'External' && !row.supplier?.trim()) { error.value = '外协批次必须填写供应商。'; return }
      if (row.stage === 'Issued' && !row.recipient?.trim()) { error.value = '已领用批次必须填写实际领料人。'; return }
      lots.push({ materialKey: row.materialKey, route: row.route as ProductionRoute,
        quantity: row.quantity, stage: row.stage, supplier: row.supplier,
        assignee: row.assignee, promisedOn: row.promisedOn, recipient: row.recipient })
    }
  }
  if (!received.value && lots.length) { error.value = '未接收的发布包不能填写期初生产批次。'; return }
  pending.value = true; error.value = ''
  try {
    await includeExistingProductionRelease(packageId.value, { verifiedAt: new Date().toISOString(),
      received: received.value, verifiedMaterialKeys: allKeys, lots,
      verificationNote: note.value.trim() }, props.token)
    emit('included', projectId.value, packageId.value)
  } catch (cause) { error.value = userErrorMessage(cause instanceof Error ? cause.message : '') }
  finally { pending.value = false }
}
watch(projectId, () => { void loadPackages() })
watch(packageId, () => { void loadPreview() })
onMounted(async () => {
  pending.value = true
  try { projects.value = await listProjects(props.token) }
  catch (cause) { error.value = userErrorMessage(cause instanceof Error ? cause.message : '') }
  finally { pending.value = false }
  if (projectId.value) await loadPackages()
})
</script>

<template>
  <div class="production-opening__backdrop" role="dialog" aria-modal="true" aria-label="核对并纳入旧发布包">
    <div class="production-opening pdm-panel">
      <header><div><h2>核对并纳入旧发布包</h2><p>仅用于功能启用前已发布的非标件；请按当前实物和流转记录填写期初状态。</p></div><button type="button" @click="emit('close')">关闭</button></header>
      <p v-if="error" role="alert" class="production-opening__error">{{ error }}</p>
      <div class="production-opening__picker"><label>搜索项目<input v-model="projectQuery" type="search" placeholder="项目号或名称"></label><label>项目<select v-model="projectId"><option value="">请选择项目</option><option v-for="item in visibleProjects" :key="item.id" :value="item.id">{{ item.code }} · {{ item.name }}</option></select></label><label>已发布非标包<select v-model="packageId"><option value="">请选择发布包</option><option v-for="item in availablePackages" :key="item.id" :value="item.id">{{ item.number }} · {{ item.publishedAt?.slice(0, 10) }}</option></select></label></div>
      <div v-if="preview" class="production-opening__body">
        <p><strong>{{ preview.packageNumber }}</strong> · {{ preview.lines.length }} 项需求。对已部分完成的物料，请拆成多个批次分别填当前环节；未分配数量保留空白。</p>
        <label class="production-opening__check"><input v-model="received" type="checkbox">供应链已接收该发布包</label>
        <section v-for="item in preview.lines" :key="item.id" class="production-opening__line">
          <header><label><input v-model="checkedKeys" type="checkbox" :value="item.materialKey">已核对 {{ item.drawingNumber }} · {{ item.name }} · {{ item.revision }} · 需求 {{ item.requiredQuantity }} {{ item.unit }}</label><button type="button" @click="addRow(item)">添加拆分批次</button></header>
          <div v-for="row in lineRows(item.materialKey)" :key="row.id" class="production-opening__row">
            <label>方式<select :value="row.route" @change="chooseRoute(row, ($event.target as HTMLSelectElement).value)"><option value="">未分配</option><option value="Self">自制</option><option value="External">外协</option></select></label>
            <template v-if="row.route"><label>数量<input v-model.number="row.quantity" type="number" min="0" step="any"></label><label>当前环节<select v-model="row.stage"><option v-for="stage in stages(row.route)" :key="stage.value" :value="stage.value">{{ stage.label }}</option></select></label><label v-if="row.route === 'External'">供应商<input v-model="row.supplier" placeholder="供应商"></label><label v-else>生产负责人<input v-model="row.assignee" placeholder="负责人"></label><label>预计完成<input v-model="row.promisedOn" type="date"></label><label v-if="row.stage === 'Issued'">实际领料人<input v-model="row.recipient" placeholder="领料人"></label></template>
            <button v-if="lineRows(item.materialKey).length > 1" type="button" @click="rows = rows.filter(entry => entry.id !== row.id)">移除</button>
          </div>
        </section>
        <label class="production-opening__note">核对说明<input v-model="note" placeholder="写明核对依据、责任部门和未分配数量的原因"></label>
        <button type="button" class="pdm-primary-action" :disabled="pending" @click="submit">确认期初记录并纳入制造中心</button>
      </div>
      <p v-else-if="packageId && !pending">无法预览该发布包，请核对其发布状态。</p>
    </div>
  </div>
</template>

<style scoped>
.production-opening__backdrop{position:fixed;inset:0;z-index:2400;background:var(--pdm-overlay);display:grid;place-items:center;padding:16px}.production-opening{width:min(1100px,100%);max-height:88vh;overflow:auto;padding:18px;box-sizing:border-box;color:var(--pdm-text)}.production-opening>header,.production-opening__line>header{display:flex;align-items:center;justify-content:space-between;gap:12px}.production-opening h2{margin:0;font-size:19px}.production-opening p{color:var(--pdm-muted)}.production-opening button{cursor:pointer}.production-opening__error{padding:8px;background:var(--pdm-danger-soft);color:var(--pdm-danger)!important}.production-opening__picker{display:grid;grid-template-columns:1fr 1fr 1fr;gap:10px;margin:14px 0}.production-opening label{display:grid;gap:4px;font-size:12px}.production-opening input,.production-opening select{height:32px;min-width:0;border:1px solid var(--pdm-border);border-radius:5px;padding:4px 7px;background:var(--pdm-surface);color:inherit}.production-opening__check,.production-opening__line header label{display:flex!important;align-items:center;gap:6px!important}.production-opening__check input,.production-opening__line header input{height:auto}.production-opening__line{border-top:1px solid var(--pdm-border);padding:10px 0}.production-opening__row{display:flex;flex-wrap:wrap;align-items:end;gap:8px;margin:8px 0}.production-opening__row label{min-width:105px;flex:1}.production-opening__row label:first-child{max-width:115px}.production-opening__row label:nth-child(2){max-width:90px}.production-opening__row button{height:32px}.production-opening__note{margin:12px 0}.production-opening__note input{width:100%}@media(max-width:700px){.production-opening__picker{grid-template-columns:1fr}.production-opening__row label{min-width:43%}}
</style>
