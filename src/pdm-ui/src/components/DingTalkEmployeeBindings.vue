<script setup lang="ts">
import UserSelect from './UserSelect'
import { onMounted, ref } from 'vue'
import { readDingTalkEmployees, saveDingTalkEmployees } from '../api'
import type { DingTalkEmployees } from '../dingtalk'
import { userErrorMessage } from '../userErrorMessage'
const props = defineProps<{ token: string }>()
const emit = defineEmits<{ saved: [rowVersion: number] }>()
const view = ref<DingTalkEmployees>(), pending = ref(false), error = ref(''), notice = ref('')
async function load() {
  pending.value = true; error.value = ''
  try { view.value = await readDingTalkEmployees(props.token) }
  catch (cause) { error.value = userErrorMessage(cause instanceof Error ? cause.message : '员工绑定读取失败，请重试。') }
  finally { pending.value = false }
}
async function save() {
  if (!view.value || pending.value) return
  pending.value = true; error.value = ''; notice.value = ''
  try {
    view.value = await saveDingTalkEmployees(view.value.bindings, view.value.rowVersion, props.token)
    emit('saved', view.value.rowVersion); notice.value = '员工绑定已保存，员工可重新打开钉钉应用免登。'
  } catch (cause) { error.value = userErrorMessage(cause instanceof Error ? cause.message : '员工绑定保存失败，请刷新后重试。') }
  finally { pending.value = false }
}
onMounted(load)
</script>
<template>
  <section class="ding-employees" aria-label="钉钉员工账号绑定">
    <p>将当前企业的钉钉用户ID与在职UPLM账号一一绑定。可在角色管理授权「自助绑定本人钉钉」，员工从个人设置发起绑定。管理员手动绑定时，用户 ID 可从钉钉后台取得。</p>
    <p v-if="error" role="alert">{{ error }}</p><p v-if="notice" role="status">{{ notice }}</p>
    <template v-if="view">
      <div v-for="(binding, index) in view.bindings" :key="index" class="ding-employee-row">
        <el-input v-model="binding.userId" aria-label="钉钉用户ID" placeholder="钉钉用户ID" :disabled="pending" />
        <UserSelect v-model="binding.username" aria-label="绑定UPLM账号" placeholder="选择员工" filterable :disabled="pending"><el-option v-for="account in view.accounts" :key="account.username" :value="account.username" :label="account.displayName" /></UserSelect>
        <el-button class="ding-action" :disabled="pending" @click="view.bindings.splice(index, 1)">移除绑定</el-button>
      </div>
      <el-button class="ding-action" :disabled="pending || !view.corpId" @click="view.bindings.push({ corpId: view.corpId, userId: '', username: '' })">添加员工绑定</el-button>
      <el-button class="ding-action" type="primary" :loading="pending" @click="save">保存员工绑定</el-button>
    </template>
    <el-button class="ding-action" :disabled="pending" @click="load">刷新</el-button>
  </section>
</template>
<style scoped>
.ding-employees{display:grid;gap:12px}.ding-employee-row{display:grid;grid-template-columns:1fr 1fr auto;gap:8px}.ding-employees p{margin:0}@media(max-width:600px){.ding-employee-row{grid-template-columns:1fr}}
</style>
