<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { readProjectNameplate } from '../api'
import type { ProjectSummary } from '../types'
import { useUserDisplayName } from '../userDisplay'
import ProjectNameplateDialog from './ProjectNameplateDialog.vue'
import ProjectContactInformation from './ProjectContactInformation.vue'

const props = defineProps<{ project: ProjectSummary; projects: ProjectSummary[]; token: string }>()
const activeTab = ref('basic')
const nameplateProject = ref<ProjectSummary | null>(null)
const nameplateOpen = ref(false)
const displayName = useUserDisplayName()
const familyProjects = computed(() => {
  const rootId = props.project.rootProjectId || props.project.parentProjectId || props.project.id
  const root = props.projects.find(project => project.id === rootId) ?? props.project
  const ids = new Set([root.id])
  let changed = true
  while (changed) {
    changed = false
    for (const project of props.projects) if (project.parentProjectId && ids.has(project.parentProjectId) && !ids.has(project.id)) { ids.add(project.id); changed = true }
  }
  return [root, ...props.projects.filter(project => project.id !== root.id && ids.has(project.id)).sort((a, b) => (a.childSequence ?? 0) - (b.childSequence ?? 0))]
})
const phaseRoles = [{ key: 'StandardProcurement', label: '标准件采购' }, { key: 'NonStandardProcurement', label: '非标件采购' }, { key: 'NonStandardProduction', label: '非标件生产' }, { key: 'MechanicalAssembly', label: '机械装配' }, { key: 'ElectricalAssembly', label: '电气装配' }, { key: 'ElectricalCommissioning', label: '电气调试' }, { key: 'Acceptance', label: '验收' }] as const
function people(usernames: Array<string | undefined>) { return [...new Set(usernames.filter((user): user is string => Boolean(user)))].map(user => displayName(user)).join('、') || '待分配' }
function leads(project: ProjectSummary) { return project.designLeads?.length ? project.designLeads : [project.designLead] }
function openNameplate(project: ProjectSummary) { nameplateProject.value = project; nameplateOpen.value = true }
const nameplateAccess = ref<Record<string, { enabled: boolean; canEdit: boolean }>>({})
let nameplateRequest = 0
async function loadNameplateAccess() {
  const request = ++nameplateRequest
  const token = props.token
  nameplateAccess.value = {}
  const results = await Promise.allSettled(familyProjects.value.filter(item => item.canReadContent).map(async item => {
    const result = await readProjectNameplate(item.id, token)
    return [item.id, { enabled: result.nameplate.enabled, canEdit: result.canEdit }] as const
  }))
  if (request === nameplateRequest) nameplateAccess.value = Object.fromEntries(results.flatMap(result => result.status === 'fulfilled' ? [result.value] : []))
}
watch(() => [props.token, ...familyProjects.value.map(item => `${item.id}:${item.canReadContent}`)], () => { void loadNameplateAccess() }, { immediate: true })
watch(nameplateOpen, open => { if (!open) void loadNameplateAccess() })
</script>

