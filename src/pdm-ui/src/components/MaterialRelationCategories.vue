<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { assignMaterialRelationCategory, getMaterialRelationCategories, listMaterialRelationCategories } from '../api'
import type { StandardLibraryCategory } from '../types'
import { isUncategorized, standardCategoryTree } from '../standardLibraryCategories'
import { userErrorMessage } from '../userErrorMessage'
const props = defineProps<{ token: string; materialId: string; canManage: boolean }>()
const emit = defineEmits<{ changed: []; loaded: [assigned: boolean] }>()
const assigned = ref<StandardLibraryCategory[]>([]), catalog = ref<StandardLibraryCategory[]>([]), selected = ref(''), loading = ref(false), saving = ref(false), error = ref('')
const tree = computed(() => standardCategoryTree(catalog.value.filter(item => !isUncategorized(item))))
let generation = 0
async function load() {
  const current = ++generation
  loading.value = true; error.value = ''; assigned.value = []; selected.value = ''; emit('loaded', false)
  try {
    const [values, categories] = await Promise.all([getMaterialRelationCategories(props.materialId, props.token), listMaterialRelationCategories(props.token)])
    if (current === generation) { assigned.value = values; catalog.value = categories; emit('loaded', values.length > 0) }
  } catch (cause) { if (current === generation) error.value = userErrorMessage(cause instanceof Error ? cause.message : '分类读取失败，请刷新后重试。') }
  finally { if (current === generation) loading.value = false }
}
async function save() {
  if (!selected.value || saving.value) return
  saving.value = true; error.value = ''
  try { assigned.value = await assignMaterialRelationCategory(props.materialId, selected.value, props.token); emit('loaded', assigned.value.length > 0); emit('changed') }
  catch (cause) { error.value = userErrorMessage(cause instanceof Error ? cause.message : '分类保存失败，请重试。') }
  finally { saving.value = false }
}
watch(() => [props.materialId, props.token], load, { immediate: true })
</script>

<template>
  <section class="material-relation-categories" aria-label="关联配置继承分类" v-loading="loading">
    <strong>标准物料分类</strong>
    <template v-if="assigned.length"><el-tag v-for="category in assigned" :key="category.id">{{ category.name }}</el-tag><span>继承主料归属；分类调整请在标准物料中维护。</span></template>
    <template v-else><span>未分类，请由标准化人员补选。</span><template v-if="canManage"><el-tree-select v-model="selected" :data="tree" node-key="id" check-strictly filterable :props="{ label: 'label', children: 'children' }" placeholder="选择标准物料分类" aria-label="补选标准物料分类" /><el-button :disabled="!selected" :loading="saving" @click="save">保存分类</el-button></template></template>
    <p v-if="error" role="alert">{{ error }}</p>
  </section>
</template>

<style scoped>
.material-relation-categories{display:flex;align-items:center;flex-wrap:wrap;gap:8px;padding:10px 0;font-size:12px}.material-relation-categories>span{color:var(--pdm-muted)}.material-relation-categories :deep(.el-tree-select){width:230px}.material-relation-categories p{width:100%;margin:0;color:var(--pdm-danger)}
</style>
