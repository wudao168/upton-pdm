<script setup lang="ts">
import { computed, nextTick, ref, watch } from 'vue'
import { ElOption, ElSelect } from 'element-plus'
import { readProductionScanOptions, readProductionScanSettings, saveProductionScanSettings } from '../api'
import { productionPosts, productionRequestId, type ProductionFlowNode, type ProductionScanCatalog } from '../productionTracking'
import { userErrorMessage } from '../userErrorMessage'
import { defaultProductionFlow, editProductionBindings, initialDepartmentFlow, saveProductionBindings, type ProductionBindingEditor } from '../productionFlowSettings'
const props = defineProps<{ token: string }>()
const catalog = ref<ProductionScanCatalog>(), bindings = ref<ProductionBindingEditor[]>([])
const pending = ref(false), error = ref(''), notice = ref(''), tab = ref('flow'), selectedId = ref('')
const deleteNodeId = ref(''), deleteStationId = ref('')
const nodeNameInput = ref<HTMLInputElement>()
const options = ref<Awaited<ReturnType<typeof readProductionScanOptions>>>({ users: [], roles: [] })
const posts = Object.entries(productionPosts).filter(([key]) => key !== 'Control')
const nodes = computed(() => catalog.value?.flowNodes || [])
const steps = computed(() => nodes.value.filter(node => node.required))
const selected = computed(() => nodes.value.find(node => node.id === selectedId.value))
const selectedIndex = computed(() => steps.value.findIndex(node => node.id === (selected.value?.required ? selectedId.value : selected.value?.afterNodeId)))
const visibleBindings = computed(() => catalog.value?.flowMode === 'DepartmentHandover' ? bindings.value.filter(binding => selectedId.value ? binding.flowNodeId === selectedId.value : !binding.flowNodeId) : bindings.value)
const stationReferences = computed(() => bindings.value.filter(binding => binding.workstationIds.includes(deleteStationId.value)).map(binding => nodes.value.find(node => node.id === binding.flowNodeId)?.name || productionPosts[binding.post]).filter(Boolean).join('、'))
function attachBindings() {
  if (catalog.value?.flowMode !== 'DepartmentHandover') return
  for (const binding of bindings.value) if (!binding.flowNodeId) binding.flowNodeId = nodes.value.find(node => node.post === binding.post)?.id
}
function setCatalog(value: ProductionScanCatalog) {
  catalog.value = { ...value, flowNodes: value.flowNodes || defaultProductionFlow(), flowMode: value.flowMode || 'ResourceOperations', controlRoleCodes: value.controlRoleCodes || [], labelWidthMm: value.labelWidthMm || 60, labelHeightMm: value.labelHeightMm || 40 }
  bindings.value = editProductionBindings(value.bindings); attachBindings()
  if (!nodes.value.some(node => node.id === selectedId.value)) selectedId.value = nodes.value.find(node => node.post === 'Packing')?.id || nodes.value[0]?.id || ''
  deleteNodeId.value = ''; deleteStationId.value = ''
}
async function load() {
  pending.value = true; error.value = ''; notice.value = ''
  try { const values = await Promise.all([readProductionScanSettings(props.token), readProductionScanOptions(props.token)]); setCatalog(values[0]); options.value = values[1] }
  catch (cause) { error.value = userErrorMessage(cause instanceof Error ? cause.message : '') }
  finally { pending.value = false }
}
watch(() => props.token, () => { void load() }, { immediate: true })
function addStation() { catalog.value?.workstations.push({ id: productionRequestId(), name: '', resourceGroup: '', isActive: true }) }
function removeStation() {
  if (!catalog.value) return
  catalog.value.workstations = catalog.value.workstations.filter(station => station.id !== deleteStationId.value)
  for (const binding of bindings.value) binding.workstationIds = binding.workstationIds.filter(id => id !== deleteStationId.value)
  deleteStationId.value = ''; notice.value = '工位已从待保存配置中移除，历史操作记录保留。请检查受影响节点的工位绑定后保存。'
}
function addBinding() { bindings.value.push({ id: productionRequestId(), subjectType: 'User', subject: '', roleCodes: [], roleIds: {}, post: catalog.value?.flowMode === 'DepartmentHandover' ? selected.value?.post || 'Reception' : 'Reception', flowNodeId: catalog.value?.flowMode === 'DepartmentHandover' ? selectedId.value || null : null, workstationIds: [], isActive: true }) }
function initialize() { if (catalog.value && !bindings.value.length && !catalog.value.workstations.length) setCatalog(initialDepartmentFlow(catalog.value, options.value.roles)) }
function changeSubject(binding: ProductionBindingEditor) { binding.subject = ''; binding.roleCodes = [] }
function changePost() { for (const binding of visibleBindings.value) if (selected.value) binding.post = selected.value.post }
function responsible(node: ProductionFlowNode) {
  const names = bindings.value.filter(binding => binding.flowNodeId === node.id && binding.isActive).flatMap(binding => binding.subjectType === 'Role'
    ? binding.roleCodes.map(code => options.value.roles.find(role => role.role === code)?.name || code)
    : [options.value.users.find(user => user.username === binding.subject)?.displayName || binding.subject]).filter(Boolean)
  return [...new Set(names)].join('、') || '待配置办理人'
}
function selectNode(id: string) { selectedId.value = id; deleteNodeId.value = '' }
function editNode() { nodeNameInput.value?.focus(); nodeNameInput.value?.select() }
function addNode() {
  if (!catalog.value) return
  const id = productionRequestId(), node = { id, name: '新增步骤', stage: '', post: 'Reception', kind: `Step:${id.replaceAll('-', '')}`, required: true }
  const index = nodes.value.findIndex(item => item.id === (selected.value?.required ? selectedId.value : selected.value?.afterNodeId))
  const issueIndex = nodes.value.findIndex(item => item.kind === 'Issue')
  const at = index < 0 ? nodes.value.length : index + 1
  catalog.value.flowNodes!.splice(issueIndex >= 0 ? Math.min(at, issueIndex) : at, 0, node); selectNode(id)
  void nextTick(editNode)
}
function moveNode(direction: number) {
  if (!selected.value || !catalog.value) return
  const index = selectedIndex.value, target = steps.value[index + direction]
  if (!target || selected.value.kind === 'Issue' || selected.value.required && target.kind === 'Issue') return
  if (!selected.value.required) selected.value.afterNodeId = target.id
  else {
    const reordered = [...steps.value], current = reordered.splice(index, 1)[0]!
    reordered.splice(index + direction, 0, current)
    catalog.value.flowNodes = reordered.flatMap(node => [node, ...nodes.value.filter(item => !item.required && item.afterNodeId === node.id)])
  }
}
function changeType() {
  if (!selected.value) return
  if (!selected.value.required) {
    const parent = steps.value.find(node => node.id !== selectedId.value)
    if (!parent) { selected.value.required = true; error.value = '请至少保留一个必经节点。'; return }
    selected.value.afterNodeId = parent.id; selected.value.stage = '保持当前阶段'
    for (const node of nodes.value) if (node.afterNodeId === selectedId.value) node.afterNodeId = parent.id
  } else selected.value.afterNodeId = null
}
function removeNode() {
  if (!catalog.value) return
  const removed = new Set(nodes.value.filter(node => node.id === deleteNodeId.value || node.afterNodeId === deleteNodeId.value).map(node => node.id))
  catalog.value.flowNodes = nodes.value.filter(node => !removed.has(node.id)); bindings.value = bindings.value.filter(binding => !removed.has(binding.flowNodeId || ''))
  selectedId.value = steps.value[0]?.id || ''; deleteNodeId.value = ''
}
async function save() {
  if (!catalog.value || pending.value) return
  error.value = ''; notice.value = ''
  if (catalog.value.workstations.some(station => !station.name.trim())) { error.value = '请填写所有工位名称。'; return }
  if (catalog.value.flowMode === 'DepartmentHandover' && (!steps.value.length || nodes.value.some(node => !node.name.trim() || !node.stage.trim()))) { error.value = '请至少配置一个必经节点，并填写所有节点的名称和进入阶段。'; return }
  if (bindings.value.some(binding => binding.subjectType === 'Role' ? !binding.roleCodes.length : !binding.subject)) { error.value = '请为每条绑定选择账号或至少一个角色。'; return }
  if (bindings.value.some(binding => !binding.workstationIds.length)) { error.value = '请为每条绑定选择允许办理的工位；删除工位后请重新选择，或删除该绑定。'; return }
  pending.value = true
  try {
    setCatalog(await saveProductionScanSettings({ workstations: catalog.value.workstations, bindings: saveProductionBindings(bindings.value), expectedRowVersion: catalog.value.rowVersion,
      flowMode: catalog.value.flowMode, flowNodes: catalog.value.flowNodes, controlRoleCodes: catalog.value.controlRoleCodes, labelWidthMm: catalog.value.labelWidthMm, labelHeightMm: catalog.value.labelHeightMm }, props.token))
    notice.value = '扫码流程及工位配置已保存，将用于下一次扫码和数量确认。'
  } catch (cause) { error.value = userErrorMessage(cause instanceof Error ? cause.message : '') }
  finally { pending.value = false }
}
</script>
<template>
  <section class="production-scan-settings pdm-panel" aria-label="生产扫码设置">
    <header class="production-scan-settings__header"><div><h2>自制生产流程</h2><p>按扫码节点配置办理人和工位</p></div><div><button type="button" class="pdm-secondary-action" :disabled="pending" @click="load">刷新配置</button><button type="submit" form="production-scan-settings-form" class="pdm-primary-action" :disabled="pending || !catalog">{{ pending ? '正在保存…' : '保存扫码配置' }}</button></div></header>
    <p v-if="error" role="alert" class="production-scan-settings__error">{{ error }}</p><p v-if="notice" role="status">{{ notice }}</p>
    <form v-if="catalog" id="production-scan-settings-form" novalidate @submit.prevent="save">
      <nav class="production-scan-settings__tabs pdm-tabs" aria-label="生产扫码配置分类"><button v-for="[key, label] in [['flow','流程配置'],['global','流程控制与标签']]" :key="key" type="button" :aria-pressed="tab === key" @click="tab = key!">{{ label }}</button></nav>
      <fieldset :disabled="pending" class="production-scan-settings__body">
        <div v-show="tab === 'flow'" class="production-scan-settings__layout">
          <section class="production-scan-settings__canvas" aria-label="自制流程配置">
            <header><h3>扫码流程</h3><select v-model="catalog.flowMode" aria-label="自制流程方式" @change="attachBindings"><option value="DepartmentHandover">部门扫码交接</option><option value="ResourceOperations">资源组开工与完工</option></select></header>
            <button v-if="!bindings.length && !catalog.workstations.length" type="button" class="pdm-secondary-action" @click="initialize">生成初版流程</button>
            <template v-if="catalog.flowMode === 'DepartmentHandover'">
              <div class="production-scan-settings__toolbar"><button type="button" class="pdm-secondary-action" @click="addNode">新增节点</button><button type="button" class="pdm-secondary-action" :disabled="selectedIndex <= 0 || selected?.kind === 'Issue'" @click="moveNode(-1)">上移</button><button type="button" class="pdm-secondary-action" :disabled="selectedIndex < 0 || selectedIndex >= steps.length - 1 || selected?.kind === 'Issue' || selected?.required && steps[selectedIndex+1]?.kind === 'Issue'" @click="moveNode(1)">下移</button><button type="button" class="pdm-secondary-action" :disabled="!selected" @click="editNode">编辑</button><button type="button" class="pdm-secondary-action" :disabled="!selected" @click="deleteNodeId = selectedId">删除节点</button></div>
              <div v-if="deleteNodeId" class="production-scan-settings__confirm" role="alert">确认删除此节点及其追溯旁支和办理人配置？已有操作记录保留。<div><button type="button" class="pdm-secondary-action" @click="removeNode">确认删除节点</button><button type="button" class="pdm-secondary-action" @click="deleteNodeId = ''">取消删除</button></div></div>
              <div class="production-scan-settings__chain"><p>已分配为自制的批次</p><template v-for="(node, index) in steps" :key="node.id"><div v-if="index" class="production-scan-settings__arrow" aria-hidden="true">↓</div><div class="production-scan-settings__node-row"><button type="button" class="production-scan-settings__node" :data-node="node.id" :aria-pressed="node.id === selectedId" @click="selectNode(node.id)"><span class="production-scan-settings__number">{{ index+1 }}</span><span><strong>{{ node.name }}</strong><small>进入{{ node.stage || '待设置阶段' }}</small><span>{{ responsible(node) }}</span></span></button><div class="production-scan-settings__branches"><button v-for="trace in nodes.filter(item => !item.required && item.afterNodeId === node.id)" :key="trace.id" type="button" class="production-scan-settings__node production-scan-settings__trace" :data-node="trace.id" :aria-pressed="trace.id === selectedId" @click="selectNode(trace.id)"><strong>{{ trace.name }}</strong><small>非必经 · 不推进阶段</small></button></div></div></template><p>{{ steps.length ? '流程结束' : '暂无节点，请新增节点' }}</p></div>
              <p class="production-scan-settings__hint">选中节点后编辑或上移／下移排序。物料领料保留在流程末尾。</p><button type="button" class="pdm-secondary-action" @click="selectNode('')">其他岗位绑定</button>
            </template><p v-else class="production-scan-settings__hint">按资源组逐一确认开工和完工；在右侧配置各岗位办理人及工位。</p>
          </section>
          <section class="production-scan-settings__editor" aria-label="选中节点设置">
            <header><h3>{{ catalog.flowMode === 'DepartmentHandover' ? selected?.name || '其他岗位绑定' : '账号与角色绑定' }}</h3><small v-if="selected && catalog.flowMode === 'DepartmentHandover'">{{ selected.required ? '必经节点' : '可选追溯节点' }}</small></header>
            <template v-if="selected && catalog.flowMode === 'DepartmentHandover'"><label>节点名称<input ref="nodeNameInput" v-model.trim="selected.name" aria-label="节点名称" maxlength="80" required></label><label>节点类型<select v-model="selected.required" aria-label="节点类型" :disabled="!selected.kind.startsWith('Step:')" @change="changeType"><option :value="true">扫码流转（必经）</option><option :value="false">资源组追溯（非必经）</option></select></label><label v-if="selected.required">进入阶段<input v-model.trim="selected.stage" aria-label="进入阶段" maxlength="80" required></label><label v-else>追溯起始节点<select v-model="selected.afterNodeId" aria-label="追溯起始节点"><option v-for="node in steps" :key="node.id" :value="node.id">{{ node.name }}</option></select></label><label>办理岗位<select v-model="selected.post" aria-label="办理岗位" @change="changePost"><option v-for="[key,label] in posts" :key="key" :value="key">{{ label }}</option></select></label></template>
            <div v-for="(binding, index) in visibleBindings" :key="binding.id" class="production-scan-settings__binding"><label>办理人来源<select v-model="binding.subjectType" :aria-label="'第'+(index+1)+'条绑定对象类型'" @change="changeSubject(binding)"><option value="User">指定账号</option><option value="Role">指定角色（可多选）</option></select></label><label>{{ binding.subjectType === 'User' ? '指定账号' : '办理角色' }}<select v-if="binding.subjectType === 'User'" v-model="binding.subject" :aria-label="'第'+(index+1)+'条绑定账号或角色'" required><option value="">请选择账号</option><option v-for="user in options.users" :key="user.username" :value="user.username">{{ user.displayName }}（{{ user.username }}）</option></select><ElSelect v-else v-model="binding.roleCodes" multiple filterable collapse-tags collapse-tags-tooltip :max-collapse-tags="3" size="small" :aria-label="'第'+(index+1)+'条绑定账号或角色'" placeholder="选择角色（可多选）"><ElOption v-for="role in options.roles" :key="role.role" :label="role.name" :value="role.role" /></ElSelect></label><label v-if="catalog.flowMode !== 'DepartmentHandover' || !selectedId">岗位<select v-model="binding.post" :aria-label="'第'+(index+1)+'条绑定岗位'"><option v-for="[key,label] in posts" :key="key" :value="key">{{ label }}</option></select></label><label>允许工位<ElSelect v-model="binding.workstationIds" multiple filterable collapse-tags collapse-tags-tooltip :max-collapse-tags="3" size="small" :aria-label="'第'+(index+1)+'条绑定允许工位'" placeholder="选择允许办理的工位"><ElOption v-for="station in catalog.workstations" :key="station.id" :label="station.name || '未命名工位'" :value="station.id" /></ElSelect></label><div class="production-scan-settings__binding-actions"><label><input v-model="binding.isActive" type="checkbox">启用绑定</label><button type="button" class="pdm-secondary-action" :aria-label="'删除第'+(index+1)+'条绑定'" @click="bindings.splice(bindings.indexOf(binding), 1)">删除绑定</button></div></div>
            <button type="button" class="pdm-secondary-action" @click="addBinding">新增绑定</button><p v-if="!visibleBindings.length" class="production-scan-settings__hint">尚未配置办理人。未绑定账号可查看记录，不能确认此步骤。</p><p class="production-scan-settings__hint">个人绑定优先于角色绑定；停用个人绑定不会继承角色。办理账号仍须具备相应业务权限。</p><p v-if="selected?.post === 'Packing'" class="production-scan-settings__hint">本岗位可打印二维码信息标签，可使用包装PDA专用账号。</p>
          </section>
        <section class="production-scan-settings__pane production-scan-settings__workstations" aria-label="工位配置"><header><h3>工位管理 <small>{{ catalog.workstations.length }} 个</small></h3><button type="button" class="pdm-secondary-action" @click="addStation">新增工位</button></header><div v-if="deleteStationId" class="production-scan-settings__confirm" role="alert">确认删除此工位？{{ stationReferences ? `影响节点：${stationReferences}。删除后需重新选择工位或删除对应绑定。` : '' }}已有操作记录保留。<div><button type="button" class="pdm-secondary-action" @click="removeStation">确认删除工位</button><button type="button" class="pdm-secondary-action" @click="deleteStationId = ''">取消删除</button></div></div><div class="production-scan-settings__station-list-group"><div class="production-scan-settings__station-columns" aria-hidden="true"><span>工位名称</span><span>资源组</span><span>启用</span><span>操作</span></div>
          <ul class="production-scan-settings__station-list" aria-label="工位列表">
            <li v-for="(station,index) in catalog.workstations" :key="station.id" class="production-scan-settings__station">
              <input v-model.trim="station.name" :aria-label="'第'+(index+1)+'个工位名称'" maxlength="80" required>
              <input v-model.trim="station.resourceGroup" :aria-label="'第'+(index+1)+'个工位资源组'" maxlength="80" placeholder="接图、包装等可留空">
              <span class="production-scan-settings__station-enabled"><input v-model="station.isActive" type="checkbox" :aria-label="'启用工位 '+station.name"></span>
              <span class="production-scan-settings__station-action"><button type="button" class="pdm-secondary-action" :aria-label="'删除工位 '+station.name" @click="deleteStationId = station.id">删除</button></span>
            </li>
          </ul></div><p v-if="!catalog.workstations.length">尚未配置工位，请先新增实际使用的工位。</p></section>
        </div>
        <section v-show="tab === 'global'" class="production-scan-settings__pane" aria-label="流程控制与标签"><h3>暂停、取消与环节报废</h3><label>流程管理角色<ElSelect v-model="catalog.controlRoleCodes" multiple filterable size="small" aria-label="流程管理授权角色" placeholder="选择可暂停、恢复及取消的角色"><ElOption v-for="role in options.roles" :key="role.role" :label="role.name" :value="role.role" /></ElSelect></label><p>适用于自制和外协，可按环节处理部分数量。取消加工任务保留需求，取消业务需求减少需求；环节报废需质量权限，取消需分配权限。必须填写原因及实物处理说明。</p><h3>二维码信息标签</h3><div class="production-scan-settings__label-size"><label>标签宽<input v-model.number="catalog.labelWidthMm" aria-label="标签宽度（毫米）" type="number" min="40" max="150" required></label><span>×</span><label>标签高<input v-model.number="catalog.labelHeightMm" aria-label="标签高度（毫米）" type="number" min="25" max="150" required></label><span>毫米</span></div><p>通过系统打印窗口选择已连接的打印机，按设置尺寸打印包装标签。</p></section>
      </fieldset>
    </form>
  </section>
