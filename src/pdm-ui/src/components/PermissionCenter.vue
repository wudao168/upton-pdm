<script setup lang="ts">
import PersonName from './PersonName.vue'
import { computed, ref, watch } from 'vue'
import { ElMessageBox } from 'element-plus'
import { ElMessage } from '../statusMessage'
import { diagnosticRequestId } from '../clientDiagnostics'
import { getAuthorization, saveAuthorization, previewAuthorization, type AuthorizationAssignment, type AuthorizationDirectory, type AuthorizationSettings, type AuthorizationDecision } from '../authorization'
import { authorizationRelationGroups, authorizationRelationLabel, type RelationOption } from '../authorizationRelations'
import AuthorizationRelationChoices from './AuthorizationRelationChoices.vue'
import type { RolePermissionSettings } from '../types'

const props = defineProps<{ token: string; companyId: string; roles?: readonly Pick<RolePermissionSettings, 'role' | 'isSystem' | 'userCount'>[] }>()
const pages = [
  { key: 'templates', label: '角色管理' },
  { key: 'assignments', label: '人员授权' },
  { key: 'preview', label: '权限检查' },
] as const
const assignmentSources = [
  { key: 'all', label: '全部授权' },
  { key: 'inherited', label: '公司角色' },
  { key: 'direct', label: '指定授权' },
] as const
const resultFilters = [
  { key: 'all', label: '全部结果' },
  { key: 'allowed', label: '允许' },
  { key: 'denied', label: '未授权' },
] as const
const data = ref<AuthorizationDirectory>()
const draft = ref<AuthorizationSettings>()
const loading = ref(false)
const saving = ref(false)
const error = ref('')
const tab = ref<typeof pages[number]['key']>('templates')
const selected = ref('')
const module = ref('')
const templateSearch = ref('')
const permissionSearch = ref('')
const userSearch = ref('')
const username = ref('')
const assignmentSource = ref<typeof assignmentSources[number]['key']>('all')
const assignmentTemplateKey = ref('')
const projectSearch = ref('')
const projectId = ref('')
const resultSearch = ref('')
const resultFilter = ref<typeof resultFilters[number]['key']>('all')
const decisions = ref<AuthorizationDecision[]>([])
const checking = ref(false)
const checkError = ref('')
const relationEdit = ref<{ code: string; scopes: string[] }>()
let generation = 0
let checkRequest = 0

function matches(query: string, ...labels: (string | undefined)[]) {
  return labels.join(' ').toLowerCase().includes(query.trim().toLowerCase())
}

const template = computed(() => draft.value?.templates.find(t => t.key === selected.value))
const changed = computed(() => JSON.stringify(draft.value) !== JSON.stringify(data.value?.settings))
const roleChanged = computed(() => JSON.stringify(draft.value?.templates) !== JSON.stringify(data.value?.settings.templates))
const editable = computed(() => data.value?.canEdit && !saving.value && !loading.value)
const deleteBlockedReason = computed(() => {
  const role = props.roles?.find(item => item.role === template.value?.roleCode)
  if (role?.isSystem) return '系统预置角色不能删除。'
  if (role?.userCount) return '请先在用户设置中解除该角色的分配。'
  if (draft.value?.assignments.some(assignment => assignment.templateKey === selected.value)) return '请先撤销该角色的指定授权。'
  return ''
})
const filteredTemplates = computed(() => draft.value?.templates.filter(t => matches(templateSearch.value, t.name)) ?? [])
const filteredUsers = computed(() => data.value?.users.filter(u => matches(userSearch.value, u.displayName, u.username)) ?? [])
const filteredProjects = computed(() => data.value?.projects.filter(p => matches(projectSearch.value, p.code, p.name)) ?? [])
const projectById = computed(() => new Map(data.value?.projects.map(project => [project.id, project])))
const currentProject = computed(() => projectById.value.get(projectId.value))
const projectContext = computed(() => currentProject.value ? `${currentProject.value.code} · ${currentProject.value.name}` : '公司级操作')
const matchingPermissions = computed(() => data.value?.permissions.filter(p => matches(permissionSearch.value, p.name, p.module)) ?? [])
const permissions = computed(() => matchingPermissions.value.filter(p => !module.value || p.module === module.value))
const modules = computed(() => {
  const granted = new Set(template.value?.grants.filter(g => g.scopes.length).map(g => g.permission))
  const counts = new Map<string, { total: number; enabled: number }>([['', { total: 0, enabled: 0 }]])
  for (const permission of matchingPermissions.value) {
    const count = counts.get(permission.module) ?? { total: 0, enabled: 0 }
    count.total++
    const all = counts.get('')!
    all.total++
    if (granted.has(permission.code)) { count.enabled++; all.enabled++ }
    counts.set(permission.module, count)
  }
  return [...counts].map(([key, count]) => ({ key, label: key || '全部模块', ...count }))
})
const scopes = computed(() => ({ ...data.value?.scopes, ...Object.fromEntries((draft.value?.positions ?? []).filter(p => !p.isSystem).map(p => [p.key, `${p.name}（项目分工）`])) }))
const selectedUser = computed(() => data.value?.users.find(u => u.username === username.value))
const directAssignments = computed(() => draft.value?.assignments.filter(a => a.companyId === props.companyId && a.username === username.value) ?? [])
const inherited = computed(() => {
  const user = selectedUser.value
  if (!user) return []
  const roles = new Set((user.roles?.length ? user.roles : [user.role]).map(role => role.toLowerCase()))
  return draft.value?.templates.filter(t => t.roleCode && roles.has(t.roleCode.toLowerCase())) ?? []
})
const sourceCounts = computed(() => ({
  all: inherited.value.length + directAssignments.value.length,
  inherited: inherited.value.length,
  direct: directAssignments.value.length,
}))
const assignmentTemplates = computed(() => draft.value?.templates.filter(t => t.isActive) ?? [])
const canAddAssignment = computed(() => Boolean(editable.value && selectedUser.value && assignmentTemplates.value.some(t => t.key === assignmentTemplateKey.value)))
const showInherited = computed(() => assignmentSource.value !== 'direct')
const showDirect = computed(() => assignmentSource.value !== 'inherited')
const permissionByCode = computed(() => new Map(data.value?.permissions.map(p => [p.code, p])))
const relationPermission = computed(() => relationEdit.value ? permissionByCode.value.get(relationEdit.value.code) : undefined)
const savedRelationScopes = computed(() => relationEdit.value ? grant(relationEdit.value.code)?.scopes ?? [] : [])
const relationGroups = computed(() => relationPermission.value
  ? authorizationRelationGroups(relationPermission.value, scopes.value, draft.value?.positions ?? [], savedRelationScopes.value)
  : [])
