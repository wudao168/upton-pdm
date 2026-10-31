<script setup lang="ts">
import { computed } from 'vue'
import type { StandardLibraryCategory } from '../types'
import { isUncategorized, standardCategoryTree } from '../standardLibraryCategories'
import CategoryNavigation from './CategoryNavigation.vue'
const props = defineProps<{ categories: StandardLibraryCategory[]; modelValue: string; includeInactive?: boolean }>()
const emit = defineEmits<{ 'update:modelValue': [id: string] }>()
const tree = computed(() => {
  const nodes = standardCategoryTree(props.categories, props.includeInactive)
  if (!props.categories.some(isUncategorized)) nodes.push({ id: '__unclassified__', label: '未分类', isActive: true, children: [] })
  return nodes
})
</script>

<template>
  <CategoryNavigation class="standard-category-filter" :nodes="tree" :model-value="modelValue" aria-label="标准物料分类筛选" @update:model-value="emit('update:modelValue', $event)"><template v-if="$slots.actions" #actions><slot name="actions" /></template></CategoryNavigation>
</template>
