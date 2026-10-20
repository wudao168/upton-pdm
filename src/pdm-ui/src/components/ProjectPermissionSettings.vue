<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { ElMessage } from '../statusMessage'
import { getProjectPermissionSettings, updateProjectPermissionSettings } from '../api'
import type { PermissionDefinition, ProjectPermissionSettings, ProjectPositionDefinition } from '../types'
import { diagnosticRequestId } from '../clientDiagnostics'

const props = defineProps<{ token: string; canEdit: boolean }>()
const settings = ref<ProjectPermissionSettings | null>(null)
const loading = ref(false)
const saving = ref(false)
const draft = ref<ProjectPermissionSettings['rules']>({})
const customPositions = ref<ProjectPositionDefinition[]>([])
const selectedModule = ref('')
const positions = computed(() => [...(settings.value?.positions?.filter(item => item.isSystem) ?? []), ...[...customPositions.value].sort((a, b) => a.sortOrder - b.sortOrder)])
const groups = computed(() => {
  const modules = new Map<string, PermissionDefinition[]>()
  for (const permission of settings.value?.permissions ?? []) {
    if (permission.hidden) continue
    const items = modules.get(permission.module) ?? []
    items.push(permission)
    modules.set(permission.module, items)
  }
  return [...modules.entries()].map(([name, permissions]) => ({ name, permissions }))
})
const visibleGroups = computed(() => groups.value.filter(group => !selectedModule.value || group.name === selectedModule.value))
const positionsChanged = computed(() => JSON.stringify(customPositions.value) !== JSON.stringify(settings.value?.customPositions ?? []))
let loadGeneration = 0
const changedCount = computed(() => positions.value.reduce((count, position) => {
  const original = settings.value?.rules[position.key] ?? []
  const current = draft.value[position.key] ?? []
  return count + original.filter(code => !current.includes(code)).length + current.filter(code => !original.includes(code)).length
}, 0))
const changed = computed(() => changedCount.value > 0 || positionsChanged.value)
const editable = computed(() => props.canEdit && !!settings.value && !loading.value && !saving.value)

function resetDraft() {
  draft.value = Object.fromEntries(Object.entries(settings.value?.rules ?? {}).map(([position, permissions]) => [position, [...permissions]]))
  customPositions.value = (settings.value?.customPositions ?? []).map(item => ({ ...item }))
}

function addPosition() {
  if (!editable.value) return
  const key = `custom-${diagnosticRequestId()}`
  let name = '新岗位'
  while (positions.value.some(item => item.name === name)) name += '（新）'
  customPositions.value.push({ key, name, description: '', scope: 'self', isSystem: false, isActive: true, sortOrder: customPositions.value.length })
  draft.value[key] = []
}
function deletePosition(position: ProjectPositionDefinition) {
  if (!editable.value || (settings.value?.positions?.find(item => item.key === position.key)?.projectCount ?? 0) > 0) return
  customPositions.value = customPositions.value.filter(item => item.key !== position.key)
  delete draft.value[position.key]
}

async function load(token: string) {
  const generation = ++loadGeneration
  settings.value = null
  draft.value = {}
  customPositions.value = []
  if (!token) { loading.value = false; return }
  loading.value = true
  try {
    const value = await getProjectPermissionSettings(token)
    if (generation === loadGeneration) { settings.value = value; resetDraft() }
  }
  catch (error) { if (generation === loadGeneration) ElMessage.error(error instanceof Error ? error.message : '读取项目权限失败') }
  finally { if (generation === loadGeneration) loading.value = false }
}
function setPermission(position: string, code: string, checked: boolean) {
  if (!editable.value) return
  const permissions = draft.value[position] ?? []
  const next = new Set(permissions)
  if (checked) {
    const add = (value: string) => {
      if (next.has(value)) return
      next.add(value)
      for (const dependency of settings.value?.permissions?.find(item => item.code === value)?.dependencies ?? [])
        if (settings.value?.permissions?.some(item => item.code === dependency && item.projectAuthorization !== 'role')) add(dependency)
    }
    add(code)
  } else {
    next.delete(code)
    let removed = true
    while (removed) {
      removed = false
      for (const value of next) if (settings.value?.permissions?.find(item => item.code === value)?.dependencies?.some(dependency => permissions.includes(dependency) && !next.has(dependency))) { next.delete(value); removed = true }
    }
  }
  draft.value[position] = [...next]
}
async function save() {
  if (!editable.value || !changed.value) return
  const generation = loadGeneration
  saving.value = true
  try {
    const value = await updateProjectPermissionSettings({ rules: draft.value, customPositions: customPositions.value, schemaVersion: 2 }, props.token)
    if (generation !== loadGeneration) return
    settings.value = value
    resetDraft()
    ElMessage.success('项目岗位授权规则已保存并立即生效')
  } catch (error) { if (generation === loadGeneration) ElMessage.error(error instanceof Error ? error.message : '保存项目权限失败') }
  finally { saving.value = false }
}
watch(() => props.token, token => { void load(token) }, { immediate: true })
</script>

