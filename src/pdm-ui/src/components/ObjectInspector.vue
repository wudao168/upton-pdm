<script setup lang="ts">
defineProps<{ title: string; fields: Array<{ label: string; value?: string | number | null }>; issues?: string[] }>()
const opened = defineModel<boolean>({ default: false })
</script>
<template>
  <el-drawer v-model="opened" :title="title" size="420px" append-to-body class="pdm-object-inspector">
    <div v-if="issues?.length" class="pdm-inspector-issues" role="status"><strong>需要核对</strong><ul><li v-for="issue in issues" :key="issue">{{ issue }}</li></ul></div>
    <dl class="pdm-inspector-fields"><div v-for="field in fields" :key="field.label"><dt>{{ field.label }}</dt><dd>{{ field.value ?? '—' }}</dd></div></dl>
    <slot />
  </el-drawer>
</template>
