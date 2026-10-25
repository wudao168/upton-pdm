import { flushPromises, mount } from '@vue/test-utils'
import { afterEach, describe, expect, it } from 'vitest'
import { defineComponent, nextTick, ref } from 'vue'
import QuantityInputNumber from '../src/components/QuantityInputNumber.vue'
import { vQuantity } from '../src/quantityInput'

const wrappers: Array<ReturnType<typeof mount>> = []
afterEach(() => { wrappers.splice(0).forEach(wrapper => wrapper.unmount()); document.body.innerHTML = '' })
function quantity(value: number | null = 1, props: Record<string, unknown> = {}) {
  const model = ref<number | null | undefined>(value)
  const { isQuantity, ...attrs } = props
  const wrapper = mount(QuantityInputNumber, { attachTo: document.body, props: {
    modelValue: value, isQuantity: isQuantity !== false,
    'onUpdate:modelValue': (next: number | null | undefined) => { model.value = next; void wrapper.setProps({ modelValue: next }) },
  }, attrs: { min: .0001, precision: 4, ...attrs } })
  wrappers.push(wrapper)
  return { wrapper, model, input: wrapper.get('input') }
}

describe('数量输入按1增减且保留手输小数', () => {
  it('初始整数不补零，外部重新读取小数时保留原精度和数值', async () => {
    const { wrapper, input, model } = quantity()
    expect((input.element as HTMLInputElement).value).toBe('1')
    expect(model.value).toBe(1)
    await wrapper.setProps({ modelValue: 1.2345 })
    expect((input.element as HTMLInputElement).value).toBe('1.2345')
  })
  it('上下键每次加减1，已有小数不被对齐到最小值或整数', async () => {
    const { input, model } = quantity(1.25)
    await input.trigger('keydown', { key: 'ArrowUp', code: 'ArrowUp' }); await flushPromises()
    expect(model.value).toBe(2.25)
    expect((input.element as HTMLInputElement).value).toBe('2.25')
    await input.trigger('keydown', { key: 'ArrowDown', code: 'ArrowDown' }); await flushPromises()
    expect(model.value).toBe(1.25)
  })
  it('加减按钮每次加减1', async () => {
    const { wrapper, model, input } = quantity(1.25)
    await wrapper.get('.el-input-number__increase').trigger('mousedown', { button: 0 }); document.dispatchEvent(new MouseEvent('mouseup')); await flushPromises()
    expect(model.value).toBe(2.25)
    expect((input.element as HTMLInputElement).value).toBe('2.25')
    await wrapper.get('.el-input-number__decrease').trigger('mousedown', { button: 0 }); document.dispatchEvent(new MouseEvent('mouseup')); await flushPromises()
    expect(model.value).toBe(1.25)
  })
  it('逐字输入1.05及小数点后零时不吞字符，失焦只去掉末尾零', async () => {
    const { input, model } = quantity()
    ;(input.element as HTMLInputElement).focus()
    for (const text of ['1', '1.', '1.0', '1.05', '1.0500']) {
      ;(input.element as HTMLInputElement).value = text
      await input.trigger('input'); await nextTick()
      expect((input.element as HTMLInputElement).value).toBe(text)
    }
    await input.trigger('change'); (input.element as HTMLInputElement).blur(); await flushPromises()
    expect(model.value).toBe(1.05)
    expect((input.element as HTMLInputElement).value).toBe('1.05')
  })
  it('输入整数尾零后失焦仍显示整数，不改变值', async () => {
    const { input, model } = quantity()
    ;(input.element as HTMLInputElement).focus()
    await input.setValue('1.0000'); await input.trigger('change'); (input.element as HTMLInputElement).blur(); await flushPromises()
    expect(model.value).toBe(1)
    expect((input.element as HTMLInputElement).value).toBe('1')
  })
  it('粘贴非数字文本不会被截断后误存成数量，手输.5仍有效', async () => {
    const { input, model } = quantity()
    await input.setValue('1abc'); await input.trigger('change'); await flushPromises()
    expect(model.value).toBeNull()
    ;(input.element as HTMLInputElement).focus()
    for (const text of ['.', '.5']) {
      ;(input.element as HTMLInputElement).value = text
      await input.trigger('input'); await nextTick()
      expect((input.element as HTMLInputElement).value).toBe(text)
    }
    await input.trigger('change'); (input.element as HTMLInputElement).blur(); await flushPromises()
    expect(model.value).toBe(.5)
  })
  it('空值不变成0或1，保留已有最大值、最小值和精度约束', async () => {
    const { input, model } = quantity(null, { max: .5 })
    expect((input.element as HTMLInputElement).value).toBe('')
    await input.setValue('0.123456'); await input.trigger('change'); await flushPromises()
    expect(model.value).toBe(.1235)
    await input.trigger('keydown', { key: 'ArrowUp', code: 'ArrowUp' }); await flushPromises()
    expect(model.value).toBe(.5)
    await input.trigger('keydown', { key: 'ArrowDown', code: 'ArrowDown' }); await flushPromises()
    expect(model.value).toBe(.0001)
  })
  it('禁用和只读数量不会被上下键修改', async () => {
    for (const props of [{ disabled: true }, { readonly: true }]) {
      const { input, model } = quantity(1.25, props)
      await input.trigger('keydown', { key: 'ArrowUp', code: 'ArrowUp' }); await flushPromises()
      expect(model.value).toBe(1.25)
    }
  })
  it('预算中的工时复用字段保留原有固定精度', async () => {
    const { input, model } = quantity(1, { isQuantity: false, precision: 2 })
    expect((input.element as HTMLInputElement).value).toBe('1.00')
    await input.setValue('0.50'); await input.trigger('change'); await flushPromises()
    expect(model.value).toBe(.5)
    expect((input.element as HTMLInputElement).value).toBe('0.50')
  })
  it('原生数量框只规范显示，保留上下限、小数校验、业务值和正在编辑的文本', async () => {
    const model = ref(1)
    const fixture = defineComponent({ directives: { quantity: vQuantity }, setup: () => ({ model }), template: '<input v-quantity v-model.number="model" type="number" min="0.0001" max="10" step="any" required>' })
    const wrapper = mount(fixture, { attachTo: document.body }); wrappers.push(wrapper)
    const input = wrapper.get('input'), element = input.element as HTMLInputElement
    element.focus(); element.value = '1.0500'; await input.trigger('input')
    expect(element.value).toBe('1.0500')
    element.blur(); await flushPromises()
    expect(element.value).toBe('1.05'); expect(model.value).toBe(1.05)
    expect(element.checkValidity()).toBe(true)
    expect([element.min, element.max, element.step, element.required]).toEqual(['0.0001', '10', 'any', true])
    await input.setValue('0'); expect(element.validity.rangeUnderflow).toBe(true)
    await input.setValue(''); expect(element.validity.valueMissing).toBe(true)
  })
})
