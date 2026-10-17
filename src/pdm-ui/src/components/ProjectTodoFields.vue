<script setup lang="ts">
import { ElDatePicker, ElOption, ElSelect } from 'element-plus'
import type { PdmUser } from '../types'
withDefaults(defineProps<{ users: PdmUser[]; pending?: boolean; description?: string }>(), { description: '保存时将上述内容推送给指定人员' })
const dueDate = defineModel<string | undefined>('dueDate')
const recipients = defineModel<string[]>('recipients', { required: true })
</script>
<template>
  <section class="pdm-project-todo-settings" aria-label="待办设置">
    <header><strong>待办设置</strong><span>{{ description }}</span></header>
    <div>
      <label class="pdm-dialog-field">截止日期（可选）<el-date-picker v-model="dueDate" type="date" value-format="YYYY-MM-DD" placeholder="不设截止日期" aria-label="待办截止日期" :disabled="pending" /></label>
      <label class="pdm-dialog-field">接收人<el-select v-model="recipients" multiple filterable collapse-tags collapse-tags-tooltip placeholder="选择接收人" aria-label="待办接收人" :disabled="pending"><el-option v-for="user in users" :key="user.username" :label="user.displayName" :value="user.username" /></el-select></label>
    </div>
  </section>
</template>
