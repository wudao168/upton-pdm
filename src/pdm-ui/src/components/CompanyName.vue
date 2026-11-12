<script setup lang="ts">
import PersonName from './PersonName.vue'

defineProps<{ name?: string | null; initialOnly?: boolean }>()
</script>

<template>
  <PersonName class="pdm-company-name" :name="name" company :initial-only="initialOnly" />
</template>
