<script setup lang="ts">
import { nextTick, ref } from 'vue'
import { ElInputNumber } from 'element-plus'
import { parseQuantityInput, quantityInputText } from '../quantityInput'

defineOptions({ inheritAttrs: false })
const props = withDefaults(defineProps<{ isQuantity?: boolean }>(), { isQuantity: true })
const model = defineModel<number | null>()
const control = ref<InstanceType<typeof ElInputNumber>>()
const typing = ref(false)
const format = (value: string | number) => typing.value ? String(value) : quantityInputText(value)
function finishTyping() {
  typing.value = false
  if (!props.isQuantity) return
  void nextTick(() => {
    const input = (control.value?.$el as HTMLElement | undefined)?.querySelector('input')
    if (input) input.value = quantityInputText(input.value)
  })
}
function keydown(event: KeyboardEvent) {
  if (event.key === 'ArrowUp' || event.key === 'ArrowDown') finishTyping()
}
function mousedown(event: MouseEvent) {
  if ((event.target as Element).closest('.el-input-number__increase, .el-input-number__decrease')) finishTyping()
}
</script>

<template>
  <ElInputNumber ref="control" v-bind="$attrs" v-model="model" :step="1" :formatter="props.isQuantity ? format : undefined" :parser="props.isQuantity ? parseQuantityInput : undefined" :inputmode="props.isQuantity ? 'decimal' : undefined"
    @input.capture="typing = true" @keydown.capture="keydown" @mousedown.capture="mousedown" @blur="finishTyping" />
</template>
