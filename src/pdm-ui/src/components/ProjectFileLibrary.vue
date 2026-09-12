<script setup lang="ts">
import { computed, onBeforeUnmount, ref, watch } from 'vue'
import { ElMessage } from '../statusMessage'
import { ElMessageBox } from 'element-plus'
import { Download, Eye, File, Folder, FolderCog, FolderPlus, History, Pencil, RotateCcw, Search, ShieldCheck, Trash2, Upload, X } from '@lucide/vue'
import type { ControlledDocumentRecycleReadiness, FolderPermissionRule, ManagedDocument, PdmUser, ProjectFile, ProjectFileVersion, ProjectFolder, RolePermissionSettings } from '../types'
import { createProjectFolder, deleteProjectFile, deleteProjectFolder, downloadProjectFile, getControlledDocumentRecycleReadiness, listControlledDocumentRecycleBin, listProjectFiles, listProjectFileVersions, moveProjectFile, moveProjectFolder, recycleControlledDocument, renameProjectFile, renameProjectFolder, restoreControlledDocument, restoreProjectFile, uploadProjectFile } from '../api'
import { resolveUserDisplayName } from '../userDisplay'

const props = defineProps<{
  projectId: string; token: string; folders: ProjectFolder[]; documents: ManagedDocument[]; users: PdmUser[]; roles: RolePermissionSettings[]
  administrator: boolean; canRecycleDocuments?: boolean; pending: boolean
  onUpdatePermissions: (folderId: string, permissions: FolderPermissionRule[]) => Promise<ProjectFolder[]>
  onReload: () => Promise<unknown>
}>()
const displayUserName = (username?: string | null, emptyText = '—') => resolveUserDisplayName(props.users, username, emptyText)
interface FolderTreeNode extends ProjectFolder { children: FolderTreeNode[] }
interface MoveFolderTreeNode extends FolderTreeNode { disabled: boolean; children: MoveFolderTreeNode[] }

const selectedFolderId = ref('')
const kindFilter = ref<'all' | 'model' | 'drawing'>('all')
const query = ref('')
const includeDeleted = ref(false)
const projectFiles = ref<ProjectFile[]>([])
const recycledDocuments = ref<ManagedDocument[]>([])
const loadingFiles = ref(false)
const uploadProgress = ref(0)
const uploadingName = ref('')
const uploadInput = ref<HTMLInputElement>()
let uploadController: AbortController | null = null
const permissionOpen = ref(false)
const permissionRows = ref<FolderPermissionRule[]>([])
const versionOpen = ref(false)
const versionFile = ref<ProjectFile | null>(null)
const versions = ref<ProjectFileVersion[]>([])
const recycleOpen = ref(false)
const recycleLoading = ref(false)
const recycleReadiness = ref<ControlledDocumentRecycleReadiness | null>(null)
const recycleReason = ref('')
const recycleConfirmation = ref('')
const moveTargetOpen = ref(false)
const moveTargetTitle = ref('')
const moveTargetId = ref('')
const moveExcludedIds = ref<string[]>([])
let resolveMoveTarget: ((folderId: string) => void) | null = null
let rejectMoveTarget: ((reason: 'cancel') => void) | null = null
const accessOptions = [
  { value: 1, label: '查看' }, { value: 2, label: '下载' }, { value: 4, label: '上传' },
  { value: 8, label: '编辑' }, { value: 16, label: '删除' }, { value: 32, label: '管理权限' }, { value: 64, label: '发布' },
]