<template>
  <section class="pdm-project-manager pdm-project-permission-settings" aria-label="项目岗位授权规则">
    <div class="pdm-project-permission-body">
      <p class="pdm-project-permission-intro"><strong>角色权限</strong>决定可使用的业务功能，<strong>项目岗位</strong>限定岗位职责及适用项目，<strong>项目分工</strong>指定具体人员。多数操作需同时满足角色与岗位授权，兼任多个岗位时合并岗位授权。</p>
      <details class="pdm-project-permission-scopes">
        <summary>岗位管理及适用范围（{{ positions.length }}类）</summary>
        <dl aria-label="项目岗位适用范围"><template v-for="position in positions" :key="position.key"><dt>{{ position.name }}</dt><dd>{{ position.description }}</dd></template></dl>
        <p>“其他人员”指未担任上述岗位但具有项目内容访问权的人员；授予修改操作会扩大可操作人员范围。</p>
        <div class="pdm-project-position-editor" aria-label="自定义岗位管理">
          <div v-for="position in customPositions" :key="position.key" class="pdm-project-position-row">
            <label>岗位名称<input v-model="position.name" :aria-label="`${position.key}岗位名称`" maxlength="60" :disabled="!editable"></label>
            <label>说明<input v-model="position.description" :aria-label="`${position.name}岗位说明`" maxlength="300" :disabled="!editable"></label>
            <label>范围<select v-model="position.scope" :aria-label="`${position.name}岗位范围`" :disabled="!editable"><option value="self">仅本项目</option><option value="subtree">本项目及子项目</option></select></label>
            <label>排序<input v-model.number="position.sortOrder" type="number" :aria-label="`${position.name}岗位排序`" :disabled="!editable"></label>
            <label><input v-model="position.isActive" type="checkbox" :aria-label="`${position.name}岗位启用`" :disabled="!editable">启用</label>
            <span>已用于 {{ settings?.positions?.find(item => item.key === position.key)?.projectCount ?? 0 }} 个项目</span>
            <button v-if="canEdit" type="button" class="pdm-text-action" :disabled="!editable || (settings?.positions?.find(item => item.key === position.key)?.projectCount ?? 0) > 0" @click="deletePosition(position)">删除岗位</button>
          </div>
          <button v-if="canEdit" type="button" class="pdm-secondary-action" :disabled="!editable" @click="addPosition">新增岗位</button>
          <p>新岗位默认无权限，保存后可在项目分工中分配多人。停用后该岗位授权停止生效，历史分工保留；已被项目使用的岗位须先调整分工才能删除。</p>
        </div>
      </details>
      <section class="pdm-panel pdm-project-permission-matrix" aria-label="功能与岗位授权矩阵" :aria-busy="loading || saving">
        <header><h2>功能 × 岗位</h2><span>功能与操作从统一目录自动加载；新增功能发布后自动出现。</span><select v-model="selectedModule" aria-label="功能模块"><option value="">全部模块</option><option v-for="group in groups" :key="group.name" :value="group.name">{{ group.name }}</option></select></header>
        <p v-if="loading" role="status">正在加载岗位授权规则…</p>
        <p v-else-if="!settings" role="status">岗位授权规则未加载。请重新进入此页面。</p>
        <div class="pdm-project-permission-table-wrap" tabindex="0" aria-label="岗位授权表，可横向滚动">
          <table class="pdm-project-permission-table" :style="{ '--pdm-position-table-width': `${210 + positions.length * 150}px` }">
            <thead><tr><th scope="col">项目功能</th><th v-for="position in positions" :key="position.key" scope="col"><strong>{{ position.name }}</strong><small>{{ settings ? `岗位规则 ${draft[position.key]?.length ?? 0} 项` : '未加载' }}</small></th></tr></thead>
            <tbody v-for="group in visibleGroups" :key="group.name">
              <tr class="pdm-project-permission-group"><th :colspan="positions.length + 1" scope="colgroup">{{ group.name }}</th></tr>
              <tr v-for="permission in group.permissions" :key="permission.code">
                <th scope="row">{{ permission.name }}<small v-if="permission.code === 'project.budget.view'">角色或岗位任一授权即可</small><small v-else-if="permission.code === 'project.designer.assign'">另受组织管理职责授权</small></th>
                <td v-if="permission.projectAuthorization === 'role'" :colspan="positions.length" class="pdm-project-role-only">由角色权限及业务规则控制，原授权范围保持不变</td>
                <td v-for="position in permission.projectAuthorization === 'role' ? [] : positions" :key="position.key"><label :class="{ 'is-checked': draft[position.key]?.includes(permission.code) }"><input type="checkbox" :aria-label="`${position.name}：${permission.name}（${permission.module}）`" :checked="draft[position.key]?.includes(permission.code) ?? false" :disabled="!editable || !position.isActive" @change="setPermission(position.key, permission.code, ($event.target as HTMLInputElement).checked)"><span>{{ !position.isActive ? '岗位停用' : draft[position.key]?.includes(permission.code) ? '允许' : '未授予' }}</span></label></td>
              </tr>
            </tbody>
          </table>
        </div>
      </section>
      <aside class="pdm-project-permission-notes" aria-label="特殊授权规则">
        <p><strong>预算查看：</strong>预算查看可由角色独立授权，也可由项目岗位授权；岗位未勾选不表示该人员一定无权查看。仍需具备项目内容访问权，预算范围保持现有规则。</p>
        <p><strong>人员分配：</strong>分配执行工程师还按事业部负责人、机械主管等组织职责授权，本表未勾选不代表所有管理人员都无权分配。</p>
        <p><strong>其他限制：</strong>项目访问仍受公司及内容权限控制；图档存档、目录权限、审批任务和业务状态还需满足各自规则，本表不是全部项目操作的总开关。系统管理员等特权账号沿用现有规则。</p>
      </aside>
    </div>
    <footer class="pdm-project-permission-actions"><span role="status">{{ !canEdit ? '当前账号仅可查看项目岗位授权规则' : changed ? `有 ${changedCount} 项权限或岗位定义修改未保存` : '岗位规则不等于人员最终权限' }}</span><template v-if="canEdit"><button type="button" class="pdm-secondary-action" :disabled="!editable || !changed" @click="resetDraft">撤销未保存修改</button><button type="button" class="pdm-primary-action" :disabled="!editable || !changed" @click="save">{{ saving ? '保存中…' : '保存并立即生效' }}</button></template></footer>
  </section>
