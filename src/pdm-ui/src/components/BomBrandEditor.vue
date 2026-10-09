<script setup lang="ts">
import { ElAutocomplete } from 'element-plus'
import { onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { listMaterialBrands } from '../api'
import { ElMessage } from '../statusMessage'

const props = defineProps<{ value: string; token?: string; persistent?: boolean; label?: string }>()
const emit = defineEmits<{ commit: [value: string]; update: [value: string]; cancel: [] }>()
const value = ref(props.value)
const editor = ref<{ focus: () => void; close: () => void }>()
let request = 0
let finished = false
let disposed = false
let committedValue = props.value
watch(() => props.value, next => { value.value = next })

function update(next: string | number) {
  request++
  value.value = String(next)
  if (props.persistent) emit('update', value.value)
}

async function suggestions(query: string, callback: (items: { value: string }[]) => void) {
  const current = ++request
  if (disposed || !props.token) return callback([])
  try {
    const brands = await listMaterialBrands(props.token, query)
    if (current === request) callback(brands.map(value => ({ value })))
  } catch {
    if (current === request) {
      callback([])
      ElMessage.warning('品牌候选查询失败，可继续手工输入或重试')
    }
  }
}
function commit() {
  if (props.persistent) {
    if (committedValue === value.value) return
    committedValue = value.value
    emit('commit', value.value)
    return
  }
  if (finished) return
  finished = true
  emit('commit', value.value)
}
function cancel() {
  if (props.persistent) return editor.value?.close()
  finished = true
  emit('cancel')
}
onMounted(() => { if (!props.persistent) editor.value?.focus() })
onBeforeUnmount(() => { disposed = true; request++ })
</script>

<template>
  <div class="pdm-bom-brand-editor" @keydown.enter.prevent="commit" @keydown.esc.capture.prevent="cancel">
    <ElAutocomplete ref="editor" :model-value="value" :aria-label="label ?? '内联编辑品牌'" @update:model-value="update"
      :fetch-suggestions="suggestions" :debounce="200" :select-when-unmatched="true" size="small"
      @select="commit" @blur="commit" />
  </div>
</template>

<style scoped>
.pdm-bom-brand-editor { width: 100%; }
.pdm-bom-brand-editor :deep(.el-autocomplete) { width: 100%; }
.pdm-bom-brand-editor :deep(.el-input__wrapper) { padding: 0 4px; }
.pdm-bom-brand-editor :deep(.el-input__inner) { height: 22px; font: inherit; }
</style>