const visibleFolders = computed(() => props.folders.filter(folder => hasAccess(folder, 1)))
const treeData = computed<FolderTreeNode[]>(() => {
  const map = new Map(visibleFolders.value.map(folder => [folder.id, { ...folder, children: [] as FolderTreeNode[] }]))
  const roots: FolderTreeNode[] = []
  for (const folder of map.values()) {
    const parent = folder.parentFolderId ? map.get(folder.parentFolderId) : undefined
    if (parent) parent.children.push(folder); else roots.push(folder)
  }
  const sort = (items: FolderTreeNode[]) => items.sort((a, b) => a.sortOrder - b.sortOrder || a.name.localeCompare(b.name, 'zh-CN')).forEach(item => sort(item.children))
  sort(roots)
  return roots
})
const selectedFolder = computed(() => visibleFolders.value.find(folder => folder.id === selectedFolderId.value))
const businessFolder = computed(() => selectedFolder.value?.purpose === 'Standard')
const folderDocuments = computed(() => [...props.documents, ...(includeDeleted.value ? recycledDocuments.value : [])].filter(document => document.folderId === selectedFolderId.value))
const modelDocumentCount = computed(() => folderDocuments.value.filter(document => document.kind !== 'Drawing').length)
const drawingDocumentCount = computed(() => folderDocuments.value.filter(document => document.kind === 'Drawing').length)
const displayedDocuments = computed(() => {
  const keyword = query.value.trim().toLocaleLowerCase('zh-CN')
  return folderDocuments.value.filter(document => {
    const matchesKind = kindFilter.value === 'all' || (kindFilter.value === 'model' && document.kind !== 'Drawing') || (kindFilter.value === 'drawing' && document.kind === 'Drawing')
    return matchesKind && (!keyword || `${document.drawingNumber} ${document.name} ${document.fileName}`.toLocaleLowerCase('zh-CN').includes(keyword))
  })
})
const displayedFiles = computed(() => {
  const keyword = query.value.trim().toLocaleLowerCase('zh-CN')
  return projectFiles.value.filter(file => file.folderId === selectedFolderId.value
    && (!keyword || `${file.fileName} ${file.currentVersion?.uploadedBy ?? ''}`.toLocaleLowerCase('zh-CN').includes(keyword)))
})
const standardTargets = computed(() => visibleFolders.value.filter(folder => folder.purpose === 'Standard' && hasAccess(folder, 8)))
const moveTargetTreeData = computed<MoveFolderTreeNode[]>(() => {
  const excluded = new Set(moveExcludedIds.value)
  const clone = (items: FolderTreeNode[]): MoveFolderTreeNode[] => items.map(item => ({
    ...item,
    disabled: item.purpose !== 'Standard' || !hasAccess(item, 8) || excluded.has(item.id),
    children: clone(item.children),
  }))
  return clone(treeData.value)
})

watch(() => props.folders, () => {
  if (!visibleFolders.value.some(folder => folder.id === selectedFolderId.value)) selectedFolderId.value = treeData.value[0]?.id ?? ''
}, { immediate: true, deep: true })
watch([selectedFolderId, includeDeleted], loadFiles, { immediate: true })
onBeforeUnmount(() => { uploadController?.abort(); rejectMoveTarget?.('cancel') })

function hasAccess(folder: ProjectFolder | undefined, mask: number) { return Boolean(folder && (folder.effectiveAccess & mask) === mask) }
function selectFolder(folder: ProjectFolder) { selectedFolderId.value = folder.id; query.value = '' }
function folderCount(folderId: string) { return props.documents.filter(document => document.folderId === folderId).length + projectFiles.value.filter(file => file.folderId === folderId && !file.deletedAt).length }
function kindLabel(kind: ManagedDocument['kind']) { return ({ Assembly: '装配体', Part: '零件', Drawing: '工程图' })[kind] }
function stateLabel(state: string | number) { return state === 'Released' || state === 2 ? '已发布' : state === 'InReview' || state === 1 ? '审批中' : state === 'Obsolete' || state === 3 ? '已作废' : '工作版' }
function formatSize(bytes = 0) { if (bytes < 1024) return `${bytes} B`; if (bytes < 1024 ** 2) return `${(bytes / 1024).toFixed(1)} KB`; if (bytes < 1024 ** 3) return `${(bytes / 1024 ** 2).toFixed(1)} MB`; return `${(bytes / 1024 ** 3).toFixed(2)} GB` }
function canPreview(file: ProjectFile) { return /\.(pdf|png|jpe?g|gif|webp|txt|csv|md)$/i.test(file.fileName) }

async function loadFiles() {
  if (!props.projectId) { projectFiles.value = []; recycledDocuments.value = []; return }
  loadingFiles.value = true
  try {
    projectFiles.value = await listProjectFiles(props.projectId, undefined, includeDeleted.value, props.token)
    recycledDocuments.value = props.canRecycleDocuments && includeDeleted.value
      ? await listControlledDocumentRecycleBin(props.projectId, props.token)
      : []
  }
  catch (error) { ElMessage.error(error instanceof Error ? error.message : '项目文件加载失败') }
  finally { loadingFiles.value = false }
}

