<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
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
  publishedDesktopVersion?: string
  solidWorksAddinVersion?: string
}>()

const emit = defineEmits<{ refresh: [] }>()
const runtimeDatabase = ref('读取中…')

const fullVersion = computed(() => props.version.trim())
const displayVersion = computed(() => displayReleaseVersion(fullVersion.value))
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
      DesktopVersion: props.publishedDesktopVersion,
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

onMounted(async () => {
  emit('refresh')
  try {
    const response = await fetch('/health', { cache: 'no-store' })
    if (!response.ok) throw new Error()
    const health = await response.json() as { database?: string; databaseName?: string }
    runtimeDatabase.value = [health.database, health.databaseName].filter(Boolean).join(' · ') || '—'
  } catch {
    runtimeDatabase.value = '暂时无法读取'
  }
})
</script>

<template>
  <section class="pdm-system-info" aria-label="系统信息">
    <section class="pdm-panel pdm-system-version-detail" aria-label="系统版本信息">
      <h2>当前版本</h2>
      <dl>
        <div><dt>服务器 / 网页端</dt><dd>{{ displayVersion }}</dd></div>
        <div><dt>发布标识</dt><dd>{{ fullVersion || '—' }}</dd></div>
        <div><dt>发布时间</dt><dd>{{ releaseTime }}</dd></div>
        <div><dt>Windows 客户端发布版本</dt><dd>{{ publishedDesktopVersion ? displayReleaseVersion(publishedDesktopVersion) : '—' }}</dd></div>
        <div v-if="desktopVersion"><dt>当前 Windows 客户端</dt><dd>{{ displayReleaseVersion(desktopVersion) }}</dd></div>
        <div><dt>SolidWorks 插件发布版本</dt><dd>{{ solidWorksAddinVersion ? displayReleaseVersion(solidWorksAddinVersion) : '—' }}</dd></div>
        <div><dt>数据库</dt><dd>{{ runtimeDatabase }}</dd></div>
      </dl>
      <section>
        <h3>版本说明</h3>
        <ol v-if="releaseNoteList.length > 1" class="pdm-system-version-note">
          <li v-for="item in releaseNoteList" :key="item">{{ item }}</li>
        </ol>
        <p v-else>{{ releaseNote }}</p>
      </section>
    </section>
    <section class="pdm-panel pdm-system-version-history" aria-label="更新日志">
      <h2>更新日志</h2>
      <p v-if="!releaseHistory.length" class="pdm-system-version-history__empty">暂无更新记录。</p>
      <article v-for="entry in releaseHistory" :key="entry.version" class="pdm-system-version-history__item">
        <header><strong>{{ displayReleaseVersion(entry.version) }}</strong><time>{{ entry.releasedAt }}</time></header>
        <ol v-if="entry.items.length > 1" class="pdm-system-version-note">
          <li v-for="item in entry.items" :key="item">{{ item }}</li>
        </ol>
        <p v-else>{{ entry.note }}</p>
      </article>
    </section>
  </section>
</template>

<style scoped>
.pdm-system-info {
  display: grid;
  grid-template-columns: minmax(0, 2fr) minmax(0, 3fr);
  align-items: start;
  gap: var(--pdm-container-gap);
  min-height: 0;
  overflow: auto;
  overflow-wrap: anywhere;
}
.pdm-system-info > section { padding: 16px; }
.pdm-system-info h2 { margin: 0 0 12px; font-size: 15px; }
.pdm-system-version-history__empty { margin: 16px 0; color: var(--pdm-text-soft); text-align: center; }
@media (max-width: 900px) {
  .pdm-system-info { grid-template-columns: minmax(0, 1fr); }
}
</style>
