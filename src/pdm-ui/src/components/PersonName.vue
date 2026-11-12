<script setup lang="ts">
import { computed } from 'vue'
import { nameColorHue } from '../nameColorHue'

const props = defineProps<{ name?: string | null; company?: boolean; initialOnly?: boolean }>()
const placeholders = new Set(['—', '-', '–', '待分配', '未分配', '未分派', '待排产', '未指定', '未设置', '无', '姓名待补充', '未知人员', '未知用户', '系统示例', '系统自动', '历史备注'])

const companyPlaceholders = new Set(['请选择公司', '未知公司', '未指定公司', '客户待补充', '未设置客户'])

const parts = computed(() => {
  const value = props.name ?? ''
  const names = props.company ? [value] : value.split(/([、，,；;])/u)
  return names.map((text, index) => {
    const name = text.trim().normalize('NFC')
    const hasInitial = index % 2 === 0 && name && !placeholders.has(name) && !(props.company && companyPlaceholders.has(name))
    const initial = hasInitial ? [...name][0] : ''
    return { text, initial, hue: initial ? nameColorHue(name.toLowerCase()) : 0 }
  })
})
</script>

<template>
  <span class="pdm-person-names" :title="initialOnly ? undefined : name || undefined">
    <template v-for="(part, index) in parts" :key="index">
      <span v-if="part.initial" class="pdm-person-name">
        <span class="pdm-person-initial" aria-hidden="true" :data-initial="part.initial" :style="{ '--pdm-person-hue': part.hue }"></span>
        <span v-if="!initialOnly" class="pdm-person-name__text">{{ part.text }}</span>
      </span>
      <template v-else-if="!initialOnly">{{ part.text }}</template>
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
:global(:root[data-pdm-color-scheme="dark"] .pdm-person-initial) {
  background-color: hsl(var(--pdm-person-hue) 38% 27%);
  color: hsl(var(--pdm-person-hue) 76% 87%);
}
</style>
