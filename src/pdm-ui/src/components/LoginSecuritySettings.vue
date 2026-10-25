<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { approveLoginDevice, getLoginSecurity, PdmApiError, revokeLoginSession, saveLoginSecurityPolicy, type LoginSecurityDirectory, type LoginSecuritySession } from '../api'

const props = defineProps<{ token: string }>()
const data = ref<LoginSecurityDirectory>()
const singleSessionEnabled = ref(false), deviceBindingEnabled = ref(false)
const pending = ref(false), error = ref(''), notice = ref(''), query = ref('')
const users = computed(() => data.value?.users.filter(user => !query.value.trim() || user.username.includes(query.value.trim()) || user.devices.some(device => device.machine.includes(query.value.trim()))) ?? [])
const devices = computed(() => users.value.flatMap(user => user.devices.map(device => ({ ...device, username: user.username }))))
const sessions = computed(() => users.value.flatMap(user => user.sessions.map(session => ({ ...session, username: user.username }))).sort((a, b) => b.lastSeenAt.localeCompare(a.lastSeenAt)))
const dirty = computed(() => data.value && (singleSessionEnabled.value !== data.value.policy.singleSessionEnabled || deviceBindingEnabled.value !== data.value.policy.deviceBindingEnabled))
const typeName = (type: string) => type === 'desktop' ? '桌面客户端' : type === 'solidworks' ? 'SolidWorks插件' : '网页端'
const time = (value?: string) => value ? new Date(value).toLocaleString('zh-CN', { hour12: false }) : '—'
function status(session: LoginSecuritySession) {
  if (session.revoked || ((data.value?.policy.singleSessionEnabled || data.value?.policy.deviceBindingEnabled) && session.version !== data.value.policy.version)) return '已失效'
  return Date.now() - new Date(session.lastSeenAt).getTime() < 5 * 60_000 ? '在线' : '已离线'
}
async function run(action: () => Promise<void>) {
  pending.value = true; error.value = ''; notice.value = ''
  try { await action() }
  catch (failure) { error.value = failure instanceof PdmApiError ? failure.message : '操作未完成，请检查网络连接后重试。' }
  finally { pending.value = false }
}
async function load() {
  const result = await getLoginSecurity(props.token)
  data.value = result
  singleSessionEnabled.value = result.policy.singleSessionEnabled
  deviceBindingEnabled.value = result.policy.deviceBindingEnabled
}
async function save() {
  if (!data.value?.canEditPolicy || !dirty.value) return
  await run(async () => {
    const previous = data.value!.policy.version
    data.value!.policy = await saveLoginSecurityPolicy({ singleSessionEnabled: singleSessionEnabled.value, deviceBindingEnabled: deviceBindingEnabled.value }, props.token)
    notice.value = previous !== data.value!.policy.version ? '设置已保存，所有旧登录需要重新登录。电脑绑定须由管理员批准后生效，网页端不限制电脑。' : '设置已保存并立即生效，原电脑绑定和操作记录已保留。'
  })
}
async function deviceAction(username: string, id: string, approved: boolean) {
  if (!window.confirm(approved ? `确认将此电脑绑定给账号“${username}”？每个账号只能绑定一台电脑。` : `确认解除“${username}”的电脑绑定？该电脑上的客户端和插件会话将失效，业务编辑锁保留。`)) return
  await run(async () => { await approveLoginDevice(username, id, approved, props.token); await load(); notice.value = approved ? '电脑已绑定，用户可以重新登录。' : '电脑已解绑，相关登录会话已下线。' })
}
async function revoke(username: string, id: string) {
  if (!window.confirm(`确认将“${username}”的此登录会话下线？本地文件和业务编辑锁保留。`)) return
  await run(async () => { await revokeLoginSession(username, id, props.token); await load(); notice.value = '该登录会话已下线，自动登录凭证同时失效。' })
}
onMounted(() => void run(load))
</script>

