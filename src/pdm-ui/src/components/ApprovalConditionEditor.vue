<script setup lang="ts">
import { computed } from 'vue'
import { conditionFields, conditionOperators, operators } from '../approvalConditions'
import type { FieldCondition } from '../approvalConditions'
import type { ApprovalWorkflowStepTemplate, OrganizationDirectory, RolePermissionSettings } from '../types'
const props = defineProps<{ step: Pick<ApprovalWorkflowStepTemplate, 'conditions'>; flowCode: string; disabled: boolean; directory?: OrganizationDirectory; roles: Pick<RolePermissionSettings, 'role' | 'name'>[] }>()
const fields = computed(() => conditionFields(props.flowCode))
const definition = (rule: FieldCondition) => fields.value.find(item => item.key === rule.field)
function choices(rule: FieldCondition): [string, string][] | undefined {
  if (rule.field === 'SubmitterRole') return props.roles.map(role => [role.role, role.name])
  if (rule.field === 'SubmitterDepartment') return props.directory?.units.filter(unit => unit.isActive).map(unit => [unit.id, unit.name]) ?? []
  if (rule.field === 'Submitter' && ['Eq','Ne'].includes(rule.operator)) return props.directory?.users.filter(user => user.isActive).map(user => [user.username, user.displayName]) ?? []
  return definition(rule)?.options
}
function add() {
  props.step.conditions ??= { match: 'All', rules: [] }
  props.step.conditions.rules.push({ field: props.flowCode.startsWith('expense-') ? 'TotalAmount' : 'ProjectCode', operator: props.flowCode.startsWith('expense-') ? 'Gt' : 'Eq', value: '' })
}
function remove(index: number) { props.step.conditions?.rules.splice(index, 1) }
function change(rule: FieldCondition) { rule.operator = 'Eq'; rule.value = '' }
</script>
<template>
  <fieldset class="condition-editor" :disabled="disabled">
    <legend>字段条件</legend>
    <label v-if="step.conditions">条件关系<select v-model="step.conditions.match" aria-label="条件关系"><option value="All">全部满足</option><option value="Any">任一满足</option></select></label>
    <div v-for="(rule, index) in step.conditions?.rules ?? []" :key="index" class="condition-rule">
      <label>条件字段<select v-model="rule.field" aria-label="条件字段" @change="change(rule)"><option v-for="field in fields" :key="field.key" :value="field.key">{{ field.label }}</option></select></label>
      <label>比较方式<select v-model="rule.operator" aria-label="比较方式"><option v-for="op in operators(definition(rule)?.type)" :key="op" :value="op">{{ conditionOperators[op] }}</option></select></label>
      <label v-if="!['Empty','NotEmpty'].includes(rule.operator)">比较值
        <select v-if="choices(rule)" v-model="rule.value" aria-label="条件比较值"><option value="" disabled>请选择</option><option v-for="[value,label] in choices(rule)" :key="value" :value="value">{{ label }}</option></select>
        <input v-else :value="rule.value" @input="rule.value = ($event.target as HTMLInputElement).value" aria-label="条件比较值" :type="definition(rule)?.type === 'number' ? 'number' : definition(rule)?.type === 'date' ? 'date' : 'text'" step="any" maxlength="500" />
      </label>
      <button type="button" class="pdm-secondary-action" aria-label="删除条件" @click="remove(index)">删除条件</button>
    </div>
    <button type="button" class="pdm-secondary-action" :disabled="(step.conditions?.rules.length ?? 0) >= 10" @click="add">添加条件</button>
    <small>按表单字段判断本分支。多个分支命中时只执行优先级最高的一条；均未命中则执行其他情况。</small>
  </fieldset>
</template>
<style scoped>
.condition-editor { display:grid;gap:10px;border:1px solid var(--pdm-border);padding:10px;min-width:0 }
.condition-rule { display:flex;align-items:end;gap:8px;flex-wrap:wrap }
label { display:grid;gap:4px;min-width:0;flex:1 1 130px } input,select { width:100%;min-width:0;box-sizing:border-box } small {color:var(--pdm-muted)}
</style>
