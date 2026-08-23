<script setup lang="ts">
import { Boxes, ContactRound, FileCheck2, FolderTree, PackageCheck, UsersRound } from '@lucide/vue'
import { computed } from 'vue'
import type { DocumentNode, DrawingReviewPackage, DrawingReviewTarget, MaterialCodeApplication, PdmUser, ProjectSummary, ReleasePackageSummary, ReleaseScope } from '../types'

const props = defineProps<{
  project: ProjectSummary
  projects: ProjectSummary[]
  users: PdmUser[]
  selected: DocumentNode
  currentUsername?: string
  hasDocuments: boolean
  documentCount: number
  modelCount: number
  drawingCount: number
  warningCount: number
  standardCount: number
  nonStandardCount: number
  electricalCount: number
  standardItemIds: string[]
  nonStandardItemIds: string[]
  electricalItemIds: string[]
  drawingReviews: DrawingReviewPackage[]
  materialApplications: MaterialCodeApplication[]
  releasePackages: ReleasePackageSummary[]
  releasePackage: ReleasePackageSummary | null
}>()

const emit = defineEmits<{ documents: []; bom: [] }>()

const rootProject = computed(() => {
  if (!props.project.parentProjectId) return props.project
  return props.projects.find(item => item.id === props.project.parentProjectId) ?? props.project
})

const familyProjects = computed(() => props.projects.filter(item => item.id === rootProject.value.id || item.parentProjectId === rootProject.value.id))

function assignedPeople(usernames: Array<string | undefined>) {
  const uniqueUsernames = [...new Set(usernames.map(item => item?.trim()).filter((item): item is string => Boolean(item)))]
  return uniqueUsernames.map(username => ({
    username,
    name: props.users.find(user => user.username.localeCompare(username, undefined, { sensitivity: 'accent' }) === 0)?.displayName || username,
  }))
}

const staffingRows = computed(() => [
  { key: 'manager', stage: '项目管理', role: '项目经理', people: assignedPeople([rootProject.value.primaryProjectManager]) },
  { key: 'collaborative-managers', stage: '项目管理', role: '协同项目经理', people: assignedPeople(rootProject.value.collaborativeProjectManagers) },
  { key: 'design-lead', stage: '设计阶段', role: '主设', people: assignedPeople([rootProject.value.designLead]) },
  { key: 'engineers', stage: '设计执行', role: '工程师', people: assignedPeople(familyProjects.value.flatMap(item => item.designers)) },
  { key: 'downstream', stage: '后续阶段', role: '各部门负责人', people: [] },
])

const drawingReviewSummary = computed(() => {
  const review = [...props.drawingReviews].sort((left, right) => right.createdAt.localeCompare(left.createdAt))[0]
  if (!review) return '图纸审核：未发起'
  const stateLabels = {
    InReview: '审核中',
    ChangesRequested: '已退改',
    WritingProperties: '写入标记',
    Approved: '已完成',
    Stale: '版本冲突',
  }
  const reviewed = review.items.reduce((count, item) => count
    + (['Approved', 'Marked'].includes(item.modelState) ? 1 : 0)
    + (['Approved', 'Marked'].includes(item.drawingState) ? 1 : 0), 0)
  return `图纸审核：${stateLabels[review.state]} · 双审 ${reviewed}/${review.items.length * 2}`
})

function drawingTargetSummary(target: DrawingReviewTarget) {
  const count = target === 'Model3D' ? props.modelCount : props.drawingCount
  if (count === 0) return '无图档'
  const review = [...props.drawingReviews].sort((left, right) => right.createdAt.localeCompare(left.createdAt))[0]
  if (!review) return '未发起'
  if (review.state === 'Stale') return '版本冲突'
  const states = review.items.map(item => target === 'Model3D' ? item.modelState : item.drawingState)
  const completed = states.filter(state => state === 'Approved' || state === 'Marked').length
  const changesRequested = states.filter(state => state === 'ChangesRequested').length
  if (states.length > 0 && completed === states.length) return '已完成'
  if (changesRequested > 0) return `已退改 ${changesRequested}`
  if (review.state === 'WritingProperties') return `写入标记 ${completed}/${states.length}`
  return `审核中 ${completed}/${states.length}`
}

