<script setup lang="ts">
import { ref } from 'vue'
import { ElPopover } from 'element-plus'
defineProps<{ disabled: boolean; full: boolean; label: string; allowConditions?: boolean }>()
const emit = defineEmits<{ insert: [kind: 'Approval' | 'Condition'] }>()
const visible = ref(false)
function insert(kind: 'Approval' | 'Condition') { visible.value = false; emit('insert',kind) }
</script>
<template>
  <ElPopover v-model:visible="visible" trigger="click" placement="bottom" :width="200" :disabled="disabled" teleported>
    <template #reference><button type="button" class="add-point" :disabled="disabled" :aria-label="label">＋</button></template>
    <div class="add-menu" role="group" aria-label="添加流程节点">
      <button type="button" :disabled="disabled || full" @click="insert('Approval')">审核节点</button>
      <button v-if="allowConditions !== false" type="button" :disabled="disabled" @click="insert('Condition')">条件分支</button>
    </div>
  </ElPopover>
</template>
<style scoped>
.add-point{position:relative;z-index:1;border-radius:50%;border:1px solid var(--pdm-border);background:var(--pdm-surface);width:24px;height:24px;padding:0;color:var(--pdm-text)}
.add-menu{display:flex;justify-content:center;gap:8px;white-space:nowrap}.add-menu button{border:1px solid var(--pdm-border);border-radius:4px;background:var(--pdm-surface);color:var(--pdm-text);padding:6px 10px;font:inherit;cursor:pointer}.add-menu button:disabled{opacity:.5;cursor:not-allowed}
</style>
