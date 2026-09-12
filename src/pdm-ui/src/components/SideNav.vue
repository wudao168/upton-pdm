<script setup lang="ts">
import { computed, ref } from 'vue'
import { Blocks, Boxes, ClipboardCheck, FolderKanban, LayoutDashboard, Library, ListTree, Network, Settings } from '@lucide/vue'
import uptonLogo from '../assets/upton-logo-white.png'
import PlmCubeIcon from './PlmCubeIcon.vue'

type NavKey = 'project-center' | 'project-workbench' | 'projects' | 'materials' | 'standard-library' | 'standard-structure' | 'program-templates' | 'tasks' | 'admin'

const props = withDefaults(defineProps<{ active: NavKey; approvalCount?: number; materialCount?: number; canManageSystem?: boolean; canViewStandardLibrary?: boolean; canViewMaterials?: boolean; collapsed?: boolean; version?: string }>(), {
  approvalCount: 0,
  materialCount: 0,
  canManageSystem: false,
  canViewStandardLibrary: false,
  canViewMaterials: false,
  collapsed: false,
  version: '',
})
const emit = defineEmits<{ navigate: [key: NavKey, label: string] }>()
const versionDialogOpen = ref(false)
const versionLoading = ref(false)
const runtimeDatabase = ref('—')

const fullVersion = computed(() => props.version.trim())
const displayVersion = computed(() => {
  if (!fullVersion.value) return '未知'
  return `V${fullVersion.value.split('-', 1)[0]}`
})
const versionTitle = computed(() => fullVersion.value ? `版本 ${fullVersion.value}` : '版本未知')
const releaseTime = computed(() => {
  const match = /^(\d{4})\.(\d{2})\.(\d{2})\.(\d{2})(\d{2})/.exec(fullVersion.value)
  return match ? `${match[1]}-${match[2]}-${match[3]} ${match[4]}:${match[5]}` : '—'
})
const releaseNote = computed(() => {
  const separator = fullVersion.value.indexOf('-')
  if (separator < 0) return '当前部署版本'
  const tag = fullVersion.value.slice(separator + 1)
  if (tag === 'version-information') return '增加网页端、Windows 客户端及 SolidWorks 插件端版本信息。'
  return tag
})

async function openVersionInfo() {
  versionDialogOpen.value = true
  versionLoading.value = true
  try {
    const response = await fetch('/health', { cache: 'no-store' })
    if (!response.ok) throw new Error()
    const health = await response.json() as { database?: string; databaseName?: string }
    runtimeDatabase.value = [health.database, health.databaseName].filter(Boolean).join(' · ') || '—'
  } catch {
    runtimeDatabase.value = '暂时无法读取'
  } finally {
    versionLoading.value = false
  }
}

const items = [
  { key: 'project-center', label: '项目中心', icon: FolderKanban },
  { key: 'project-workbench', label: '项目工作台', icon: LayoutDashboard },
  { key: 'projects', label: '项目列表', icon: ListTree },
  { key: 'standard-library', label: '标准物料', icon: Library },
  { key: 'standard-structure', label: '标准结构', icon: Network },
  { key: 'materials', label: '料品管理', icon: Boxes },
  { key: 'program-templates', label: '程序模板', icon: Blocks },
  { key: 'tasks', label: '我的待办', icon: ClipboardCheck },
] satisfies Array<{ key: NavKey; label: string; icon: typeof FolderKanban }>
</script>

<template>
  <aside class="pdm-sidebar" :class="{ 'is-collapsed': props.collapsed }" aria-label="主导航">
    <div class="pdm-sidebar__brand">
      <PlmCubeIcon class="pdm-sidebar__brand-mark" />
      <div class="pdm-sidebar__brand-copy">
        <img class="pdm-sidebar__brand-logo" :src="uptonLogo" alt="UPTON">
      </div>
    </div>
    <nav class="pdm-sidebar__nav">
      <button
        v-for="item in items.filter(entry => (!['standard-library', 'standard-structure'].includes(entry.key) || props.canViewStandardLibrary) && (entry.key !== 'materials' || props.canViewMaterials))"
        :key="item.label"
        type="button"
        class="pdm-nav-item"
        :class="{ 'is-active': props.active === item.key }"
        :aria-current="props.active === item.key ? 'page' : undefined"
        :aria-label="props.collapsed ? item.label : undefined"
        :title="props.collapsed ? item.label : undefined"
        @click="emit('navigate', item.key, item.label)"
      >
        <component :is="item.icon" :size="18" aria-hidden="true" />
        <span>{{ item.label }}</span>
        <em v-if="item.key === 'materials' && props.materialCount">{{ props.materialCount }}</em>
        <em v-else-if="item.key === 'tasks' && props.approvalCount">{{ props.approvalCount }}</em>
      </button>
    </nav>
    <div class="pdm-sidebar__footer">
      <button type="button" class="pdm-sidebar__version" :title="`${versionTitle}，点击查看详情`" :aria-label="`${versionTitle}，点击查看详情`" @click="openVersionInfo">
        <span class="pdm-sidebar__version-icon" aria-hidden="true" />
        <span class="pdm-sidebar__version-copy">版本 {{ displayVersion }}</span>
      </button>
      <button
        v-if="props.canManageSystem"
        type="button"
        class="pdm-nav-item pdm-sidebar__settings"
        :class="{ 'is-active': props.active === 'admin' }"
        :aria-current="props.active === 'admin' ? 'page' : undefined"
        :aria-label="props.collapsed ? '系统管理' : undefined"
        :title="props.collapsed ? '系统管理' : undefined"
        @click="emit('navigate', 'admin', '系统管理')"
      >
        <Settings :size="18" aria-hidden="true" /><span>系统管理</span>
      </button>
    </div>
    <el-drawer v-model="versionDialogOpen" title="系统版本信息" size="420px" append-to-body>
      <section v-loading="versionLoading" class="pdm-system-version-detail" aria-label="系统版本详情">
        <header><strong>{{ displayVersion }}</strong><el-tag type="success" effect="plain">当前版本</el-tag></header>
        <dl>
          <div><dt>发布标识</dt><dd>{{ fullVersion || '—' }}</dd></div>
          <div><dt>发布时间</dt><dd>{{ releaseTime }}</dd></div>
          <div><dt>运行端</dt><dd>网页端 / Windows 客户端</dd></div>
          <div><dt>数据库</dt><dd>{{ runtimeDatabase }}</dd></div>
        </dl>
        <section><h3>版本说明</h3><p>{{ releaseNote }}</p></section>
        <p class="pdm-system-version-detail__note">版本信息来自当前部署标识；数据库状态在打开详情时实时读取。</p>
      </section>
    </el-drawer>
  </aside>
</template>
