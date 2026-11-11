<script setup lang="ts">
import { ref } from 'vue'
import SideNav from './SideNav.vue'
import ProjectReport from './ProjectReportPage.vue'
import { applyPdmTheme } from '../themes'

const dark = ref(document.documentElement.dataset.pdmColorScheme === 'dark')
const companyName = '昆山阿普顿自动化系统有限公司'
const previewNavigation = ['project-workbench', 'project-center', 'work-hours', 'tasks', 'production-drawings', 'standard-library', 'program-templates', 'materials']
const tabs = ['项目报表', '项目明细', '项目日历', '项目列表', '立项申请']
function toggleTheme() { dark.value = !dark.value; applyPdmTheme(dark.value ? 'c' : 'g') }
</script>

<template>
  <div class="report-preview-shell">
    <div class="report-preview-sidebar" inert aria-hidden="true">
      <SideNav active="project-workbench" :visible-keys="previewNavigation" :can-view-materials="true" />
    </div>
    <div class="report-preview-content">
      <header class="report-preview-header">
        <strong>{{ companyName }}</strong>
        <div class="report-preview-header__actions">
          <span class="report-preview-disclosure">设计待确认</span>
          <button type="button" @click="toggleTheme">{{ dark ? '浅色主题' : '深色主题' }}</button>
        </div>
      </header>
      <nav class="report-preview-tabs" aria-label="项目总览页签">
        <button v-for="(tab, index) in tabs" :key="tab" type="button" role="tab" :aria-selected="index === 0"
          :disabled="index !== 0" :class="{ 'is-active': index === 0 }">{{ tab }}</button>
      </nav>
      <main class="report-preview-main">
        <ProjectReport active :company-name="companyName" />
      </main>
    </div>
  </div>
</template>

<style scoped>
.report-preview-shell{display:grid;grid-template-columns:140px minmax(0,1fr);height:100dvh;overflow:hidden;background:var(--pdm-bg);color:var(--pdm-text);font-family:var(--pdm-font-ui)}
.report-preview-sidebar{height:100%;min-height:0;background:var(--pdm-nav-bg)}
.report-preview-sidebar :deep(.pdm-sidebar){height:100%}
.report-preview-sidebar :deep(.pdm-sidebar__footer){display:none}
.report-preview-content{display:flex;flex-direction:column;min-width:0;min-height:0}
.report-preview-header{height:50px;flex:0 0 50px;box-sizing:border-box;display:flex;align-items:center;justify-content:space-between;gap:16px;padding:0 22px;background:var(--pdm-nav-bg);border-bottom:1px solid var(--pdm-border-soft)}
.report-preview-header>strong{font-size:13px;font-weight:600}
.report-preview-header__actions{display:flex;align-items:center;gap:16px;font-size:12px;flex-shrink:0}
.report-preview-disclosure{color:var(--pdm-text-soft)}
.report-preview-header button{border:1px solid var(--pdm-border-soft);background:var(--pdm-nav-capsule-bg);color:var(--pdm-text);border-radius:999px;padding:5px 15px;cursor:pointer;font:inherit}
.report-preview-tabs{height:44px;flex:0 0 44px;display:flex;align-items:stretch;gap:24px;padding:0 23px;border-bottom:1px solid var(--pdm-border-soft);background:var(--pdm-surface)}
.report-preview-tabs button{position:relative;border:0;background:transparent;color:var(--pdm-text);padding:0 3px;font:13px var(--pdm-font-ui)}
.report-preview-tabs button:disabled{opacity:.65;cursor:default}
.report-preview-tabs .is-active{color:var(--pdm-theme-accent);font-weight:600}
.report-preview-tabs .is-active::after{content:'';position:absolute;bottom:0;height:2px;left:0;right:0;background:var(--pdm-theme-accent)}
.report-preview-main{display:flex;flex:1;min-width:0;min-height:0;overflow:hidden}
@media(max-width:1100px){.report-preview-header{padding:0 14px}.report-preview-header__actions{gap:10px}.report-preview-header>strong{max-width:45%;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}}
@media(max-width:760px){.report-preview-shell{grid-template-columns:minmax(0,1fr)}.report-preview-sidebar{display:none}.report-preview-header>strong{font-size:12px;max-width:48%}.report-preview-disclosure{display:none}.report-preview-tabs{gap:16px;padding:0 15px;overflow-x:auto;white-space:nowrap}.report-preview-tabs button{flex-shrink:0}}
</style>
