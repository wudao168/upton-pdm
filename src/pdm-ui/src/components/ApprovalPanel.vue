<script setup lang="ts">
import { Check } from '@lucide/vue'
import type { ReleasePackageSummary } from '../types'

defineProps<{ releasePackage: ReleasePackageSummary | null }>()
</script>

<template>
  <section class="pdm-panel pdm-info-panel" aria-label="当前发布包">
    <header class="pdm-panel-heading"><h2>当前发布包</h2><span v-if="releasePackage" class="pdm-package-number">{{ releasePackage.number }}</span></header>
    <div v-if="releasePackage" class="pdm-approval-flow">
      <template v-for="(step, index) in releasePackage.steps" :key="step.stage">
        <div class="pdm-approval-step" :class="`is-${step.status}`">
          <span class="pdm-approval-step__number"><Check v-if="step.status === 'done'" :size="14" /><template v-else>{{ index + 1 }}</template></span>
          <p><strong>{{ step.stage }}</strong><small>{{ step.assignee }} · {{ step.detail }}</small></p>
        </div>
        <i v-if="index < releasePackage.steps.length - 1" class="pdm-approval-line" :class="{ 'is-done': step.status === 'done' }" />
      </template>
    </div>
    <p v-else class="pdm-empty-info">当前项目暂无发布包</p>
  </section>
</template>
