<script setup lang="ts">
import { computed, onBeforeUnmount, ref, watch } from 'vue'
import { readProductionSuppliers } from '../api'
import type { ProductionSupplier, ProductionSupplierCatalog } from '../productionSuppliers'
import { ElMessage } from '../statusMessage'

const props = defineProps<{ token: string; modelValue: string; disabled?: boolean }>()
const emit = defineEmits<{ 'update:modelValue': [id: string]; selected: [supplier: ProductionSupplier | undefined] }>()
const catalog = ref<ProductionSupplierCatalog>(), categoryId = ref(''), pending = ref(false)
const categories = computed(() => catalog.value?.categories.filter(item => item.isActive) ?? [])
const choices = computed(() => catalog.value?.suppliers.filter(item => item.isActive
  && item.categoryIds.some(id => categories.value.some(category => category.id === id))
  && (!categoryId.value || item.categoryIds.includes(categoryId.value))) ?? [])
let generation = 0
onBeforeUnmount(() => { generation++ })
function choose(id: string) {
  const selected = choices.value.find(item => item.id === id)
  emit('update:modelValue', selected?.id ?? '')
  emit('selected', selected)
}
async function load() {
  const current = ++generation
  pending.value = true
  try {
    const value = await readProductionSuppliers(props.token)
    if (current !== generation) return
    catalog.value = value
    if (categoryId.value && !categories.value.some(item => item.id === categoryId.value)) categoryId.value = ''
    if (props.modelValue) choose(props.modelValue)
  } catch (cause) {
    if (current === generation) ElMessage.error(cause instanceof Error ? cause.message : '供应商列表读取失败，请稍后重试。')
  } finally { if (current === generation) pending.value = false }
}
watch(() => props.token, () => { if (props.token) void load() }, { immediate: true })
watch(categoryId, () => { if (props.modelValue && !choices.value.some(item => item.id === props.modelValue)) choose('') })
</script>

<template>
  <div class="production-supplier-picker">
    <label>供应商分类<select v-model="categoryId" aria-label="供应商分类" :disabled="disabled || pending"><option value="">全部分类</option><option v-for="item in categories" :key="item.id" :value="item.id">{{ item.name }}</option></select></label>
    <label class="production-supplier-picker__supplier">供应商<select :value="modelValue" aria-label="供应商" :disabled="disabled || pending" @change="choose(($event.target as HTMLSelectElement).value)"><option value="">{{ pending ? '正在读取供应商…' : '请选择供应商' }}</option><option v-for="item in choices" :key="item.id" :value="item.id">{{ item.code }} · {{ item.name }}</option></select></label>
    <button type="button" :disabled="disabled || pending" aria-label="刷新供应商" @click="load">刷新</button>
    <small v-if="catalog && !choices.length && !pending">暂无符合条件的启用供应商，请在“供应商管理”中维护。</small>
  </div>
</template>

<style scoped>
.production-supplier-picker{display:flex;flex-wrap:wrap;gap:10px;align-items:end;max-width:100%;min-width:0}.production-supplier-picker label{display:grid;gap:5px;font-size:12px;min-width:0;flex:1 1 120px}.production-supplier-picker__supplier{flex:2 1 190px!important}.production-supplier-picker select{height:30px;width:100%;min-width:0;box-sizing:border-box;padding:0 8px;border:1px solid var(--pdm-border);border-radius:var(--pdm-radius-control);background:var(--pdm-surface);color:inherit;font:inherit}.production-supplier-picker button{height:30px;padding:0 7px;border:1px solid var(--pdm-border);border-radius:var(--pdm-radius-control);background:var(--pdm-surface);color:var(--pdm-blue);cursor:pointer}.production-supplier-picker small{flex-basis:100%;color:var(--pdm-muted);font-size:11px}
</style>