function chooseFiles() { uploadInput.value?.click() }
async function handleFiles(event: Event) {
  const input = event.target as HTMLInputElement
  const selected = [...(input.files ?? [])]
  input.value = ''
  for (const file of selected) {
    if (projectFiles.value.some(item => !item.deletedAt && item.fileName.localeCompare(file.name, undefined, { sensitivity: 'accent' }) === 0)) {
      try { await ElMessageBox.confirm(`“${file.name}”已存在，继续上传将创建不可变的新版本。`, '确认创建新版本', { confirmButtonText: '创建新版本', cancelButtonText: '跳过' }) }
      catch { continue }
    }
    uploadController = new AbortController(); uploadingName.value = file.name; uploadProgress.value = 0
    try {
      await uploadProjectFile(props.projectId, selectedFolderId.value, file, props.token, '', value => { uploadProgress.value = value }, uploadController.signal)
      ElMessage.success(`${file.name} 已上传`); await loadFiles()
    } catch (error) { if ((error as Error).name !== 'AbortError') ElMessage.error(error instanceof Error ? error.message : '文件上传失败') }
    finally { uploadController = null; uploadingName.value = ''; uploadProgress.value = 0 }
  }
}
function cancelUpload() { uploadController?.abort(); ElMessage.info('正在取消上传') }

