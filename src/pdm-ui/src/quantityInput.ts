import type { ObjectDirective } from 'vue'

export function quantityInputText(value: string | number | null | undefined): string {
  const text = String(value ?? '')
  return /^[+-]?\d+(?:\.\d+)?$/.test(text)
    ? text.replace(/(\.\d*?[1-9])0+$|\.0+$/, '$1').replace(/^-0$/, '0')
    : text
}

export function parseQuantityInput(value: string): string {
  return /^[+-]?\d*(?:\.\d*)?(?:[eE][+-]?\d+)?$/.test(value) ? value : ''
}

const listeners = new WeakMap<HTMLInputElement, () => void>()
function normalize(input: HTMLInputElement) {
  const text = quantityInputText(input.value)
  if (input.value !== text) input.value = text
}

export const vQuantity: ObjectDirective<HTMLInputElement> = {
  mounted(input) {
    normalize(input)
    const blur = () => normalize(input)
    listeners.set(input, blur)
    input.addEventListener('blur', blur)
  },
  updated(input) {
    if (input.ownerDocument.activeElement !== input) normalize(input)
  },
  beforeUnmount(input) {
    const blur = listeners.get(input)
    if (blur) input.removeEventListener('blur', blur)
    listeners.delete(input)
  },
}
