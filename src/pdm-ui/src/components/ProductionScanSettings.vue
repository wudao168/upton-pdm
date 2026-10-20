<script setup lang="ts">
import { ref, watch } from 'vue'
import { readProductionScanOptions, readProductionScanSettings, saveProductionScanSettings } from '../api'
import { productionPosts, productionRequestId, type ProductionScanCatalog } from '../productionTracking'
import { userErrorMessage } from '../userErrorMessage'
const props = defineProps<{ token: string }>()
const catalog = ref<ProductionScanCatalog>(), pending = ref(false), error = ref(''), notice = ref('')
const options = ref<Awaited<ReturnType<typeof readProductionScanOptions>>>({ users: [], roles: [] })
async function load() {
  pending.value = true; error.value = ''; notice.value = ''
  try { const values = await Promise.all([readProductionScanSettings(props.token), readProductionScanOptions(props.token)]); catalog.value = values[0]; options.value = values[1] }
  catch (cause) { error.value = userErrorMessage(cause instanceof Error ? cause.message : '') }
  finally { pending.value = false }
}
watch(() => props.token, () => { void load() }, { immediate: true })
function addStation() { catalog.value?.workstations.push({ id: productionRequestId(), name: '', resourceGroup: '', isActive: true }) }
function addBinding() { catalog.value?.bindings.push({ id: productionRequestId(), subjectType: 'User', subject: '', post: 'Reception', workstationIds: [], isActive: true }) }
async function save() {
  if (!catalog.value || pending.value) return
  pending.value = true; error.value = ''; notice.value = ''
  try {
    catalog.value = await saveProductionScanSettings({ workstations: catalog.value.workstations, bindings: catalog.value.bindings, expectedRowVersion: catalog.value.rowVersion }, props.token)
    notice.value = '扫码岗位及工位配置已保存，将用于下一次扫码和数量确认。'
  } catch (cause) { error.value = userErrorMessage(cause instanceof Error ? cause.message : '') }
  finally { pending.value = false }
}
</script>
<template>
  <section class="production-scan-settings pdm-panel" aria-label="生产扫码设置">
    <header><h2>生产扫码岗位与工位</h2><button type="button" class="pdm-secondary-action" :disabled="pending" @click="load">刷新配置</button></header>
    <p>按当前公司配置账号或角色可办理的岗位及工位，不自动绑定任何账号。绑定仍须具备相应生产操作和项目内容查看权限。</p>
    <p>个人绑定优先于角色绑定；停用个人绑定时，该账号不会继承角色绑定。机加工位的资源组须与批次排程一致。</p>
    <p v-if="error" role="alert" class="production-scan-settings__error">{{ error }}</p><p v-if="notice" role="status">{{ notice }}</p>
    <form v-if="catalog" @submit.prevent="save"><fieldset :disabled="pending">
      <section aria-label="工位配置"><header><h3>工位</h3><button type="button" class="pdm-secondary-action" @click="addStation">新增工位</button></header>
        <div v-for="(station, index) in catalog.workstations" :key="station.id" class="production-scan-settings__station">
          <label>工位名称<input v-model.trim="station.name" :aria-label="`第${index + 1}个工位名称`" maxlength="80" required></label>
          <label>排程资源组<input v-model.trim="station.resourceGroup" :aria-label="`第${index + 1}个工位资源组`" maxlength="80" placeholder="机加工位必填，如CNC组；其他岗位可留空"></label>
          <label class="production-scan-settings__check"><input v-model="station.isActive" type="checkbox">启用</label>
          <button type="button" class="pdm-secondary-action" @click="catalog.workstations.splice(index, 1)">删除工位</button>
        </div><p v-if="!catalog.workstations.length">尚未配置工位，请先新增实际使用的工位。</p>
      </section>
      <section aria-label="岗位绑定"><header><h3>账号／角色绑定</h3><button type="button" class="pdm-secondary-action" @click="addBinding">新增绑定</button></header>
        <div v-for="(binding, index) in catalog.bindings" :key="binding.id" class="production-scan-settings__binding">
          <label>绑定对象<select v-model="binding.subjectType" :aria-label="`第${index + 1}条绑定对象类型`" @change="binding.subject = ''"><option value="User">指定账号</option><option value="Role">指定角色</option></select></label>
          <label>{{ binding.subjectType === 'User' ? '账号' : '角色' }}<select v-model="binding.subject" :aria-label="`第${index + 1}条绑定账号或角色`" required><option value="">请选择</option><template v-if="binding.subjectType === 'User'"><option v-for="user in options.users" :key="user.username" :value="user.username">{{ user.displayName }}（{{ user.username }}）</option></template><template v-else><option v-for="role in options.roles" :key="role.role" :value="role.role">{{ role.name }}</option></template></select></label>
          <label>岗位<select v-model="binding.post" :aria-label="`第${index + 1}条绑定岗位`"><option v-for="(label, value) in productionPosts" :key="value" :value="value">{{ label }}</option></select></label>
          <fieldset class="production-scan-settings__stations"><legend>允许办理的工位</legend><label v-for="station in catalog.workstations" :key="station.id" class="production-scan-settings__check"><input v-model="binding.workstationIds" type="checkbox" :value="station.id">{{ station.name || '未命名工位' }}{{ station.resourceGroup ? ` · ${station.resourceGroup}` : '' }}</label><span v-if="!catalog.workstations.length">请先新增工位</span></fieldset>
          <div class="production-scan-settings__binding-actions"><label class="production-scan-settings__check"><input v-model="binding.isActive" type="checkbox">启用绑定</label><button type="button" class="pdm-secondary-action" @click="catalog.bindings.splice(index, 1)">删除绑定</button></div>
        </div><p v-if="!catalog.bindings.length">尚未配置岗位绑定。未绑定的账号只能查看生产记录，不能确认流转。</p>
      </section>
      <button type="submit" class="pdm-primary-action">{{ pending ? '正在保存…' : '保存扫码配置' }}</button>
    </fieldset></form>
  </section>
