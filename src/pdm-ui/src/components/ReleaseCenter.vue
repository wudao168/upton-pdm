<script setup lang="ts">
import { computed, ref } from 'vue'
import type { ReleasePackageSummary } from '../types'

const props = defineProps<{ releasePackage: ReleasePackageSummary | null; serialNumbers: string[]; username: string; pending: boolean; progress: number; error: string; canManage: boolean; canDecide: boolean }>()
const emit = defineEmits<{
  create: [number: string, changeNumber: string, changeReason: string, effectiveSerialFrom: string, effectiveSerialTo: string, processReviewer: string, approver: string]
  upload: [file: File]
  submit: []
  withdraw: []
  decide: [taskId: string, decision: 'Approved' | 'Rejected', comment: string]
}>()
const showCreate = ref(false)
const number = ref(`RP-${new Date().toISOString().slice(0, 10).replaceAll('-', '')}-${String(Date.now()).slice(-4)}`)
const changeNumber = ref(`ECN-${new Date().toISOString().slice(0, 10).replaceAll('-', '')}-${String(Date.now()).slice(-4)}`)
const changeReason = ref('')
const effectiveSerialFrom = ref(props.serialNumbers[0] ?? '未指定')
const effectiveSerialTo = ref('')
const processReviewer = ref(props.username)
const approver = ref(props.username)
const comment = ref('同意')
const currentTask = computed(() => props.releasePackage?.steps.find(step => step.id !== 'production-release' && step.status === 'current'))
const canPrepare = computed(() => !props.releasePackage || ['草稿', '已驳回', '发布失败'].includes(props.releasePackage.state))

function uploadSelected(event: Event) {
  const input = event.target as HTMLInputElement
  const file = input.files?.[0]
  if (file) emit('upload', file)
  input.value = ''
}
</script>

<template>
  <section class="pdm-panel pdm-manager-panel" aria-label="审批与生产发包">
    <div v-if="canManage && releasePackage?.state === '已发布'" class="pdm-manager-actions">
      <button type="button" class="pdm-primary-action" @click="showCreate = !showCreate">新建发布包</button>
    </div>

    <form v-if="canManage && (!releasePackage || showCreate)" class="pdm-form-grid pdm-release-create-form" @submit.prevent="emit('create', number, changeNumber, changeReason, effectiveSerialFrom, effectiveSerialTo, processReviewer, approver)">
      <label>发布包编号<input v-model.trim="number" required></label>
      <label>ECN编号<input v-model.trim="changeNumber" required></label>
      <label>生效起始序列号<input v-model.trim="effectiveSerialFrom" list="pdm-release-serials" required placeholder="输入或选择序列号"></label>
      <label>生效截止序列号（可选）<input v-model.trim="effectiveSerialTo" list="pdm-release-serials" placeholder="留空表示后续序列号"></label>
      <label>工艺审核账号<input v-model.trim="processReviewer" required></label>
      <label>批准账号<input v-model.trim="approver" required></label>
      <label class="pdm-release-reason">设变原因<textarea v-model.trim="changeReason" rows="3" maxlength="1000" required placeholder="说明增加、减少或修改物料的原因"></textarea></label>
      <datalist id="pdm-release-serials"><option v-for="serial in serialNumbers" :key="serial" :value="serial" /></datalist>
      <button type="submit" class="pdm-primary-action" :disabled="pending">创建草稿</button>
    </form>

    <template v-if="releasePackage && !showCreate">
      <div class="pdm-release-summary">
        <div><small>发布包</small><strong>{{ releasePackage.number }}</strong></div>
        <div><small>当前状态</small><strong>{{ releasePackage.state }}</strong></div>
        <div><small>ECN</small><strong>{{ releasePackage.changeNumber || '—' }}</strong></div>
        <div><small>生效序列号</small><strong>{{ releasePackage.effectiveSerialFrom || '—' }}{{ releasePackage.effectiveSerialTo ? ` ～ ${releasePackage.effectiveSerialTo}` : ' 起' }}</strong></div>
        <div><small>三套BOM版本</small><strong>S {{ releasePackage.standardBomRevision || '—' }} · N {{ releasePackage.nonStandardBomRevision || '—' }} · E {{ releasePackage.electricalBomRevision || '—' }}</strong></div>
        <div><small>生产目录</small><strong>{{ releasePackage.publishedPath || '审批通过后自动投放' }}</strong></div>
      </div>
      <p v-if="releasePackage.changeReason" class="pdm-release-change-reason"><strong>设变原因：</strong>{{ releasePackage.changeReason }}</p>

      <div v-if="canManage && ['工艺审核', '待批准'].includes(releasePackage.state)" class="pdm-manager-actions">
        <button type="button" class="pdm-secondary-action is-danger" :disabled="pending" @click="emit('withdraw')">撤回审批</button>
        <small>撤回后图档恢复为工作中，可修改后重新提交。</small>
      </div>

      <div class="pdm-approval-chain">
        <article v-for="step in releasePackage.steps" :key="step.id" :class="`is-${step.status}`">
          <span>{{ step.status === 'done' ? '✓' : step.status === 'current' ? '●' : '○' }}</span>
          <div><strong>{{ step.stage }}</strong><small>{{ step.assignee }} · {{ step.detail }}</small><em v-if="step.comment">{{ step.comment }}</em></div>
        </article>
      </div>

      <div v-if="canManage && canPrepare" class="pdm-release-preparation">
        <h3>发包资料</h3>
        <p>标准件、非标件和电气BOM已分别固化为XLSX；请上传至少一份PDF和一份DWG。</p>
        <div class="pdm-manager-actions">
          <label class="pdm-secondary-action pdm-file-button">上传PDF<input type="file" accept=".pdf" @change="uploadSelected"></label>
          <label class="pdm-secondary-action pdm-file-button">上传DWG<input type="file" accept=".dwg" @change="uploadSelected"></label>
          <button type="button" class="pdm-primary-action" :disabled="pending" @click="emit('submit')">{{ releasePackage.state === '已驳回' ? '重新提交审批' : '提交审批' }}</button>
        </div>
        <progress v-if="pending && progress > 0" :value="progress" max="100">{{ progress }}%</progress>
      </div>

      <div v-if="canDecide && currentTask" class="pdm-decision-box">
        <label>审批意见<textarea v-model.trim="comment" rows="3" maxlength="1000" /></label>
        <div class="pdm-manager-actions">
          <button type="button" class="pdm-secondary-action is-danger" :disabled="pending" @click="emit('decide', currentTask.id, 'Rejected', comment || '驳回')">驳回</button>
          <button type="button" class="pdm-primary-action" :disabled="pending" @click="emit('decide', currentTask.id, 'Approved', comment || '同意')">同意并流转</button>
        </div>
      </div>
      <p v-if="releasePackage.publishError" class="pdm-inline-error">发布失败：{{ releasePackage.publishError }}</p>
    </template>
    <p v-if="error" class="pdm-inline-error">{{ error }}</p>
  </section>
</template>
