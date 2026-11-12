<script setup lang="ts">
import { computed } from 'vue'
import type { PdmCustomer } from '../types'
import CompanyName from './CompanyName.vue'

const props = defineProps<{ customers?: readonly Pick<PdmCustomer, 'id' | 'name' | 'code'>[] }>()
const customerId = defineModel<string>({ required: true })
const selected = computed(() => props.customers?.find(item => item.id === customerId.value))
</script>

<template>
  <el-select v-model="customerId" filterable>
    <template #label="{ label }">
      <CompanyName :name="selected?.name ?? label" /><template v-if="selected">（{{ selected.code }}）</template>
    </template>
    <el-option v-for="item in customers" :key="item.id" :value="item.id" :label="`${item.name}（${item.code}）`" :aria-label="`${item.name}（${item.code}）`">
      <CompanyName :name="item.name" />（{{ item.code }}）
    </el-option>
  </el-select>
</template>