function latestReleaseFor(scopes: ReleaseScope[]) {
  return [...props.releasePackages]
    .filter(item => scopes.includes(item.scope))
    .sort((left, right) => (right.createdAt ?? '').localeCompare(left.createdAt ?? ''))[0]
}

function bomApprovalStatus(scopes: ReleaseScope[]) {
  const release = latestReleaseFor(scopes)
  if (!release) return '未发起'
  const currentStep = release.steps.find(step => step.status === 'current')
  return `${release.state}${currentStep ? ` · ${currentStep.stage}` : ''}`
}

function materialApplicationStatus(itemIds: string[]) {
  const ids = new Set(itemIds)
  const applications = props.materialApplications.filter(item => ids.has(item.bomItemId))
  if (!applications.length) return '暂无'
  const pending = applications.filter(item => item.status === 'Pending').length
  const approved = applications.filter(item => item.status === 'Approved').length
  const rejected = applications.filter(item => item.status === 'Rejected').length
  return [`待审${pending}`, `已批${approved}`, `驳回${rejected}`].filter((_, index) => [pending, approved, rejected][index] > 0).join(' · ')
}

const documentRows = computed(() => [
  { key: 'model', label: '3D图档', count: props.modelCount, approval: drawingTargetSummary('Model3D') },
  { key: 'drawing', label: '2D图纸', count: props.drawingCount, approval: drawingTargetSummary('Drawing2D') },
])

const bomRows = computed(() => [
  { key: 'standard', label: '标准件', count: props.standardCount, approval: bomApprovalStatus(['StandardLongLead', 'StandardFormal', 'StandardSupplement']), application: materialApplicationStatus(props.standardItemIds) },
  { key: 'non-standard', label: '非标件', count: props.nonStandardCount, approval: bomApprovalStatus(['NonStandardWithDrawing']), application: materialApplicationStatus(props.nonStandardItemIds) },
  { key: 'electrical', label: '电气', count: props.electricalCount, approval: bomApprovalStatus(['ElectricalFormal', 'ElectricalSupplement']), application: materialApplicationStatus(props.electricalItemIds) },
])

const bomApprovalSummary = computed(() => {
  if (!props.releasePackage) return 'BOM审批：未发起'
  const currentStep = props.releasePackage.steps.find(step => step.status === 'current')
  return `BOM审批：${props.releasePackage.state}${currentStep ? ` · ${currentStep.stage}` : ''}`
})

const materialApplicationSummary = computed(() => {
  if (!props.materialApplications.length) return '物料申请：暂无'
  const pending = props.materialApplications.filter(item => item.status === 'Pending').length
  const approved = props.materialApplications.filter(item => item.status === 'Approved').length
  const rejected = props.materialApplications.filter(item => item.status === 'Rejected').length
  return `物料申请：待审 ${pending} · 已批 ${approved} · 驳回 ${rejected}`
})
</script>

