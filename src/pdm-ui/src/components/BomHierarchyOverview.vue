<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import { executeProjectBomU9Sync, generateProjectBomHeaderHierarchy, listBom, listBomVersions, listProjectBomHeaders, previewProjectBomU9Sync } from '../api'
import type { BomHeaderKind, BomItem, BomKind, BomVersion, ProjectBomHeader, ProjectSummary } from '../types'
import { useUserDisplayName } from '../userDisplay'

const displayUserName = useUserDisplayName()

type VisibleBomKind = Exclude<BomKind, 'Unclassified'>
type ProjectDetail = {
  current: Record<VisibleBomKind, BomItem[]>
  versions: BomVersion[]
  headers: ProjectBomHeader[]
}

const props = defineProps<{
  project: ProjectSummary
  projects: ProjectSummary[]
  token: string
  editable?: boolean
}>()

const categoryOrder: VisibleBomKind[] = ['Standard', 'NonStandard', 'Electrical']
const categoryLabels: Record<VisibleBomKind, string> = {
  Standard: '标准件BOM',
  NonStandard: '非标件BOM',
  Electrical: '电气BOM',
}
const detailCache = ref<Record<string, ProjectDetail>>({})
const loading = ref(false)
const error = ref('')
const hierarchyGenerating = ref(false)
const syncingRowKey = ref('')
type U9BomViewState = 'checking' | 'synced' | 'empty' | 'approval-pending' | 'waiting-components' | 'create-pending' | 'modify-pending' | 'failed'
const u9BomStates = ref<Record<string, U9BomViewState>>({})

const rootProjectId = computed(() => props.project.rootProjectId || props.project.id)
const hierarchyProjects = computed(() => props.projects
  .filter(project => (project.rootProjectId || project.id) === rootProjectId.value)
  .sort((left, right) => (left.childSequence ?? 0) - (right.childSequence ?? 0) || left.code.localeCompare(right.code)))
const hierarchySignature = computed(() => JSON.stringify(hierarchyProjects.value.map(project => ({
  id: project.id,
  code: project.code,
  name: project.name,
  parentProjectId: project.parentProjectId,
  rootProjectId: project.rootProjectId,
  childSequence: project.childSequence,
  bomItemCategoryCode: project.bomItemCategoryCode,
}))))
const projectById = computed(() => new Map(hierarchyProjects.value.map(project => [project.id, project])))
const childrenByParent = computed(() => {
  const result = new Map<string, ProjectSummary[]>()
  for (const project of hierarchyProjects.value) {
    if (!project.parentProjectId || !projectById.value.has(project.parentProjectId)) continue
    const children = result.get(project.parentProjectId) ?? []
    children.push(project)
    result.set(project.parentProjectId, children)
  }
  return result
})
const rootProjects = computed(() => hierarchyProjects.value.filter(project => !project.parentProjectId || !projectById.value.has(project.parentProjectId)))
const orderedProjects = computed(() => {
  const result: Array<{ project: ProjectSummary; depth: number }> = []
  const visit = (project: ProjectSummary, depth: number) => {
    result.push({ project, depth })
    for (const child of childrenByParent.value.get(project.id) ?? []) visit(child, depth + 1)
  }
  for (const root of rootProjects.value) visit(root, 0)
  return result
})
const rootHasChildren = computed(() => hierarchyProjects.value.some(project => project.parentProjectId === rootProjectId.value))

function includedRows(items: BomItem[]) {
  return items.filter(item => !item.manuallyExcluded && !item.pendingClassification)
}

function effectiveRows(items: BomItem[]) {
  return items.filter(item => !item.manuallyExcluded && !item.pendingRemoval)
}

function latestReleased(detail: ProjectDetail | undefined, kind: VisibleBomKind) {
  return detail?.versions
    .filter(version => version.kind === kind && version.state === 'Released')
    .sort((left, right) => right.versionNumber - left.versionNumber)[0]
}

function unresolvedItems(detail: ProjectDetail | undefined, kind: VisibleBomKind) {
  return (detail?.current[kind] ?? []).filter(item => !item.manuallyExcluded
    && (item.pendingRemoval || item.pendingClassification || item.manualUnmatched || !item.complete))
}

function projectTypeLabel(project: ProjectSummary) {
  return project.bomItemCategoryCode === '0301' ? '产线 0301' : project.bomItemCategoryCode === '0302' ? '设备 0302' : '类型待维护'
}

function headerCategoryLabel(project: ProjectSummary, kind: BomHeaderKind) {
  return kind === 'Master' ? projectTypeLabel(project) : '0201'
}

