<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { listMaterialBrandCatalog, type MaterialBrand } from '../api'
import { ElMessage } from '../statusMessage'
const props = defineProps<{ token: string; modelValue?: string | null; disabled?: boolean }>()
const emit = defineEmits<{ 'update:modelValue': [value: string] }>()
const brands = ref<MaterialBrand[]>([]), loading = ref(false)
const legacyValue = computed(() => props.modelValue && !brands.value.some(brand => brand.name === props.modelValue) ? props.modelValue : '')
onMounted(async () => { loading.value = true; try { brands.value = await listMaterialBrandCatalog(props.token) } catch { ElMessage.error('品牌库读取失败，请刷新后重试。') } finally { loading.value = false } })
</script>
<template>
  <el-select :model-value="modelValue ?? ''" filterable clearable :disabled="disabled" :loading="loading" placeholder="从品牌库选择品牌" aria-label="品牌" @update:model-value="emit('update:modelValue', $event ?? '')">
    <el-option v-if="legacyValue" :label="legacyValue" :value="legacyValue" disabled />
    <el-option v-for="brand in brands" :key="brand.id" :label="`${brand.name}${brand.isActive ? '' : '（已停用）'}`" :value="brand.name" :disabled="!brand.isActive" />
  </el-select>
</template>
