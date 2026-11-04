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
function applyQuantityStep(input: HTMLInputElement, whole = false, declaredMin?: unknown) {
  if (input.type !== 'number') return
  // Chromium's native arrows use a delta of 1 with "any", preserving fractional values.
  // A literal step=1 would align to min (e.g. 0.0001) and invalidate hand-entered decimals.
  const step = whole ? '1' : 'any'
  const sourceMin = declaredMin == null ? input.min : String(declaredMin)
  if (sourceMin && Number.isFinite(Number(sourceMin))) {
    const minimum = whole ? String(Math.ceil(Number(sourceMin))) : sourceMin
    if (input.min !== minimum) input.min = minimum
  }
  if (input.step !== step) input.step = step
  if (input.dataset.pdmQuantityStep !== '1') input.dataset.pdmQuantityStep = '1'
}
function normalize(input: HTMLInputElement) {
  const text = quantityInputText(input.value)
  if (input.value !== text) input.value = text
}

export const vQuantity: ObjectDirective<HTMLInputElement> = {
  mounted(input, binding, vnode) {
    applyQuantityStep(input, binding.value === true, vnode.props?.min)
    normalize(input)
    const blur = () => normalize(input)
    listeners.set(input, blur)
    input.addEventListener('blur', blur)
  },
  updated(input, binding, vnode) {
    applyQuantityStep(input, binding.value === true, vnode.props?.min)
    if (input.ownerDocument.activeElement !== input) normalize(input)
  },
  beforeUnmount(input) {
    const blur = listeners.get(input)
    if (blur) input.removeEventListener('blur', blur)
    listeners.delete(input)
  },
}
