<script setup lang="ts">
import { onMounted, ref, watch } from 'vue'
import { ElDatePicker } from 'element-plus'
import { readClientLogs, downloadClientLogs, type ClientLogPage } from '../api'

const props = defineProps<{ token: string }>()
const data = ref<ClientLogPage>({ items: [], total: 0, pageSize: 50, runtimeDays: 30, errorDays: 90 })
const device = ref(''), user = ref(''), component = ref(''), kind = ref(''), version = ref('')
const dateRange = ref<[Date, Date] | null>(null)
const page = ref(1), pending = ref(false), downloading = ref(false), error = ref(''), notice = ref('')
let generation = 0
function query(downloadKind = kind.value) {
  const values = new URLSearchParams({ page: String(page.value) })
  for (const [key, value] of Object.entries({ device: device.value, user: user.value, component: component.value, kind: downloadKind, version: version.value }))
    if (value.trim()) values.set(key, value.trim())
  if (dateRange.value) {
    const [from, to] = dateRange.value
    if (from > to) throw new Error('开始时间不能晚于结束时间。')
    values.set('from', from.toISOString())
    values.set('to', to.toISOString())
  }
  return values
}
async function load(reset = false) {
  const current = ++generation
  if (reset) page.value = 1
  pending.value = true; error.value = ''; notice.value = ''
  try {
    const result = await readClientLogs(query(), props.token)
    if (current === generation) data.value = result
  } catch { if (current === generation) { data.value.items = []; data.value.total = 0; error.value = '日志查询失败，请检查时间范围、网络和管理员权限后重试。' } }
  finally { if (current === generation) pending.value = false }
}
async function download(downloadKind: string) {
  if (downloading.value) return
  downloading.value = true; error.value = ''; notice.value = ''
  try { await downloadClientLogs(query(downloadKind), props.token); notice.value = '诊断包已下载，可交给管理员或开发人员分析。' }
  catch (cause) { error.value = cause instanceof Error ? cause.message : '日志下载失败，请稍后重试。' }
  finally { downloading.value = false }
}
async function movePage(offset: number) { page.value += offset; await load() }
const time = (value: string) => new Date(value).toLocaleString('zh-CN', { hour12: false })
onMounted(() => load())
watch(() => props.token, () => load(true))
</script>