</template>
<style scoped>
.production-scan-settings{padding:15px;min-width:0;overflow:auto}.production-scan-settings header{display:flex;align-items:center;justify-content:space-between;gap:8px}.production-scan-settings h2{font-size:17px}.production-scan-settings h3{font-size:15px}.production-scan-settings p{font-size:12px;color:var(--pdm-muted);line-height:1.6}.production-scan-settings label{display:grid;gap:5px;font-size:12px;min-width:0}.production-scan-settings input:not([type=checkbox]),.production-scan-settings select{width:100%;height:32px;box-sizing:border-box;min-width:0;background:var(--pdm-surface);color:var(--pdm-text);border:1px solid var(--pdm-border);border-radius:5px;padding:0 8px}.production-scan-settings fieldset{border:0;padding:0;margin:0;min-width:0}.production-scan-settings section{margin-bottom:18px}.production-scan-settings__station{display:grid;grid-template-columns:minmax(0,1fr) minmax(0,2fr) auto auto;gap:10px;align-items:end;padding:10px 0;border-bottom:1px solid var(--pdm-border)}.production-scan-settings__binding{display:grid;grid-template-columns:minmax(0,1fr) minmax(0,2fr) minmax(0,1fr);gap:10px;padding:12px;margin:10px 0;border:1px solid var(--pdm-border);border-radius:6px}.production-scan-settings .production-scan-settings__check{display:flex;align-items:center;gap:6px}.production-scan-settings__check input{width:15px;height:15px;margin:0}.production-scan-settings .production-scan-settings__stations{grid-column:1/-1;display:flex;flex-wrap:wrap;gap:12px;line-height:2}.production-scan-settings__binding-actions{grid-column:1/-1;display:flex;justify-content:space-between;align-items:center}.production-scan-settings .production-scan-settings__error{color:var(--pdm-danger)}@media(max-width:700px){.production-scan-settings__station,.production-scan-settings__binding{grid-template-columns:1fr}.production-scan-settings__station{gap:8px}.production-scan-settings header{flex-wrap:wrap}}
</style>
