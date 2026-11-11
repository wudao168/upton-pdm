<script setup lang="ts">
import { computed } from 'vue'

const props = defineProps<{ name?: string | null }>()
const placeholders = new Set(['—', '-', '–', '待分配', '未分配', '未分派', '待排产', '未指定', '未设置', '无', '姓名待补充', '未知人员', '未知用户', '系统示例', '系统自动', '历史备注'])

function colorHue(name: string) {
  let hash = 2166136261
  for (const letter of name) hash = Math.imul(hash ^ letter.codePointAt(0)!, 16777619)
  return (hash >>> 0) % 360
}

const parts = computed(() => (props.name ?? '').split(/([、，,；;])/u).map((text, index) => {
  const name = text.trim().normalize('NFC')
  const initial = index % 2 || !name || placeholders.has(name) ? '' : [...name][0]
  return { text, initial, hue: initial ? colorHue(name.toLowerCase()) : 0 }
}))
</script>

<template>
  <span class="pdm-person-names" :title="name || undefined">
    <template v-for="(part, index) in parts" :key="index">
      <span v-if="part.initial" class="pdm-person-name">
        <span class="pdm-person-initial" aria-hidden="true" :data-initial="part.initial" :style="{ '--pdm-person-hue': part.hue }"></span>
        <span class="pdm-person-name__text">{{ part.text }}</span>
      </span>
      <template v-else>{{ part.text }}</template>
    </template>
  </span>
</template>

<style scoped>
.pdm-person-names {
  display: inline-block;
  max-inline-size: 100%;
  overflow: hidden;
  text-overflow: ellipsis;
  vertical-align: middle;
}
.pdm-person-name {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  max-inline-size: 100%;
  vertical-align: middle;
}
.pdm-person-name__text {
  min-inline-size: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}
.pdm-person-initial {
  display: inline-grid;
  place-items: center;
  flex: 0 0 16px;
  inline-size: 16px;
  block-size: 16px;
  border-radius: 5px;
  background-color: hsl(var(--pdm-person-hue) 63% 87%);
  color: hsl(var(--pdm-person-hue) 56% 22%);
  font-size: 11px;
  font-weight: 600;
  line-height: 1;
  user-select: none;
}
.pdm-person-initial::before { content: attr(data-initial); }
:global(:root[data-pdm-color-scheme="dark"]) .pdm-person-initial {
  background-color: hsl(var(--pdm-person-hue) 38% 27%);
  color: hsl(var(--pdm-person-hue) 76% 87%);
}
</style>
