<script setup lang="ts">
import type { BonusIndicator, BonusDepartment } from '../projectBonus'
import { bonusId } from '../projectBonus'
const props = defineProps<{ modelValue: BonusIndicator[]; departments: BonusDepartment[] }>()
const emit = defineEmits<{ 'update:modelValue': [BonusIndicator[]] }>()
function add() { emit('update:modelValue', [...props.modelValue, { id: bonusId(), name: '新指标', maxScore: 100, weight: 10, guidance: '', evidence: '', departments: [{ departmentKey: props.departments[0]?.key ?? '', weight: 100 }] }]) }
</script>
<template>
  <div class="bonus-indicators">
    <p>指标权重合计 <strong :class="{ 'bonus-error': modelValue.reduce((s, i) => s + Number(i.weight), 0) !== 100 }">{{ modelValue.reduce((s, i) => s + Number(i.weight), 0) }}%</strong> · 每项满分与权重独立设置</p>
    <details v-for="(item, index) in modelValue" :key="item.id" class="bonus-indicator" :open="!item.guidance">
      <summary>{{ index + 1 }} · {{ item.name }} <span>{{ item.weight }}% · 满分 {{ item.maxScore }}</span></summary>
      <div class="bonus-form-grid">
        <label>指标名称<input v-model="item.name" maxlength="120" /></label>
        <label>满分<el-input-number v-model="item.maxScore" :min="1" :max="1000" :controls="false" /></label>
        <label>权重（%）<el-input-number v-model="item.weight" :min="0.01" :max="100" :precision="2" :controls="false" /></label>
      </div>
      <label>评分标准 / 扣分规则<textarea v-model="item.guidance" rows="3" maxlength="2000" /></label>
      <label>证据要求<textarea v-model="item.evidence" rows="2" maxlength="2000" /></label>
      <div class="bonus-table-scroll"><table><thead><tr><th>评分部门</th><th>该指标内权重（%）</th><th>操作</th></tr></thead><tbody>
        <tr v-for="(department, d) in item.departments" :key="d"><td><select v-model="department.departmentKey"><option v-for="option in departments" :key="option.key" :value="option.key">{{ option.name }}</option></select></td><td><el-input-number v-model="department.weight" :min="0.01" :max="100" :controls="false" :precision="2" /></td><td><button type="button" class="pdm-secondary-action" @click="item.departments.splice(d, 1)">移除</button></td></tr>
      </tbody><tfoot><tr><th>合计</th><td>{{ item.departments.reduce((s, d) => s + Number(d.weight), 0) }}%</td><td><button type="button" class="pdm-secondary-action" @click="item.departments.push({ departmentKey: departments[0]?.key ?? '', weight: 0 })">增加评分部门</button></td></tr></tfoot></table></div>
      <button type="button" class="pdm-secondary-action" @click="emit('update:modelValue', modelValue.filter(i => i.id !== item.id))">删除该指标</button>
    </details>
    <button type="button" class="pdm-secondary-action" @click="add">添加考核指标</button>
  </div>
</template>