</template>
<style scoped>
.production-scan-settings{display:flex;flex-direction:column;gap:8px;flex:1;min-width:0;min-height:0;height:100%;padding:10px;box-sizing:border-box;overflow:hidden}
.production-scan-settings header{display:flex;align-items:center;justify-content:space-between;gap:8px;flex-wrap:wrap}.production-scan-settings h2{font-size:16px;margin:0}.production-scan-settings h3{font-size:14px;margin:0}.production-scan-settings p,.production-scan-settings small{font-size:12px;color:var(--pdm-muted);margin:0;line-height:1.6}.production-scan-settings__header>div:last-child{display:flex;gap:5px}.production-scan-settings button{font-size:12px;min-height:28px;padding:4px 9px}.production-scan-settings form{display:flex;flex-direction:column;gap:10px;flex:1;min-height:0}.production-scan-settings fieldset{padding:0;margin:0;border:0;min-width:0;min-height:0;flex:1}.production-scan-settings__tabs{display:flex;gap:18px;border-bottom:1px solid var(--pdm-border);flex-wrap:wrap}.production-scan-settings__tabs button{border:0;border-bottom:2px solid transparent;background:transparent;color:var(--pdm-text);padding:6px 0}.production-scan-settings__tabs [aria-pressed=true]{color:var(--pdm-theme-accent);border-bottom-color:var(--pdm-theme-accent)}
.production-scan-settings__layout{display:grid;grid-template-columns:repeat(3,minmax(0,1fr));gap:5px;height:100%;min-height:0}.production-scan-settings__canvas,.production-scan-settings__editor,.production-scan-settings__pane{min-width:0;min-height:0;border:1px solid var(--pdm-border);border-radius:5px;padding:12px;overflow:auto;display:flex;flex-direction:column;gap:12px}.production-scan-settings__canvas{background:var(--pdm-panel-muted,var(--pdm-surface))}.production-scan-settings__canvas header>select{width:190px}.production-scan-settings__toolbar{display:flex;gap:5px;flex-wrap:wrap}.production-scan-settings__chain{width:100%;max-width:600px;margin:0 auto}.production-scan-settings__chain>p{width:60%;text-align:center;padding:8px 0}.production-scan-settings__node-row{display:grid;grid-template-columns:60% minmax(0,1fr);gap:12px;align-items:start}.production-scan-settings__node{display:flex;align-items:center;gap:10px;width:100%;padding:10px!important;min-height:60px!important;text-align:left;border:1px solid var(--pdm-border);border-radius:5px;background:var(--pdm-surface);color:var(--pdm-text);cursor:pointer;overflow-wrap:anywhere}.production-scan-settings__node[aria-pressed=true]{border-color:var(--pdm-theme-accent);background:var(--pdm-accent-soft,var(--pdm-surface))}.production-scan-settings__node>span:last-child{display:grid;grid-template-columns:minmax(0,1fr) auto;gap:4px;width:100%;min-width:0}.production-scan-settings__node strong{font-size:13px;font-weight:600}.production-scan-settings__node small{font-size:11px}.production-scan-settings__node>span:last-child>span{grid-column:1/-1;font-size:12px;color:var(--pdm-muted)}.production-scan-settings__number{border-radius:50%;background:var(--pdm-theme-accent);color:var(--pdm-surface);font-size:12px;min-width:24px;height:24px;display:grid;place-items:center}.production-scan-settings__arrow{width:60%;text-align:center;color:var(--pdm-muted);height:18px}.production-scan-settings__branches{display:grid;gap:8px}.production-scan-settings__trace{display:grid;border-style:dashed;position:relative}.production-scan-settings__trace:before{content:'';position:absolute;left:-13px;top:29px;width:12px;border-top:1px dashed var(--pdm-border)}
.production-scan-settings label{display:grid;gap:5px;font-size:12px;min-width:0}.production-scan-settings input:not([type=checkbox]),.production-scan-settings select{height:28px;box-sizing:border-box;min-width:0;width:100%;border:1px solid var(--pdm-border);border-radius:4px;background:var(--pdm-surface);color:var(--pdm-text);padding:0 7px;font:inherit;font-size:12px}.production-scan-settings input[type=checkbox]{margin:0;width:13px;height:13px}.production-scan-settings__binding{display:grid;gap:9px;border-top:1px solid var(--pdm-border);padding-top:10px}.production-scan-settings__binding-actions{display:flex;align-items:center;justify-content:space-between;gap:6px}.production-scan-settings__binding-actions>label{display:flex;align-items:center;gap:5px}.production-scan-settings__label-size{display:flex;gap:8px;align-items:end;flex-wrap:wrap}.production-scan-settings__label-size label{width:100px}.production-scan-settings__label-size>span{padding-bottom:6px}.production-scan-settings__confirm{padding:10px;border:1px solid var(--pdm-border);border-radius:4px;font-size:12px;line-height:1.6}.production-scan-settings__confirm>div{display:flex;gap:5px;margin-top:7px}.production-scan-settings__station-list{list-style:none;padding:0;margin:0;background:var(--pdm-surface)}.production-scan-settings__station-columns,.production-scan-settings__station{display:grid;grid-template-columns:minmax(0,1fr) minmax(0,1fr) 50px 70px;align-items:center;gap:5px;text-align:center;font-size:12px}.production-scan-settings__station-columns{padding:5px 0;border-bottom:1px solid var(--pdm-border);font-weight:700}.production-scan-settings__station{padding:5px 0;border-bottom:1px solid var(--pdm-border);background:var(--pdm-surface)}.production-scan-settings__station input:not([type=checkbox]){text-align:center}.production-scan-settings__station-enabled,.production-scan-settings__station-action{display:flex;align-items:center;justify-content:center}.production-scan-settings .production-scan-settings__error{color:var(--pdm-danger)}.production-scan-settings :deep(.el-select__wrapper){min-height:28px;font-size:12px}.production-scan-settings :deep(.el-tag){max-width:160px}
@media(max-width:1100px){.production-scan-settings{height:auto;overflow:visible}.production-scan-settings__layout{grid-template-columns:1fr;height:auto}.production-scan-settings__canvas,.production-scan-settings__editor,.production-scan-settings__workstations{overflow:visible}}
@media(max-width:700px){.production-scan-settings__node>span:last-child{grid-template-columns:1fr}.production-scan-settings__node small{grid-column:1}.production-scan-settings__canvas,.production-scan-settings__editor,.production-scan-settings__workstations{padding:9px}.production-scan-settings__header>div:last-child{flex-wrap:wrap}.production-scan-settings__station-columns,.production-scan-settings__station{grid-template-columns:minmax(0,1fr) minmax(0,1fr) 35px 55px}}
</style>