const overviewRows = computed(() => orderedProjects.value.flatMap(({ project, depth }) => {
  const detail = detailCache.value[project.id]
  const masterHeader = detail?.headers.find(header => header.kind === 'Master')
  const categoryRows = categoryOrder.map(kind => {
    const released = latestReleased(detail, kind)
    const currentItems = includedRows(detail?.current[kind] ?? [])
    const unresolvedCount = unresolvedItems(detail, kind).length
    const header = detail?.headers.find(item => item.kind === kind)
    return {
      key: `${project.id}-${kind}`,
      project,
      depth: depth + 1,
      kind,
      headerKind: kind as BomHeaderKind,
      label: categoryLabels[kind],
      isMaster: false,
      itemCount: currentItems.length,
      unresolvedCount,
      version: released?.label || '工作区',
      releaseStatus: released ? '已发布' : currentItems.length ? '未发布' : '空BOM',
      releasedAt: released?.releasedAt,
      header,
      parentHeader: masterHeader,
    }
  })
  const releasedDates = categoryRows.map(row => row.releasedAt).filter((value): value is string => !!value)
  const activeCategoryRows = categoryRows.filter(row => effectiveRows(detail?.current[row.kind] ?? []).length > 0)
  const directChildren = props.projects.filter(item => item.parentProjectId === project.id)
  const masterItemCount = activeCategoryRows.length + directChildren.length
  const masterUnresolvedCount = activeCategoryRows.filter(row => !row.header?.materialCode).length
    + directChildren.filter(child => !detailCache.value[child.id]?.headers.find(header => header.kind === 'Master')?.materialCode).length
  const allCategoriesReleased = categoryRows.every(row => row.releaseStatus === '已发布')
  const hasBomData = categoryRows.some(row => row.releaseStatus !== '空BOM')
  return [{
    key: `${project.id}-Master`,
    project,
    depth,
    kind: null,
    headerKind: 'Master' as BomHeaderKind,
    label: '项目主BOM',
    isMaster: true,
    itemCount: masterItemCount,
    unresolvedCount: masterUnresolvedCount,
    version: '三类汇总',
    releaseStatus: allCategoriesReleased ? '已发布' : hasBomData ? '部分/未发布' : '空BOM',
    releasedAt: releasedDates.sort((left, right) => new Date(right).getTime() - new Date(left).getTime())[0],
    header: masterHeader,
    parentHeader: undefined,
  }, ...categoryRows]
}))
function isHeaderEligible(row: (typeof overviewRows.value)[number]) {
  return !rootHasChildren.value || row.project.id !== rootProjectId.value || row.headerKind === 'Master'
}
const eligibleHeaderRows = computed(() => overviewRows.value.filter(isHeaderEligible))
const missingHeaderCount = computed(() => eligibleHeaderRows.value.filter(row => !row.header?.materialId || row.header.applicationStatus === 'Rejected').length)
const pendingHeaderCount = computed(() => eligibleHeaderRows.value.filter(row => row.header?.materialId && !row.header.materialCode).length)

function u9BomStateText(row: (typeof overviewRows.value)[number]) {
  if (!isHeaderEligible(row)) return '不纳入'
  if (!row.header?.materialCode) return '待料品回写'
  const state = u9BomStates.value[row.key]
  if (state === 'checking') return '检查中…'
  if (state === 'synced') return '已同步'
  if (state === 'empty') return '空BOM'
  if (state === 'approval-pending') return '待BOM审核'
  if (state === 'waiting-components') return '待首个正式子件'
  if (state === 'create-pending') return '待自动创建'
  if (state === 'modify-pending') return '待同步子件'
  if (state === 'failed') return '检查失败'
  return props.editable ? (row.releaseStatus === '已发布' ? '检查同步' : '检查BOM状态') : '未检查'
}

function viewStateFromPreview(preview: Awaited<ReturnType<typeof previewProjectBomU9Sync>>): U9BomViewState {
  if (preview.state === 'Empty') return 'empty'
  if (preview.state === 'AwaitingApproval') return 'approval-pending'
  if (preview.state === 'UpToDate') return 'synced'
  if (preview.state === 'CreateRequired') return preview.componentCount === 0 ? 'waiting-components' : 'create-pending'
  return 'modify-pending'
}

async function refreshU9BomStates() {
  const rows = eligibleHeaderRows.value.filter(row => row.header?.materialCode)
  if (!rows.length) return
  const updates = await Promise.all(rows.map(async row => {
    try {
      const preview = await previewProjectBomU9Sync(row.project.id, row.headerKind, props.token)
      return [row.key, viewStateFromPreview(preview)] as const
    } catch {
      return [row.key, 'failed'] as const
    }
  }))
  u9BomStates.value = { ...u9BomStates.value, ...Object.fromEntries(updates) }
}