const commonRelationGroups = computed(() => relationGroups.value.filter(group => !group.secondary))
const extraRelationGroups = computed(() => relationGroups.value.filter(group => group.secondary))
const extraRelationOptions = computed(() => extraRelationGroups.value.flatMap(group => group.options))
const previewRows = computed(() => decisions.value
  .filter(d => resultFilter.value === 'all' || d.allowed === (resultFilter.value === 'allowed'))
  .map(d => ({ ...d, definition: permissionByCode.value.get(d.permission) }))
  .filter(d => matches(resultSearch.value, d.definition?.name, d.definition?.module, d.permission)))
const canCheck = computed(() => Boolean(selectedUser.value && data.value && !loading.value && !saving.value && !changed.value))

function searchPermissions(value: string) {
  permissionSearch.value = value
  if (module.value && matchingPermissions.value.length && !modules.value.some(item => item.key === module.value)) module.value = ''
}

function reset() {
  relationEdit.value = undefined
  draft.value = data.value ? JSON.parse(JSON.stringify(data.value.settings)) : undefined
  if (!draft.value?.templates.some(role => role.key === selected.value)) selected.value = draft.value?.templates[0]?.key ?? ''
}
async function load() {
  const run = ++generation
  relationEdit.value = undefined
  loading.value = true
  error.value = ''
  data.value = undefined
  draft.value = undefined
  try {
    const value = await getAuthorization(props.token)
    if (run !== generation) return
    data.value = value
    reset()
    if (!value.settings.templates.some(t => t.key === assignmentTemplateKey.value && t.isActive)) assignmentTemplateKey.value = ''
    if (!value.permissions.some(p => p.module === module.value)) {
      const granted = new Set(template.value?.grants.filter(g => g.scopes.length).map(g => g.permission))
      module.value = (value.permissions.find(p => granted.has(p.code)) ?? value.permissions[0])?.module ?? ''
    }
    if (!value.users.some(u => u.username === username.value)) username.value = ''
    if (!value.projects.some(p => p.id === projectId.value)) projectId.value = ''
  }
  catch { if (run === generation) error.value = '权限配置读取失败，请重试；未读取成功时不会使用默认授权。' }
  finally { if (run === generation) loading.value = false }
}
function grant(code: string) { return template.value?.grants.find(g => g.permission === code) }
function openRelations(code: string) {
  const current = grant(code)
  if (current) relationEdit.value = { code, scopes: [...current.scopes] }
}
function hasSavedRelation(options: RelationOption[]) {
  return options.some(option => savedRelationScopes.value.includes(option.key))
}
function selectedRelationCount(options: RelationOption[]) {
  return options.filter(option => relationEdit.value?.scopes.includes(option.key)).length
}
function finishRelations() {
  const edit = relationEdit.value
  if (editable.value && edit) {
    const current = grant(edit.code)
    if (current) {
      current.scopes = [
        ...current.scopes.filter(key => edit.scopes.includes(key)),
        ...edit.scopes.filter(key => !current.scopes.includes(key)),
      ]
    }
  }
  relationEdit.value = undefined
}
function toggle(code: string, enabled: boolean) {
  if (!editable.value || !template.value || !data.value) return
  const target = template.value
  if (enabled) {
    const seen = new Set<string>()
    const add = (key: string) => { if (seen.has(key)) return; seen.add(key); if (!target.grants.some(g => g.permission === key)) target.grants.push({ permission: key, scopes: ['company'] }); data.value?.permissions.find(p => p.code === key)?.dependencies?.forEach(add) }
    add(code)
  } else {
    const removed = new Set([code]); let expanded = true
    while (expanded) { expanded = false; for (const p of data.value.permissions) if (!removed.has(p.code) && p.dependencies?.some(d => removed.has(d))) { removed.add(p.code); expanded = true } }
    target.grants = target.grants.filter(g => !removed.has(g.permission))
  }
}
function addRole(mode: 'new' | 'copy' | 'project') {
  if (!editable.value || !draft.value || changed.value) return
  const source = mode === 'new' ? undefined : template.value
  const projectOnly = mode === 'project'
  const baseName = source ? `${source.name.slice(0, 69)}${projectOnly ? '项目角色' : '副本'}` : '新角色'
  const names = new Set(draft.value.templates.map(role => role.name.toLowerCase()))
  let name = baseName
  for (let number = 2; names.has(name.toLowerCase()); number++) name = `${baseName}${number}`
  const key = `role-${diagnosticRequestId()}`
  const presets = data.value?.defaultRoleGrants ?? []
  const inherited = (source?.grants ?? []).filter(grant => !presets.some(preset => preset.permission === grant.permission)
    && (!projectOnly || permissionByCode.value.get(grant.permission)?.scope === 'project'))
  draft.value.templates.push({
    key, name, description: source?.description ?? '',
    grants: [...presets, ...inherited].map(grant => ({ permission: grant.permission, scopes: [...grant.scopes] })),
    roleCode: null, isActive: true, projectAssignable: projectOnly || Boolean(source?.projectAssignable),
  })
  templateSearch.value = ''
  selected.value = key
}

