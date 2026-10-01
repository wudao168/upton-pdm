<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import ValidationPlanManager from './ValidationPlanManager.vue'
import QualityInspectionPanel from './QualityInspectionPanel.vue'
import { readQualityUploadAccess } from '../api'
import { ElMessage } from '../statusMessage'
import type { ProjectSummary } from '../types'

const props = defineProps<{ projectId: string; projectCode: string; projectName: string; projects: ProjectSummary[]; token: string; currentUsername: string; currentDisplayName: string; canEdit: boolean; canManageCatalog: boolean; canDecideApproval?: boolean; requestedQualityAcceptance?: boolean; requestedProjectId?: string }>()
const emit = defineEmits<{ requestHandled: [] }>()
const inspectionRevision = ref(0)
const activeDetail = ref('')
const activeTab = ref('quality')
const access = ref<Record<string, string[]>>({})
let accessGeneration = 0
function writableIds(kind: string) { return familyProjects.value.filter(x => access.value[x.id]?.includes(kind)).map(x => x.id) }
const canEditQuality = computed(() => access.value[props.projectId]?.includes('quality') ?? false)
watch(() => props.requestedProjectId, id => { if (id) activeTab.value = 'quality' }, { immediate: true })
function selectTab(tab: string) { activeTab.value = tab }

const familyProjects = computed(() => {
  let root = props.projects.find(x => x.id === props.projectId)
  const seen = new Set<string>()
  while (root?.parentProjectId && !seen.has(root.id)) {
    seen.add(root.id)
    root = props.projects.find(x => x.id === root?.parentProjectId) ?? root
  }
  const ids = new Set([root?.id ?? props.projectId])
  let changed = true
  while (changed) {
    changed = false
    for (const project of props.projects) if (project.parentProjectId && ids.has(project.parentProjectId) && !ids.has(project.id)) { ids.add(project.id); changed = true }
  }
  return props.projects.filter(x => ids.has(x.id))
})
watch(() => [familyProjects.value.map(x => x.id).join(','), props.token], async () => {
  const generation = ++accessGeneration
  access.value = {}
  try {
    const entries = await Promise.all(familyProjects.value.filter(x => x.canReadContent).map(async x => [x.id, await readQualityUploadAccess(x.id, props.token)] as const))
    if (generation === accessGeneration) access.value = Object.fromEntries(entries)
  } catch (e) { if (generation === accessGeneration) ElMessage.error(e instanceof Error ? e.message : '上传权限加载失败') }
}, { immediate: true })
function changeView(section: string, view: string) { activeDetail.value = view === 'detail' ? section : '' }
</script>

<template>
  <div class="quality-manager" :class="{ 'has-detail': activeTab === 'quality' && activeDetail }" aria-label="项目质量">
    <nav class="quality-manager__tabs pdm-project-subtabs pdm-segmented" aria-label="质量分类" role="tablist">
      <button v-for="tab in [{ key: 'quality', label: '质量检验' }, { key: 'process', label: '过程检验' }, { key: 'customer', label: '客户验收' }]" :key="tab.key" type="button" :class="{ 'is-active': activeTab === tab.key }" :aria-pressed="activeTab === tab.key" role="tab" :aria-selected="activeTab === tab.key" @click="selectTab(tab.key)">{{ tab.label }}</button>
    </nav>
    <div v-show="activeTab === 'quality'" class="quality-manager__sections">
      <ValidationPlanManager v-show="!activeDetail || activeDetail === 'validation'" v-bind="props" :can-edit="canEditQuality" :requested-project-id="requestedQualityAcceptance ? undefined : requestedProjectId" class="quality-manager__plan" @view-changed="changeView('validation', $event)" @request-handled="emit('requestHandled')" />
      <ValidationPlanManager v-show="!activeDetail || activeDetail === 'acceptance'" v-bind="props" :can-edit="canEditQuality" :requested-project-id="requestedQualityAcceptance ? requestedProjectId : undefined" quality-acceptance class="quality-manager__plan" @view-changed="changeView('acceptance', $event)" @request-handled="emit('requestHandled')" />
    </div>
    <div v-show="activeTab === 'process'" class="quality-manager__sections">
      <QualityInspectionPanel v-for="kind in ['incoming', 'assembly'] as const" :key="kind" :kind="kind" :projects="familyProjects" :project-id="projectId" :token="token" :can-edit="canEdit" :writable-project-ids="writableIds(kind)" :refresh-version="inspectionRevision" @saved="inspectionRevision++" />
    </div>
    <div v-show="activeTab === 'customer'" class="quality-manager__sections">
      <QualityInspectionPanel v-for="kind in ['preAcceptance', 'finalAcceptance'] as const" :key="kind" :kind="kind" :projects="familyProjects" :project-id="projectId" :token="token" :can-edit="canEdit" :writable-project-ids="writableIds(kind)" :refresh-version="inspectionRevision" @saved="inspectionRevision++" />
    </div>
  </div>
</template>

<style scoped>
.quality-manager :deep(.quality-inspection){gap:12px}
.quality-manager :deep(.quality-section__toolbar){height:32px;min-height:32px;flex-shrink:0;display:flex;align-items:center;justify-content:space-between;gap:12px}
.quality-manager :deep(.quality-section__heading){display:flex;align-items:center;justify-content:flex-start;gap:12px;flex:1;min-width:0}
.quality-manager :deep(.quality-section__heading h2){display:flex;align-items:center;gap:7px;flex-shrink:0;margin:0;font-size:16px;white-space:nowrap}
.quality-manager :deep(.quality-section__heading p){margin:0;min-width:0;overflow:hidden;text-overflow:ellipsis;white-space:nowrap;color:var(--pdm-muted);font-size:12px}
.quality-manager :deep(.quality-section__actions){flex-shrink:0}
.quality-manager :deep(.quality-section__toolbar button){box-sizing:border-box;width:100px;height:32px;min-height:32px;padding:0;white-space:nowrap}
.quality-manager :deep(.quality-inspection__footer),.quality-manager :deep(.validation-plan__footer){font-size:11px;line-height:16px;min-height:16px;flex-shrink:0}

.quality-manager{display:flex;flex-direction:column;gap:8px;flex:1;min-height:0;overflow:hidden}.quality-manager__sections{display:grid;grid-template-rows:repeat(2,minmax(0,1fr));gap:8px;flex:1;min-height:0}.quality-manager__plan{min-height:0;overflow:hidden}.quality-manager.has-detail .quality-manager__sections{display:flex;flex-direction:column}.quality-manager.has-detail .quality-manager__plan{flex:1}.quality-manager :deep(.validation-plan-summary__table){min-width:980px}.quality-manager :deep(.validation-plan__toolbar),.quality-manager :deep(.validation-plan__footer){flex-shrink:0}.quality-manager :deep(.validation-plan__empty){padding:12px}
@media(max-width:900px){.quality-manager{overflow:auto}.quality-manager__sections{flex:none;min-height:600px}}
</style>
