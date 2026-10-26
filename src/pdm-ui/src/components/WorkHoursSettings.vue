<script setup lang="ts">
import { computed, onBeforeUnmount, reactive, ref, watch } from 'vue'
import { RefreshCw } from '@lucide/vue'
import { ElMessageBox } from 'element-plus'
import { ElMessage } from '../statusMessage'
import { userErrorMessage } from '../userErrorMessage'
import { readWorkHoursSettings, saveWorkHoursSettings, removeWorkHoursException } from '../api'
import type { WorkHoursDepartmentGroup, WorkHoursManagement } from '../workHours'
const props = defineProps<{ token: string; companyId: string }>()
const emit = defineEmits<{ changed: []; dirtyChange: [value: boolean] }>()
const data = ref<WorkHoursManagement>(), loading = ref(false), saving = ref(false), error = ref(''), historyOpen = ref(false)
const departments = reactive<Record<string, boolean>>({}), people = reactive<Record<string, boolean>>({})
const selectedDepartmentId = ref('')
let generation = 0
const groups = computed(() => data.value?.departmentGroups ?? [])
const activeGroup = computed(() => groups.value.find(group => group.id === selectedDepartmentId.value))
const departmentChanges = computed(() => data.value?.canAuthorize ? groups.value.filter(g => departments[g.id] !== g.participating).map(g => ({unitId:g.id,participating:!!departments[g.id]})) : [])
const peopleChanges = computed(() => groups.value.flatMap(g => departments[g.id] ? g.people.filter(p => p.canEdit && people[p.username] !== p.required).map(p => ({username:p.username,required:!!people[p.username]})) : []))
const dirty = computed(() => departmentChanges.value.length + peopleChanges.value.length > 0)
const busy = computed(() => loading.value || saving.value)
const selected = (g: WorkHoursDepartmentGroup) => g.people.filter(p => people[p.username]).length
const editable = (g: WorkHoursDepartmentGroup) => departments[g.id] && g.people.some(p => p.canEdit)
const name = (username: string) => data.value?.people.find(p => p.username === username)?.displayName ?? groups.value.flatMap(g => g.people).find(p => p.username === username)?.displayName ?? username
const departmentName = (id: string) => groups.value.find(g => g.id === id)?.name ?? data.value?.units.find(u => u.id === id)?.name ?? '已归档部门'
function apply(value: WorkHoursManagement) {
  data.value = value
  for (const key of Object.keys(departments)) delete departments[key]
  for (const key of Object.keys(people)) delete people[key]
  for (const group of value.departmentGroups ?? []) {
    departments[group.id] = group.participating
    for (const person of group.people) people[person.username] = person.required
  }
  if (!value.departmentGroups?.some(group => group.id === selectedDepartmentId.value)) selectedDepartmentId.value = value.departmentGroups?.[0]?.id ?? ''
}
async function mayDiscard() {
  if (!dirty.value) return true
  try { await ElMessageBox.confirm('放弃尚未保存的部门或人员勾选？已经保存的设置不会改变。','未保存的工时设置',{confirmButtonText:'放弃更改',cancelButtonText:'继续编辑'}); return true }
  catch { return false }
}
async function load() {
  if (busy.value || !await mayDiscard()) return
  const request = ++generation; loading.value = true; error.value = ''
  try { const value = await readWorkHoursSettings(props.token); if (request === generation) apply(value) }
  catch (cause) { if (request === generation) error.value = userErrorMessage(cause instanceof Error ? cause.message : '工时设置读取失败，请重试。') }
  finally { if (request === generation) loading.value = false }
}
function toggleDepartment(group: WorkHoursDepartmentGroup, checked: boolean) {
  if (!data.value?.canAuthorize || busy.value) return
  departments[group.id] = checked
  if (!checked) for (const person of group.people) people[person.username] = person.required
}
function toggleAll(group: WorkHoursDepartmentGroup, checked: boolean) {
  if (busy.value || !departments[group.id]) return
  for (const person of group.people) if (person.canEdit) people[person.username] = checked
}
async function save() {
  if (busy.value || !data.value || !dirty.value) return
  const request = generation
  const input = {companyId:props.companyId,rowVersion:data.value.rowVersion,departments:departmentChanges.value,people:peopleChanges.value}
  saving.value = true; error.value = ''
  try {
    const value = await saveWorkHoursSettings(input,props.token)
    if (request !== generation) return
    apply(value); emit('changed'); ElMessage.success('各部门的填报范围已保存，从今天起生效；历史工时与修改记录已保留。')
  } catch (cause) { if (request === generation) error.value = userErrorMessage(cause instanceof Error ? cause.message : '设置保存失败，勾选内容已保留，请核对后重试。') }
  finally { if (request === generation) saving.value = false }
}
async function cancel() { if (data.value && !busy.value && await mayDiscard()) apply(data.value) }
async function removeException(id: string) {
  if (dirty.value) return ElMessage.warning('请先保存或取消名单修改，再处理特殊记录。')
  const request = generation
  try {
    const answer = await ElMessageBox.prompt('撤销后将按原应填要求重新检查，请填写原因。','撤销特殊处理',{inputValidator:value=>!!value?.trim()||'请填写原因',confirmButtonText:'确认撤销',cancelButtonText:'取消'})
    if (request !== generation || !data.value) return
    saving.value = true
    const value = await removeWorkHoursException({id,reason:answer.value,rowVersion:data.value.rowVersion},props.token)
    if (request !== generation) return
    apply(value); emit('changed'); ElMessage.success('特殊处理已撤销，修改记录已保留。')
  } catch (cause) { if (request === generation && cause instanceof Error) error.value = userErrorMessage(cause.message) }
  finally { if (request === generation) saving.value = false }
}
function beforeUnload(event: BeforeUnloadEvent) { if (dirty.value) { event.preventDefault(); event.returnValue = '' } }
window.addEventListener('beforeunload',beforeUnload)
onBeforeUnmount(() => { generation++; emit('dirtyChange',false); window.removeEventListener('beforeunload',beforeUnload) })
watch(dirty,value=>emit('dirtyChange',value),{flush:'sync'})
watch(() => [props.token,props.companyId],() => {
  generation++; data.value=undefined; historyOpen.value=false; loading.value=false; saving.value=false; error.value=''
  selectedDepartmentId.value=''
  void load()
},{immediate:true})
</script>
<template>
  <section class="wh-settings" aria-label="工时设置" :aria-busy="busy">
    <p v-if="error" class="wh-setting-error" role="alert">{{ error }}</p>
    <div class="wh-settings-layout">
        <aside class="pdm-panel wh-department-list" aria-label="部门列表">
          <h3>部门</h3>
          <div class="wh-department-options"><button v-for="group in groups" :key="group.id" class="wh-department-name" :class="{active:group.id === selectedDepartmentId}" :aria-pressed="group.id === selectedDepartmentId" aria-controls="wh-selected-department" @click="selectedDepartmentId = group.id"><span class="wh-department-label" :title="group.name">{{ group.name }}</span><small v-if="departments[group.id]" class="wh-department-participating">参与填报</small></button></div>
          <div class="wh-settings-actions"><button :disabled="busy" @click="load"><RefreshCw :size="13" />刷新设置</button><button v-if="dirty" :disabled="busy" @click="cancel">取消更改</button><button class="pdm-primary-action" :disabled="busy || !dirty" @click="save">保存设置</button></div>
        </aside>
        <section v-if="data && activeGroup" id="wh-selected-department" class="pdm-panel wh-department-group" :aria-label="activeGroup.name">
          <header><strong>{{ activeGroup.name }}</strong><label class="wh-participation" :title="data.canAuthorize ? '管理员设置部门是否参与填报' : '参与部门由管理员维护'"><input type="checkbox" :checked="departments[activeGroup.id]" :disabled="!data.canAuthorize || busy" :aria-label="`${activeGroup.name}参与工时`" @change="toggleDepartment(activeGroup,($event.target as HTMLInputElement).checked)">参与填报</label><span class="wh-group-count">{{ departments[activeGroup.id] ? `${activeGroup.people.length} 人 · 已选 ${selected(activeGroup)} 人` : '未参与' }}</span></header>
          <div class="wh-department-body">
            <div class="wh-staff-heading"><span>应填人员 · {{ editable(activeGroup) ? '可编辑授权范围内人员' : '只读' }}</span><label><input type="checkbox" :checked="activeGroup.people.length > 0 && selected(activeGroup) === activeGroup.people.length" :indeterminate="selected(activeGroup) > 0 && selected(activeGroup) < activeGroup.people.length" :disabled="busy || !editable(activeGroup)" :aria-label="`全选${activeGroup.name}人员`" @change="toggleAll(activeGroup,($event.target as HTMLInputElement).checked)">全选本部门</label></div>
            <p v-if="!departments[activeGroup.id]" class="wh-setting-note">该部门未参与工时填报，人员名单保留，仅供查看。</p>
            <div class="wh-person-grid"><label v-for="person in activeGroup.people" :key="person.username" :title="`${person.displayName} · ${person.username} · ${person.department}`"><input v-model="people[person.username]" type="checkbox" :disabled="busy || !departments[activeGroup.id] || !person.canEdit" :aria-label="`${activeGroup.name}${person.displayName}需要填报`"><span>{{ person.displayName }}</span><small>{{ person.department === activeGroup.name ? '部门直属' : person.department }}</small></label></div>
            <p v-if="!activeGroup.people.length" class="wh-setting-note">该部门暂无已分配的人员。</p>
          </div>
        </section>
        <section v-else class="pdm-panel wh-department-group wh-department-empty"><p class="wh-setting-note">{{ loading ? '正在读取部门与人员…' : data ? '暂无可配置部门，请先在组织架构中建立部门并分配人员。' : '请刷新设置以读取部门与人员。' }}</p></section>
    </div>
    <template v-if="data">
      <footer class="wh-settings-footer"><span>默认每天8小时，保存当天生效。未勾选人员不提醒，历史工时保留。</span><button class="wh-text-button" @click="historyOpen=true">修改记录</button></footer>
      <p v-if="data.canAuthorize && data.unassignedPeopleCount" class="wh-setting-note">{{ data.unassignedPeopleCount }}个启用账号未分配部门；如需纳入填报，请先在组织架构中分配。</p>
    </template>
    <el-drawer v-model="historyOpen" title="工时设置修改记录" size="min(780px,95vw)" append-to-body>
      <p v-if="error" class="wh-setting-error" role="alert">{{ error }}</p>
      <details v-if="data?.exceptions.length" class="wh-settings-history"><summary>特殊处理记录（{{ data.exceptions.length }}条）</summary><div v-for="item in data.exceptions" :key="item.id" class="wh-exception-record"><strong>{{ name(item.username) }} · {{ item.from }} 至 {{ item.to }}</strong><p>{{ item.kind === 'mute' ? '暂停提醒' : item.hours === 0 ? '豁免填报' : `应填${item.hours}小时` }} · {{ item.reason }}{{ item.kind === 'mute' && item.to < data!.today ? '（已到期）' : '' }}</p><button v-if="data?.canAdjust" :disabled="busy || dirty" @click="removeException(item.id)">撤销特殊处理</button></div></details>
      <p v-if="!data?.history.length" class="wh-setting-note">暂无设置修改记录。</p>
      <details v-for="h in [...(data?.history ?? [])].reverse()" :key="h.id" class="wh-settings-history"><summary>{{ new Date(h.at).toLocaleString('zh-CN',{hour12:false}) }} · {{ h.actor }} · {{ h.action }}</summary><p>原因：{{ h.reason }}</p><div v-for="(records,index) in [h.beforeDepartments ?? [],h.afterDepartments ?? []]" :key="`d${index}`"><p v-for="(d,i) in records" :key="i">{{ index ? '修改后' : '修改前' }}：{{ departmentName(d.unitId) }} · {{ d.from }} 至 {{ d.to || '长期' }} · 参与填报</p></div><div v-for="(records,index) in [h.beforeRequirements,h.afterRequirements]" :key="`r${index}`"><p v-for="r in records" :key="r.id">{{ index ? '修改后' : '修改前' }}：{{ name(r.username) }} · {{ r.from }} 至 {{ r.to || '长期' }} · {{ r.hours }}小时／天</p></div><div v-for="(records,index) in [h.beforeExceptions,h.afterExceptions]" :key="`e${index}`"><p v-for="e in records" :key="e.id">{{ index ? '修改后' : '修改前' }}：{{ name(e.username) }} · {{ e.from }} 至 {{ e.to }} · {{ e.kind === 'mute' ? '暂停提醒' : `应填${e.hours}小时` }} · {{ e.reason }}</p></div><div v-for="(records,index) in [h.beforeGrants,h.afterGrants]" :key="`g${index}`"><p v-for="g in records" :key="g.username">{{ index ? '修改后' : '修改前' }}：{{ name(g.username) }} · {{ g.unitIds.map(departmentName).join('、') }}</p></div></details>
    </el-drawer>
  </section>