function removeRole() {
  if (!editable.value || !draft.value || !template.value || changed.value || deleteBlockedReason.value) return
  draft.value.templates = draft.value.templates.filter(role => role.key !== selected.value)
}
function addAssignment() {
  const templateKey = assignmentTemplateKey.value
  if (!canAddAssignment.value || !draft.value) return
  if (directAssignments.value.some(a => a.templateKey === templateKey && a.scope === 'company')) return ElMessage.warning('该人员已有相同的公司范围授权')
  const raw = diagnosticRequestId().replaceAll('-', '')
  const id = `${raw.slice(0,8)}-${raw.slice(8,12)}-${raw.slice(12,16)}-${raw.slice(16,20)}-${raw.slice(20,32)}`
  draft.value.assignments.push({ id, username: username.value, templateKey, companyId: props.companyId, scope: 'company', targetId: null })
  assignmentSource.value = 'direct'
}
function revokeAssignment(id: string) {
  if (editable.value && draft.value) draft.value.assignments = draft.value.assignments.filter(a => a.id !== id)
}
function assignmentTarget(a: AuthorizationAssignment) {
  if (!a.targetId) return ''
  if (a.scope === 'department') return data.value?.departments.find(d => d.id === a.targetId)?.name ?? ''
  const project = projectById.value.get(a.targetId)
  return project ? `${project.code} · ${project.name}` : ''
}
function scopeLabel(key: string, permissionScope?: string) {
  return authorizationRelationLabel(key, permissionScope, scopes.value)
}
function relationSummary(code: string, permissionScope?: string) {
  const chosen = grant(code)?.scopes ?? []
  if (!chosen.length) return '选择适用关系'
  const first = scopeLabel(chosen[0]!, permissionScope)
  return chosen.length > 1 ? first + ' +' + (chosen.length - 1) : first
}
const changes = computed(() => {
  if (!draft.value || !data.value) return []
  const rows: string[] = []
  for (const t of draft.value.templates) {
    const old = data.value.settings.templates.find(o => o.key === t.key)
    if (!old) { rows.push(`新增角色：${t.name}`); continue }
    if (t.name !== old.name || t.description !== old.description) rows.push(`更新角色名称或说明：${old.name} → ${t.name}`)
    if (t.projectAssignable !== old.projectAssignable) rows.push(`${t.projectAssignable ? '允许' : '禁止'}在项目中分配角色：${t.name}`)
    if (t.isActive !== old.isActive) rows.push(`${t.isActive ? '启用' : '停用'}角色：${t.name}`)
    const codes = new Set([...t.grants, ...old.grants].map(g => g.permission))
    for (const code of codes) {
      const before = old.grants.find(g => g.permission === code)?.scopes ?? []; const after = t.grants.find(g => g.permission === code)?.scopes ?? []
      if (JSON.stringify(before) !== JSON.stringify(after)) {
        const permission = permissionByCode.value.get(code)
        const describeScopes = (keys: string[]) => keys.map(key => scopeLabel(key, permission?.scope)).join('、') || '未授权'
        rows.push(`${t.name} · ${permission?.name ?? code}：${describeScopes(before)} → ${describeScopes(after)}`)
      }
    }
  }
  for (const role of data.value.settings.templates)
    if (!draft.value.templates.some(item => item.key === role.key)) rows.push(`删除角色：${role.name}`)
  const describe = (a: AuthorizationSettings['assignments'][number]) => {
    const name = data.value?.users.find(u => u.username === a.username)?.displayName || '姓名待补充'
    const title = draft.value?.templates.find(t => t.key === a.templateKey)?.name ?? a.templateKey
    const target = a.scope === 'company' ? '当前公司' : a.scope === 'department' ? data.value?.departments.find(d => d.id === a.targetId)?.name : data.value?.projects.find(p => p.id === a.targetId)?.code
    return name + ' · ' + title + ' · ' + (target ?? '未选目标') + (a.scope === 'subtree' ? '及子项目' : '')
  }
  for (const a of draft.value.assignments) { const old = data.value.settings.assignments.find(x => x.id === a.id); if (!old) rows.push('新增授权：' + describe(a)); else if (JSON.stringify(a) !== JSON.stringify(old)) rows.push('调整授权：' + describe(old) + ' → ' + describe(a)) }
  for (const a of data.value.settings.assignments) if (!draft.value.assignments.some(x => x.id === a.id)) rows.push('撤销授权：' + describe(a))
  return rows
})
async function save() {
  if (!draft.value || !editable.value || !changed.value) return
  const run = generation; const token = props.token
  const deletedRole = data.value?.settings.templates.find(role => !draft.value?.templates.some(item => item.key === role.key))
  const message = deletedRole ? `保存后将删除角色「${deletedRole.name}」。` : `将保存 ${changes.value.length} 项配置变化。请核对待保存变更。`
  try { await ElMessageBox.confirm(message, '确认权限变更', { confirmButtonText: '保存生效', cancelButtonText: '继续核对', type: 'warning' }) } catch { return }
  if (run !== generation) return
  saving.value = true
  try {
    const presets = data.value?.defaultRoleGrants ?? []
    const templates = draft.value.templates.map(role => role.roleCode ? role : {
      ...role,
      grants: [...role.grants, ...presets
        .filter(preset => !role.grants.some(grant => grant.permission === preset.permission))
        .map(preset => ({ permission: preset.permission, scopes: [] }))],
    })
    const saved = await saveAuthorization({ ...draft.value, templates }, token)
    if (run !== generation) return
    if (data.value) data.value.settings = saved
    reset()
    decisions.value = []
    window.dispatchEvent(new Event('pdm-authorization-changed'))
    ElMessage.success('权限配置已保存并生效')
  }
  catch (e) { ElMessage.error(e instanceof Error ? e.message : '权限保存失败，请刷新配置后重试') }
  finally { saving.value = false }
}
async function check() {
  if (!canCheck.value || tab.value !== 'preview') return
  const request = ++checkRequest
  const person = username.value
  const project = projectId.value
  checking.value = true
  checkError.value = ''
  decisions.value = []
  try {
    const result = await previewAuthorization(person, project, props.token)
    if (request === checkRequest) decisions.value = result
  } catch (e) {
    if (request === checkRequest) checkError.value = e instanceof Error ? e.message : '权限检查失败，请重试。'
  } finally {
    if (request === checkRequest) checking.value = false
  }
}
watch([tab, username, projectId, canCheck], () => {
  if (tab.value === 'preview' && canCheck.value) void check()
  else {
    checkRequest++
    checking.value = false
    checkError.value = ''
    decisions.value = []
  }
})
watch(() => [props.token, props.companyId], () => { void load() }, { immediate: true })
</script>

