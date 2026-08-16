<script setup lang="ts">
import { Boxes, FileCheck2, FolderTree, PackageCheck } from '@lucide/vue'
import type { DocumentNode, ProjectSummary, ReleasePackageSummary } from '../types'

defineProps<{
  project: ProjectSummary
  selected: DocumentNode
  hasDocuments: boolean
  documentCount: number
  warningCount: number
  mechanicalCount: number
  electricalCount: number
  releasePackage: ReleasePackageSummary | null
}>()

const emit = defineEmits<{ documents: []; bom: [] }>()
</script>

<template>
  <section class="pdm-workbench" aria-label="工作台主页面">
    <header class="pdm-project-overview-heading">
      <div><h2>项目概览</h2><p>汇总图档、BOM、发布状态和项目存储位置。</p></div>
      <div class="pdm-page-actions">
        <button type="button" class="pdm-secondary-action" @click="emit('bom')"><Boxes :size="16" />查看BOM</button>
        <button type="button" class="pdm-primary-action" @click="emit('documents')"><FolderTree :size="16" />进入项目图档</button>
      </div>
    </header>

    <div class="pdm-workbench-grid">
      <article class="pdm-panel pdm-stat-card">
        <span class="is-blue"><FolderTree :size="19" /></span>
        <div><small>项目图档</small><strong>{{ documentCount }}</strong><em>{{ warningCount ? `${warningCount} 个异常引用` : '引用结构正常' }}</em></div>
      </article>
      <article class="pdm-panel pdm-stat-card">
        <span class="is-green"><Boxes :size="19" /></span>
        <div><small>BOM数据</small><strong>{{ mechanicalCount + electricalCount }}</strong><em>机械 {{ mechanicalCount }} · 电气 {{ electricalCount }}</em></div>
      </article>
      <article class="pdm-panel pdm-stat-card">
        <span class="is-orange"><PackageCheck :size="19" /></span>
        <div><small>当前发布包</small><strong class="is-code">{{ releasePackage?.number || '暂无' }}</strong><em>{{ releasePackage?.state || '尚未创建发布包' }}</em></div>
      </article>

      <article class="pdm-panel pdm-workbench-detail">
        <header class="pdm-panel-heading"><h2>当前工作图档</h2><button type="button" class="pdm-text-action" @click="emit('documents')">查看结构</button></header>
        <div v-if="hasDocuments" class="pdm-current-document">
          <span><FileCheck2 :size="22" /></span>
          <div><strong>{{ selected.drawingNumber }} · {{ selected.name }}</strong><small>{{ selected.fileName }}</small></div>
          <dl>
            <div><dt>工作版本</dt><dd>{{ selected.version }}</dd></div>
            <div><dt>状态</dt><dd>{{ selected.checkedOutBy ? `正在编辑 · ${selected.checkedOutBy}` : '可用' }}</dd></div>
            <div><dt>配置</dt><dd>{{ selected.configuration }}</dd></div>
          </dl>
        </div>
        <div v-else class="pdm-project-link-guide">
          <FolderTree :size="34" />
          <div><strong>项目尚未关联图纸</strong><p>请在SolidWorks端刷新项目列表，选择“{{ project.code }} · {{ project.name }}”，再提交图纸存档。</p></div>
        </div>
      </article>

      <article class="pdm-panel pdm-workbench-detail">
        <header class="pdm-panel-heading"><h2>项目存储位置</h2></header>
        <dl class="pdm-location-list">
          <div><dt>图档库</dt><dd :title="project.vaultLocation">{{ project.vaultLocation }}</dd></div>
          <div><dt>发包目录</dt><dd :title="project.releaseLocation">{{ project.releaseLocation }}</dd></div>
        </dl>
      </article>
    </div>
  </section>
</template>