<template>
  <section class="login-security" aria-label="登录安全设置">
    <header><h2>登录安全</h2><p>调试阶段两个开关默认关闭。开启后由服务器统一校验登录、自动登录和业务请求。</p></header>
    <p v-if="error" role="alert" class="error">{{ error }}</p>
    <p v-if="notice" role="status">{{ notice }}</p>
    <form v-if="data" @submit.prevent="save">
      <label class="policy"><input v-model="singleSessionEnabled" type="checkbox" role="switch" :disabled="pending || !data.canEditPolicy" /><span><strong>启用同端登录限制</strong><small>每个账号允许桌面客户端、SolidWorks插件、网页端各登录一次；重复登录拒绝新会话。同一浏览器多标签共用登录。</small></span></label>
      <label class="policy"><input v-model="deviceBindingEnabled" type="checkbox" role="switch" :disabled="pending || !data.canEditPolicy" /><span><strong>启用电脑绑定</strong><small>客户端与插件共用一台经管理员批准的电脑；网页端只限制登录会话，不限制电脑。设备凭证用于验证身份，MAC辅助核对。</small></span></label>
      <p class="hint">开启任一限制后，所有旧登录均需重新登录。关闭开关保留绑定资料；异常退出约5分钟后释放登录名额，业务编辑锁不自动解除。</p>
      <div class="actions"><button type="submit" :disabled="pending || !dirty || !data.canEditPolicy">保存设置</button><span v-if="!data.canEditPolicy">全系统开关仅平台管理员可修改；公司管理员可管理本公司电脑和登录。</span></div>
    </form>
    <div class="actions search"><label>搜索账号或电脑 <input v-model="query" type="search" /></label><button type="button" :disabled="pending" @click="run(load)">刷新记录</button></div>
    <h3>电脑绑定与待确认申请</h3>
    <p class="hint">目标电脑登录一次后会出现在此处。更换电脑需先解绑；重装系统或更换Windows用户后可能需要重新确认电脑凭证。</p>
    <div class="table-wrap"><table><thead><tr><th>账号</th><th>电脑名称 / MAC</th><th>状态</th><th>登记 / 批准时间</th><th>最近使用</th><th>操作</th></tr></thead><tbody>
      <tr v-for="device in devices" :key="device.username + device.id"><td>{{ device.username }}</td><td>{{ device.machine }}<small>{{ device.mac || '未读取到物理网卡地址' }}</small></td><td>{{ device.approved ? '已绑定' : '待确认' }}</td><td>{{ time(device.requestedAt) }}<small>{{ time(device.approvedAt) }}</small></td><td>{{ time(device.lastSeenAt) }}</td><td><button :disabled="pending" @click="deviceAction(device.username, device.id, !device.approved)">{{ device.approved ? '解除绑定' : '确认绑定' }}</button></td></tr>
      <tr v-if="!devices.length"><td colspan="6">暂无电脑记录；请先在目标电脑使用新版客户端或插件登录。</td></tr>
    </tbody></table></div>
    <h3>登录会话</h3>
    <div class="table-wrap"><table><thead><tr><th>账号</th><th>终端</th><th>电脑</th><th>状态</th><th>登录时间</th><th>最近活动</th><th>操作</th></tr></thead><tbody>
      <tr v-for="session in sessions" :key="session.id"><td>{{ session.username }}</td><td>{{ typeName(session.type) }}</td><td>{{ session.machine }}</td><td>{{ status(session) }}</td><td>{{ time(session.createdAt) }}</td><td>{{ time(session.lastSeenAt) }}</td><td><button :disabled="pending || session.revoked" @click="revoke(session.username, session.id)">强制下线</button></td></tr>
      <tr v-if="!sessions.length"><td colspan="7">暂无登录会话。</td></tr>
    </tbody></table></div>
    <p class="hint">开关、绑定、解绑和强制下线操作保存在全局审计中。</p>
  </section>
</template>

<style scoped>
.login-security { padding: 20px; min-width: 0; }
h2 { margin: 0 0 8px; } h3 { margin: 24px 0 10px; }
header p, .hint, small, .actions span { color: var(--pdm-text-soft); font-size: 13px; line-height: 1.6; }
form { padding: 16px; border: 1px solid var(--pdm-border); border-radius: 6px; }
.policy { display: flex; align-items: flex-start; gap: 12px; margin-bottom: 18px; }
.policy input { margin-top: 4px; width: 18px; height: 18px; accent-color: var(--pdm-accent, #1677ff); }
small { display: block; } .actions { display: flex; align-items: center; gap: 12px; flex-wrap: wrap; }
.search { margin-top: 20px; } .search input { min-height: 32px; border: 1px solid var(--pdm-border); border-radius: 4px; padding: 4px 8px; }
button { border: 1px solid var(--pdm-border); border-radius: 4px; padding: 7px 12px; background: var(--pdm-panel, white); color: inherit; cursor: pointer; }
button:disabled { opacity: .5; cursor: default; } .error { color: #c22; }
.table-wrap { overflow: auto; } table { width: 100%; border-collapse: collapse; font-size: 13px; }
th, td { text-align: left; padding: 10px; border-bottom: 1px solid var(--pdm-border); vertical-align: top; }
th { white-space: nowrap; } td { min-width: 85px; } .table-wrap button { white-space: nowrap; }
</style>