</template>
<style scoped>
.wh-settings{display:flex;flex:1;flex-direction:column;gap:10px;min-width:0;min-height:360px}.wh-settings-footer,.wh-settings>.wh-setting-note,.wh-settings>.wh-setting-error{flex-shrink:0}.wh-settings-layout{display:grid;grid-template-columns:200px minmax(0,1fr);gap:5px;flex:1;min-height:0}.wh-department-list{display:flex;flex-direction:column;padding:10px;min-height:0;overflow:hidden}.wh-department-options{flex:1;min-height:0;overflow:auto}.wh-department-list h3{flex-shrink:0}.wh-department-empty{padding:12px}.wh-department-list h3{font-size:14px;margin:0 0 8px;padding:4px 6px}.wh-settings .wh-department-name{display:flex;width:100%;border:0;background:transparent;justify-content:flex-start;text-align:left;padding:10px 8px;font-weight:600;overflow-wrap:anywhere}.wh-settings .wh-department-name:hover,.wh-settings .wh-department-name.active{background:var(--pdm-theme-accent-soft);color:var(--pdm-theme-accent)}.wh-department-group{display:flex;flex-direction:column}.wh-department-group>header{flex-shrink:0}.wh-department-group>header>strong{font-size:14px}.wh-settings-actions,.wh-settings-footer{display:flex;align-items:center;justify-content:space-between;gap:10px;flex-wrap:wrap}.wh-setting-note{font-size:12px;color:var(--pdm-muted);line-height:1.7;margin:5px 0}.wh-settings-actions{flex-shrink:0;justify-content:flex-start;gap:6px;padding-top:10px;margin-top:8px;border-top:1px solid var(--pdm-border)}.wh-settings-actions>button{flex:1 0 calc(50% - 3px);font-size:12px;white-space:nowrap;padding:6px}.wh-settings button,.wh-exception-record button{font:inherit;display:inline-flex;align-items:center;justify-content:center;gap:5px;border:1px solid var(--pdm-border);border-radius:var(--pdm-radius-control);padding:6px 10px;background:var(--pdm-surface);color:var(--pdm-text);min-height:28px}.wh-settings button:disabled{opacity:.5}.wh-settings .pdm-primary-action{background:var(--pdm-theme-accent);border-color:var(--pdm-theme-accent);color:var(--pdm-theme-on-accent,#fff)}.wh-department-group{overflow:hidden;min-width:0}.wh-department-group>header{display:flex;align-items:center;gap:18px;flex-wrap:wrap;padding:7px 14px;min-height:47px;background:var(--pdm-table-header)}.wh-participation,.wh-staff-heading>label{display:inline-flex;align-items:center;gap:7px;font-size:12px}.wh-group-count{margin-left:auto;color:var(--pdm-muted);font-size:12px;font-variant-numeric:tabular-nums}.wh-settings input[type=checkbox]{width:15px;height:15px;min-height:0;padding:0;margin:0;accent-color:var(--pdm-theme-accent);flex-shrink:0}.wh-department-body{flex:1;min-height:0;overflow:auto;border-top:1px solid var(--pdm-border);padding:10px 18px 13px}.wh-staff-heading{display:flex;align-items:center;justify-content:space-between;gap:12px;flex-wrap:wrap;font-size:12px;color:var(--pdm-muted);margin-bottom:7px}.wh-staff-heading>label{color:var(--pdm-text)}.wh-person-grid{display:grid;grid-template-columns:repeat(3,minmax(0,1fr));column-gap:22px;row-gap:1px}.wh-person-grid>label{display:flex;align-items:center;gap:8px;min-height:36px;min-width:0;padding:5px 0}.wh-person-grid>label>span{font-size:12px;flex-shrink:0}.wh-person-grid small{font-size:11px;color:var(--pdm-muted);overflow:hidden;text-overflow:ellipsis;white-space:nowrap;min-width:0}.wh-person-grid>label:has(input:disabled){color:var(--pdm-muted)}.wh-settings-footer{color:var(--pdm-muted);font-size:11px;line-height:1.7}.wh-settings .wh-text-button{padding:3px 0;border:0;background:transparent;color:var(--pdm-theme-accent)}.wh-setting-error{padding:10px;color:var(--pdm-danger);background:var(--pdm-danger-soft);margin:0}.wh-settings-history{padding:12px 0;border-bottom:1px solid var(--pdm-border);font-size:12px}.wh-settings-history summary{cursor:pointer;font-weight:600}.wh-settings-history p{overflow-wrap:anywhere}.wh-exception-record{padding:10px;border-bottom:1px solid var(--pdm-border)}
.wh-department-label{min-width:0;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}.wh-department-participating{flex-shrink:0;margin-left:auto;padding:2px 5px;border-radius:4px;background:var(--pdm-green-soft);color:var(--pdm-green);font-size:11px;font-weight:500;line-height:1.4;white-space:nowrap}
@media(max-width:1100px){.wh-person-grid{grid-template-columns:repeat(2,minmax(0,1fr))}}@media(max-width:760px){.wh-person-grid{grid-template-columns:minmax(0,1fr)}.wh-department-group>header{gap:7px 12px;padding:9px 10px}.wh-group-count{flex-basis:100%;margin-left:0}.wh-department-body{padding:10px 12px}.wh-settings-actions{width:100%}}
</style>
