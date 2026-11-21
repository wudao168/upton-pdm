<script setup lang="ts">
defineProps<{ label: string }>()
</script>

<template>
  <div class="pdm-table-scroll storage-directory-list">
    <table class="pdm-project-table pdm-fit-columns" :aria-label="label">
      <colgroup><col class="storage-directory-list__name"><col></colgroup>
      <thead><tr><th scope="col">目录名称</th><th scope="col">路径</th></tr></thead>
      <tbody><slot /></tbody>
    </table>
  </div>
</template>

<style scoped>
.storage-directory-list { width: 100%; min-width: 0; }
.storage-directory-list__name { width: clamp(96px, 28%, 220px); }
.storage-directory-list :deep(:is(th, td)) {
  padding: 4px 8px;
  text-align: center;
  vertical-align: middle;
  white-space: normal;
  overflow-wrap: anywhere;
}
.storage-directory-list :deep(tbody th) {
  position: static;
  border-top: 1px solid var(--pdm-border-soft);
  background: transparent;
  color: var(--pdm-text);
}
.storage-directory-list :deep(.pdm-table-input) { width: 100%; min-width: 0; }
</style>