async function syncU9Bom(row: (typeof overviewRows.value)[number]) {
  if (!props.editable || !isHeaderEligible(row) || !row.header?.materialCode || syncingRowKey.value) return
  syncingRowKey.value = row.key
  try {
    const preview = await previewProjectBomU9Sync(row.project.id, row.headerKind, props.token)
    if (preview.state === 'Empty') {
      u9BomStates.value = { ...u9BomStates.value, [row.key]: 'empty' }
      ElMessage.info('当前BOM没有正式子件，无需在U9C创建空BOM')
      return
    }
    if (preview.state === 'UpToDate' || !preview.writePreview) {
      if (preview.state === 'AwaitingApproval') {
        u9BomStates.value = { ...u9BomStates.value, [row.key]: 'approval-pending' }
        ElMessage.info('U9C A1 BOM档案已创建；当前BOM尚未审核发布，未写入工作区子件')
        return
      }
      u9BomStates.value = { ...u9BomStates.value, [row.key]: 'synced' }
      ElMessage.success('U9C BOM已存在，PLM当前没有需要追加的新子件')
      return
    }
    u9BomStates.value = {
      ...u9BomStates.value,
      [row.key]: preview.state === 'CreateRequired' ? 'create-pending' : 'modify-pending',
    }
    const headerOnly = preview.state === 'CreateRequired' && preview.componentCount === 0
    if (headerOnly) {
      u9BomStates.value = { ...u9BomStates.value, [row.key]: 'waiting-components' }
      ElMessage.info('BOM料号已就绪；U9C不接受零子件BOM，BOM审核产生首个正式子件后系统会自动创建A1 BOM，无需二次操作。')
      return
    }
    const operation = preview.state === 'CreateRequired' ? '创建BOM并同步子件' : '同步审核通过的子件'
    const required = preview.writePreview.requiredConfirmation
    try {
      await ElMessageBox.confirm(
        `将以母件 ${preview.itemCode}、固定版本 A1 在U9C${operation}，仅使用BOM审核发布版本的 ${preview.componentCount} 个正式子件。U9C既有子件只保留、不删除；确认后立即执行并自动回查。`,
        `确认${operation}`,
        { confirmButtonText: '确认同步', cancelButtonText: '取消', type: 'warning' },
      )
    } catch {
      return
    }
    await executeProjectBomU9Sync(
      row.project.id, row.headerKind, preview.writePreview.requestSha256,
      required, props.token,
    )
    u9BomStates.value = { ...u9BomStates.value, [row.key]: 'synced' }
    ElMessage.success(`U9C BOM${operation}成功，自动回查通过`)
  } catch (reason) {
    ElMessage.error(reason instanceof Error ? reason.message : 'U9C BOM同步失败')
  } finally {
    syncingRowKey.value = ''
  }
}

function headerCodeText(header?: ProjectBomHeader, eligible = true) {
  if (!eligible && !header?.materialId) return '不申请'
  if (!header?.materialId) return '待申请'
  if (header.materialCode) return header.materialCode
  if (header.applicationStatus === 'Rejected') return '已退回'
  if (header.applicationStatus === 'Approved') return '待同步U9C'
  return '审批中'
}

function headerCodeTitle(header?: ProjectBomHeader) {
  if (!header?.materialId) return '尚未提交BOM料号申请'
  if (header.materialCode) return `U9C正式料号：${header.materialCode}`
  const applicant = header.requestedBy ? `申请人：${displayUserName(header.requestedBy)}` : '申请人待补充'
  const requestedAt = header.requestedAt ? `，申请时间：${formatDate(header.requestedAt)}` : ''
  return `${applicant}${requestedAt}；审批并同步成功后显示U9C返回的正式料号`
}

async function fetchProjectDetail(projectId: string): Promise<ProjectDetail> {
  const [standard, nonStandard, electrical, versions, headers] = await Promise.all([
    listBom(projectId, 'Standard', props.token),
    listBom(projectId, 'NonStandard', props.token),
    listBom(projectId, 'Electrical', props.token),
    listBomVersions(projectId, props.token),
    listProjectBomHeaders(projectId, props.token),
  ])
  return { current: { Standard: standard, NonStandard: nonStandard, Electrical: electrical }, versions, headers }
}

