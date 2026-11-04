<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue'
import SearchField from './SearchField.vue'
import { listMaterialBrandCatalog, saveMaterialBrand, type MaterialBrand } from '../api'
import { ElMessage } from '../statusMessage'
const props = defineProps<{ token: string; canManage: boolean }>()
const emit = defineEmits<{ close: [] }>()
const rows = ref<MaterialBrand[]>([]), loading = ref(false), saving = ref(false), name = ref(''), editing = ref<MaterialBrand>()
const query = ref(''), page = ref(1), pageSize = ref(50)
const filteredRows = computed(() => {
  const keyword = query.value.trim().toLocaleLowerCase()
  return keyword ? rows.value.filter(brand => [brand.name, ...brand.previousNames].some(value => value.toLocaleLowerCase().includes(keyword))) : rows.value
})
const pageRows = computed(() => filteredRows.value.slice((page.value - 1) * pageSize.value, page.value * pageSize.value))
watch([query, pageSize], () => { page.value = 1 })
watch(() => filteredRows.value.length, total => { page.value = Math.min(page.value, Math.max(1, Math.ceil(total / pageSize.value))) })
async function load() { loading.value = true; try { rows.value = await listMaterialBrandCatalog(props.token) } catch { ElMessage.error('品牌库读取失败，请重试。') } finally { loading.value = false } }
function edit(brand?: MaterialBrand) { editing.value = brand; name.value = brand?.name ?? '' }
async function save(brand?: MaterialBrand, active?: boolean) {
  const current = brand ?? editing.value, value = brand?.name ?? name.value.trim()
  if (!value) return ElMessage.warning('请输入品牌名称。')
  saving.value = true
  try { await saveMaterialBrand(current?.id, { name: value, isActive: active ?? current?.isActive ?? true, rowVersion: current?.rowVersion }, props.token); edit(); await load(); ElMessage.success('品牌库已保存。') }
  catch (cause) { ElMessage.error(cause instanceof Error && /品牌/.test(cause.message) ? cause.message : '品牌库保存失败，请刷新后重试。') }
  finally { saving.value = false }
}
onMounted(load)
</script>
<template>
  <el-dialog :model-value="true" title="品牌库" width="760px" align-center @close="emit('close')">
    <p>新增和编辑物料时从品牌库选择。停用品牌保留历史数据；名称调整保留曾用名称，后续编辑时采用规范名称。</p>
    <div class="pdm-toolbar"><SearchField><el-input v-model="query" clearable aria-label="搜索品牌" placeholder="搜索品牌名称或曾用名称" /></SearchField></div>
    <div v-if="canManage" class="pdm-toolbar"><el-input v-model="name" aria-label="品牌名称" maxlength="160" placeholder="输入品牌名称" :disabled="saving" style="width:300px" /><el-button type="primary" :disabled="saving || !name.trim()" @click="save()">{{ editing ? '保存品牌' : '新增品牌' }}</el-button><el-button v-if="editing" :disabled="saving" @click="edit()">取消编辑</el-button></div>
    <el-table v-loading="loading" :data="pageRows" max-height="460"><el-table-column prop="name" label="品牌名称" min-width="160" /><el-table-column label="曾用名称" min-width="180"><template #default="{ row }">{{ row.previousNames.join('、') || '—' }}</template></el-table-column><el-table-column label="状态" width="90"><template #default="{ row }">{{ row.isActive ? '启用' : '已停用' }}</template></el-table-column><el-table-column v-if="canManage" label="操作" width="160"><template #default="{ row }"><el-button link :disabled="saving" @click="edit(row)">编辑</el-button><el-button link :disabled="saving" @click="save(row, !row.isActive)">{{ row.isActive ? '停用' : '启用' }}</el-button></template></el-table-column></el-table>
    <el-pagination v-model:current-page="page" v-model:page-size="pageSize" class="material-brand-pagination" aria-label="品牌库分页" :page-sizes="[50, 100, 200]" :total="filteredRows.length" layout="total, sizes, prev, pager, next" />
    <template #footer><el-button :disabled="saving" @click="emit('close')">关闭</el-button></template>
  </el-dialog>
</template>
<style scoped>
.material-brand-pagination{flex:0 0 auto;justify-content:flex-end;padding-top:8px}
</style>
