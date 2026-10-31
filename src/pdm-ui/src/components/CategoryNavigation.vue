<script setup lang="ts">
import { Folder } from '@lucide/vue'
type CategoryNode = { id: string; label: string; isActive?: boolean; children: CategoryNode[] }
withDefaults(defineProps<{ nodes: CategoryNode[]; modelValue: string; ariaLabel?: string; allLabel?: string; collapsed?: boolean; collapsible?: boolean; defaultExpandAll?: boolean }>(), { ariaLabel: '分类筛选', allLabel: '全部分类' })
const emit = defineEmits<{ 'update:modelValue': [id: string]; toggle: [] }>()
</script>

<template>
  <aside class="pdm-category-sidebar standard-library__tree pdm-panel" :class="{ 'is-collapsed': collapsed }" :aria-label="ariaLabel">
    <div class="pdm-category-sidebar__top">
      <button v-if="!collapsed" type="button" class="standard-library__all" :class="{ 'is-active': !modelValue }" @click="emit('update:modelValue', '')">{{ allLabel }}</button>
      <button v-if="collapsible" type="button" class="pdm-category-sidebar__toggle" :aria-label="`${collapsed ? '展开' : '收起'}${ariaLabel}`" :aria-expanded="!collapsed" @click="emit('toggle')">{{ collapsed ? '›' : '‹' }}</button>
    </div>
    <el-tree v-if="!collapsed" :data="nodes" node-key="id" :props="{ label: 'label', children: 'children' }" :current-node-key="modelValue || undefined" :default-expand-all="defaultExpandAll" highlight-current @node-click="emit('update:modelValue', $event.id)">
      <template #default="{ data }"><span class="standard-library__category-label" :class="{ 'is-inactive': data.isActive === false, 'is-selected': data.id === modelValue }"><Folder :size="15" /><span>{{ data.label }}</span><small v-if="data.isActive === false">停用</small></span></template>
    </el-tree>
    <div v-if="!collapsed && $slots.actions" class="standard-library__category-actions"><slot name="actions" /></div>
  </aside>
</template>

<style scoped>
.pdm-category-sidebar{box-sizing:border-box;width:100%;max-width:none;display:flex;min-width:0;min-height:0;padding:10px 6px;flex-direction:column;overflow:auto;font-size:var(--pdm-font-size-body)}
.pdm-category-sidebar__top{display:flex;align-items:center;gap:4px}.standard-library__all{flex:1;min-width:0;border:0;border-radius:5px;background:transparent;text-align:left;padding:10px;margin:6px 0;color:var(--pdm-text);font:inherit;cursor:pointer}.standard-library__all.is-active{background:var(--pdm-theme-accent-soft);color:var(--pdm-theme-accent)}
.pdm-category-sidebar :deep(.el-tree){background:transparent;font-size:13px}.pdm-category-sidebar :deep(.el-tree-node__content){height:26px;border-radius:0}.pdm-category-sidebar :deep(.el-tree--highlight-current .el-tree-node.is-current > .el-tree-node__content){color:inherit}
.standard-library__category-label{width:100%;display:flex;gap:6px;align-items:center;overflow:hidden}.standard-library__category-label>span{overflow:hidden;text-overflow:ellipsis;white-space:nowrap}.standard-library__category-label>svg{flex-shrink:0;color:#b07c2c;fill:#f7e9ca}.standard-library__category-label.is-selected>svg{color:#2563eb;fill:#dbeafe}.standard-library__category-label.is-inactive,.standard-library__category-label small{color:var(--pdm-muted)}
.standard-library__category-actions{margin-top:auto;padding:14px 8px 5px}.standard-library__category-actions :deep(.el-button){font-size:12px}.pdm-category-sidebar__toggle{width:24px;height:24px;flex:0 0 24px;padding:0;border:1px solid var(--shell-accent-border);border-radius:5px;background:var(--pdm-theme-accent-soft);color:var(--pdm-theme-accent);font-size:16px;cursor:pointer}.pdm-category-sidebar.is-collapsed{padding:5px}.is-collapsed .pdm-category-sidebar__top{justify-content:center}
:global(:root body .pdm-category-sidebar.material-category-nav){border:1px solid var(--pdm-container-border)!important}
@media(max-width:700px){.pdm-category-sidebar{max-height:190px}}
</style>
