<script setup lang="ts">
import { ref } from 'vue'
import { ElMessageBox } from 'element-plus'
import { ElMessage } from '../statusMessage'
import { setEngineeringKitActive } from '../api'
import type { EngineeringKit } from '../types'
import { userErrorMessage } from '../userErrorMessage'

const props = defineProps<{ kit: EngineeringKit; token: string; canManage: boolean; disabled?: boolean; link?: boolean }>()
const emit = defineEmits<{ changed: [kit: EngineeringKit] }>()
const saving = ref(false)
async function toggle() {
  if (!props.canManage || props.disabled || saving.value) return
  saving.value = true
  try {
    const activate = props.kit.isActive === false
    try {
      await ElMessageBox.confirm(activate ? '恢复启用后，可再次引用此套件。' : '停用后禁止新增BOM引用，已有BOM引用、套件版本及附件均保留。',
        `${activate ? '恢复启用' : '停用'}套件 ${props.kit.code || props.kit.name}`, { type: 'warning', confirmButtonText: activate ? '确认启用' : '确认停用', cancelButtonText: '取消' })
    } catch { return }
    const saved = await setEngineeringKitActive(props.kit.id, activate, props.kit.rowVersion, props.token)
    emit('changed', saved)
    ElMessage.success(activate ? '套件已恢复启用' : '套件已停用')
  } catch (error) { ElMessage.error(userErrorMessage(error instanceof Error ? error.message : '套件启停状态更新失败，请刷新后重试')) }
  finally { saving.value = false }
}
</script>

<template>
  <el-button v-if="canManage" :link="link" :type="kit.isActive === false ? 'primary' : 'danger'" :disabled="disabled || saving" :loading="saving" @click="toggle">{{ kit.isActive === false ? '恢复启用' : '停用' }}</el-button>
</template>