<template>
  <section class="pdm-workbench" aria-label="工作台主页面">
    <div class="pdm-workbench-grid">
      <button type="button" class="pdm-panel pdm-stat-card pdm-stat-card-action" aria-label="进入项目图档" @click="emit('documents')">
        <span class="is-blue"><FolderTree :size="19" /></span>
        <div><small>项目图档</small><strong>{{ documentCount }}</strong><em>{{ warningCount ? `${warningCount} 个异常引用` : '引用结构正常' }}</em><em class="pdm-stat-card__flow">{{ drawingReviewSummary }}</em><div class="pdm-stat-card__table is-document"><div class="is-heading"><span>类型</span><span>数量</span><span>审批情况</span></div><div v-for="row in documentRows" :key="row.key" :aria-label="`${row.label}统计`"><span>{{ row.label }}</span><b>{{ row.count }}</b><em>{{ row.approval }}</em></div></div></div>
      </button>
      <button type="button" class="pdm-panel pdm-stat-card pdm-stat-card-action" aria-label="进入BOM数据" @click="emit('bom')">
        <span class="is-green"><Boxes :size="19" /></span>
        <div><small>BOM数据</small><strong>{{ standardCount + nonStandardCount + electricalCount }}</strong><em class="pdm-stat-card__flow">{{ bomApprovalSummary }}</em><em class="pdm-stat-card__flow">{{ materialApplicationSummary }}</em><div class="pdm-stat-card__table is-bom"><div class="is-heading"><span>类别</span><span>零件</span><span>审批情况</span><span>物料申请</span></div><div v-for="row in bomRows" :key="row.key" :aria-label="`${row.label}BOM统计`"><span>{{ row.label }}</span><b>{{ row.count }}</b><em>{{ row.approval }}</em><em>{{ row.application }}</em></div></div></div>
      </button>
      <article class="pdm-panel pdm-stat-card">
        <span class="is-orange"><PackageCheck :size="19" /></span>
        <div><small>当前发布包</small><strong class="is-code">{{ releasePackage?.number || '暂无' }}</strong><em>{{ releasePackage?.state || '尚未创建发布包' }}</em></div>
      </article>

      <div class="pdm-workbench-primary">
        <article class="pdm-panel pdm-workbench-detail">
          <header class="pdm-panel-heading"><h2>当前工作图档</h2><button type="button" class="pdm-text-action" @click="emit('documents')">查看结构</button></header>
          <div v-if="hasDocuments" class="pdm-current-document">
            <span><FileCheck2 :size="22" /></span>
            <div><strong>{{ selected.drawingNumber }} · {{ selected.name }}</strong><small>{{ selected.fileName }}</small></div>
            <dl>
              <div><dt>工作版本</dt><dd>{{ selected.version }}</dd></div>
              <div><dt>状态</dt><dd>{{ !selected.checkedOutBy ? '正常' : selected.checkedOutBy.toLocaleLowerCase('zh-CN') === (currentUsername || '').toLocaleLowerCase('zh-CN') ? '可编辑' : `${selected.checkedOutBy}编辑中` }}</dd></div>
              <div><dt>配置</dt><dd>{{ selected.configuration }}</dd></div>
            </dl>
          </div>
          <div v-else class="pdm-project-link-guide">
            <FolderTree :size="34" />
            <div><strong>项目尚未关联图纸</strong><p>请在SolidWorks端刷新项目列表，选择“{{ project.code }} · {{ project.name }}”，再提交图纸存档。</p></div>
          </div>
        </article>

        <article class="pdm-panel pdm-workbench-detail pdm-project-people" aria-label="人员组织结构">
          <header class="pdm-panel-heading"><h2>人员组织结构</h2><small>按项目阶段展示当前负责人</small></header>
          <div class="pdm-project-people__layout">
            <section class="pdm-project-people__section" aria-label="项目阶段负责人">
              <header><span><UsersRound :size="17" /></span><div><strong>项目阶段负责人</strong><small>{{ rootProject.executionUnitName || '执行事业部待分配' }}</small></div></header>
              <div class="pdm-project-staffing-list">
                <div v-for="row in staffingRows" :key="row.key" class="pdm-project-staffing-row">
                  <span><small>{{ row.stage }}</small><strong>{{ row.role }}</strong></span>
                  <div v-if="row.people.length" class="pdm-project-person-list">
                    <span v-for="person in row.people" :key="person.username" :title="person.username">{{ person.name }}</span>
                  </div>
                  <em v-else>待配置</em>
                </div>
              </div>
            </section>

            <section class="pdm-project-people__section is-customer" aria-label="客户联络人">
              <header><span><ContactRound :size="17" /></span><div><strong>客户联络人</strong><small>项目外部沟通窗口</small></div></header>
              <dl>
                <div><dt>客户单位</dt><dd :title="rootProject.customerName">{{ rootProject.customerName || '待关联客户' }}</dd></div>
                <div><dt>联络人</dt><dd class="is-pending">待维护</dd></div>
              </dl>
            </section>
          </div>
        </article>
      </div>

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