async function generateHierarchy() {
  if (!props.editable || missingHeaderCount.value === 0) return
  try {
    await ElMessageBox.confirm(
      `将为当前层级缺失的 ${missingHeaderCount.value} 个BOM容器提交料号申请。审批并同步成功后，以U9C返回的正式料号为准。`,
      '确认申请BOM料号',
      { confirmButtonText: '确认申请', cancelButtonText: '取消', type: 'warning' },
    )
  } catch {
    return
  }
  hierarchyGenerating.value = true
  try {
    const result = await generateProjectBomHeaderHierarchy(rootProjectId.value, props.token)
    detailCache.value = {}
    await loadOverview(true)
    ElMessage.success(`已提交 ${result.generatedCount} 个BOM料号申请`)
  } catch (reason) {
    detailCache.value = {}
    await loadOverview(true)
    ElMessage.error(reason instanceof Error ? reason.message : 'BOM料号申请失败')
  } finally {
    hierarchyGenerating.value = false
  }
}

async function loadOverview(force = false) {
  if (!props.token) return
  const projectsToLoad = hierarchyProjects.value.filter(project => force || !detailCache.value[project.id])
  if (!projectsToLoad.length) return
  loading.value = true
  error.value = ''
  try {
    const loaded = await Promise.all(projectsToLoad.map(async project => [project.id, await fetchProjectDetail(project.id)] as const))
    detailCache.value = { ...detailCache.value, ...Object.fromEntries(loaded) }
    await refreshU9BomStates()
  } catch (reason) {
    error.value = reason instanceof Error ? reason.message : 'BOM层级状态加载失败'
  } finally {
    loading.value = false
  }
}

function formatDate(value?: string) {
  if (!value) return '—'
  const date = new Date(value)
  return Number.isNaN(date.getTime()) ? value : date.toLocaleString('zh-CN', { hour12: false })
}

watch([rootProjectId, hierarchySignature, () => props.token], () => {
  detailCache.value = {}
  void loadOverview(true)
}, { immediate: true })
</script>

<template>
  <section class="bom-overview" aria-label="BOM多级总览">
    <div v-if="error" class="bom-overview__error" role="alert">{{ error }}</div>
    <div v-if="editable" class="bom-overview__generation">
      <span>{{ missingHeaderCount ? `待申请 ${missingHeaderCount} 个BOM料号` : pendingHeaderCount ? `${pendingHeaderCount} 个BOM料号申请中` : '全部BOM料号已由U9C回写' }}</span>
      <el-button v-if="missingHeaderCount" type="primary" :loading="hierarchyGenerating" @click="generateHierarchy">申请BOM料号</el-button>
    </div>

    <div class="bom-overview__table-wrap">
      <table>
        <thead>
          <tr><th>层级 / BOM</th><th>料号分类</th><th>本级BOM料号</th><th>上级BOM料号</th><th>物料数</th><th>PLM版本</th><th>发布状态</th><th>待处理</th><th>最近发布</th><th>审批任务</th><th>U9C料品</th><th>U9C BOM</th></tr>
        </thead>
        <tbody>
          <tr v-for="row in overviewRows" :key="row.key" :class="{ 'is-master-row': row.isMaster }">
            <td :title="`${row.project.code} · ${row.project.name}`">
              <span class="bom-overview__project" :style="{ paddingLeft: `${row.depth * 18}px` }"><i v-if="!row.isMaster">↳</i><strong v-if="row.isMaster">{{ row.project.code }}</strong><span>{{ row.isMaster ? `${row.project.name} · 项目主BOM` : row.label }}</span></span>
            </td>
            <td>{{ headerCategoryLabel(row.project, row.headerKind) }}</td>
            <td><span :class="row.header?.materialCode ? '' : isHeaderEligible(row) ? 'is-warning' : 'is-muted'" :title="isHeaderEligible(row) ? headerCodeTitle(row.header) : '主项目已有子项目，本级三类BOM不申请料号'">{{ headerCodeText(row.header, isHeaderEligible(row)) }}</span></td>
            <td><span :class="row.parentHeader?.materialCode ? '' : row.parentHeader?.materialId ? 'is-warning' : 'is-muted'">{{ row.parentHeader ? headerCodeText(row.parentHeader) : '—' }}</span></td>
            <td>{{ row.itemCount }}</td>
            <td>{{ row.version }}</td>
            <td><span :class="row.releaseStatus === '已发布' ? 'is-success' : row.releaseStatus.includes('未发布') ? 'is-warning' : 'is-muted'">{{ row.releaseStatus }}</span></td>
            <td><span :class="row.unresolvedCount ? 'is-warning' : 'is-success'">{{ row.unresolvedCount ? `${row.unresolvedCount} 项` : '正常' }}</span></td>
            <td :title="formatDate(row.releasedAt)">{{ formatDate(row.releasedAt) }}</td>
            <td :title="isHeaderEligible(row) ? headerCodeTitle(row.header) : '主项目已有子项目，本级三类BOM不申请料号'"><span :class="row.header?.applicationStatus === 'Rejected' ? 'is-warning' : row.header?.applicationStatus === 'Approved' ? 'is-success' : row.header?.applicationId ? 'is-warning' : 'is-muted'">{{ !isHeaderEligible(row) && !row.header?.materialId ? '不申请' : row.header?.applicationStatus === 'Approved' ? '已批准' : row.header?.applicationStatus === 'Rejected' ? '已退回' : row.header?.applicationId ? `待审批 · ${displayUserName(row.header.requestedBy, '未知申请人')}` : '未提交' }}</span></td>
            <td><span :class="row.header?.materialCode ? 'is-success' : row.header?.materialId ? 'is-warning' : 'is-muted'">{{ !isHeaderEligible(row) && !row.header?.materialId ? '不申请' : row.header?.materialCode ? '已回写' : row.header?.materialId ? '申请中' : '未申请' }}</span></td>
            <td>
              <button v-if="editable && isHeaderEligible(row) && row.header?.materialCode" type="button" class="bom-overview__sync" :disabled="!!syncingRowKey" @click="syncU9Bom(row)">{{ syncingRowKey === row.key ? '检查中…' : u9BomStateText(row) }}</button>
              <span v-else :class="row.header?.materialCode ? 'is-muted' : 'is-warning'">{{ u9BomStateText(row) }}</span>
            </td>
          </tr>
          <tr v-if="!loading && overviewRows.length === 0"><td colspan="12" class="bom-overview__empty">当前范围没有可显示的BOM层级状态。</td></tr>
          <tr v-if="loading"><td colspan="12" class="bom-overview__empty">正在加载BOM层级状态…</td></tr>
        </tbody>
      </table>
    </div>
  </section>