</template>

<style scoped>
.pdm-project-permission-settings { min-width: 0; }
.pdm-project-position-editor { display: grid; gap: 10px; margin-top: 12px; }
.pdm-project-position-row { display: flex; align-items: center; gap: 10px; flex-wrap: wrap; padding: 10px 0; border-top: 1px solid var(--pdm-border); }.pdm-project-position-row label { display: grid; gap: 4px; }.pdm-project-position-row input,.pdm-project-position-row select,.pdm-project-permission-matrix header select { padding: 5px; border: 1px solid var(--pdm-border); border-radius: 4px; }.pdm-project-position-row input[type=number] { width: 64px; }.pdm-project-role-only { color: var(--pdm-muted); }
.pdm-project-permission-body { min-height: 0; overflow: auto; flex: 1 1 auto; padding: 0 2px 14px; }
.pdm-project-permission-intro { margin: 0 0 12px; color: var(--pdm-text-soft); font-size: 12px; line-height: 1.8; }
.pdm-project-permission-intro strong { color: var(--pdm-text); }
.pdm-project-permission-scopes { margin-bottom: 12px; border: 1px solid var(--pdm-border); border-radius: 6px; padding: 10px 12px; background: var(--pdm-surface); font-size: 12px; }
.pdm-project-permission-scopes summary { color: var(--pdm-blue); cursor: pointer; }
.pdm-project-permission-scopes dl { display: grid; grid-template-columns: 210px minmax(0, 1fr); margin: 10px 0; }
.pdm-project-permission-scopes dt,.pdm-project-permission-scopes dd { margin: 0; padding: 7px 8px; border-top: 1px solid var(--pdm-border); line-height: 1.6; }
.pdm-project-permission-scopes dt { font-weight: 600; }.pdm-project-permission-scopes p { margin: 8px 0 0; color: var(--pdm-muted); line-height: 1.6; }
.pdm-project-permission-matrix { padding: 0; overflow: hidden; }
.pdm-project-permission-matrix > header { display: flex; flex-wrap: wrap; align-items: center; gap: 8px 18px; padding: 12px; }
.pdm-project-permission-matrix h2 { margin: 0; font-size: 14px; }.pdm-project-permission-matrix > header span,.pdm-project-permission-matrix > p { color: var(--pdm-muted); font-size: 12px; }.pdm-project-permission-matrix > p { padding: 0 12px; }
.pdm-project-permission-table-wrap { max-height: 58vh; overflow: auto; }
.pdm-project-permission-settings .pdm-project-permission-matrix table.pdm-project-permission-table { width: 100%; min-width: var(--pdm-position-table-width, 960px) !important; max-width: none !important; border-collapse: separate; border-spacing: 0; table-layout: fixed; font-size: 12px; }
.pdm-project-permission-matrix th,.pdm-project-permission-matrix td { padding: 9px 10px; border-top: 1px solid var(--pdm-border); border-right: 1px solid var(--pdm-border); text-align: center; vertical-align: middle; }
.pdm-project-permission-matrix th:last-child,.pdm-project-permission-matrix td:last-child { border-right: 0; }
.pdm-project-permission-matrix thead th { position: sticky; top: 0; z-index: 2; background: var(--pdm-table-header); line-height: 1.6; }
.pdm-project-permission-settings .pdm-project-permission-matrix table.pdm-project-permission-table th:first-child { width: 210px !important; text-align: left; }
.pdm-project-permission-matrix tr:not(.pdm-project-permission-group) > th:first-child { position: sticky; left: 0; z-index: 1; background: var(--pdm-surface); }
.pdm-project-permission-matrix thead th:first-child { z-index: 3; background: var(--pdm-table-header) !important; }
.pdm-project-permission-matrix small { display: block; margin-top: 4px; color: var(--pdm-muted); font-size: 11px; font-weight: 400; }
.pdm-project-permission-group th { background: var(--pdm-table-header); color: var(--pdm-muted); text-align: left; }
.pdm-project-permission-matrix td label { display: inline-flex; align-items: center; justify-content: center; gap: 7px; min-height: 26px; color: var(--pdm-muted); cursor: pointer; }
.pdm-project-permission-matrix td label.is-checked { color: var(--pdm-theme-accent); }.pdm-project-permission-matrix input { accent-color: var(--pdm-theme-accent); }
.pdm-project-permission-notes { margin-top: 12px; padding: 4px 12px; border-left: 3px solid var(--pdm-border); color: var(--pdm-muted); font-size: 12px; line-height: 1.7; }
.pdm-project-permission-notes p { margin: 6px 0; }
.pdm-project-permission-actions { display: flex; flex-wrap: wrap; flex: 0 0 auto; align-items: center; gap: 8px; padding: 12px 0 0; border-top: 1px solid var(--pdm-border); }
.pdm-project-permission-actions > span { flex: 1 1 200px; color: var(--pdm-muted); font-size: 12px; }
@media (max-width: 700px) { .pdm-project-permission-scopes dl { grid-template-columns: 1fr; }.pdm-project-permission-scopes dd { border-top: 0; padding-top: 0; } }
</style>