async function newFolder() {
  if (!selectedFolder.value) return
  try { const { value } = await ElMessageBox.prompt('请输入新文件夹名称', '新建文件夹', { inputPattern: /\S+/, inputErrorMessage: '文件夹名称不能为空' }); await createProjectFolder(props.projectId, selectedFolder.value.id, value, props.token); await props.onReload(); ElMessage.success('文件夹已创建') }
  catch (error) { if (error !== 'cancel' && error !== 'close') ElMessage.error(error instanceof Error ? error.message : '新建文件夹失败') }
}
async function renameFolderEntry() {
  const folder = selectedFolder.value; if (!folder) return
  try { const { value } = await ElMessageBox.prompt('请输入新名称', '重命名文件夹', { inputValue: folder.name }); await renameProjectFolder(props.projectId, folder.id, value, props.token); await props.onReload(); ElMessage.success('文件夹已重命名') }
  catch (error) { if (error !== 'cancel' && error !== 'close') ElMessage.error(error instanceof Error ? error.message : '重命名失败') }
}
async function selectTarget(title: string, excludedIds: string[]) {
  const excluded = new Set(excludedIds)
  const options = standardTargets.value.filter(item => !excluded.has(item.id))
  if (!options.length) throw new Error('没有可用的目标目录')
  rejectMoveTarget?.('cancel')
  moveTargetTitle.value = title
  moveTargetId.value = ''
  moveExcludedIds.value = excludedIds
  moveTargetOpen.value = true
  return await new Promise<string>((resolve, reject) => { resolveMoveTarget = resolve; rejectMoveTarget = reject })
}
function selectMoveTarget(folder: MoveFolderTreeNode) { if (!folder.disabled) moveTargetId.value = folder.id }
function confirmMoveTarget() {
  if (!moveTargetId.value || !resolveMoveTarget) return
  const resolve = resolveMoveTarget
  resolveMoveTarget = null; rejectMoveTarget = null
  moveTargetOpen.value = false
  resolve(moveTargetId.value)
}
function cancelMoveTarget() {
  const reject = rejectMoveTarget
  resolveMoveTarget = null; rejectMoveTarget = null
  reject?.('cancel')
}
function folderMoveExcludedIds(folderId: string) {
  const excluded = new Set([folderId])
  let changed = true
  while (changed) {
    changed = false
    for (const folder of props.folders) {
      if (folder.parentFolderId && excluded.has(folder.parentFolderId) && !excluded.has(folder.id)) { excluded.add(folder.id); changed = true }
    }
  }
  return [...excluded]
}
async function moveFolderEntry() {
  const folder = selectedFolder.value; if (!folder) return
  try { await moveProjectFolder(props.projectId, folder.id, await selectTarget('移动文件夹', folderMoveExcludedIds(folder.id)), props.token); await props.onReload(); ElMessage.success('文件夹已移动') }
  catch (error) { if (error !== 'cancel' && error !== 'close') ElMessage.error(error instanceof Error ? error.message : '移动失败') }
}
async function removeFolder() {
  const folder = selectedFolder.value; if (!folder) return
  try { await ElMessageBox.confirm(`仅空文件夹可以删除。确认删除“${folder.name}”？`, '删除文件夹', { type: 'warning' }); await deleteProjectFolder(props.projectId, folder.id, props.token); selectedFolderId.value = ''; await props.onReload(); ElMessage.success('空文件夹已删除') }
  catch (error) { if (error !== 'cancel' && error !== 'close') ElMessage.error(error instanceof Error ? error.message : '删除失败') }
}
async function renameFileEntry(file: ProjectFile) {
  try { const { value } = await ElMessageBox.prompt('请输入新文件名（含扩展名）', '重命名文件', { inputValue: file.fileName }); await renameProjectFile(props.projectId, file.id, value, props.token); await loadFiles(); ElMessage.success('文件已重命名') }
  catch (error) { if (error !== 'cancel' && error !== 'close') ElMessage.error(error instanceof Error ? error.message : '重命名失败') }
}
async function moveFileEntry(file: ProjectFile) {
  try { await moveProjectFile(props.projectId, file.id, await selectTarget('移动文件', [file.folderId]), props.token); await loadFiles(); ElMessage.success('文件已移动') }
  catch (error) { if (error !== 'cancel' && error !== 'close') ElMessage.error(error instanceof Error ? error.message : '移动失败') }
}
async function removeFile(file: ProjectFile) {
  try { await ElMessageBox.confirm(`确认将“${file.fileName}”移入回收站？历史版本会保留。`, '删除文件', { type: 'warning' }); await deleteProjectFile(props.projectId, file.id, props.token); await loadFiles(); ElMessage.success('文件已移入回收站') }
  catch (error) { if (error !== 'cancel' && error !== 'close') ElMessage.error(error instanceof Error ? error.message : '删除失败') }
}
async function restoreFile(file: ProjectFile) { try { await restoreProjectFile(props.projectId, file.id, props.token); await loadFiles(); ElMessage.success('文件已恢复') } catch (error) { ElMessage.error(error instanceof Error ? error.message : '恢复失败') } }
async function prepareDocumentRecycle(document: ManagedDocument) {
  recycleLoading.value = true
  try {
    recycleReadiness.value = await getControlledDocumentRecycleReadiness(props.projectId, document.id, props.token)
    recycleReason.value = ''
    recycleConfirmation.value = ''
    recycleOpen.value = true
  } catch (error) { ElMessage.error(error instanceof Error ? error.message : '删除检查失败') }
  finally { recycleLoading.value = false }
}
async function confirmDocumentRecycle() {
  const readiness = recycleReadiness.value
  if (!readiness || !readiness.canRecycle || !recycleReason.value.trim() || !recycleConfirmation.value.trim()) return
  recycleLoading.value = true
  try {
    await recycleControlledDocument(props.projectId, readiness.document.id, readiness.document.rowVersion ?? 1, recycleReason.value.trim(), recycleConfirmation.value.trim(), props.token)
    recycleOpen.value = false
    await props.onReload()
    await loadFiles()
    ElMessage.success('受控图档已移入回收站，30天内可恢复')
  } catch (error) { ElMessage.error(error instanceof Error ? error.message : '删除失败') }
  finally { recycleLoading.value = false }
}
async function restoreDocument(document: ManagedDocument) {
  try {
    await ElMessageBox.confirm(`确认恢复“${document.drawingNumber}”？`, '恢复受控图档', { type: 'warning' })
    await restoreControlledDocument(props.projectId, document.id, document.rowVersion ?? 1, props.token)
    await props.onReload()
    await loadFiles()
    ElMessage.success('受控图档已恢复')
  } catch (error) { if (error !== 'cancel' && error !== 'close') ElMessage.error(error instanceof Error ? error.message : '恢复失败') }
}
async function download(file: ProjectFile, versionId?: string, preview = false) { try { await downloadProjectFile(props.projectId, file, props.token, versionId, preview) } catch (error) { ElMessage.error(error instanceof Error ? error.message : '文件读取失败') } }
async function openVersions(file: ProjectFile) { try { versionFile.value = file; versions.value = await listProjectFileVersions(props.projectId, file.id, props.token); versionOpen.value = true } catch (error) { ElMessage.error(error instanceof Error ? error.message : '版本历史加载失败') } }

