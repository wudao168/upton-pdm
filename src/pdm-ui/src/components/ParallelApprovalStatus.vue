<script setup lang="ts">
import DepartmentName from './DepartmentName.vue'
import PersonName from './PersonName.vue'
import { useUserDisplayName as usePersonDisplayName } from '../userDisplay'
const displayUserName = usePersonDisplayName()
import type { ApprovalParticipants } from '../approvalParticipants'
defineProps<{ departments?: ApprovalParticipants['departments'] }>()
</script>
<template>
  <div v-if="departments?.length" class="parallel-approval-status" aria-label="并行部门审批记录"><table><thead><tr><th>审批部门</th><th>处理方式</th><th>状态 / 待处理人</th><th>审批意见</th></tr></thead><tbody><tr v-for="department in departments" :key="department.unitId"><td><DepartmentName :name="department.name" /></td><td>{{ department.requireAll ? '部门全员通过' : '部门任一人通过' }}</td><td>{{ department.rejectedBy ? '已退回' : department.complete ? '已通过' : '待处理' }}<small v-if="!department.complete && !department.rejectedBy">{{ department.members.filter(m => !department.signatures.some(s => s.actor.toLowerCase() === m.toLowerCase())).map(m => displayUserName(m)).join('、') }}</small></td><td><p v-for="(signature, i) in department.signatures" :key="i"><PersonName :name="displayUserName(signature.actor)" /> · {{ signature.comment || '同意' }} · {{ new Date(signature.signedAt).toLocaleString('zh-CN') }}</p><p v-if="department.rejectedBy">{{ department.rejectedBy }} · {{ department.rejection }}</p><span v-if="!department.signatures.length && !department.rejectedBy">—</span></td></tr></tbody></table></div>
</template>
<style scoped>
.parallel-approval-status{overflow:auto;width:100%;margin:8px 0}.parallel-approval-status table{width:100%;border-collapse:collapse;font-size:12px}.parallel-approval-status th,.parallel-approval-status td{text-align:center!important;vertical-align:middle!important;padding:8px;border-bottom:1px solid var(--el-border-color-lighter)}.parallel-approval-status small{display:block;color:var(--el-text-color-secondary)}.parallel-approval-status p{margin:3px}
</style>
