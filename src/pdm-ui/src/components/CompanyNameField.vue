<script setup lang="ts">
import CompanyName from './CompanyName.vue'

defineProps<{ name?: string | null }>()
</script>

<template>
  <span class="pdm-company-name-field">
    <slot />
    <CompanyName v-if="name" class="pdm-company-name-field__initial" :name="name" initial-only aria-hidden="true" />
  </span>
</template>

<style scoped>
.pdm-company-name-field { position: relative; display: grid; min-width: 0; }
.pdm-company-name-field:has(.pdm-person-initial) > :slotted(:is(select, input)) { padding-inline-start: 32px !important; }
.pdm-company-name-field__initial { position: absolute; inset-inline-start: 10px; align-self: center; pointer-events: none; }
</style>
