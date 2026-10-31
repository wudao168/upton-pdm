<script setup lang="ts">
import { computed, ref } from 'vue'
import { Boxes, ClipboardCheck, Clock, FileCode, FileText, FolderKanban, LayoutDashboard, Library, Settings } from '@lucide/vue'
import { repairMojibake } from '../mojibake'
import navBrandLogo from '../assets/upton-horizontal-brand-white.png'

type NavKey = 'work-hours' | 'project-applications' | 'project-calendar' | 'project-center' | 'project-workbench' | 'projects' | 'production-drawings' | 'materials' | 'standard-library' | 'stocked-materials' | 'standard-structure' | 'program-templates' | 'tasks' | 'admin'

type SystemReleaseHistoryEntry = {
  Version?: string
  ReleasedAt?: string
  ReleaseNote?: string
  DesktopVersion?: string
  SolidWorksAddinVersion?: string
}

const props = withDefaults(defineProps<{
  active: NavKey
  approvalCount?: number
  materialCount?: number
  canManageSystem?: boolean
  canViewMaterials?: boolean
  collapsed?: boolean
  version?: string
  releasedAt?: string
  releaseNote?: string
  releaseHistory?: SystemReleaseHistoryEntry[]
  desktopVersion?: string
  solidWorksAddinVersion?: string
  token?: string
}>(), {
  approvalCount: 0,
  materialCount: 0,
  canManageSystem: false,
  canViewMaterials: false,
  collapsed: false,
  version: '',
  releasedAt: '',
  releaseNote: '',
  releaseHistory: () => [],
  desktopVersion: '',
  solidWorksAddinVersion: '',
  token: '',
})
const emit = defineEmits<{ navigate: [key: NavKey, label: string] }>()
const versionDialogOpen = ref(false)
const versionHistoryOpen = ref(false)
const versionLoading = ref(false)
const runtimeDatabase = ref('—')

const fullVersion = computed(() => props.version.trim())
const displayVersion = computed(() => {
  if (!fullVersion.value) return '未知'
  return `V${fullVersion.value.split('-', 1)[0]}`
})
const versionTitle = computed(() => fullVersion.value ? `版本 ${fullVersion.value}` : '版本未知')
function displayReleaseVersion(version: string) {
  const normalized = version.trim()
  return normalized ? `V${normalized.split('-', 1)[0]}` : '未知'
}
function formatReleaseTime(releasedAt: string | undefined, version: string) {
  const normalizedReleasedAt = releasedAt?.trim() ?? ''
  const isoMatch = /^(\d{4})-(\d{2})-(\d{2})T(\d{2}):(\d{2})/.exec(normalizedReleasedAt)
  if (isoMatch) return `${isoMatch[1]}-${isoMatch[2]}-${isoMatch[3]} ${isoMatch[4]}:${isoMatch[5]}`
  const match = /^(\d{4})\.(\d{2})\.(\d{2})\.(\d{2})(\d{2})/.exec(version)
  return match ? `${match[1]}-${match[2]}-${match[3]} ${match[4]}:${match[5]}` : '—'
}
function releaseNoteFromVersion(version: string) {
  const separator = version.indexOf('-')
  if (separator < 0) return ''
  const tag = version.slice(separator + 1).trim()
  if (tag === 'version-information') return '增加网页端、Windows 客户端及 SolidWorks 插件端版本信息。'
  return tag
}
// 发布说明按条填写（换行或分号分隔），详情里按 1./2./3. 逐条列举。
function releaseNoteItems(note: string) {
  const normalized = repairMojibake(note.trim())
  if (!normalized) return []
  const items = normalized.split(/\r?\n|；|;/).map(item => item.trim()).filter(Boolean)
  return items.length > 1 ? items : [normalized]
}
const releaseTime = computed(() => formatReleaseTime(props.releasedAt, fullVersion.value))
const releaseNote = computed(() => {
  return repairMojibake(props.releaseNote.trim()) || releaseNoteFromVersion(fullVersion.value) || '本次发布未填写版本说明。'
})
const releaseNoteList = computed(() => releaseNoteItems(releaseNote.value))
const releaseHistory = computed(() => {
  const entries = [...props.releaseHistory]
  if (fullVersion.value && !entries.some(entry => entry.Version?.trim() === fullVersion.value)) {
    entries.unshift({
      Version: fullVersion.value,
      ReleasedAt: props.releasedAt,
      ReleaseNote: releaseNote.value,
      DesktopVersion: props.desktopVersion,
      SolidWorksAddinVersion: props.solidWorksAddinVersion,
    })
  }
  return entries.filter(entry => entry.Version?.trim()).map(entry => {
    const note = repairMojibake(entry.ReleaseNote?.trim() ?? '') || releaseNoteFromVersion(entry.Version!.trim()) || '历史发布未填写说明。'
    return {
      version: entry.Version!.trim(),
      releasedAt: formatReleaseTime(entry.ReleasedAt, entry.Version!.trim()),
      note,
      items: releaseNoteItems(note),
    }
  })
})

