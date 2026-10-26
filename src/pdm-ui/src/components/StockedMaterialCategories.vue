<script setup lang="ts">
import { computed, reactive, ref, watch } from 'vue'
import { ElMessageBox } from 'element-plus'
import { deleteStandardLibraryCategory, listStandardLibraryCategories, saveStandardLibraryCategory } from '../api'
import type { StandardLibraryCategory } from '../types'
import { ElMessage } from '../statusMessage'
import { userErrorMessage } from '../userErrorMessage'

const props = defineProps<{ token: string; modelValue: boolean }>()
const emit = defineEmits<{ 'update:modelValue': [value: boolean]; changed: [] }>()
const open = computed({ get: () => props.modelValue, set: value => emit('update:modelValue', value) })
const categories = ref<StandardLibraryCategory[]>([]), saving = ref(false), editingId = ref<string | undefined>(), error = ref('')
const form = reactive({ name: '', parentId: '', isActive: true, sortOrder: 0, expectedRowVersion: undefined as number | undefined })
const editable = computed(() => categories.value.filter(category => category.parentId || category.name !== '未分类'))
async function load() { try { categories.value = await listStandardLibraryCategories(props.token, true) } catch (reason) { showError(reason) } }
function showError(reason: unknown) { error.value = userErrorMessage(reason instanceof Error ? reason.message : '分类维护失败，请重试。') }
function edit(category?: StandardLibraryCategory) {
  error.value = ''; editingId.value = category?.id
  Object.assign(form, { name: category?.name ?? '', parentId: category?.parentId ?? '', isActive: category?.isActive ?? true,
    sortOrder: category?.sortOrder ?? (Math.max(0, ...categories.value.map(row => row.sortOrder)) + 10), expectedRowVersion: category?.rowVersion })
}
async function save() {
  if (!form.name.trim()) { error.value = '请输入分类名称。'; return }
  saving.value = true; error.value = ''
  try { await saveStandardLibraryCategory({ ...form, parentId: form.parentId || null }, props.token, editingId.value); await load(); edit(); emit('changed'); ElMessage.success('分类已保存') }
  catch (reason) { showError(reason) } finally { saving.value = false }
}
async function remove(category: StandardLibraryCategory) {
  try { await ElMessageBox.confirm(`确认删除分类“${category.name}”？仅空分类可以删除。`, '删除分类', { type: 'warning' }) } catch { return }
  saving.value = true
  try { await deleteStandardLibraryCategory(category.id, category.rowVersion, props.token); await load(); edit(); emit('changed') }
  catch (reason) { showError(reason) } finally { saving.value = false }
}
watch(open, async value => { if (value) { await load(); edit() } })
</script>
<template>
  <el-dialog v-model="open" title="分类维护" width="min(900px, calc(100vw - 32px))">
    <p>分类与标准物料共用；常备件同时收录在标准库，移出常备件后仍保留标准库收录。</p>
    <p v-if="error" role="alert" class="category-error">{{ error }}</p>
    <div class="stocked-category-layout">
      <el-table :data="categories" height="400"><el-table-column prop="name" label="分类名称" min-width="130" /><el-table-column label="操作" width="120"><template #default="{ row }"><template v-if="row.parentId || row.name !== '未分类'"><el-button link :disabled="saving" @click="edit(row)">编辑</el-button><el-button link type="danger" :disabled="saving" @click="remove(row)">删除</el-button></template></template></el-table-column></el-table>
      <el-form label-position="top"><el-button :disabled="saving" @click="edit()">新增分类</el-button><el-form-item label="分类名称"><el-input v-model="form.name" maxlength="150" /></el-form-item><el-form-item label="上级分类"><el-select v-model="form.parentId" clearable placeholder="顶级分类"><el-option v-for="category in editable.filter(item => item.id !== editingId)" :key="category.id" :label="category.name" :value="category.id" /></el-select></el-form-item><el-checkbox v-model="form.isActive">启用</el-checkbox><el-button type="primary" :loading="saving" @click="save">保存分类</el-button></el-form>
    </div>
    <template #footer><el-button :disabled="saving" @click="open = false">关闭</el-button></template>
  </el-dialog>
</template>
<style scoped>
.stocked-category-layout{display:grid;grid-template-columns:1fr 280px;gap:20px}.stocked-category-layout .el-form-item{margin-top:12px}.category-error{color:var(--el-color-danger)}@media(max-width:650px){.stocked-category-layout{grid-template-columns:1fr}}
</style>
