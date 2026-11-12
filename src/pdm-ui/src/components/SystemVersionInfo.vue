<script setup lang="ts">
import { computed, ref } from 'vue'
import { repairMojibake } from '../mojibake'

type SystemReleaseHistoryEntry = {
  Version?: string
  ReleasedAt?: string
  ReleaseNote?: string
  DesktopVersion?: string
  SolidWorksAddinVersion?: string
}

const props = defineProps<{
  version: string
  releasedAt?: string
  releaseNote?: string
  releaseHistory?: SystemReleaseHistoryEntry[]
  desktopVersion?: string
  solidWorksAddinVersion?: string
}>()

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
  return repairMojibake((props.releaseNote ?? '').trim()) || releaseNoteFromVersion(fullVersion.value) || '本次发布未填写版本说明。'
})
const releaseNoteList = computed(() => releaseNoteItems(releaseNote.value))
const releaseHistory = computed(() => {
  const entries = [...(props.releaseHistory ?? [])]
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

</script>

<template>
  <button type="button" class="pdm-system-version-trigger" :title="`${versionTitle}，点击查看详情`" :aria-label="`${versionTitle}，点击查看详情`" @click="openVersionInfo">
    版本 {{ displayVersion }}
  </button>
  <el-drawer v-model="versionDialogOpen" title="系统版本信息" size="min(420px, 100%)" append-to-body>
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
</template>

<style scoped>
:root body .pdm-system-version-trigger { flex: 0 0 auto; width: auto !important; min-width: max-content; white-space: nowrap; }
</style>