<template>
  <section class="pdm-project-manager authorization-center" aria-label="权限中心" :aria-busy="loading || saving">
    <el-dialog :model-value="!!relationEdit" :title="relationPermission ? '适用关系 · ' + relationPermission.name : '适用关系'"
      width="min(820px, calc(100vw - 24px))" append-to-body @close="relationEdit = undefined">
      <div v-if="relationEdit" class="authorization-scope-picker">
        <p class="authorization-scope-help">先看岗位，再选适用范围。勾选多项时，满足任一项即可匹配。</p>
        <fieldset v-for="group in commonRelationGroups" :key="group.title" class="authorization-scope-group">
          <legend>{{ group.title }}</legend>
          <AuthorizationRelationChoices v-model="relationEdit.scopes" :options="group.options" :disabled="!editable" />
          <details v-if="group.advanced?.length" class="authorization-scope-details" :open="hasSavedRelation(group.advanced)">
            <summary>细分范围 <small v-if="selectedRelationCount(group.advanced)">已选 {{ selectedRelationCount(group.advanced) }} 项</small></summary>
            <AuthorizationRelationChoices v-model="relationEdit.scopes" :options="group.advanced" :disabled="!editable" />
          </details>
        </fieldset>
        <details v-if="extraRelationGroups.length" class="authorization-scope-details authorization-scope-extra" :open="hasSavedRelation(extraRelationOptions)">
          <summary>其他适用条件 <small v-if="selectedRelationCount(extraRelationOptions)">已选 {{ selectedRelationCount(extraRelationOptions) }} 项</small></summary>
          <fieldset v-for="group in extraRelationGroups" :key="group.title" class="authorization-scope-group">
            <legend>{{ group.title }}</legend>
            <AuthorizationRelationChoices v-model="relationEdit.scopes" :options="group.options" :disabled="!editable" />
          </fieldset>
        </details>
      </div>
      <template #footer>
        <div class="authorization-scope-footer">
          <span v-if="editable">完成后请在权限中心保存生效。</span>
          <el-button @click="relationEdit = undefined">{{ editable ? '取消' : '关闭' }}</el-button>
          <el-button v-if="editable" type="primary" @click="finishRelations">完成</el-button>
        </div>
      </template>
    </el-dialog>
    <header class="authorization-intro">
      <div>
        <h2>权限中心</h2>
        <p>在这里维护角色及其权限。用户设置和人员授权使用同一份角色清单。</p>
      </div>
      <span v-if="draft">配置版本 {{ draft.revision }}</span>
      <button class="pdm-secondary-action" :disabled="loading || saving || changed" @click="load">刷新</button>
    </header>
    <p v-if="error" role="alert">{{ error }}</p>
    <p v-if="loading" role="status">正在读取权限配置…</p>
    <template v-if="draft && data">
      <nav class="pdm-tabs authorization-tabs" aria-label="权限中心功能">
        <button
          v-for="page in pages"
          :key="page.key"
          type="button"
          :class="{ 'is-active': tab === page.key }"
          :aria-pressed="tab === page.key"
          :disabled="roleChanged && page.key !== 'templates'"
          @click="tab = page.key"
        >{{ page.label }}</button>
      </nav>

      <div class="authorization-grid" :class="{ 'authorization-grid--preview': tab === 'preview' }">
        <section v-if="tab === 'templates'" class="authorization-pane" aria-label="角色列表">
          <div class="authorization-detail-head">
            <h3>角色列表</h3>
            <button class="pdm-primary-action" :disabled="!editable || changed" @click="addRole('new')">新增角色</button>
          </div>
          <input v-model="templateSearch" type="search" aria-label="搜索角色" placeholder="搜索角色" />
          <div class="authorization-list">
            <button
              v-for="item in filteredTemplates"
              :key="item.key"
              type="button"
              class="authorization-pick"
              :aria-pressed="selected === item.key"
              :disabled="roleChanged && selected !== item.key"
              @click="selected = item.key"
            >
              <span>{{ item.name }}</span>
              <small v-if="!item.isActive">已停用</small>
            </button>
            <p v-if="!filteredTemplates.length" class="authorization-empty">没有符合条件的角色。</p>
          </div>
        </section>

        <section v-else class="authorization-pane" aria-label="人员列表">
          <h3>人员</h3>
          <input v-model="userSearch" type="search" aria-label="搜索人员" placeholder="姓名／账号" />
          <div class="authorization-list">
            <button
              v-for="user in filteredUsers"
              :key="user.username"
              type="button"
              class="authorization-pick"
              :aria-pressed="username === user.username"
              @click="username = user.username"
            >
              <span><PersonName :name="user.displayName" /></span>
              <small v-if="!user.isActive">已停用</small>
            </button>
            <p v-if="!filteredUsers.length" class="authorization-empty">没有符合条件的人员。</p>
          </div>
        </section>

        <section v-if="tab === 'templates'" class="authorization-pane" aria-label="权限模块">
          <h3>权限模块</h3>
          <p class="authorization-note">已授权／权限项</p>
          <div class="authorization-list">
            <button
              v-for="item in modules"
              :key="item.key"
              type="button"
              class="authorization-pick"
              :aria-pressed="module === item.key"
              @click="module = item.key"
            ><span>{{ item.label }}</span><small class="authorization-count">{{ item.enabled }}/{{ item.total }}</small></button>
          </div>
        </section>

        <section v-else-if="tab === 'assignments'" class="authorization-pane" aria-label="授权来源">
          <h3>授权来源</h3>
          <div class="authorization-list">
            <button
              v-for="source in assignmentSources"
              :key="source.key"
              type="button"
              class="authorization-pick"
              :aria-pressed="assignmentSource === source.key"
              @click="assignmentSource = source.key"
            ><span>{{ source.label }}</span><small class="authorization-count">{{ sourceCounts[source.key] }}</small></button>
          </div>
        </section>

        <section v-else class="authorization-pane" aria-label="检查范围">
          <h3>检查范围</h3>
          <input v-model="projectSearch" type="search" aria-label="搜索项目" placeholder="项目号／名称" />
          <div class="authorization-list">
            <button type="button" class="authorization-pick" :aria-pressed="!projectId" @click="projectId = ''">公司级操作</button>
            <button
              v-for="project in filteredProjects"
              :key="project.id"
              type="button"
              class="authorization-pick"
              :aria-pressed="projectId === project.id"
              @click="projectId = project.id"
            ><span>{{ project.code }}<small>{{ project.name }}</small></span></button>
            <p v-if="!filteredProjects.length" class="authorization-empty">没有符合条件的项目。</p>
          </div>
        </section>

        <section v-if="tab === 'templates'" class="authorization-pane authorization-detail" aria-label="权限设置">
          <div class="authorization-detail-head">
            <div>
              <h3>{{ module || '全部模块' }} · 权限设置</h3>
              <p v-if="template" class="authorization-note">{{ template.name }}</p>
            </div>
            <input
              :value="permissionSearch"
              type="search"
              aria-label="搜索权限"
              placeholder="权限名称／模块"
              @input="searchPermissions(($event.target as HTMLInputElement).value)"
            />
          </div>
          <template v-if="template">
            <div class="authorization-filters authorization-template-fields">
              <label>角色名称<input v-model="template.name" :disabled="!editable" maxlength="80" /></label>
              <label class="authorization-description">说明<input v-model="template.description" :disabled="!editable" maxlength="300" /></label>
            </div>
            <div class="authorization-filters">
              <label class="authorization-check"><input v-model="template.isActive" type="checkbox" :disabled="!editable" />启用</label>
              <label class="authorization-check"><input v-model="template.projectAssignable" type="checkbox" :disabled="!editable" />允许项目分配</label>
              <button class="pdm-secondary-action" :disabled="!editable || changed" @click="addRole('copy')">复制角色</button>
              <button class="pdm-secondary-action" :disabled="!editable || changed" @click="addRole('project')">项目副本</button>
              <button class="pdm-secondary-action" :disabled="!editable || changed || Boolean(deleteBlockedReason)" :title="deleteBlockedReason || undefined" @click="removeRole">删除角色</button>
            </div>
            <div aria-label="授权范围从哪里来">
              <p class="authorization-note">
                <strong>公司角色</strong>
                保存后可在「用户设置 → 用户 → 编辑 → 系统角色」分配「{{ template.name }}」，按公司范围生效。
              </p>
              <p class="authorization-note">
                <strong>指定授权</strong>
                在「人员授权」选人和角色，点「新增授权」，设置范围和目标后保存。选「仅指定项目」并指定项目 A，这条授权就只覆盖项目 A。
              </p>
              <p v-if="permissions.some(p => p.scope === 'project')" class="authorization-note">
                公司角色和指定授权可同时生效。同一项权限有多个适用关系时，满足任一项即可。
              </p>
            </div>
            <div class="authorization-table">
              <table class="authorization-permission-table pdm-fit-columns">
                <colgroup><col class="authorization-operation-col" /><col class="authorization-grant-col" /><col /></colgroup>
                <thead><tr><th scope="col">操作</th><th scope="col">授权</th><th scope="col">适用关系</th></tr></thead>
                <tbody>
                  <tr v-for="p in permissions" :key="p.code">
                    <td data-label="操作">
                      <div :title="p.description || undefined">
                        {{ p.name }}
                        <small v-if="!module">{{ p.module }}</small>
                        <small v-if="p.sensitive">敏感操作</small>
                      </div>
                    </td>
                    <td data-label="授权">
                      <input type="checkbox" :aria-label="`授权${p.name}`" :checked="Boolean(grant(p.code))" :disabled="!editable" @change="toggle(p.code, ($event.target as HTMLInputElement).checked)" />
                    </td>
                    <td data-label="适用关系">
                      <el-button v-if="grant(p.code)" link type="primary" aria-haspopup="dialog"
                        :aria-label="p.name + '适用关系'" :title="grant(p.code)?.scopes.map(key => scopeLabel(key, p.scope)).join('、')"
                        class="authorization-scope-trigger" @click="openRelations(p.code)">
                        <span>{{ relationSummary(p.code, p.scope) }}</span>
                      </el-button>
                      <span v-else class="authorization-muted">未授权</span>
                    </td>
                  </tr>
                  <tr v-if="!permissions.length"><td colspan="3" class="authorization-empty">没有符合条件的权限。</td></tr>
                </tbody>
              </table>
            </div>
          </template>
          <p v-else class="authorization-empty">{{ roleChanged ? '已标记删除。请选择保存生效或撤销修改。' : '请选择角色。' }}</p>
        </section>

        <section v-else-if="tab === 'assignments'" class="authorization-pane authorization-detail" aria-label="授权明细">
          <div class="authorization-detail-head">
            <div>
              <h3>{{ selectedUser ? `${selectedUser.displayName} · 授权明细` : '授权明细' }}</h3>
              <p v-if="selectedUser" class="authorization-note"><PersonName :name="selectedUser.displayName" />{{ selectedUser.isActive ? '' : ' · 已停用' }}</p>
            </div>
          </div>
          <template v-if="selectedUser">
            <div v-if="showDirect && data.canEdit" class="authorization-filters">
              <label class="authorization-assignment-template">
                授权角色
                <select v-model="assignmentTemplateKey" aria-label="授权角色" :disabled="!editable">
                  <option value="">请选择角色</option>
                  <option v-for="item in assignmentTemplates" :key="item.key" :value="item.key">{{ item.name }}</option>
                </select>
              </label>
              <button class="pdm-primary-action" :disabled="!canAddAssignment" @click="addAssignment">新增授权</button>
            </div>
            <p class="authorization-note">公司角色来自「用户设置」中的系统角色。指定授权按下表的范围和目标生效，两种来源可以叠加。</p>
            <div class="authorization-table">
              <table class="authorization-assignment-table pdm-fit-columns">
                <colgroup>
                  <col class="authorization-template-col" /><col class="authorization-scope-col" /><col /><col class="authorization-action-col" />
                </colgroup>
                <thead><tr><th scope="col">角色</th><th scope="col">范围</th><th scope="col">授权目标</th><th scope="col">操作</th></tr></thead>
                <tbody>
                  <template v-if="showInherited">
                    <tr v-for="item in inherited" :key="item.key">
                      <td data-label="角色"><div>{{ item.name }}<small>公司角色{{ item.isActive ? '' : ' · 已停用' }}</small></div></td>
                      <td data-label="范围">公司范围</td>
                      <td data-label="授权目标">当前公司</td>
                      <td data-label="操作"><span class="authorization-muted">随岗位</span></td>
                    </tr>
                  </template>
                  <template v-if="showDirect">
                    <tr v-for="a in directAssignments" :key="a.id">
                      <td data-label="角色"><div>{{ draft.templates.find(t => t.key === a.templateKey)?.name ?? a.templateKey }}<small>指定授权</small></div></td>
                      <td data-label="范围">
                        <select v-model="a.scope" :disabled="!editable" aria-label="授权范围" @change="a.targetId = null">
                          <option value="company">当前公司</option>
                          <option value="department">执行部门及下级</option>
                          <option value="project">仅指定项目</option>
                          <option value="subtree">项目及子项目</option>
                        </select>
                      </td>
                      <td data-label="授权目标">
                        <span v-if="a.scope === 'company'">当前公司</span>
                        <div v-else>
                          <select v-model="a.targetId" :disabled="!editable" aria-label="授权目标" :title="assignmentTarget(a)">
                            <option :value="null">请选择</option>
                            <template v-if="a.scope === 'department'">
                              <option v-for="d in data.departments" :key="d.id" :value="d.id">{{ d.name }}</option>
                            </template>
                            <template v-else>
                              <option v-for="p in data.projects" :key="p.id" :value="p.id">{{ p.code }} · {{ p.name }}</option>
                            </template>
                          </select>
                          <small v-if="assignmentTarget(a).length > 20">{{ assignmentTarget(a) }}</small>
                        </div>
                      </td>
                      <td data-label="操作">
                        <button type="button" class="pdm-text-action" :disabled="!editable" @click="revokeAssignment(a.id)">撤销</button>
                      </td>
                    </tr>
                  </template>
                  <tr v-if="!sourceCounts[assignmentSource]">
                    <td colspan="4" class="authorization-empty">{{ assignmentSource === 'inherited' ? '暂无公司角色授权。' : '暂无指定范围授权。' }}</td>
                  </tr>
                </tbody>
              </table>
            </div>
          </template>
          <p v-else class="authorization-empty">请选择人员查看授权。</p>
        </section>

        <section v-else class="authorization-pane authorization-detail" aria-label="检查结果">
          <div class="authorization-detail-head">
            <div>
              <h3>{{ selectedUser ? `${selectedUser.displayName} · 检查结果` : '检查结果' }}</h3>
              <p class="authorization-note">{{ projectContext }}</p>
            </div>
            <button class="pdm-secondary-action" :disabled="!canCheck || checking" @click="check">{{ checking ? '检查中' : '检查权限' }}</button>
          </div>
          <div class="authorization-filters authorization-result-filters">
            <div class="authorization-result-options" aria-label="结果筛选">
              <button
                v-for="filter in resultFilters"
                :key="filter.key"
                type="button"
                :aria-pressed="resultFilter === filter.key"
                @click="resultFilter = filter.key"
              >{{ filter.label }}</button>
            </div>
            <input v-model="resultSearch" type="search" aria-label="搜索检查结果" placeholder="权限名称／模块" />
          </div>
          <p class="authorization-note">
            {{ changed ? '请先保存或撤销当前修改。' : '按已保存的配置检查。目录权限、文件锁定及审批任务状态在执行时核验。' }}
          </p>
          <p v-if="checkError" class="authorization-empty" role="alert">{{ checkError }}</p>
          <div v-else class="authorization-table" :aria-busy="checking">
            <table class="authorization-result-table pdm-fit-columns">
              <colgroup><col class="authorization-operation-col" /><col class="authorization-result-col" /><col /></colgroup>
              <thead><tr><th scope="col">操作</th><th scope="col">结果</th><th scope="col">原因／来源</th></tr></thead>
              <tbody>
                <tr v-for="d in previewRows" :key="d.permission">
                  <td data-label="操作"><div>{{ d.definition?.name ?? d.permission }}<small v-if="d.definition">{{ d.definition.module }}</small></div></td>
                  <td data-label="结果"><span class="authorization-badge" :class="{ 'is-allowed': d.allowed }">{{ d.allowed ? '允许' : '未授权' }}</span></td>
                  <td data-label="原因／来源">
                    <div>
                      {{ d.reason }}
                      <small v-if="d.sources.length">{{ d.sources.join('、') }}</small>
                    </div>
                  </td>
                </tr>
                <tr v-if="!previewRows.length">
                  <td colspan="3" class="authorization-empty" role="status">
                    {{ checking ? '正在检查权限…' : !selectedUser ? '请选择人员和检查范围。' : changed ? '保存或撤销修改后可检查权限。' : '没有符合条件的检查结果。' }}
                  </td>
                </tr>
              </tbody>
            </table>
          </div>
        </section>
      </div>

      <details v-if="changed" class="authorization-changes">
        <summary>待保存变更（{{ changes.length }}项）</summary>
        <ul><li v-for="(change, i) in changes" :key="i">{{ change }}</li></ul>
      </details>
      <footer class="authorization-footer">
        <span>{{ changed ? '有未保存的修改' : data.canEdit ? '配置已保存' : '当前账号仅可查看' }}</span>
        <button class="pdm-secondary-action" :disabled="!changed || saving" @click="reset">撤销修改</button>
        <button v-if="data.canEdit" class="pdm-primary-action" :disabled="!changed || !editable" @click="save">{{ saving ? '保存中' : '保存生效' }}</button>
      </footer>
    </template>
  </section>
