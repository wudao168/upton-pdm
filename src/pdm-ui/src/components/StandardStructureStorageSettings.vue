<script setup lang="ts">
import { onMounted, ref, useId } from 'vue'
import { readStructureStorage, saveStructureStorage } from '../api'
import { ElMessage } from '../statusMessage'
import StorageDirectoryList from './StorageDirectoryList.vue'

const props = defineProps<{ token: string }>()
const storageId = useId()
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
    <header class="pdm-manager-heading"><h2>标准化存档目录</h2></header>
    <div v-if="error" role="alert">{{ error }}<el-button @click="load">重新加载</el-button></div>
    <template v-else>
      <StorageDirectoryList label="标准化存档目录">
        <tr>
          <th scope="row"><label :for="storageId">标准化存档根目录</label></th>
          <td><input :id="storageId" v-model="storageRoot" :disabled="busy" class="pdm-table-input"></td>
        </tr>
      </StorageDirectoryList>
      <div class="pdm-settings-actions"><button type="button" class="pdm-primary-action" :disabled="busy || !storageRoot.trim()" @click="save">保存标准化存档目录</button></div>
    </template>
  </section>
</template>