<template>
  <section class="pdm-project-manager pdm-client-logs" aria-label="客户端日志">
    <header><h2>客户端日志</h2><p>运行日志保留 {{ data.runtimeDays }} 天，错误日志保留 {{ data.errorDays }} 天。仅显示当前公司已收到的日志；离线客户机将在恢复登录和连接后补传。</p></header>
    <form class="log-filters" @submit.prevent="load(true)">
      <label>计算机或设备编号<input v-model="device" placeholder="计算机名或完整设备编号" /></label>
      <label>用户<input v-model="user" placeholder="登录账号" /></label>
      <label>组件<select v-model="component" aria-label="组件"><option value="">全部组件</option><option value="desktop">桌面客户端</option><option value="solidworks">SolidWorks 插件</option><option value="preview-agent">转图代理</option></select></label>
      <label>日志类型<select v-model="kind" aria-label="日志类型"><option value="">全部日志</option><option value="runtime">运行日志</option><option value="error">错误日志</option></select></label>
      <label>组件或界面版本<input v-model="version" placeholder="版本号或界面构建标识" /></label>
      <label class="log-date-range">日期时间区间<el-date-picker v-model="dateRange" type="datetimerange" aria-label="日志日期时间区间" format="YYYY-MM-DD HH:mm" range-separator="-" start-placeholder="开始时间" end-placeholder="结束时间" /></label>
      <button type="submit" :disabled="pending">{{ pending ? '查询中…' : '查询日志' }}</button>
    </form>
    <div class="log-actions">
      <button type="button" :disabled="downloading || pending || !data.total" @click="download('')">下载完整诊断包</button>
      <button type="button" :disabled="downloading || pending || !data.total" @click="download('runtime')">下载运行日志</button>
      <button type="button" :disabled="downloading || pending || !data.total" @click="download('error')">下载错误日志</button>
      <span>{{ downloading ? '正在生成诊断包…' : `共 ${data.total} 个日志分片` }}</span>
    </div>
    <p class="log-hint">下载包含当前计算机、用户、组件、版本和时间筛选范围内的全部匹配记录，不受当前页限制。完整诊断包同时包含两类日志；每行显示该分片的记录数及上传时间。</p>
    <p v-if="error" role="alert" class="log-error">{{ error }}</p><p v-if="notice" role="status">{{ notice }}</p>
    <div class="log-table-wrap"><table>
      <thead><tr><th>计算机 / 用户</th><th>组件 / 版本</th><th>日志类型</th><th>记录数</th><th>覆盖时间</th><th>上传时间</th></tr></thead>
      <tbody>
        <tr v-for="row in data.items" :key="row.id">
          <td><strong>{{ row.machine }}</strong><br />{{ row.username }}<small>设备：{{ row.deviceId }}</small></td>
          <td>{{ row.component === 'desktop' ? '桌面客户端' : row.component === 'preview-agent' ? '转图代理' : 'SolidWorks 插件' }}<br />{{ row.version || '版本未记录' }}<small>界面：{{ row.component === 'preview-agent' ? '代理前台界面' : row.uiVersion || '尚未加载网页界面' }}</small></td>
          <td :class="{ 'log-error': row.kind === 'error' }">{{ row.kind === 'error' ? '错误日志' : '运行日志' }}</td><td>{{ row.entryCount }}</td>
          <td>{{ time(row.startedAt) }}<br />至 {{ time(row.endedAt) }}</td><td>{{ time(row.receivedAt) }}</td>
        </tr>
        <tr v-if="!pending && !data.items.length"><td colspan="6">暂无符合条件的日志。请确认客户机已升级、登录并连接服务器。</td></tr>
      </tbody>
    </table></div>
    <footer><button :disabled="pending || page <= 1" @click="movePage(-1)">上一页</button><span>第 {{ page }} 页</span><button :disabled="pending || page * data.pageSize >= data.total" @click="movePage(1)">下一页</button></footer>
  </section>
</template>

<style scoped>
.pdm-client-logs { padding: 20px; min-width: 0; }
header h2 { margin: 0 0 8px; } header p, .log-hint { color: var(--pdm-text-soft); font-size: 13px; line-height: 1.6; }
.log-filters { display: flex; align-items: end; gap: 12px; flex-wrap: wrap; margin: 18px 0; }
.log-filters label { display: grid; gap: 6px; font-size: 13px; }
input, select { min-height: 34px; border: 1px solid var(--pdm-border); border-radius: 4px; padding: 5px 8px; max-width: 230px; }
button { border: 1px solid var(--pdm-border); border-radius: 4px; background: white; padding: 8px 12px; cursor: pointer; } button:disabled { opacity: .5; cursor: default; }
.log-actions, footer { display: flex; align-items: center; flex-wrap: wrap; gap: 12px; } footer { justify-content: flex-end; margin-top: 16px; }
.log-table-wrap { overflow: auto; } table { width: 100%; border-collapse: collapse; font-size: 13px; } th { text-align: left; background: var(--pdm-surface-muted); } td, th { padding: 12px; border-bottom: 1px solid var(--pdm-surface-muted); vertical-align: top; } small { display: block; max-width: 240px; overflow-wrap: anywhere; color: var(--pdm-text-soft); margin-top: 5px; }
.log-date-range { min-width: 0; max-width: 100%; }
.log-date-range :deep(.el-date-editor) { width: 380px; max-width: 100%; min-height: 34px; box-sizing: border-box; }
.log-error { color: var(--pdm-danger); }
</style>
