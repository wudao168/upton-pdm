<script setup lang="ts">
import SquareLoader from './SquareLoader.vue'
import QuantityInputNumber from './QuantityInputNumber.vue'
import { Box, FileSearch, FileText, Folder, RotateCw } from '@lucide/vue'
import { computed, defineAsyncComponent, nextTick, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { ElMessageBox } from 'element-plus'
import { ElMessage } from '../statusMessage'
import { userErrorMessage } from '../userErrorMessage'
import { rememberedTab } from '../pagePreference'
import { changeStandardStructureState, classifyStandardStructures, deleteStructureCategory, listStandardStructureReferences, moveStructureCategory, readStandardStructures, readStructurePreview, referenceStandardStructure, saveStructureCategory } from '../api'
import { createClientUuid } from '../clientId'
import { structureCategoryActive, structureCategoryScope, structureCategoryTree, structureVersion, structureTypeName, unclassifiedStructureCategoryId } from '../standardStructure'
import type { StandardStructure, StructureCatalog, StructureCategory, StructureNode, StructureReference, StructureRevision, StructureView } from '../standardStructure'
import type { ProjectSummary } from '../types'
import BrowserCadArchiveButton from './BrowserCadArchiveButton.vue'

const StepPreviewViewer = defineAsyncComponent(() => import('./StepPreviewViewer.vue'))
const PdfDrawingViewer = defineAsyncComponent(() => import('./PdfDrawingViewer.vue'))
const props = withDefaults(defineProps<{ token: string; projects: ProjectSummary[]; canReference: boolean; active?: boolean }>(), { active: true })
const view = ref<StructureView>()
const loading = ref(false)
const loadError = ref('')
const activeTab = rememberedTab('standard-structure.tab', 'drawings', ['drawings','bom'])
const categoryId = ref('')
const statusFilter = ref<'active' | 'inactive' | 'deleted'>('active')
const search = ref('')
const fileFilter = ref('all')
const selectedStructureId = ref('')
const selectedNodeId = ref('')
const revisionId = ref('')
const bomKind = ref<'Standard' | 'NonStandard' | 'Unclassified'>('Standard')
const catalog = computed(() => view.value?.catalog)
const categoryTree = computed(() => structureCategoryTree(catalog.value?.categories ?? []))
const categoryActive = (id: string) => structureCategoryActive(catalog.value?.categories ?? [], id)
const structureActive = (item: StandardStructure) => item.isActive !== false && categoryActive(item.categoryId)
const visibleStructures = computed(() => {
  const scope = structureCategoryScope(catalog.value?.categories ?? [], categoryId.value)
  return (catalog.value?.structures ?? []).filter(s => (!categoryId.value || scope.has(s.categoryId))
    && (statusFilter.value === 'deleted' ? !!s.deletedAt : !s.deletedAt && (statusFilter.value === 'active' ? structureActive(s) : !structureActive(s))))
    .sort((a, b) => a.code.localeCompare(b.code, 'zh-CN'))
})
const selectedStructure = computed(() => visibleStructures.value.find(s => s.id === selectedStructureId.value))
const revisions = computed(() => [...(selectedStructure.value?.revisions ?? [])].sort((a, b) => b.version - a.version))
const revision = computed(() => revisions.value.find(r => r.id === revisionId.value) ?? revisions.value[0])
type DrawingNode = StructureNode & { structureId: string; revisionId: string; version: number; displayNumber: string; children: DrawingNode[] }
const drawingTree = computed<DrawingNode[]>(() => {
  function map(node: StructureNode, structure: StandardStructure, rev: StructureRevision): DrawingNode {
    return { ...node, structureId: structure.id, revisionId: rev.id, version: rev.version,
      displayNumber: node.id === rev.root.id ? `${structure.code}-${structureVersion(rev.version)}` : node.drawingNumber,
      children: node.children.map(c => map(c, structure, rev)) }
  }
  return visibleStructures.value.flatMap(s => {
    const rev = s.id === selectedStructureId.value ? revision.value : [...s.revisions].sort((a, b) => b.version - a.version)[0]
    return rev ? [map(rev.root, s, rev)] : []
  })
})
function walk(nodes: DrawingNode[]): DrawingNode[] { return nodes.flatMap(n => [n, ...walk(n.children)]) }
const allDrawingNodes = computed(() => walk(drawingTree.value))
const selectedNode = computed(() => allDrawingNodes.value.find(n => n.structureId === selectedStructureId.value && n.id === selectedNodeId.value))
const filterNodes = (nodes: DrawingNode[]): DrawingNode[] => nodes.flatMap(node => {
  const children = filterNodes(node.children)
  const keyword = search.value.trim().toLocaleLowerCase()
  const matches = (!keyword || `${node.displayNumber} ${node.drawingNumber} ${node.name} ${node.fileName}`.toLocaleLowerCase().includes(keyword))
    && (fileFilter.value === 'all' || (fileFilter.value === '2d') === ['Drawing', 'Pdf'].includes(node.kind))
  return matches || children.length ? [{ ...node, children }] : []
})
const filteredDrawingTree = computed(() => filterNodes(drawingTree.value))
const drawingCount = (kind: string) => allDrawingNodes.value.filter(n => kind === 'all' || (kind === '2d') === ['Drawing', 'Pdf'].includes(n.kind)).length
const breadcrumb = computed(() => {
  const result: string[] = []
  let id = selectedStructure.value?.categoryId || categoryId.value
  while (id) { const c = catalog.value?.categories.find(c => c.id === id); if (!c) break; result.unshift(c.name); id = c.parentId ?? '' }
  return ['标准结构', ...result]
})
const bomRows = computed(() => revision.value?.bom.filter(b => b.kind === bomKind.value) ?? [])
const moveOpen = ref(false)
const moveSaving = ref(false)
const moveIds = ref<string[]>([])
const moveTarget = ref('')
function openMove(target = '') {
  if (!selectedStructure.value) return
  moveIds.value = [selectedStructure.value.id]
  moveTarget.value = target
  moveOpen.value = true
}
async function moveStructures() {
  if (!catalog.value || !moveIds.value.length || !moveTarget.value) return ElMessage.warning('请选择根图档和目标分类。')
  moveSaving.value = true
  try {
    const ids = [...moveIds.value]
    const target = moveTarget.value
    setCatalog(await classifyStandardStructures(ids, target, catalog.value.rowVersion, props.token))
    categoryId.value = target
    selectStructure(ids[0]!)
    moveOpen.value = false
    ElMessage.success('完整图档及BOM已调整分类，编号、版本和存档文件保持原有内容。')
  } catch (error) { ElMessage.error(error instanceof Error ? error.message : '调整分类失败，请刷新后重试。') }
  finally { moveSaving.value = false }
}

function selectStructure(id: string) {
  selectedStructureId.value = id
  const structure = visibleStructures.value.find(s => s.id === id)
  const latest = [...(structure?.revisions ?? [])].sort((a, b) => b.version - a.version)[0]
  revisionId.value = latest?.id ?? ''
  selectedNodeId.value = latest?.root.id ?? ''
}
function selectNode(node: DrawingNode) {
  if (selectedStructureId.value !== node.structureId) { selectedStructureId.value = node.structureId; revisionId.value = node.revisionId }
  selectedNodeId.value = node.id
}
watch(visibleStructures, rows => { if (!rows.some(s => s.id === selectedStructureId.value)) selectStructure(rows[0]?.id ?? '') })
watch(revisionId, () => { selectedNodeId.value = revision.value?.root.id ?? '' })

async function load() {
  loading.value = true
  loadError.value = ''
  try { view.value = await readStandardStructures(props.token) }
  catch (error) { loadError.value = userErrorMessage(error instanceof Error ? error.message : '标准结构加载失败，请重试。') }
  finally { loading.value = false }
}
onMounted(load)

const previewState = ref<'idle' | 'loading' | 'ready' | 'error'>('idle')
const previewError = ref('')
const previewBuffer = ref<ArrayBuffer>()
const previewUrl = ref('')
const previewKind = ref('')
const stepViewer = ref<{ setView: (view: 'fit' | 'front' | 'top' | 'isometric') => void }>()
let previewRequest = 0
function clearPreview() {
  previewRequest++
  if (previewUrl.value) URL.revokeObjectURL(previewUrl.value)
  previewUrl.value = ''
  previewBuffer.value = undefined
  previewError.value = ''
  previewState.value = 'idle'
}
watch(() => `${revision.value?.id}/${selectedNode.value?.id}`, clearPreview)
onBeforeUnmount(clearPreview)
async function loadPreview() {
  const node = selectedNode.value
  const rev = revision.value
  if (!node?.previewFileKey || !rev) return
  clearPreview()
  const request = previewRequest
  previewState.value = 'loading'
  try {
    const blob = await readStructurePreview(rev.id, node.previewFileKey, props.token)
    if (request !== previewRequest) return
    const file = rev.files.find(f => f.key === node.previewFileKey)
    previewKind.value = file?.fileName.toLowerCase().endsWith('.pdf') ? 'pdf' : 'step'
    if (previewKind.value === 'pdf') { previewUrl.value = URL.createObjectURL(blob); previewState.value = 'ready' }
    else { const buffer = await blob.arrayBuffer(); if (request === previewRequest) previewBuffer.value = buffer }
  } catch (error) {
    if (request !== previewRequest) return
    previewState.value = 'error'
    previewError.value = userErrorMessage(error instanceof Error ? error.message : '预览加载失败，请重试。')
  }
}
function previewFailed(message: string) { previewState.value = 'error'; previewError.value = userErrorMessage(message) }
function fullscreen() { document.querySelector('.standard-structure__preview')?.requestFullscreen?.() }

const managerOpen = ref(false)
const categorySaving = ref(false)
const editingId = ref('')
const categoryForm = ref({ name: '', parentId: null as string | null, isActive: true })
const editorOpen = ref(false)
const flatCategories = computed(() => {
  const result: Array<StructureCategory & { depth: number }> = []
  const visit = (nodes: ReturnType<typeof structureCategoryTree>, depth: number) => nodes.forEach(n => { result.push({ ...n, depth }); visit(n.children, depth + 1) })
  visit(categoryTree.value, 0)
  return result
})
function editCategory(category?: StructureCategory, parentId: string | null = null) {
  editingId.value = category?.id ?? ''
  categoryForm.value = { name: category?.name ?? '', parentId: category?.parentId ?? parentId, isActive: category?.isActive ?? true }
  editorOpen.value = true
}
function setCatalog(value: StructureCatalog) { if (view.value) view.value = { ...view.value, catalog: value } }
async function saveCategory() {
  if (!catalog.value || !categoryForm.value.name.trim()) return ElMessage.warning('请填写分类名称。')
  categorySaving.value = true
  try {
    const previous = catalog.value.categories.find(c => c.id === editingId.value)
    const siblings = catalog.value.categories.filter(c => c.parentId === categoryForm.value.parentId)
    setCatalog(await saveStructureCategory(editingId.value || createClientUuid(), { ...categoryForm.value,
      sortOrder: previous?.sortOrder ?? Math.max(0, ...siblings.map(c => c.sortOrder)) + 1, expectedRowVersion: catalog.value.rowVersion }, props.token))
    editorOpen.value = false
    ElMessage.success('标准结构分类已保存。')
  } catch (error) { ElMessage.error(error instanceof Error ? error.message : '分类保存失败，请重试。') }
  finally { categorySaving.value = false }
}
async function removeCategory(category: StructureCategory) {
  try { await ElMessageBox.confirm(`确认删除空分类“${category.name}”？`, '删除标准结构分类', { confirmButtonText: '删除', cancelButtonText: '取消', type: 'warning' }) } catch { return }
  if (!catalog.value) return
  categorySaving.value = true
  try { setCatalog(await deleteStructureCategory(category.id, catalog.value.rowVersion, props.token)); if (categoryId.value === category.id) categoryId.value = ''; ElMessage.success('空分类已删除。') }
  catch (error) { ElMessage.error(error instanceof Error ? error.message : '分类删除失败，请重试。') }
  finally { categorySaving.value = false }
}
async function moveCategory(category: StructureCategory, direction: number) {
  if (!catalog.value) return
  categorySaving.value = true
  try {
    setCatalog(await moveStructureCategory(category.id, direction, catalog.value.rowVersion, props.token))
  } catch (error) { ElMessage.error(error instanceof Error ? error.message : '分类排序失败，请重试。') }
  finally { categorySaving.value = false }
}

const referenceOpen = ref(false)
const referenceSaving = ref(false)
const targetProjectId = ref('')
const referenceQuantity = ref(1)
const referenceRequestId = ref('')
const lifecycleSaving = ref(false)
const referencesOpen = ref(false)
const referencesLoading = ref(false)
const referenceItems = ref<StructureReference[]>([])
const referenceStructureCode = ref('')
async function showReferences() {
  const item = selectedStructure.value
  if (!item) return
  referenceItems.value = []
  referenceStructureCode.value = item.code
  referencesOpen.value = true
  referencesLoading.value = true
  try { referenceItems.value = await listStandardStructureReferences(item.id, props.token) }
  catch (error) { referencesOpen.value = false; ElMessage.error(userErrorMessage(error instanceof Error ? error.message : '引用记录读取失败，请重试。')) }
  finally { referencesLoading.value = false }
}
async function changeStructureState(action: 'Disable' | 'Enable' | 'Delete' | 'Restore') {
  const item = selectedStructure.value
  if (!item || !catalog.value || lifecycleSaving.value) return
  lifecycleSaving.value = true
  try {
    if (action === 'Delete') {
      const references = await listStandardStructureReferences(item.id, props.token)
      if (references.length) {
        referenceItems.value = references; referenceStructureCode.value = item.code; referencesOpen.value = true
        ElMessage.warning('该标准结构已有项目引用或待核对引用，不能删除，可改为停用。')
        return
      }
    }
    const label = { Disable: '停用', Enable: '启用', Delete: '移入回收站', Restore: '恢复' }[action]
    await ElMessageBox.confirm(`${label}整套标准结构 ${item.code}？包含 ${item.revisions.length} 个历史版本的完整图档、工程图及对应BOM。原存档文件和已有项目引用保留。${action === 'Delete' ? '当前未查到项目引用，删除后可在回收站恢复。' : ''}`, label + '标准结构', { confirmButtonText: '确认' + label, cancelButtonText: '取消', type: action === 'Delete' ? 'warning' : 'info' })
    setCatalog(await changeStandardStructureState(item.id, action, catalog.value.rowVersion, props.token))
    const current = catalog.value.structures.find(s => s.id === item.id)!
    statusFilter.value = current.deletedAt ? 'deleted' : structureActive(current) ? 'active' : 'inactive'
    await nextTick(); selectStructure(item.id)
    ElMessage.success(`标准结构 ${item.code} 已${label}。`)
  } catch (error) {
    if (error !== 'cancel' && error !== 'close') ElMessage.error(userErrorMessage(error instanceof Error ? error.message : '状态操作未完成，请刷新后重试。'))
  } finally { lifecycleSaving.value = false }
}
async function toggleSelectedCategory() {
  const category = catalog.value?.categories.find(c => c.id === categoryId.value)
  if (!category || category.id === unclassifiedStructureCategoryId || !catalog.value || categorySaving.value) return
  categorySaving.value = true
  try {
    const label = category.isActive ? '停用' : '启用'
    const scope = structureCategoryScope(catalog.value.categories, category.id)
    const count = catalog.value.structures.filter(s => scope.has(s.categoryId) && !s.deletedAt).length
    await ElMessageBox.confirm(`${label}分类“${category.name}”？其下共有 ${count} 套标准结构。停用后不能新增项目引用，已有项目BOM保留；启用分类不会改变各结构自身的停用状态。`, label + '分类', { confirmButtonText: '确认' + label, cancelButtonText: '取消' })
    setCatalog(await saveStructureCategory(category.id, { ...category, isActive: !category.isActive, expectedRowVersion: catalog.value.rowVersion }, props.token))
    statusFilter.value = label === '停用' ? 'inactive' : 'active'
    ElMessage.success(`分类已${label}。`)
  } catch (error) {
    if (error !== 'cancel' && error !== 'close') ElMessage.error(userErrorMessage(error instanceof Error ? error.message : '分类状态修改失败，请重试。'))
  } finally { categorySaving.value = false }
}
function openReference() { referenceRequestId.value = createClientUuid(); targetProjectId.value = ''; referenceQuantity.value = 1; referenceOpen.value = true }
async function referenceBom() {
  if (!selectedStructure.value || selectedStructure.value.deletedAt || !structureActive(selectedStructure.value)) return ElMessage.warning('该标准结构或所属分类已停用或删除，不能新增项目引用。')
  if (!revision.value || !targetProjectId.value || referenceQuantity.value <= 0) return ElMessage.warning('请选择目标项目并填写正数引用套数。')
  referenceSaving.value = true
  try {
    const result = await referenceStandardStructure({ revisionId: revision.value.id, projectId: targetProjectId.value,
      kind: bomKind.value === 'Unclassified' ? 'NonStandard' : bomKind.value, quantity: referenceQuantity.value, requestId: referenceRequestId.value }, props.token)
    referenceOpen.value = false
    ElMessage.success(`已引用标准结构 ${structureVersion(result.version)}，新增 ${result.addedCount} 行BOM。`)
  } catch (error) { ElMessage.error(error instanceof Error ? error.message : '引用失败，请保留当前窗口并核对项目BOM后重试。') }
  finally { referenceSaving.value = false }
}
</script>

<template>
  <section v-loading="loading" class="standard-structure pdm-project-list-layout" aria-label="标准结构">
    <aside class="standard-structure__categories pdm-panel">
      <strong class="standard-structure__category-title">分类筛选</strong>
      <button type="button" class="standard-structure__all" :class="{ 'is-active': !categoryId }" @click="categoryId = ''">全部分类</button>
      <el-tree :data="categoryTree" node-key="id" :props="{ label: 'name', children: 'children' }" highlight-current :current-node-key="categoryId" default-expand-all @node-click="categoryId = $event.id">
        <template #default="{ data }"><span class="standard-structure__category-label" :class="{ 'is-inactive': !categoryActive(data.id) }"><Folder :size="15" /><span>{{ data.name }}</span><small v-if="!categoryActive(data.id)">停用</small><el-button v-if="view?.canManage && selectedStructure" class="standard-structure__move-here" link size="small" @click.stop="openMove(data.id)">移入</el-button></span></template>
      </el-tree>
      <div v-if="view?.canManage" class="standard-structure__category-actions"><el-button size="small" type="primary" @click="managerOpen = true">分类维护</el-button></div>
    </aside>
    <main class="standard-structure__main">
      <div class="standard-structure__tabs pdm-panel">
        <nav class="standard-structure__tab-buttons pdm-tabs" aria-label="标准结构功能">
        <button type="button" :class="{ 'is-active': activeTab === 'drawings' }" @click="activeTab = 'drawings'">图档</button>
        <button type="button" :class="{ 'is-active': activeTab === 'bom' }" @click="activeTab = 'bom'">BOM</button>
        </nav>
        <el-button v-if="view?.canManage" text :disabled="!selectedStructure || !!selectedStructure.deletedAt" @click="openMove()">调整分类</el-button>
        <select v-model="statusFilter" aria-label="标准结构状态"><option value="active">正常</option><option value="inactive">已停用</option><option value="deleted">回收站</option></select>
        <template v-if="view?.canManage && selectedStructure">
          <el-button v-if="!selectedStructure.deletedAt" text :disabled="lifecycleSaving" @click="changeStructureState(selectedStructure.isActive === false ? 'Enable' : 'Disable')">{{ selectedStructure.isActive === false ? '启用' : '停用' }}</el-button>
          <el-button v-if="!selectedStructure.deletedAt" text type="danger" :disabled="lifecycleSaving" @click="changeStructureState('Delete')">删除</el-button>
          <el-button v-else text :disabled="lifecycleSaving" @click="changeStructureState('Restore')">恢复</el-button>
          <el-button text :disabled="lifecycleSaving" @click="showReferences">查看引用</el-button>
        </template>
        <el-button v-if="view?.canManage && categoryId && categoryId !== unclassifiedStructureCategoryId" text :disabled="categorySaving" @click="toggleSelectedCategory">{{ catalog?.categories.find(c => c.id === categoryId)?.isActive ? '停用分类' : '启用分类' }}</el-button>
        <span class="standard-structure__readonly">集团共享 · 只读预览</span>
        <el-button text :icon="RotateCw" aria-label="刷新标准结构" @click="load" />
      </div>
      <div class="standard-structure__breadcrumb pdm-panel"><span v-for="(part, index) in breadcrumb" :key="index">{{ index ? ' › ' : '' }}{{ part }}</span></div>
      <p v-if="selectedStructure?.deletedAt" class="standard-structure__lifecycle-note" role="status">已移入回收站，保留完整历史图档和BOM；恢复后才可继续使用。</p>
      <p v-else-if="selectedStructure && !structureActive(selectedStructure)" class="standard-structure__lifecycle-note" role="status">已停用，不能新增项目引用；历史预览及已有项目BOM保留。{{ !categoryActive(selectedStructure.categoryId) ? '所属分类已停用。' : '' }}</p>
      <div v-if="loadError" class="standard-structure__error pdm-panel" role="alert">{{ loadError }}<el-button @click="load">重新加载</el-button></div>
      <template v-else>
        <div v-if="activeTab === 'drawings'" class="standard-structure__drawings">
          <aside class="standard-structure__files pdm-panel">
            <el-input v-model="search" clearable placeholder="搜索图号或名称" aria-label="搜索标准结构图档" />
            <div class="standard-structure__file-filters"><button v-for="filter in [{ key: 'all', label: '全部' }, { key: '3d', label: '3D' }, { key: '2d', label: '2D' }]" :key="filter.key" type="button" :class="{ 'is-active': fileFilter === filter.key }" @click="fileFilter = filter.key">{{ filter.label }} {{ drawingCount(filter.key) }}</button></div>
            <div class="standard-structure__file-heading"><span>图档名称</span><span>版本</span></div>
            <el-tree v-if="filteredDrawingTree.length" class="standard-structure__drawing-tree" :data="filteredDrawingTree" node-key="id" :props="{ children: 'children' }" default-expand-all highlight-current :current-node-key="selectedNodeId" @node-click="selectNode">
              <template #default="{ data }"><span class="standard-structure__drawing-label"><component :is="['Drawing', 'Pdf'].includes(data.kind) ? FileText : Box" :size="17" /><span :title="data.name">{{ data.displayNumber || data.name || data.fileName }}<small v-if="data.embedded">嵌入</small></span><span>{{ structureVersion(data.version) }}</span></span></template>
            </el-tree>
            <el-empty v-else :image-size="56" :description="search ? '没有符合条件的图档' : '当前分类暂无图档'" />
            <div class="standard-structure__file-help" style="display:flex;align-items:center;gap:8px"><small>选择图档查看预览</small><BrowserCadArchiveButton v-if="!selectedStructure?.deletedAt" :token="token" :structure="selectedStructure" :revision="revision" @archived="load" /></div>
          </aside>
          <section class="standard-structure__preview pdm-panel" aria-label="标准结构图档预览">
            <header class="standard-structure__properties"><strong>图档属性</strong><span v-if="selectedStructure && revision">标准库编号 {{ selectedStructure.code }}-{{ structureVersion(revision.version) }}</span><span v-if="selectedStructure">类型 {{ structureTypeName(selectedStructure.drawingType) }}</span><span>图号 {{ selectedNode?.drawingNumber || '—' }}</span><span>名称 {{ selectedNode?.name || '—' }}</span><label>版本 <select v-model="revisionId" aria-label="标准结构版本" :disabled="!revisions.length"><option v-for="rev in revisions" :key="rev.id" :value="rev.id">{{ structureVersion(rev.version) }}</option><option v-if="!revisionId && revision" value="">{{ structureVersion(revision.version) }}</option></select></label></header>
            <div class="standard-structure__preview-toolbar"><span>{{ selectedStructure?.name || '图档预览' }}</span><el-button v-for="control in ([{ key: 'fit', label: '适应窗口' }, { key: 'front', label: '正视' }, { key: 'top', label: '俯视' }, { key: 'isometric', label: '等轴测' }] as const)" :key="control.key" text :disabled="previewState !== 'ready' || previewKind !== 'step'" @click="stepViewer?.setView(control.key)">{{ control.label }}</el-button><el-button v-if="selectedNode?.previewFileKey" text :disabled="previewState === 'loading'" @click="loadPreview">{{ previewState === 'idle' ? '加载预览' : '重新加载' }}</el-button><el-button text @click="fullscreen">全屏</el-button></div>
            <div class="standard-structure__viewer">
              <StepPreviewViewer ref="stepViewer" v-if="active && previewBuffer && previewKind === 'step' && previewState !== 'error'" :buffer="previewBuffer" @ready="previewState = 'ready'" @error="previewFailed" />
              <PdfDrawingViewer v-else-if="previewUrl && previewKind === 'pdf'" :url="previewUrl" />
              <div v-if="previewState !== 'ready'" class="standard-structure__preview-placeholder"><SquareLoader v-if="previewState === 'loading'" label="正在加载预览" /><FileSearch v-else :size="48" /><strong>{{ previewState === 'loading' ? '正在加载预览' : previewState === 'error' ? '预览加载失败' : selectedNode ? '预览尚未加载' : '请选择标准结构图档' }}</strong><span v-if="previewError">{{ previewError }}</span><span v-else-if="selectedNode?.embedded && !selectedNode.previewFileKey">嵌入组件随父装配完整保存，请选择父装配查看预览。</span><span v-else-if="selectedNode && !selectedNode.previewFileKey">此图档暂无预览，可通过存档入口补充或稍后重试。</span><span v-else-if="!selectedNode">获授权的标准化人员可使用下方“存档”入口保存图档。</span><span v-else>{{ selectedNode.fileName }} · {{ revision ? structureVersion(revision.version) : '—' }}</span><el-button v-if="selectedNode?.previewFileKey && previewState !== 'loading'" type="primary" @click="loadPreview">加载预览</el-button></div>
            </div>
          </section>
        </div>
        <section v-else class="standard-structure__bom pdm-panel" aria-label="标准结构BOM">
          <div class="standard-structure__bom-toolbar"><el-select :model-value="selectedStructureId" placeholder="选择标准结构" aria-label="选择标准结构" @update:model-value="selectStructure"><el-option v-for="s in visibleStructures" :key="s.id" :label="`${s.code} · ${s.name}`" :value="s.id" /></el-select><el-select v-model="revisionId" placeholder="版本" aria-label="BOM来源版本"><el-option v-for="rev in revisions" :key="rev.id" :label="structureVersion(rev.version)" :value="rev.id" /></el-select><el-button v-if="canReference" type="primary" :disabled="!bomRows.length || revision?.bom.some(b => b.kind === 'Unclassified') || !selectedStructure || !!selectedStructure.deletedAt || !structureActive(selectedStructure)" @click="openReference">引用到项目</el-button></div>
          <nav class="pdm-tabs standard-structure__bom-tabs" aria-label="标准结构BOM类型"><button type="button" :class="{ 'is-active': bomKind === 'Standard' }" @click="bomKind = 'Standard'">标准件BOM</button><button type="button" :class="{ 'is-active': bomKind === 'NonStandard' }" @click="bomKind = 'NonStandard'">非标件BOM</button><button v-if="revision?.bom.some(b => b.kind === 'Unclassified')" type="button" :class="{ 'is-active': bomKind === 'Unclassified' }" @click="bomKind = 'Unclassified'">待分类</button></nav>
          <el-table :data="bomRows" height="100%" stripe empty-text="当前分类或版本暂无此类BOM"><el-table-column type="index" label="序号" width="58" /><el-table-column prop="drawingNumber" label="料号 / 图号" min-width="140" ><template #default="{ row }"><span v-stocked-material="row.drawingNumber">{{ row.drawingNumber }}</span></template></el-table-column><el-table-column prop="name" label="名称" min-width="150" ><template #default="{ row }"><span v-stocked-material="row.drawingNumber">{{ row.name }}</span></template></el-table-column><el-table-column prop="quantity" label="每套数量" width="100" /><el-table-column prop="unit" label="单位" width="65" /><el-table-column prop="specification" label="型号" min-width="130" /><el-table-column prop="brand" label="品牌" width="90" /><el-table-column prop="material" label="材质" width="90" /><el-table-column prop="revision" label="图档版本" width="90" /><el-table-column prop="remark" label="备注" min-width="160" /></el-table>
        </section>
      </template>
    </main>
    <el-dialog v-model="referencesOpen" :title="`${referenceStructureCode} · 项目引用`" width="min(1000px, 95vw)">
      <p>停用不改变已有项目BOM；存在引用或待核对记录时不能删除整套标准结构。</p>
      <el-table v-loading="referencesLoading" :data="referenceItems" empty-text="未查到项目引用" max-height="450">
        <el-table-column prop="projectCode" label="项目号" min-width="150" />
        <el-table-column label="来源版本" width="95"><template #default="{ row }">{{ structureVersion(row.version) }}</template></el-table-column>
        <el-table-column label="BOM" width="100"><template #default="{ row }">{{ row.kind === 'Standard' ? '标准件' : '非标件' }}</template></el-table-column>
        <el-table-column prop="quantity" label="引用套数" width="90" />
        <el-table-column prop="actor" label="操作人" width="100" />
        <el-table-column label="时间" min-width="160"><template #default="{ row }">{{ new Date(row.referencedAt).toLocaleString('zh-CN', { hour12: false }) }}</template></el-table-column>
        <el-table-column label="状态" width="100"><template #default="{ row }">{{ row.pending ? '待核对' : '已引用' }}</template></el-table-column>
      </el-table>
    </el-dialog>

    <el-dialog v-model="moveOpen" title="调整标准库分类" width="560px" :close-on-click-modal="false" :show-close="!moveSaving">
      <el-form label-position="top"><el-form-item label="完整根图档" required><el-select v-model="moveIds" multiple filterable :disabled="moveSaving" placeholder="选择一份或多份根图档"><el-option v-for="s in catalog?.structures ?? []" :key="s.id" :label="`${s.code} · ${s.name}`" :value="s.id" /></el-select></el-form-item><el-form-item label="目标分类" required><el-tree-select v-model="moveTarget" :data="categoryTree" node-key="id" :props="{ label: 'name', children: 'children', disabled: (data: StructureCategory) => !categoryActive(data.id) }" check-strictly :disabled="moveSaving" placeholder="选择左侧分类中的目标位置" /></el-form-item></el-form>
      <p class="standard-structure__reference-note">内部层级、关联工程图和BOM整体跟随；分类调整不改变图档编号、版本及物理存档位置。</p>
      <template #footer><el-button :disabled="moveSaving" @click="moveOpen = false">取消</el-button><el-button type="primary" :loading="moveSaving" :disabled="!moveTarget || !moveIds.length" @click="moveStructures">确认调整</el-button></template>
    </el-dialog>
    <el-dialog v-model="managerOpen" title="标准结构分类维护" width="min(860px, calc(100vw - 32px))" destroy-on-close>
      <div class="standard-structure__category-manager"><section><header><strong>多级分类</strong><el-button type="primary" size="small" @click="editCategory()">新增顶级分类</el-button></header><div v-for="c in flatCategories" :key="c.id" class="standard-structure__category-row" v-show="c.id !== unclassifiedStructureCategoryId"><span :style="{ paddingLeft: `${c.depth * 16}px` }">{{ c.name }}<small v-if="!c.isActive">停用</small></span><div><el-button text size="small" :disabled="categorySaving" @click="moveCategory(c, -1)">上移</el-button><el-button text size="small" :disabled="categorySaving" @click="moveCategory(c, 1)">下移</el-button><el-button text size="small" @click="editCategory(undefined, c.id)">添加子级</el-button><el-button text size="small" @click="editCategory(c)">编辑</el-button><el-button text size="small" type="danger" :disabled="categorySaving" @click="removeCategory(c)">删除</el-button></div></div><el-empty v-if="!flatCategories.length" description="暂无标准结构分类" :image-size="60" /></section><section><el-form v-if="editorOpen" label-position="top"><el-form-item label="名称" required><el-input v-model="categoryForm.name" maxlength="150" /></el-form-item><el-form-item label="上级分类"><el-select v-model="categoryForm.parentId" clearable placeholder="顶级分类" @clear="categoryForm.parentId = null"><el-option v-for="c in flatCategories.filter(c => c.id !== editingId)" :key="c.id" :label="c.name" :value="c.id" /></el-select></el-form-item><el-form-item><el-switch v-model="categoryForm.isActive" active-text="启用" inactive-text="停用" /></el-form-item><el-button type="primary" :loading="categorySaving" @click="saveCategory">保存分类</el-button></el-form><el-empty v-else description="选择分类进行编辑" :image-size="60" /></section></div>
    </el-dialog>
    <el-dialog v-model="referenceOpen" title="引用标准结构BOM到项目" width="520px" :close-on-click-modal="false" :close-on-press-escape="!referenceSaving" :show-close="!referenceSaving"><p>{{ selectedStructure?.code }} · {{ selectedStructure?.name }} / {{ revision ? structureVersion(revision.version) : '—' }} · {{ bomKind === 'Standard' ? '标准件BOM' : '非标件BOM' }}</p><el-form label-position="top"><el-form-item label="目标项目 / 子项目" required><el-select v-model="targetProjectId" filterable :disabled="referenceSaving" placeholder="选择有BOM维护权限的项目"><el-option v-for="p in projects" :key="p.id" :label="`${p.code} · ${p.name}`" :value="p.id" /></el-select></el-form-item><el-form-item label="引用套数" required><QuantityInputNumber v-model="referenceQuantity" :disabled="referenceSaving" :min="0.001" :max="100000" :precision="3" /></el-form-item></el-form><p class="standard-structure__reference-note">新增 {{ bomRows.length }} 行明细，每行数量乘以引用套数；保留来源版本，后续标准结构升版不会自动改变项目BOM。</p><template #footer><el-button :disabled="referenceSaving" @click="referenceOpen = false">取消</el-button><el-button type="primary" :disabled="!targetProjectId" :loading="referenceSaving" @click="referenceBom">确认引用</el-button></template></el-dialog>
  </section>
</template>

<style scoped>
.standard-structure{flex:1;min-height:0;height:100%;font-size:var(--pdm-font-size-body);overflow:hidden}.standard-structure__categories{display:flex;min-height:0;flex-direction:column;padding:10px 6px;overflow:auto}.standard-structure__category-title{padding:5px 10px 12px;border-bottom:1px solid var(--pdm-border-soft)}.standard-structure__all{border:0;border-radius:5px;background:transparent;text-align:left;padding:10px;margin:6px 0;color:var(--pdm-text);font:inherit;cursor:pointer}.standard-structure__all.is-active{background:var(--pdm-theme-accent-soft);color:var(--pdm-theme-accent)}.standard-structure__move-here{margin-left:auto}.standard-structure__category-label{width:100%;display:flex;gap:6px;align-items:center;overflow:hidden}.standard-structure__category-label svg{flex-shrink:0;color:var(--pdm-theme-accent)}.is-inactive,small{color:var(--pdm-muted)}.standard-structure__category-actions{margin-top:auto;padding:14px 8px 5px}.standard-structure__main{display:flex;min-width:0;min-height:0;flex-direction:column;gap:var(--pdm-container-gap)}.standard-structure__tabs{display:flex;flex-wrap:wrap;gap:4px;align-items:center;padding:0;flex-shrink:0;border-radius:7px;box-shadow:none}.standard-structure__tabs .standard-structure__tab-buttons.pdm-tabs{--pdm-tabs-height:var(--pdm-tab-primary);align-self:center;min-height:calc(var(--pdm-tabs-height) + 2 * var(--pdm-tabs-spacing));margin:0!important;border:0!important;background:var(--pdm-tabs-track)!important}.standard-structure__readonly{margin-left:auto;margin-right:var(--pdm-tabs-spacing);background:var(--pdm-theme-accent-soft);color:var(--pdm-theme-accent);padding:5px 10px;border-radius:5px;font-size:11px}.standard-structure__breadcrumb{padding:9px 12px;flex-shrink:0;color:var(--pdm-muted);min-height:34px;box-sizing:border-box}.standard-structure__drawings{display:grid;grid-template-columns:280px minmax(0,1fr);gap:var(--pdm-container-gap);min-height:0;flex:1}.standard-structure__files{padding:12px;display:flex;flex-direction:column;gap:8px;min-height:0}.standard-structure__file-filters{display:flex;gap:4px;border:1px solid var(--pdm-border-soft);padding:4px;border-radius:6px}.standard-structure__file-filters button{font:inherit;padding:6px;border:0;border-radius:4px;background:transparent;color:var(--pdm-muted);cursor:pointer}.standard-structure__file-filters button.is-active{background:var(--pdm-theme-accent-soft);color:var(--pdm-theme-accent)}.standard-structure__file-heading{display:flex;justify-content:space-between;padding:8px 2px;border-bottom:1px solid var(--pdm-border-soft);color:var(--pdm-muted)}.standard-structure__drawing-tree{flex:1;min-height:0;overflow:auto}.standard-structure__drawing-tree :deep(.el-tree-node__content){height:36px}.standard-structure__drawing-label{display:flex;gap:6px;align-items:center;min-width:0;flex:1;padding-right:4px}.standard-structure__drawing-label svg{flex-shrink:0;color:var(--pdm-theme-accent)}.standard-structure__drawing-label>span:first-of-type{overflow:hidden;text-overflow:ellipsis;white-space:nowrap;flex:1}.standard-structure__drawing-label>span:last-child{color:var(--pdm-muted);font-size:11px}.standard-structure__drawing-label small{margin-left:5px;font-size:10px}.standard-structure__file-help{padding-top:10px;border-top:1px solid var(--pdm-border-soft);margin-top:auto}.standard-structure__preview{display:flex;min-height:0;min-width:0;flex-direction:column;overflow:hidden}.standard-structure__properties{display:flex;flex-wrap:wrap;align-items:center;gap:10px 16px;padding:13px;border-bottom:1px solid var(--pdm-border-soft);min-height:45px}.standard-structure__properties select{font:inherit;color:inherit;border:1px solid var(--pdm-border-soft);border-radius:4px;padding:3px;background:var(--pdm-surface)}.standard-structure__preview-toolbar{display:flex;flex-wrap:wrap;row-gap:4px;align-items:center;padding:4px 12px;border-bottom:1px solid var(--pdm-border-soft)}.standard-structure__preview-toolbar>span{flex:1 0 140px;max-width:100%;white-space:nowrap;overflow:hidden;text-overflow:ellipsis;color:var(--pdm-muted)}.standard-structure__preview-toolbar :deep(.el-button){flex:0 0 auto;margin-left:0;padding-inline:5px}.standard-structure .standard-structure__tabs{align-self:stretch;width:auto;flex-shrink:0;margin-bottom:0!important}.standard-structure__viewer{position:relative;flex:1;min-height:0;background:var(--pdm-surface-muted,#edf2f8);overflow:hidden}.standard-structure__viewer :deep(.pdm-step-preview-canvas){width:100%;height:100%;display:block}.standard-structure__viewer :deep(.pdf-drawing-viewer){height:100%;min-height:0;display:flex;flex-direction:column}.standard-structure__viewer :deep(.pdf-drawing-viewer__pages){height:auto;flex:1;min-height:0}.standard-structure__preview-placeholder{position:absolute;inset:0;display:flex;flex-direction:column;align-items:center;justify-content:center;gap:14px;padding:24px;text-align:center;color:var(--pdm-muted);background:var(--pdm-surface-muted,#edf2f8)}.standard-structure__preview-placeholder svg{color:var(--pdm-theme-accent)}.standard-structure__preview-placeholder strong{font-size:16px;font-weight:500;color:var(--pdm-text)}.standard-structure__bom{display:flex;flex-direction:column;min-height:0;flex:1;padding:12px;gap:10px;overflow:hidden}.standard-structure__bom-toolbar{display:flex;gap:10px}.standard-structure__bom-toolbar .el-select:first-child{width:320px}.standard-structure__bom-toolbar .el-select:nth-child(2){width:100px}.standard-structure__bom :deep(.el-table){min-height:0;flex:1}.standard-structure__category-manager{display:grid;grid-template-columns:minmax(0,1.6fr) minmax(220px,1fr);gap:14px}.standard-structure__category-manager>section{padding:12px;border:1px solid var(--pdm-border-soft);border-radius:8px;min-width:0;max-height:500px;overflow:auto}.standard-structure__category-manager header{display:flex;justify-content:space-between;align-items:center;margin-bottom:10px}.standard-structure__category-row{display:flex;align-items:center;justify-content:space-between;gap:6px;border-bottom:1px solid var(--pdm-border-soft);min-height:40px}.standard-structure__category-row>span{overflow:hidden;text-overflow:ellipsis;white-space:nowrap;min-width:50px}.standard-structure__category-row>div{display:flex;flex-shrink:0}.standard-structure__category-row .el-button{margin:0;padding:4px;font-size:11px}.standard-structure__reference-note{color:var(--pdm-muted);line-height:1.6}.standard-structure__error{padding:20px;display:flex;align-items:center;gap:12px}@media(max-width:1100px){.standard-structure__drawings{grid-template-columns:220px minmax(0,1fr)}}@media(max-width:700px){.standard-structure{overflow:auto}.standard-structure__categories{max-height:190px}.standard-structure__drawings{grid-template-columns:minmax(0,1fr);grid-template-rows:220px minmax(350px,1fr)}.standard-structure__main{min-height:650px}.standard-structure__category-manager{grid-template-columns:minmax(0,1fr)}.standard-structure__bom-toolbar{flex-wrap:wrap}}
.standard-structure__tabs>select{padding:5px 8px;border:1px solid var(--pdm-border);border-radius:5px;color:var(--pdm-text);background:var(--pdm-surface);font:inherit}.standard-structure__lifecycle-note{margin:0;padding:8px 12px;color:var(--pdm-muted);background:var(--pdm-surface-muted);border-radius:5px}.standard-structure:has(.standard-structure__lifecycle-note) .standard-structure__drawing-label{color:var(--pdm-muted)}</style>