async function openVersionInfo() {
  versionDialogOpen.value = true
  versionHistoryOpen.value = false
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

const groups = [
  { key: 'primary', label: '', items: [
    { key: 'project-workbench', label: '项目总览', icon: LayoutDashboard, iconColor: '#4f6fb5' },
    { key: 'project-center', label: '项目中心', icon: FolderKanban, iconColor: '#2f7e78' },
    { key: 'work-hours', label: '工时管理', icon: Clock, iconColor: '#a47a32' },
    { key: 'tasks', label: '我的待办', icon: ClipboardCheck, iconColor: '#8063aa' },
  ] },
  { key: 'production', label: '生产', items: [
    { key: 'production-drawings', label: '制造中心', icon: FileText, iconColor: '#b96643' },
  ] },
  { key: 'standardization', label: '标准化', items: [
    { key: 'standard-library', label: '标准物料', icon: Library, iconColor: '#5d8045' },
    { key: 'program-templates', label: '标准程序', icon: FileCode, iconColor: '#367f9f' },
    { key: 'materials', label: '料品管理', icon: Boxes, iconColor: '#ac5873' },
  ] },
] satisfies Array<{ key: string; label: string; items: Array<{ key: NavKey; label: string; icon: typeof FolderKanban; iconColor: string }> }>
</script>

<template>
  <aside class="pdm-sidebar" :class="{ 'is-collapsed': props.collapsed }" aria-label="主导航">
    <div class="pdm-sidebar__brand">
      <svg class="pdm-sidebar__brand-logo" :viewBox="props.collapsed ? '0 0 527 346' : '0 0 1712 346'" role="img" aria-label="UPTON" overflow="hidden">
        <image :href="navBrandLogo" width="1712" height="346" />
      </svg>
    </div>
    <nav class="pdm-sidebar__nav">
      <div v-for="group in groups" :key="group.key" class="pdm-nav-group" role="group" :aria-label="group.label || '常用功能'">
        <p v-if="group.label" class="pdm-nav-group__label" aria-hidden="true">{{ group.label }}</p>
        <button
          v-for="item in group.items.filter(entry => entry.key !== 'materials' || props.canViewMaterials)"
          :key="item.label"
          type="button"
          class="pdm-nav-item"
          :class="{ 'is-active': props.active === item.key }"
          :aria-current="props.active === item.key ? 'page' : undefined"
          :aria-label="props.collapsed ? item.label : undefined"
          :title="props.collapsed ? item.label : undefined"
          @click="emit('navigate', item.key, item.label)"
        >
          <component :is="item.icon" :size="14" :style="{ color: item.iconColor }" aria-hidden="true" />
          <span>{{ item.label }}</span>
          <em v-if="item.key === 'materials' && props.materialCount">{{ props.materialCount }}</em>
          <em v-else-if="item.key === 'tasks' && props.approvalCount">{{ props.approvalCount }}</em>
        </button>
      </div>
    </nav>
    <div class="pdm-sidebar__footer">
      <button type="button" class="pdm-sidebar__version" :title="`${versionTitle}，点击查看详情`" :aria-label="`${versionTitle}，点击查看详情`" @click="openVersionInfo">
        <span class="pdm-sidebar__version-icon" aria-hidden="true" />
        <span class="pdm-sidebar__version-copy">{{ displayVersion }}</span>
      </button>
      <button
        v-if="props.canManageSystem"
        type="button"
        class="pdm-nav-item pdm-sidebar__settings"
        :class="{ 'is-active': props.active === 'admin' }"
        :aria-current="props.active === 'admin' ? 'page' : undefined"
        :aria-label="props.collapsed ? '系统设置' : undefined"
        :title="props.collapsed ? '系统设置' : undefined"
        @click="emit('navigate', 'admin', '系统设置')"
      >
        <Settings :size="14" style="color: #65768a" aria-hidden="true" /><span>系统设置</span>
      </button>
    </div>
    <el-drawer v-model="versionDialogOpen" title="系统版本信息" size="420px" append-to-body>
      <section v-loading="versionLoading" class="pdm-system-version-detail" aria-label="系统版本详情">
        <header><strong>{{ displayVersion }}</strong><el-tag type="success" effect="plain">当前版本</el-tag></header>
        <dl>
          <div><dt>发布标识</dt><dd>{{ fullVersion || '—' }}</dd></div>
          <div><dt>发布时间</dt><dd>{{ releaseTime }}</dd></div>
          <div><dt>服务器 / 网页端</dt><dd>{{ displayVersion }}</dd></div>
          <div><dt>Windows客户端</dt><dd>{{ props.desktopVersion ? displayReleaseVersion(props.desktopVersion) : '未连接客户端' }}</dd></div>
          <div><dt>SolidWorks插件</dt><dd>{{ props.solidWorksAddinVersion ? displayReleaseVersion(props.solidWorksAddinVersion) : '—' }}</dd></div>
          <div><dt>数据库</dt><dd>{{ runtimeDatabase }}</dd></div>
        </dl>
        <section>
          <h3>版本说明</h3>
          <ol v-if="releaseNoteList.length > 1" class="pdm-system-version-note">
            <li v-for="item in releaseNoteList" :key="item">{{ item }}</li>
          </ol>
          <p v-else>{{ releaseNote }}</p>
        </section>
        <button
          type="button"
          class="pdm-system-version-history__toggle"
          :aria-expanded="versionHistoryOpen"
          aria-controls="pdm-system-version-history"
          @click="versionHistoryOpen = !versionHistoryOpen"
        >
          版本记录
        </button>
        <section
          v-if="versionHistoryOpen"
          id="pdm-system-version-history"
          class="pdm-system-version-history"
          aria-label="版本记录"
        >
          <article v-for="entry in releaseHistory" :key="entry.version" class="pdm-system-version-history__item">
            <header><strong>{{ displayReleaseVersion(entry.version) }}</strong><time>{{ entry.releasedAt }}</time></header>
            <ol v-if="entry.items.length > 1" class="pdm-system-version-note">
              <li v-for="item in entry.items" :key="item">{{ item }}</li>
            </ol>
            <p v-else>{{ entry.note }}</p>
          </article>
        </section>
        <p class="pdm-system-version-detail__note">版本说明与历史记录来自服务器发布清单；数据库状态在打开详情时实时读取。</p>
      </section>
    </el-drawer>
  </aside>
</template>
