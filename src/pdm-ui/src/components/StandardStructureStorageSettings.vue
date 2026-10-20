<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { readStructureStorage, saveStructureStorage } from '../api'
import { ElMessage } from '../statusMessage'

const props = defineProps<{ token: string }>()
const storageRoot = ref('')
const rowVersion = ref(0)
const busy = ref(false)
const error = ref('')
async function load() {
  busy.value = true
  error.value = ''
  try { const result = await readStructureStorage(props.token); storageRoot.value = result.storageRoot; rowVersion.value = result.rowVersion }
  catch { error.value = '标准化存档目录设置加载失败，请重新加载。' }
  finally { busy.value = false }
}
async function save() {
  busy.value = true
  try {
    const result = await saveStructureStorage(storageRoot.value.trim(), rowVersion.value, props.token)
    storageRoot.value = result.storageRoot
    rowVersion.value = result.rowVersion
    ElMessage.success('标准化存档目录已保存。')
  } catch (cause) { ElMessage.error(cause instanceof Error ? cause.message : '标准化存档目录保存失败。') }
  finally { busy.value = false }
}
onMounted(load)
</script>

<template>
  <section class="pdm-panel pdm-manager-panel" aria-label="标准化存档目录设置">
    <header class="pdm-manager-heading"><div><h2>标准化存档目录</h2><p>独立保存标准结构图档、预览及历史版本，不能与项目图档和生产发包目录相同或互相包含。</p></div></header>
    <div v-if="error" role="alert">{{ error }}<el-button @click="load">重新加载</el-button></div>
    <template v-else><div class="pdm-settings-form"><label>标准化存档根目录<input v-model="storageRoot" :disabled="busy" placeholder="例如 D:\PLM\StandardStructures" aria-label="标准化存档根目录"><small>已有存档时切换目录需先完成文件迁移和核对，系统会阻止直接改到新位置。</small></label></div><div class="pdm-settings-actions"><button type="button" class="pdm-primary-action" :disabled="busy || !storageRoot.trim()" @click="save">保存标准化存档目录</button></div></template>
  </section>
</template>
