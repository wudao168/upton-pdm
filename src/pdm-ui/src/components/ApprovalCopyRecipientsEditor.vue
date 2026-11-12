<script setup lang="ts">
import DepartmentSelectField from './DepartmentSelectField.vue'
import { computed } from 'vue'
import UserSelect from './UserSelect'
import { notificationDepartments, notificationDepartmentPath } from '../workflowNotifications'
import { projectPersonnelRoles } from '../projectPersonnelRoles'
import type { ApprovalCopyRecipient, OrganizationDirectory, RolePermissionSettings } from '../types'
const props=defineProps<{modelValue:ApprovalCopyRecipient[];directory?:OrganizationDirectory;roles?:Pick<RolePermissionSettings,'role'|'name'>[];companyId?:string;disabled?:boolean}>()
const emit=defineEmits<{'update:modelValue':[value:ApprovalCopyRecipient[]]}>()
const sources: Array<[ApprovalCopyRecipient['source'],string]>=[['User','指定人员'],['Submitter','发起人本人'],['SubmitterDepartment','发起人部门'],['ParentDepartment','发起人上级部门'],['SubmitterDivision','发起人事业部'],['Department','指定接收部门'],['Manager','发起人部门负责人'],['FixedManager','指定部门负责人'],['Role','当前公司角色'],['ProjectRole','项目关联角色'],['ProjectMembers','项目全部关联人员']]
const departments=computed(()=>props.companyId?notificationDepartments(props.directory,props.companyId):(props.directory?.units??[]).filter(unit=>unit.isActive))
const users=computed(()=>(props.directory?.users??[]).filter(user=>user.isActive&&(!props.companyId||user.companyId===props.companyId||!user.companyId&&props.directory?.memberships.some(member=>member.username===user.username&&departments.value.some(unit=>unit.id===member.unitId)))))
const departmentScope=(source:string)=>['Department','SubmitterDepartment','ParentDepartment','SubmitterDivision'].includes(source)
function update(index:number,patch:Partial<ApprovalCopyRecipient>){emit('update:modelValue',props.modelValue.map((row,i)=>i===index?{...row,...patch}:row))}
function source(index:number,value:ApprovalCopyRecipient['source']){update(index,{source:value,target:'',roleCode:null,includeSubdepartments:false})}
</script>
<template>
 <div class="copy-recipients">
  <div v-for="(row,index) in modelValue" :key="index" class="copy-recipient-row">
   <label>抄送范围<select :value="row.source" :aria-label="`抄送对象${index+1}范围`" :disabled="disabled" @change="source(index,($event.target as HTMLSelectElement).value as ApprovalCopyRecipient['source'])"><option v-for="[value,label] in sources" :key="value" :value="value">{{label}}</option></select></label>
   <label v-if="row.source==='User'">指定人员<UserSelect :model-value="row.target" :aria-label="`抄送对象${index+1}人员`" :disabled="disabled" @update:model-value="update(index,{target:$event})"><option value="" disabled>请选择人员</option><option v-for="user in users" :key="user.username" :value="user.username">{{ user.displayName }}</option></UserSelect></label>
   <label v-if="['Department','FixedManager'].includes(row.source)">接收部门<DepartmentSelectField :names="notificationDepartmentPath(directory, row.target ?? '').split(' / ')"><select :value="row.target" :aria-label="`抄送对象${index+1}部门`" :disabled="disabled" @change="update(index,{target:($event.target as HTMLSelectElement).value})"><option value="" disabled>请选择部门</option><option v-for="unit in departments" :key="unit.id" :value="unit.id">{{notificationDepartmentPath(directory,unit.id)}}</option></select></DepartmentSelectField></label>
   <label v-if="row.source==='Role'">角色<select :value="row.target" :aria-label="`抄送对象${index+1}角色`" :disabled="disabled" @change="update(index,{target:($event.target as HTMLSelectElement).value})"><option value="" disabled>请选择角色</option><option v-for="role in roles" :key="role.role" :value="role.role">{{role.name}}</option></select></label>
   <label v-if="row.source==='ProjectRole'">项目角色<select :value="row.target" :aria-label="`抄送对象${index+1}项目角色`" :disabled="disabled" @change="update(index,{target:($event.target as HTMLSelectElement).value})"><option value="" disabled>请选择项目角色</option><option v-for="role in projectPersonnelRoles" :key="role.key" :value="role.key">{{role.label}}</option></select></label>
   <label v-if="departmentScope(row.source)">部门内角色<select :value="row.roleCode??''" :aria-label="`抄送对象${index+1}角色`" :disabled="disabled" @change="update(index,{roleCode:($event.target as HTMLSelectElement).value||null})"><option value="">全部有效成员</option><option v-for="role in roles" :key="role.role" :value="role.role">{{role.name}}</option></select></label>
   <label v-if="departmentScope(row.source)" class="copy-check"><input type="checkbox" :checked="row.includeSubdepartments" :disabled="disabled" :aria-label="`抄送对象${index+1}包含下级部门`" @change="update(index,{includeSubdepartments:($event.target as HTMLInputElement).checked})" />包含下级部门</label>
   <button type="button" class="pdm-secondary-action" :disabled="disabled" :aria-label="`删除抄送对象${index+1}`" @click="emit('update:modelValue',modelValue.filter((_,i)=>i!==index))">删除</button>
  </div>
  <p v-if="!modelValue.length" class="copy-empty">暂无抄送对象</p>
  <button type="button" class="pdm-secondary-action" :disabled="disabled||modelValue.length>=100" @click="emit('update:modelValue',[...modelValue,{source:'User',target:''}])">添加抄送对象</button>
  <p class="copy-hint">多组对象合并去重；部门负责人含主负责人和协同负责人。项目角色按当前项目及所属主项目分工匹配，查看详情仍遵守项目权限。</p>
 </div>
</template>
<style scoped>
.copy-recipients{display:grid;gap:10px;min-width:0}.copy-recipient-row{display:flex;flex-wrap:wrap;gap:8px;align-items:center;padding:10px;border:1px solid var(--pdm-border);border-radius:6px}.copy-recipient-row>label{display:grid;gap:5px;flex:1 1 170px;min-width:0;text-align:center}.copy-recipient-row select{min-width:0;width:100%;height:32px;padding:0 8px;color:var(--pdm-text);background:var(--pdm-surface);border:1px solid var(--pdm-border);border-radius:5px;text-align:center}.copy-recipient-row>.copy-check{display:flex;align-items:center;justify-content:center;flex:1 1 120px;white-space:nowrap}.copy-recipient-row>.copy-check input{width:auto}.copy-hint{color:var(--pdm-muted);font-size:11px;line-height:1.6;margin:0}.copy-empty{text-align:center;color:var(--pdm-muted)}
</style>
