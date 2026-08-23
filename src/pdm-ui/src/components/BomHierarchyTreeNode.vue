<script setup lang="ts">
import type { ProjectSummary } from '../types'

type TreeCategory = 'All' | 'Standard' | 'NonStandard' | 'Electrical'

defineOptions({ name: 'BomHierarchyTreeNode' })
const props = defineProps<{
  project: ProjectSummary
  childrenByParent: Map<string, ProjectSummary[]>
  expandedProjectIds: Set<string>
  selectedProjectId: string
  selectedCategory: TreeCategory
  categoryLabels: Record<'Standard' | 'NonStandard' | 'Electrical', string>
}>()
const emit = defineEmits<{
  toggle: [projectId: string]
  selectProject: [projectId: string, category: TreeCategory]
}>()
const categoryKinds = ['Standard', 'NonStandard', 'Electrical'] as const
</script>

<template>
  <li class="bom-tree-node">
    <div class="bom-tree-node__project" :class="{ 'is-selected': selectedProjectId === project.id && selectedCategory === 'All' }">
      <button type="button" class="bom-tree-node__toggle" :aria-label="expandedProjectIds.has(project.id) ? '收起项目BOM' : '展开项目BOM'" @click="emit('toggle', project.id)">{{ expandedProjectIds.has(project.id) ? '▾' : '▸' }}</button>
      <button type="button" class="bom-tree-node__select" @click="emit('selectProject', project.id, 'All')"><strong>{{ project.code }}</strong><span>{{ project.name }}</span><small>{{ project.bomItemCategoryCode === '0301' ? '产线' : project.bomItemCategoryCode === '0302' ? '设备' : '待维护' }}</small></button>
    </div>
    <ul v-if="expandedProjectIds.has(project.id)" class="bom-tree-node__children">
      <li v-for="kind in categoryKinds" :key="kind"><button type="button" class="bom-tree-node__category" :class="{ 'is-selected': selectedProjectId === project.id && selectedCategory === kind }" @click="emit('selectProject', project.id, kind)">{{ categoryLabels[kind] }}</button></li>
      <BomHierarchyTreeNode
        v-for="child in childrenByParent.get(project.id) || []"
        :key="child.id"
        :project="child"
        :children-by-parent="childrenByParent"
        :expanded-project-ids="expandedProjectIds"
        :selected-project-id="selectedProjectId"
        :selected-category="selectedCategory"
        :category-labels="categoryLabels"
        @toggle="emit('toggle', $event)"
        @select-project="(projectId, category) => emit('selectProject', projectId, category)"
      />
    </ul>
  </li>
</template>

<style scoped>
.bom-tree-node__project{display:grid;grid-template-columns:22px minmax(0,1fr);align-items:stretch;border-radius:5px}.bom-tree-node__project.is-selected,.bom-tree-node__category.is-selected{background:#eaf3ff;color:#1677ff}.bom-tree-node__toggle,.bom-tree-node__select,.bom-tree-node__category{border:0;background:transparent;color:inherit;cursor:pointer}.bom-tree-node__toggle{padding:0}.bom-tree-node__select{display:grid;grid-template-columns:auto minmax(0,1fr) auto;gap:6px;padding:6px;text-align:left}.bom-tree-node__select span{overflow:hidden;text-overflow:ellipsis;white-space:nowrap}.bom-tree-node__select small{font-size:10px;color:var(--pdm-muted)}.bom-tree-node__children{margin:0 0 0 10px;padding:0 0 0 12px;border-left:1px solid #dbe4ee;list-style:none}.bom-tree-node__category{display:block;width:100%;padding:5px 8px;text-align:left;border-radius:4px}
</style>
