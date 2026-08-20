<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { ElMessage } from 'element-plus'
import { File, Folder, FolderCog, Search, ShieldCheck } from '@lucide/vue'
import type { FolderPermissionRule, ManagedDocument, PdmUser, ProjectFolder } from '../types'

const props = defineProps<{
  folders: ProjectFolder[]
  documents: ManagedDocument[]
  users: PdmUser[]
  administrator: boolean
  pending: boolean
  onUpdatePermissions: (folderId: string, permissions: FolderPermissionRule[]) => Promise<ProjectFolder[]>
}>()

interface FolderTreeNode extends ProjectFolder { children: FolderTreeNode[] }
const selectedFolderId = ref('')
const kindFilter = ref<'all' | 'model' | 'drawing'>('all')
const documentQuery = ref('')
const permissionOpen = ref(false)
const permissionRows = ref<FolderPermissionRule[]>([])
const roles = ['Engineer', 'PlanningManager', 'ProcessReviewer', 'Approver', 'ProductionViewer', 'Administrator']
const accessOptions = [
  { value: 1, label: '查看' }, { value: 2, label: '下载' }, { value: 4, label: '上传' },
  { value: 8, label: '编辑' }, { value: 16, label: '删除' }, { value: 32, label: '管理权限' }, { value: 64, label: '发布' },
]

const visibleFolders = computed(() => props.folders.filter(folder => (folder.effectiveAccess & 1) === 1))
const treeData = computed<FolderTreeNode[]>(() => {
  const map = new Map(visibleFolders.value.map(folder => [folder.id, { ...folder, children: [] as FolderTreeNode[] }]))
  const roots: FolderTreeNode[] = []
  for (const folder of map.values()) {
    const parent = folder.parentFolderId ? map.get(folder.parentFolderId) : undefined
    if (parent) parent.children.push(folder)
    else roots.push(folder)
  }
  const sort = (items: FolderTreeNode[]) => items.sort((a, b) => a.sortOrder - b.sortOrder || a.name.localeCompare(b.name, 'zh-CN')).forEach(item => sort(item.children))
  sort(roots)
  return roots
})
const selectedFolder = computed(() => visibleFolders.value.find(folder => folder.id === selectedFolderId.value))
const folderDocuments = computed(() => props.documents.filter(document => document.folderId === selectedFolderId.value))
const modelDocumentCount = computed(() => folderDocuments.value.filter(document => document.kind !== 'Drawing').length)
const drawingDocumentCount = computed(() => folderDocuments.value.filter(document => document.kind === 'Drawing').length)
const displayedDocuments = computed(() => {
  const query = documentQuery.value.trim().toLocaleLowerCase('zh-CN')
  return folderDocuments.value.filter(document => {
    const matchesKind = kindFilter.value === 'all'
      || (kindFilter.value === 'model' && document.kind !== 'Drawing')
      || (kindFilter.value === 'drawing' && document.kind === 'Drawing')
    const text = `${document.drawingNumber} ${document.name} ${document.fileName}`.toLocaleLowerCase('zh-CN')
    return matchesKind && (!query || text.includes(query))
  })
})

watch(() => props.folders, () => {
  if (!visibleFolders.value.some(folder => folder.id === selectedFolderId.value)) selectedFolderId.value = treeData.value[0]?.id ?? ''
}, { immediate: true, deep: true })

function selectFolder(folder: ProjectFolder) { selectedFolderId.value = folder.id }
function folderCount(folderId: string) { return props.documents.filter(document => document.folderId === folderId).length }
function kindLabel(kind: ManagedDocument['kind']) { return ({ Assembly: '装配体', Part: '零件', Drawing: '工程图' })[kind] }
function stateLabel(state: string | number) { return state === 'Released' || state === 2 ? '已发布' : state === 'InReview' || state === 1 ? '审批中' : '工作版' }

function openPermissions() {
  if (!selectedFolder.value) return
  permissionRows.value = selectedFolder.value.permissions.map(item => ({ ...item }))
  permissionOpen.value = true
}
function addPermission() { permissionRows.value.push({ principalType: 'Role', principalKey: 'Engineer', access: 3 }) }
function accessValues(rule: FolderPermissionRule) { return accessOptions.filter(item => (rule.access & item.value) === item.value).map(item => item.value) }
function setAccess(rule: FolderPermissionRule, values: number[]) { rule.access = values.reduce((mask, value) => mask | value, 0) }
async function savePermissions() {
  if (!selectedFolder.value) return
  try {
    await props.onUpdatePermissions(selectedFolder.value.id, permissionRows.value)
    permissionOpen.value = false
    ElMessage.success('目录权限已保存')
  } catch (error) { ElMessage.error(error instanceof Error ? error.message : '目录权限保存失败') }
}
</script>