</template>

<style scoped>
.bom-overview{display:flex;min-height:560px;min-width:0;flex-direction:column;padding:0;border:1px solid var(--pdm-border);border-radius:7px;background:#fff}.bom-overview__error{margin:10px;padding:8px 10px;border-radius:5px;background:#fef2f2;color:#b91c1c}.bom-overview__generation{display:flex;min-height:38px;align-items:center;justify-content:flex-end;gap:10px;padding:5px 8px;border-bottom:1px solid var(--pdm-border);color:#64748b}.bom-overview__table-wrap{min-height:0;flex:1;overflow:auto;border-radius:6px}.bom-overview__table-wrap table{width:100%;table-layout:fixed;border-collapse:collapse;white-space:nowrap}.bom-overview__table-wrap th,.bom-overview__table-wrap td{overflow:hidden;padding:8px;border-bottom:1px solid var(--pdm-border);text-align:left;text-overflow:ellipsis}.bom-overview__table-wrap th{position:sticky;top:0;z-index:1;background:#f3f6fa}.bom-overview__table-wrap th:nth-child(1){width:205px}.bom-overview__table-wrap th:nth-child(2){width:85px}.bom-overview__table-wrap th:nth-child(3),.bom-overview__table-wrap th:nth-child(4){width:112px}.bom-overview__table-wrap th:nth-child(5){width:58px}.bom-overview__table-wrap th:nth-child(6){width:80px}.bom-overview__table-wrap th:nth-child(7){width:82px}.bom-overview__table-wrap th:nth-child(8){width:68px}.bom-overview__table-wrap th:nth-child(9){width:118px}.bom-overview__table-wrap th:nth-child(10){width:145px}.bom-overview__table-wrap th:nth-child(11){width:72px}.bom-overview__table-wrap th:nth-child(12){width:92px}.bom-overview__table-wrap tr.is-master-row td{background:#f8fafc;font-weight:600}.bom-overview__project{display:flex;min-width:0;align-items:center;gap:6px}.bom-overview__project i{color:var(--pdm-muted);font-style:normal}.bom-overview__project strong{flex:0 0 auto}.bom-overview__project span{overflow:hidden;color:var(--pdm-muted);text-overflow:ellipsis}.bom-overview__sync{border:0;background:transparent;color:#2563eb;cursor:pointer;font:inherit}.bom-overview__sync:disabled{cursor:wait;opacity:.6}.is-warning{color:#b45309}.is-success{color:#15803d}.is-muted{color:#64748b}.bom-overview__empty{padding:24px;text-align:center;color:var(--pdm-muted)}
</style>
