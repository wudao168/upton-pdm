<script setup lang="ts">
withDefaults(defineProps<{ disabled?: boolean; label?: string }>(), { label: '添加图片' })
defineEmits<{ click: [] }>()
</script>

<template>
  <button class="material-image-add" type="button" :disabled="disabled" @click="$emit('click')"><span aria-hidden="true">＋</span>{{ label }}</button>
</template>

<style scoped>
.material-image-add{display:flex;align-items:center;justify-content:center;flex-direction:column;flex-shrink:0;width:112px;min-height:116px;border:1px dashed var(--pdm-border);border-radius:4px;background:var(--pdm-surface-muted);color:var(--pdm-theme-accent);font:inherit;cursor:pointer}.material-image-add>span{font-size:28px}.material-image-add:disabled{cursor:default;opacity:.5}
</style>