function openPermissions() { if (!selectedFolder.value) return; permissionRows.value = selectedFolder.value.permissions.map(item => ({ ...item })); permissionOpen.value = true }
function addPermission() { permissionRows.value.push({ principalType: 'Role', principalKey: props.roles[0]?.role ?? '', access: 3 }) }
function accessValues(rule: FolderPermissionRule) { return accessOptions.filter(item => (rule.access & item.value) === item.value).map(item => item.value) }
function setAccess(rule: FolderPermissionRule, values: number[]) { rule.access = values.reduce((mask, value) => mask | value, 0) }
async function savePermissions() { if (!selectedFolder.value) return; try { await props.onUpdatePermissions(selectedFolder.value.id, permissionRows.value); permissionOpen.value = false; ElMessage.success('目录权限已保存') } catch (error) { ElMessage.error(error instanceof Error ? error.message : '目录权限保存失败') } }
</script>

<template>
  <section class="pdm-file-library">
    <aside class="pdm-panel pdm-folder-pane">
      <header class="pdm-panel-heading"><div><h2>项目文件夹</h2><p>业务资料与受控图档分区管理</p></div></header>
      <el-tree :data="treeData" node-key="id" :default-expanded-keys="visibleFolders.filter(item => ['Root', 'MechanicalRoot', 'ElectricalRoot'].includes(item.purpose)).map(item => item.id)" highlight-current @node-click="selectFolder">
        <template #default="{ data }"><span class="pdm-folder-node"><span class="pdm-folder-node__icon" :class="{ 'is-populated': folderCount(data.id) > 0 }"><Folder :size="15" /></span><span>{{ data.name }}</span><small v-if="folderCount(data.id)">{{ folderCount(data.id) }}</small></span></template>
      </el-tree>
    </aside>
    <section class="pdm-panel pdm-folder-content">
      <header class="pdm-folder-toolbar">
        <div><div class="pdm-breadcrumb">项目文件 <span>/</span> {{ selectedFolder?.name || '请选择目录' }}</div><h2>{{ selectedFolder?.name || '项目文件库' }}</h2><p v-if="!businessFolder && selectedFolder">该目录由图档或发布流程受控，网页不维护普通文件。</p></div>
        <div class="pdm-file-actions" v-if="selectedFolder">
          <button v-if="businessFolder && hasAccess(selectedFolder, 4)" type="button" class="pdm-primary-action" :disabled="Boolean(uploadingName)" @click="chooseFiles"><Upload :size="15" />上传文件</button>
          <button v-if="businessFolder && hasAccess(selectedFolder, 8)" type="button" class="pdm-secondary-action" @click="newFolder"><FolderPlus :size="15" />新建文件夹</button>
          <el-dropdown v-if="businessFolder && !selectedFolder.isSystem && hasAccess(selectedFolder, 8)"><button type="button" class="pdm-secondary-action">文件夹操作</button><template #dropdown><el-dropdown-menu><el-dropdown-item @click="renameFolderEntry">重命名</el-dropdown-item><el-dropdown-item @click="moveFolderEntry">移动</el-dropdown-item><el-dropdown-item v-if="hasAccess(selectedFolder, 16)" divided @click="removeFolder">删除空文件夹</el-dropdown-item></el-dropdown-menu></template></el-dropdown>
          <button v-if="administrator" type="button" class="pdm-secondary-action" @click="openPermissions"><ShieldCheck :size="15" />目录权限</button>
          <input ref="uploadInput" type="file" multiple hidden @change="handleFiles">
        </div>
      </header>
      <div v-if="uploadingName" class="pdm-upload-strip"><span>正在上传 {{ uploadingName }}</span><el-progress :percentage="uploadProgress" /><button type="button" class="pdm-icon-action" aria-label="取消上传" @click="cancelUpload"><X :size="15" /></button></div>
      <div class="pdm-file-filters">
        <div v-if="!businessFolder" class="pdm-document-filters" role="tablist" aria-label="项目图档类型筛选"><button type="button" role="tab" :aria-selected="kindFilter === 'all'" @click="kindFilter = 'all'">全部<small>{{ folderDocuments.length }}</small></button><button type="button" role="tab" :aria-selected="kindFilter === 'model'" @click="kindFilter = 'model'">3D结构<small>{{ modelDocumentCount }}</small></button><button type="button" role="tab" :aria-selected="kindFilter === 'drawing'" @click="kindFilter = 'drawing'">2D图纸<small>{{ drawingDocumentCount }}</small></button></div>
        <el-checkbox v-if="(businessFolder && hasAccess(selectedFolder, 16)) || (!businessFolder && canRecycleDocuments)" v-model="includeDeleted">显示回收站</el-checkbox>
        <label class="pdm-inline-search"><Search :size="15" /><input v-model="query" type="search" :placeholder="businessFolder ? '搜索文件名或上传人' : '搜索图号、名称或文件名'"></label>
      </div>
      <div v-if="businessFolder && displayedFiles.length" v-loading="loadingFiles" class="pdm-file-table-wrap">
        <table class="pdm-file-detail-table" aria-label="项目资料文件"><thead><tr><th>文件名</th><th>版本</th><th>大小</th><th>上传人</th><th>更新时间</th><th>操作</th></tr></thead><tbody><tr v-for="item in displayedFiles" :key="item.id" :class="{ 'is-deleted': item.deletedAt }">
          <td><span class="pdm-file-name" :title="item.fileName"><File :size="15" />{{ item.fileName }}</span><small v-if="item.deletedAt" class="pdm-deleted-badge">回收站</small></td><td>V{{ item.currentVersion?.versionNumber ?? 0 }}</td><td>{{ formatSize(item.currentVersion?.fileLength) }}</td><td>{{ displayUserName(item.currentVersion?.uploadedBy) }}</td><td>{{ new Date(item.updatedAt).toLocaleString() }}</td>
          <td><div class="pdm-row-actions"><button v-if="!item.deletedAt && canPreview(item) && hasAccess(selectedFolder, 2)" type="button" title="预览" @click="download(item, undefined, true)"><Eye :size="14" /></button><button v-if="!item.deletedAt && hasAccess(selectedFolder, 2)" type="button" title="下载" @click="download(item)"><Download :size="14" /></button><button v-if="!item.deletedAt && hasAccess(selectedFolder, 1)" type="button" title="版本历史" @click="openVersions(item)"><History :size="14" /></button><button v-if="!item.deletedAt && hasAccess(selectedFolder, 8)" type="button" title="重命名" @click="renameFileEntry(item)"><Pencil :size="14" /></button><button v-if="!item.deletedAt && hasAccess(selectedFolder, 8)" type="button" title="移动" @click="moveFileEntry(item)"><Folder :size="14" /></button><button v-if="!item.deletedAt && hasAccess(selectedFolder, 16)" type="button" title="删除" @click="removeFile(item)"><Trash2 :size="14" /></button><button v-if="item.deletedAt && hasAccess(selectedFolder, 16)" type="button" title="恢复" @click="restoreFile(item)"><RotateCcw :size="14" /></button></div></td>
        </tr></tbody></table>
      </div>
      <div v-else-if="!businessFolder && displayedDocuments.length" class="pdm-file-table-wrap"><table class="pdm-file-detail-table" aria-label="受控图档"><colgroup><col class="pdm-file-column-number"><col class="pdm-file-column-name"><col class="pdm-file-column-kind"><col class="pdm-file-column-revision"><col class="pdm-file-column-state"><col class="pdm-file-column-editor"><col class="pdm-file-column-updated"><col v-if="canRecycleDocuments" class="pdm-file-column-actions"></colgroup><thead><tr><th>图号</th><th>名称</th><th>类型</th><th>版本</th><th>状态</th><th>编辑人</th><th>更新时间</th><th v-if="canRecycleDocuments">操作</th></tr></thead><tbody><tr v-for="document in displayedDocuments" :key="document.id" :class="{ 'is-deleted': document.deletedAt }"><td><span class="pdm-file-name"><File :size="15" />{{ document.drawingNumber }}</span><small v-if="document.deletedAt" class="pdm-deleted-badge">回收站</small></td><td>{{ document.name }}</td><td>{{ kindLabel(document.kind) }}</td><td>{{ document.revision }}</td><td>{{ document.deletedAt ? '待清理' : stateLabel(document.state) }}</td><td>{{ document.deletedAt ? displayUserName(document.deletedBy) : displayUserName(document.checkedOutBy) }}</td><td>{{ document.updatedAt ? new Date(document.updatedAt).toLocaleString() : '—' }}</td><td v-if="canRecycleDocuments"><div class="pdm-row-actions"><button v-if="!document.deletedAt" type="button" :title="stateLabel(document.state) === '已发布' ? '已发布图档仅允许作废' : '删除前检查'" :disabled="recycleLoading" @click="prepareDocumentRecycle(document)"><Trash2 :size="14" /></button><button v-else type="button" title="恢复" :disabled="recycleLoading" @click="restoreDocument(document)"><RotateCcw :size="14" /></button></div></td></tr></tbody></table></div>
      <div v-else class="pdm-folder-empty" v-loading="loadingFiles"><FolderCog :size="34" /><strong>{{ query ? '没有匹配的文件' : includeDeleted ? '此目录及回收站暂无文件' : '此目录暂无文件' }}</strong><span v-if="businessFolder && hasAccess(selectedFolder, 4)">可上传项目资料，或在此目录下新建子文件夹。</span><span v-else-if="businessFolder">当前账号可查看该目录，但没有上传权限。</span><span v-else>受控图档由SolidWorks存档，发布文件由审批发布流程生成。</span></div>
    </section>
  </section>
  <el-dialog v-model="moveTargetOpen" :title="moveTargetTitle" width="520px" class="pdm-move-folder-dialog" modal-class="pdm-move-folder-overlay" @closed="cancelMoveTarget">
    <p class="pdm-dialog-help">请选择目标文件夹。灰色目录不可作为移动目标。</p>
    <el-tree class="pdm-move-folder-tree" :data="moveTargetTreeData" node-key="id" :current-node-key="moveTargetId" default-expand-all highlight-current @current-change="selectMoveTarget">
      <template #default="{ data }"><span class="pdm-folder-node" :class="{ 'is-move-disabled': data.disabled }"><Folder :size="15" /><span>{{ data.name }}</span></span></template>
    </el-tree>
    <template #footer><button type="button" class="pdm-secondary-action" @click="moveTargetOpen=false">取消</button><button type="button" class="pdm-primary-action" :disabled="!moveTargetId" @click="confirmMoveTarget">确定</button></template>
  </el-dialog>
  <el-dialog v-model="versionOpen" :title="`版本历史 · ${versionFile?.fileName ?? ''}`" width="720px"><el-table :data="versions"><el-table-column prop="versionNumber" label="版本" width="80"><template #default="scope">V{{ scope.row.versionNumber }}</template></el-table-column><el-table-column prop="fileName" label="原始文件名" /><el-table-column label="大小" width="100"><template #default="scope">{{ formatSize(scope.row.fileLength) }}</template></el-table-column><el-table-column label="上传人" width="100"><template #default="scope">{{ displayUserName(scope.row.uploadedBy) }}</template></el-table-column><el-table-column label="上传时间" width="170"><template #default="scope">{{ new Date(scope.row.uploadedAt).toLocaleString() }}</template></el-table-column><el-table-column label="操作" width="70"><template #default="scope"><button class="pdm-icon-action" type="button" title="下载该版本" @click="versionFile && download(versionFile, scope.row.id)"><Download :size="14" /></button></template></el-table-column></el-table></el-dialog>
  <el-dialog v-model="recycleOpen" title="删除受控图档" width="620px" class="pdm-document-recycle-dialog">
    <template v-if="recycleReadiness">
      <div class="pdm-recycle-summary"><strong>{{ recycleReadiness.document.drawingNumber }} · {{ recycleReadiness.document.name }}</strong><span>{{ recycleReadiness.storedVersionCount }} 个历史版本 · 进入回收站后30天内可恢复</span></div>
      <el-alert v-if="!recycleReadiness.canRecycle" title="当前不能删除" type="error" :closable="false"><ul><li v-for="blocker in recycleReadiness.blockers" :key="blocker">{{ blocker }}</li></ul></el-alert>
      <template v-else>
        <label class="pdm-recycle-field"><span>删除原因 <b>*</b></span><el-input v-model="recycleReason" type="textarea" :rows="3" maxlength="500" show-word-limit placeholder="请填写可审计的删除原因" /></label>
        <label class="pdm-recycle-field"><span>确认图号或文件名 <b>*</b></span><el-input v-model="recycleConfirmation" :placeholder="`输入 ${recycleReadiness.document.drawingNumber} 或完整文件名`" /></label>
        <p class="pdm-dialog-help">系统不会覆盖或立即删除历史版本文件；30天后关闭恢复入口，并保留审计记录。</p>
      </template>
    </template>
    <template #footer><button type="button" class="pdm-secondary-action" @click="recycleOpen=false">取消</button><button v-if="recycleReadiness?.canRecycle" type="button" class="pdm-danger-action" :disabled="recycleLoading || !recycleReason.trim() || !recycleConfirmation.trim()" @click="confirmDocumentRecycle">移入回收站</button></template>
  </el-dialog>
  <el-dialog v-model="permissionOpen" title="目录独立权限" width="760px"><p class="pdm-dialog-help">未配置时继承上级或模板权限；后端对每个文件操作再次校验当前有效权限。</p><div class="pdm-permission-list"><div v-for="(rule, index) in permissionRows" :key="rule.id || index" class="pdm-permission-row"><el-select v-model="rule.principalType" style="width:100px"><el-option label="角色" value="Role" /><el-option label="用户" value="User" /></el-select><el-select v-if="rule.principalType === 'Role'" v-model="rule.principalKey" filterable style="width:165px"><el-option v-for="role in roles" :key="role.role" :label="role.name" :value="role.role" /></el-select><el-select v-else v-model="rule.principalKey" filterable style="width:165px"><el-option v-for="user in users" :key="user.username" :label="user.displayName" :value="user.username" /></el-select><el-checkbox-group :model-value="accessValues(rule)" @update:model-value="setAccess(rule, $event as number[])"><el-checkbox v-for="item in accessOptions" :key="item.value" :value="item.value">{{ item.label }}</el-checkbox></el-checkbox-group><button type="button" class="pdm-text-danger" @click="permissionRows.splice(index, 1)">移除</button></div></div><button type="button" class="pdm-secondary-action" @click="addPermission">添加权限主体</button><template #footer><button type="button" class="pdm-secondary-action" @click="permissionOpen=false">取消</button><button type="button" class="pdm-primary-action" :disabled="pending" @click="savePermissions">保存权限</button></template></el-dialog>