<template>
  <section class="project-information" aria-label="项目信息">
    <nav class="pdm-project-subtabs pdm-segmented" aria-label="信息分类" role="tablist">
      <button v-for="tab in [{ key: 'basic', label: '基本信息' }, { key: 'people', label: '人员信息' }]" :key="tab.key" type="button" role="tab" :class="{ 'is-active': activeTab === tab.key }" :aria-selected="activeTab === tab.key" :aria-pressed="activeTab === tab.key" @click="activeTab = tab.key">{{ tab.label }}</button>
    </nav>
    <ProjectContactInformation v-if="activeTab === 'basic'" :project-id="project.rootProjectId || project.parentProjectId || project.id" :token="token" />
    <section class="pdm-panel project-information__panel" :aria-label="activeTab === 'basic' ? '项目基本信息列表' : '项目人员信息列表'">
      <header><h2>{{ activeTab === 'basic' ? '基本信息' : '人员信息' }}</h2><span>共 {{ familyProjects.length }} 个主、子项目</span></header>
      <div class="project-information__table-wrap">
        <table v-if="activeTab === 'basic'" class="project-information__table">
          <thead><tr><th>项目号</th><th>项目名称</th><th>别名</th><th>型号</th><th>序列号</th><th>客户</th><th>事业部</th><th>数量</th><th>状态</th><th>订单日期</th><th>铭牌</th></tr></thead>
          <tbody><tr v-for="item in familyProjects" :key="item.id" :class="{ 'is-current': item.id === project.id }"><td>{{ item.code }}</td><td>{{ item.name }}</td><td>{{ item.projectAlias || '—' }}</td><td>{{ item.deviceModel || '—' }}</td><td>{{ item.serialNumbers.join('、') || '—' }}</td><td>{{ item.customerName || '—' }}</td><td>{{ item.executionUnitName || '—' }}</td><td>{{ item.quantity }}</td><td>{{ item.businessStatus || item.stage }}</td><td>{{ item.signedDate || '—' }}</td><td><span v-if="nameplateAccess[item.id] && !nameplateAccess[item.id].enabled && !nameplateAccess[item.id].canEdit">未启用</span><button v-else-if="nameplateAccess[item.id]" type="button" class="pdm-text-action" :aria-label="`查看铭牌 ${item.code}`" @click="openNameplate(item)">查看铭牌</button><span v-else>—</span></td></tr></tbody>
        </table>
        <table v-else class="project-information__table project-information__people">
          <thead><tr><th>项目号</th><th>项目名称</th><th>项目经理／负责人</th><th>协同项目经理</th><th>主设</th><th>执行工程师</th><th v-for="phase in phaseRoles" :key="phase.key">{{ phase.label }}</th></tr></thead>
          <tbody><tr v-for="item in familyProjects" :key="item.id" :class="{ 'is-current': item.id === project.id }"><td>{{ item.code }}</td><td>{{ item.name }}</td><td>{{ people([item.primaryProjectManager]) }}</td><td>{{ people(item.collaborativeProjectManagers) }}</td><td>{{ people(leads(item.parentProjectId ? familyProjects[0]! : item)) }}</td><td>{{ people(item.designers) }}</td><td v-for="phase in phaseRoles" :key="phase.key">{{ people([item.phaseOwners?.[phase.key]]) }}</td></tr></tbody>
        </table>
      </div>
    </section>
    <ProjectNameplateDialog v-if="nameplateProject" v-model="nameplateOpen" :project="nameplateProject" :token="token" />
  </section>
</template>

<style scoped>
.project-information{display:flex;flex-direction:column;gap:5px;flex:1;min-height:0;overflow:hidden}
.project-information__panel{display:flex;flex-direction:column;flex:1;min-height:0;overflow:hidden}
.project-information__panel>header{display:flex;align-items:center;gap:12px;min-height:40px;padding:0 12px;border-bottom:1px solid var(--pdm-border)}
.project-information h2{margin:0;font-size:14px}.project-information header>span{font-size:11px;color:var(--pdm-muted)}
.project-information__table-wrap{flex:1;min-height:0;overflow:auto}
.project-information__table{width:100%;min-width:1100px;border-collapse:collapse;font-size:11px}
.project-information__table th,.project-information__table td{height:32px;padding:0 8px;text-align:center;border-right:1px solid var(--pdm-border-soft);border-bottom:1px solid var(--pdm-border-soft);white-space:nowrap}
.project-information__table th{position:sticky;top:0;z-index:1;background:var(--pdm-panel-muted,#f8fafc);font-weight:500}
.project-information__table tbody tr:nth-child(even){background:var(--pdm-row-stripe,#f1f5f9)}
.project-information__table .is-current{background:var(--pdm-blue-soft,#eff6ff)}
.project-information__people{min-width:1600px}.project-information__table button{font-size:11px}
</style>