</template>

<style scoped>
.authorization-scope-trigger { max-width: 100%; }
.authorization-scope-trigger :deep(span) {
  overflow: hidden;
  text-overflow: ellipsis;
}
.authorization-scope-picker { display: grid; gap: 10px; }
.authorization-scope-help { color: var(--pdm-muted); margin: 0; }
.authorization-scope-group { min-width: 0; border: 1px solid var(--pdm-border-soft); border-radius: 8px; margin: 0; padding: 6px 10px 10px; }
.authorization-scope-group legend { padding-inline: 4px; font-weight: 600; color: var(--pdm-text); }
.authorization-scope-details { margin-top: 6px; }
.authorization-scope-details summary { padding: 5px 6px; color: var(--pdm-theme-accent); cursor: pointer; }
.authorization-scope-details summary small { margin-inline-start: 8px; color: var(--pdm-muted); }
.authorization-scope-extra { border-top: 1px solid var(--pdm-border-soft); padding-top: 6px; margin: 0; }
.authorization-scope-extra .authorization-scope-group { margin-top: 8px; }
.authorization-scope-footer { display: flex; flex-wrap: wrap; align-items: center; justify-content: flex-end; gap: 8px; }
.authorization-scope-footer span { color: var(--pdm-muted); margin-inline-end: auto; }
.authorization-scope-footer :deep(.el-button + .el-button) { margin-left: 0; }
.authorization-center {
  container-type: inline-size;
  min-height: 0;
  gap: 10px;
  overflow: auto;
}
.authorization-intro,
.authorization-detail-head,
.authorization-filters,
.authorization-footer {
  display: flex;
  align-items: center;
  gap: 8px;
  flex-wrap: wrap;
}
.authorization-intro > div,
.authorization-detail-head > div,
.authorization-footer > span {
  flex: 1;
  min-width: 0;
}
.authorization-intro h2 {
  margin: 0;
  font-size: 18px;
}
.authorization-intro p,
.authorization-note,
.authorization-muted,
.authorization-empty {
  color: var(--pdm-muted);
}
.authorization-intro p,
.authorization-note {
  margin: 3px 0;
  font-size: 12px;
  line-height: 1.5;
  overflow-wrap: anywhere;
}
.authorization-tabs {
  display: flex;
  flex-wrap: wrap;
  gap: 8px;
}
.authorization-tabs button,
.authorization-result-options button {
  width: 80px;
  height: 28px;
  border: 1px solid var(--pdm-border);
  border-radius: 999px;
  background: var(--pdm-surface);
  color: var(--pdm-text);
}
.authorization-tabs button[aria-pressed='true'],
.authorization-result-options button[aria-pressed='true'] {
  border-color: var(--pdm-theme-accent);
  color: var(--pdm-theme-accent);
}
.authorization-grid {
  display: grid;
  grid-template-columns: clamp(200px, 16cqi, 224px) clamp(144px, 12cqi, 176px) minmax(0, 1fr);
  grid-template-rows: minmax(0, 1fr);
  gap: 10px;
  flex: 1 1 auto;
  min-height: 0;
}
.authorization-grid--preview {
  grid-template-columns: clamp(200px, 16cqi, 224px) clamp(216px, 18cqi, 248px) minmax(0, 1fr);
}
.authorization-pane {
  display: flex;
  flex-direction: column;
  gap: 8px;
  min-width: 0;
  min-height: 0;
  padding: 10px;
  overflow: hidden;
  border: 1px solid var(--pdm-border);
  border-radius: 10px;
  background: var(--pdm-surface);
}
.authorization-pane h3 {
  margin: 0;
  font-size: 14px;
  overflow-wrap: anywhere;
}
.authorization-list {
  min-height: 0;
  display: grid;
  grid-auto-rows: max-content;
  gap: 3px;
  overflow: auto;
  scrollbar-gutter: stable;
}
.authorization-pick {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 6px;
  width: 100%;
  min-height: 28px;
  padding: 4px 8px;
  border: 1px solid transparent;
  border-radius: 999px;
  background: transparent;
  color: var(--pdm-text);
  font: inherit;
  text-align: left;
  overflow-wrap: anywhere;
}
.authorization-pick:hover {
  background: var(--pdm-table-header);
}
.authorization-pick[aria-pressed='true'] {
  color: var(--pdm-theme-accent);
  background: var(--pdm-blue-soft);
}
.authorization-pick > span {
  min-width: 0;
}
.authorization-pick small,
.authorization-table small {
  display: block;
  margin-top: 2px;
  color: var(--pdm-muted);
  font-size: 12px;
  overflow-wrap: anywhere;
}
.authorization-pick > small {
  flex-shrink: 0;
  margin: 0;
}
.authorization-count {
  font-variant-numeric: tabular-nums;
}
.authorization-filters label {
  display: flex;
  align-items: center;
  gap: 6px;
  min-width: 0;
}
.authorization-template-fields label:first-child {
  flex: 1 1 150px;
}
.authorization-description {
  flex: 2 1 220px;
}
.authorization-assignment-template {
  flex: 0 1 300px;
}
.authorization-template-fields input,
.authorization-assignment-template select {
  flex: 1;
  min-width: 0;
}
.authorization-check {
  white-space: nowrap;
}
.authorization-detail-head input,
.authorization-result-filters input {
  width: min(200px, 100%);
}
.authorization-result-options {
  display: flex;
  flex-wrap: wrap;
  gap: 6px;
}
.authorization-center input:not([type=checkbox]),
.authorization-center select {
  height: 28px;
  border-radius: 999px;
  border: 1px solid var(--pdm-border);
  background: var(--pdm-surface);
  color: var(--pdm-text);
  padding: 0 10px;
}
.authorization-center input[type=search] {
  min-width: 0;
  box-shadow: none !important;
}
.authorization-center button.pdm-primary-action,
.authorization-center button.pdm-secondary-action {
  width: 80px;
  height: 28px;
  border-radius: 999px;
}
.authorization-table {
  overflow: auto;
  flex: 1 1 auto;
  min-height: 0;
  border: 1px solid var(--pdm-border);
  border-radius: 8px;
}
.authorization-table table {
  width: 100%;
  table-layout: fixed;
  border-collapse: separate;
  border-spacing: 0;
}
.authorization-table .authorization-operation-col { width: clamp(160px, 19cqi, 280px); }
.authorization-result-table .authorization-operation-col { width: clamp(150px, 17cqi, 240px); }
.authorization-table .authorization-grant-col { width: 56px; }
.authorization-table .authorization-template-col { width: clamp(120px, 12cqi, 180px); }
.authorization-table .authorization-scope-col { width: 120px; }
.authorization-table .authorization-action-col { width: 80px; }
.authorization-table .authorization-result-col { width: 72px; }
.authorization-table th,
.authorization-table td {
  text-align: center;
  vertical-align: middle;
  padding: 6px 8px;
  border-bottom: 1px solid var(--pdm-border);
  overflow-wrap: anywhere;
}
.authorization-table th {
  position: sticky;
  top: 0;
  background: var(--pdm-table-header);
  z-index: 1;
}
.authorization-table select {
  width: 100%;
  min-width: 0;
}
.authorization-table select {
  height: 24px;
  text-overflow: ellipsis;
  padding-inline: 6px;
}
.authorization-table button {
  height: 24px;
  border-radius: 999px;
}
.authorization-badge {
  display: inline-block;
  padding: 1px 6px;
  border-radius: 999px;
  white-space: nowrap;
  color: var(--pdm-muted);
  background: var(--pdm-table-header);
}
.authorization-badge.is-allowed {
  color: var(--pdm-theme-accent);
  background: var(--pdm-blue-soft);
}
.authorization-empty {
  text-align: center;
  padding: 10px;
}
.authorization-footer {
  border-top: 1px solid var(--pdm-border);
  padding-top: 8px;
  color: var(--pdm-muted);
}
.authorization-changes {
  max-height: 150px;
  overflow: auto;
  padding: 8px;
  border: 1px solid var(--pdm-border);
  border-radius: 8px;
  font-size: 12px;
}
@container (width < 1000px) {
  .authorization-grid,
  .authorization-grid--preview {
    grid-template-columns: repeat(2, minmax(0, 1fr));
    grid-template-rows: auto auto;
    flex: 0 0 auto;
  }
  .authorization-detail { grid-column: 1 / -1; }
  .authorization-list { max-height: 144px; }
  .authorization-table { max-height: min(52vh, 520px); }
  .authorization-footer {
    position: sticky;
    bottom: 0;
    z-index: 2;
    flex-shrink: 0;
    background: var(--pdm-surface);
  }
}
@container (width < 520px) {
  .authorization-grid,
  .authorization-grid--preview {
    grid-template-columns: minmax(0, 1fr);
    grid-template-rows: auto;
  }
  .authorization-list {
    grid-template-columns: repeat(2, minmax(0, 1fr));
    max-height: 112px;
  }
  .authorization-table table,
  .authorization-table tbody,
  .authorization-table tr { display: block; }
  .authorization-table thead,
  .authorization-table colgroup { display: none; }
  .authorization-table tr {
    border-bottom: 1px solid var(--pdm-border);
    padding: 4px 0;
  }
  .authorization-table td {
    display: grid;
    grid-template-columns: 5em minmax(0, 1fr);
    align-items: center;
    column-gap: 6px;
    border: 0;
  }
  .authorization-table td[data-label]::before {
    content: attr(data-label);
    color: var(--pdm-muted);
    font-size: 12px;
  }
  .authorization-table td > * { min-width: 0; }
  .authorization-table td > :is(button, input, .authorization-badge) { justify-self: center; }
  .authorization-table td.authorization-empty { display: block; }
}
</style>