</template>

<style scoped>
.pdm-folder-node__icon{display:inline-flex;flex:0 0 auto}.pdm-folder-node__icon.is-populated{color:var(--pdm-theme-accent)}.pdm-folder-node__icon.is-populated svg{fill:currentColor}
.pdm-file-column-actions{width:76px}.pdm-recycle-summary{display:flex;flex-direction:column;gap:5px;margin-bottom:14px;padding:12px;border:1px solid #dbe7ef;border-radius:6px;background:#f7fafc}.pdm-recycle-summary span{font-size:12px;color:#64748b}.pdm-recycle-field{display:flex;flex-direction:column;gap:6px;margin-top:14px}.pdm-recycle-field>span{font-size:13px;color:#334155}.pdm-recycle-field b{color:#dc2626}.pdm-document-recycle-dialog ul{margin:8px 0 0;padding-left:20px}.pdm-danger-action{display:inline-flex;min-height:34px;align-items:center;border:1px solid #f2c5c0;border-radius:5px;padding:0 11px;background:#fff7f6;color:#c8473d;cursor:pointer}.pdm-danger-action:disabled{cursor:not-allowed;opacity:.45}
.pdm-file-actions,.pdm-row-actions,.pdm-upload-strip{display:flex;align-items:center;gap:8px}.pdm-file-actions{flex-wrap:wrap;justify-content:flex-end}.pdm-upload-strip{padding:8px 14px;background:#f0fdfa;border-bottom:1px solid #ccfbf1}.pdm-upload-strip>span{max-width:260px;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}.pdm-upload-strip :deep(.el-progress){flex:1}.pdm-row-actions button,.pdm-icon-action{display:inline-flex;align-items:center;justify-content:center;border:0;background:transparent;color:var(--pdm-blue);cursor:pointer;padding:4px;border-radius:4px}.pdm-row-actions button:hover,.pdm-icon-action:hover{background:var(--pdm-blue-soft)}.is-deleted{opacity:.65}.pdm-deleted-badge{margin-left:8px;color:#b45309}.pdm-file-detail-table th:last-child{width:180px}
:global(.pdm-move-folder-overlay .el-overlay-dialog){align-items:center;justify-content:center;padding:12px}:global(.pdm-move-folder-overlay .el-overlay-dialog>.pdm-move-folder-dialog){width:min(520px,calc(100vw - 24px))!important;height:min(720px,calc(100dvh - 24px));max-height:calc(100dvh - 24px);margin:auto;border-radius:8px;box-shadow:0 16px 42px rgba(15,23,42,.22)}:global(.pdm-move-folder-overlay .el-overlay-dialog>.pdm-move-folder-dialog .el-dialog__body){display:flex;min-height:0;flex-direction:column;overflow:hidden}.pdm-move-folder-tree{box-sizing:border-box;min-height:0;flex:1 1 auto;padding:8px;border:1px solid var(--pdm-border);border-radius:6px;overflow:auto}.pdm-move-folder-tree :deep(.el-tree-node__content){height:28px}.pdm-folder-node.is-move-disabled{color:#94a3b8}
</style>