<template>
  <section class="pdm-file-library">
    <aside class="pdm-panel pdm-folder-pane">
      <header class="pdm-panel-heading"><div><h2>项目文件夹</h2><p>机械、电气均按主项目与子项目独立归档</p></div></header>
      <el-tree :data="treeData" node-key="id" :default-expanded-keys="visibleFolders.filter(item => ['Root', 'MechanicalRoot', 'ElectricalRoot'].includes(item.purpose)).map(item => item.id)" highlight-current @node-click="selectFolder">
        <template #default="{ data }"><span class="pdm-folder-node"><Folder :size="15" /><span>{{ data.name }}</span><small v-if="folderCount(data.id)">{{ folderCount(data.id) }}</small></span></template>
      </el-tree>
    </aside>
    <section class="pdm-panel pdm-folder-content">
      <header class="pdm-folder-toolbar">
        <div><div class="pdm-breadcrumb">项目图档 <span>/</span> {{ selectedFolder?.name || '请选择目录' }}</div><h2>{{ selectedFolder?.name || '项目文件库' }}</h2><p v-if="selectedFolder?.purpose === 'ProjectContainer'">该目录只接收“{{ selectedFolder.name }}”对应项目的图档。</p></div>
        <button v-if="administrator && selectedFolder" type="button" class="pdm-secondary-action" @click="openPermissions"><ShieldCheck :size="15" />目录权限</button>
      </header>
      <div class="pdm-file-filters">
        <div class="pdm-document-filters" role="tablist" aria-label="项目图档类型筛选">
          <button type="button" role="tab" :aria-selected="kindFilter === 'all'" @click="kindFilter = 'all'">全部<small>{{ folderDocuments.length }}</small></button>
          <button type="button" role="tab" :aria-selected="kindFilter === 'model'" @click="kindFilter = 'model'">3D结构<small>{{ modelDocumentCount }}</small></button>
          <button type="button" role="tab" :aria-selected="kindFilter === 'drawing'" @click="kindFilter = 'drawing'">2D图纸<small>{{ drawingDocumentCount }}</small></button>
        </div>
        <label class="pdm-inline-search"><Search :size="15" /><input v-model="documentQuery" type="search" placeholder="搜索图号、名称或文件名"></label>
      </div>
      <div v-if="displayedDocuments.length" class="pdm-file-table-wrap">
        <table class="pdm-file-detail-table" aria-label="文件明细">
          <colgroup>
            <col class="pdm-file-column-number">
            <col class="pdm-file-column-name">
            <col class="pdm-file-column-kind">
            <col class="pdm-file-column-revision">
            <col class="pdm-file-column-state">
            <col class="pdm-file-column-editor">
            <col class="pdm-file-column-updated">
          </colgroup>
          <thead><tr><th>图号</th><th>名称</th><th>类型</th><th>版本</th><th>状态</th><th>编辑人</th><th>更新时间</th></tr></thead>
          <tbody>
            <tr v-for="document in displayedDocuments" :key="document.id">
              <td><span class="pdm-file-name" :title="document.drawingNumber"><File :size="15" />{{ document.drawingNumber }}</span></td>
              <td :title="document.name">{{ document.name }}</td>
              <td>{{ kindLabel(document.kind) }}</td>
              <td>{{ document.revision }}</td>
              <td>{{ stateLabel(document.state) }}</td>
              <td :title="document.checkedOutBy || '—'">{{ document.checkedOutBy || '—' }}</td>
              <td>{{ document.updatedAt ? new Date(document.updatedAt).toLocaleString() : '—' }}</td>
            </tr>
          </tbody>
        </table>
      </div>
      <div v-else class="pdm-folder-empty"><FolderCog :size="34" /><strong>{{ folderDocuments.length ? '没有匹配的图档' : '此目录暂无文件' }}</strong><span v-if="folderDocuments.length">请调整3D/2D筛选或搜索关键字。</span><span v-else-if="selectedFolder?.purpose === 'ProjectContainer'">SolidWorks首次存档时将自动归入机械图纸的对应项目目录；电气图档可显式选择电气目录。</span><span v-else>该目录可用于项目资料上传，实际上传操作将在后续文件操作入口中提供。</span></div>
    </section>
  </section>

  <el-dialog v-model="permissionOpen" title="目录独立权限" width="760px">
    <p class="pdm-dialog-help">未配置时继承上级或模板权限；当前目录的显式权限会优先应用。</p>
    <div class="pdm-permission-list">
      <div v-for="(rule, index) in permissionRows" :key="rule.id || index" class="pdm-permission-row">
        <el-select v-model="rule.principalType" style="width:100px"><el-option label="角色" value="Role" /><el-option label="用户" value="User" /></el-select>
        <el-select v-if="rule.principalType === 'Role'" v-model="rule.principalKey" style="width:165px"><el-option v-for="role in roles" :key="role" :label="role" :value="role" /></el-select>
        <el-select v-else v-model="rule.principalKey" filterable style="width:165px"><el-option v-for="user in users" :key="user.username" :label="`${user.displayName} (${user.username})`" :value="user.username" /></el-select>
        <el-checkbox-group :model-value="accessValues(rule)" @update:model-value="setAccess(rule, $event as number[])"><el-checkbox v-for="item in accessOptions" :key="item.value" :value="item.value">{{ item.label }}</el-checkbox></el-checkbox-group>
        <button type="button" class="pdm-text-danger" @click="permissionRows.splice(index, 1)">移除</button>
      </div>
    </div>
    <button type="button" class="pdm-secondary-action" @click="addPermission">添加权限主体</button>
    <template #footer><button type="button" class="pdm-secondary-action" @click="permissionOpen=false">取消</button><button type="button" class="pdm-primary-action" :disabled="pending" @click="savePermissions">保存权限</button></template>
  </el-dialog>
</template>
