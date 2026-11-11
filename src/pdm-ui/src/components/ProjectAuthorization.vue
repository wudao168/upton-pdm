<script setup lang="ts">
import PersonName from './PersonName.vue'
import { computed, ref, watch } from 'vue'
import { requestJson } from '../api'
import { diagnosticRequestId } from '../clientDiagnostics'
import { ElMessage } from '../statusMessage'
import type { AuthorizationAssignment, AuthorizationTemplate } from '../authorization'
const props = defineProps<{ projectId: string; companyId: string; token: string }>()
interface ProjectAuthorizationData { revision: number; templates: AuthorizationTemplate[]; assignments: AuthorizationAssignment[]; users: { username: string; displayName: string }[] }
const data = ref<ProjectAuthorizationData>()
const rows = ref<AuthorizationAssignment[]>([]), person = ref(''), template = ref(''), scope = ref<'project' | 'subtree'>('project'), error = ref(''), busy = ref(false)
let generation = 0
const changed = computed(() => JSON.stringify(rows.value) !== JSON.stringify(data.value?.assignments ?? []))
const editable = (a: AuthorizationAssignment) => data.value?.templates.some(t => t.key === a.templateKey) === true
async function load() {
  const run = ++generation; data.value = undefined; rows.value = []; error.value = ''; busy.value = true
  try { const value = await requestJson<ProjectAuthorizationData>(`/api/authorization-center/projects/${props.projectId}`, {}, props.token); if (run !== generation) return; data.value = value; rows.value = JSON.parse(JSON.stringify(value.assignments)); template.value = value.templates[0]?.key ?? '' }
  catch { if (run === generation) error.value = '项目授权读取失败，请刷新后重试。' }
  finally { if (run === generation) busy.value = false }
}
function add() {
  if (!person.value || !template.value || !data.value) return
  if (rows.value.some(a => a.username === person.value && a.templateKey === template.value && a.scope === scope.value)) return ElMessage.warning('已存在相同授权')
  const raw = diagnosticRequestId().replaceAll('-', '')
  rows.value.push({ id: `${raw.slice(0,8)}-${raw.slice(8,12)}-${raw.slice(12,16)}-${raw.slice(16,20)}-${raw.slice(20,32)}`, username: person.value, templateKey: template.value, companyId: props.companyId, scope: scope.value, targetId: props.projectId })
}
async function save() {
  if (!data.value || busy.value) return
  const run = generation; busy.value = true
  try { await requestJson(`/api/authorization-center/projects/${props.projectId}`, { method: 'PUT', body: JSON.stringify({ revision: data.value.revision, assignments: rows.value }) }, props.token); if (run !== generation) return; ElMessage.success('项目岗位授权已保存'); window.dispatchEvent(new Event('pdm-authorization-changed')); await load() }
  catch (e) { if (run === generation) error.value = e instanceof Error ? e.message : '项目授权保存失败，请刷新后重试。' }
  finally { if (run === generation) busy.value = false }
}
watch(() => [props.projectId, props.token], load, { immediate: true })
</script>
<template>
  <details class="project-authorization"><summary>项目岗位授权</summary>
    <p>选择权限中心批准的模板。本处只分配人员与范围；公司岗位及其他授权仍按各自范围生效。</p>
    <p v-if="error" role="alert">{{ error }}</p>
    <div class="project-authorization-controls"><select v-model="person" aria-label="项目授权人员" :disabled="busy"><option value="">选择人员</option><option v-for="u in data?.users" :key="u.username" :value="u.username">{{ u.displayName }}</option></select><select v-model="template" aria-label="项目岗位模板" :disabled="busy"><option v-for="t in data?.templates" :key="t.key" :value="t.key">{{ t.name }}</option></select><select v-model="scope" aria-label="项目授权范围" :disabled="busy"><option value="project">仅本项目</option><option value="subtree">本项目及子项目</option></select><button class="pdm-primary-action" :disabled="busy || !person || !template" @click="add">新增授权</button><button class="pdm-secondary-action" :disabled="busy || changed" @click="load">刷新</button></div>
    <div class="project-authorization-table"><table><thead><tr><th>人员</th><th>岗位模板</th><th>范围</th><th>操作</th></tr></thead><tbody><tr v-for="a in rows" :key="a.id"><td><PersonName :name="data?.users.find(u => u.username === a.username)?.displayName || '姓名待补充'" /></td><td>{{ data?.templates.find(t => t.key === a.templateKey)?.name ?? '管理员分配的模板' }}</td><td>{{ a.scope === 'subtree' ? '本项目及子项目' : '仅本项目' }}</td><td><button v-if="editable(a)" class="pdm-text-action" :disabled="busy" @click="rows = rows.filter(r => r.id !== a.id)">撤销</button><span v-else>权限中心维护</span></td></tr><tr v-if="!rows.length"><td colspan="4">{{ busy ? '正在读取…' : '暂无单独分配的项目岗位。' }}</td></tr></tbody></table></div>
    <div class="project-authorization-controls"><span>{{ changed ? '有待保存的授权调整' : '授权与权限中心一致' }}</span><button class="pdm-primary-action" :disabled="busy || !changed" @click="save">保存</button><button class="pdm-secondary-action" :disabled="busy || !changed" @click="rows = JSON.parse(JSON.stringify(data?.assignments ?? []))">撤销修改</button></div>
  </details>
</template>
<style scoped>
.project-authorization{padding:12px;border:1px solid var(--pdm-border);border-radius:12px;margin:12px 0}.project-authorization p{font-size:12px;color:var(--pdm-muted)}.project-authorization-controls{display:flex;gap:8px;flex-wrap:wrap;align-items:center;margin:8px 0}.project-authorization-controls select{height:28px;border-radius:999px;border:1px solid var(--pdm-border);background:var(--pdm-surface);color:var(--pdm-text);padding:0 10px;max-width:260px}.project-authorization-controls button{width:80px;height:28px;border-radius:999px}.project-authorization-table{overflow:auto}.project-authorization table{width:100%;min-width:500px;border-collapse:collapse}.project-authorization td,.project-authorization th{text-align:center;vertical-align:middle;padding:8px;border-bottom:1px solid var(--pdm-border)}.project-authorization td button{height:24px;border-radius:999px}
</style>
