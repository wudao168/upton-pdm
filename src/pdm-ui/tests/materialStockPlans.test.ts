import { afterEach, describe, expect, it } from 'vitest'
import { mount } from '@vue/test-utils'
import { nextTick } from 'vue'
import { materialStockPlan, refreshMaterialStockPlans, setMaterialStockContext, stockedMaterial, type MaterialStockPlanSnapshot } from '../src/materialStockPlans'

const snapshot = (): MaterialStockPlanSnapshot => ({ enterpriseCode: '01', organizationCode: '7', items: [{ enterpriseCode: '01', organizationCode: '7', materialCode: '01020014079', safetyStockQuantity: 15, reorderQuantity: 50, unitName: '个', isEffective: true, syncedAt: '2026-10-23T08:00:00Z' }] })
afterEach(() => setMaterialStockContext(''))
describe('常备件显示', () => {
  it('keeps the current cell title when a table reuses the element for another material', async () => {
    setMaterialStockContext('reused-row')
    await refreshMaterialStockPlans(async () => snapshot())
    const wrapper = mount({ props: ['code'], template: '<span :title="code" v-stocked-material="code">物料</span>' }, { props: { code: '01020014079' }, global: { directives: { 'stocked-material': stockedMaterial } } })
    expect(wrapper.attributes('title')).toContain('安全库存：15个')
    await wrapper.setProps({ code: 'UKIT-000001' })
    expect(wrapper.attributes('title')).toBe('UKIT-000001')
    expect(wrapper.classes()).not.toContain('pdm-stocked-material')
    wrapper.unmount()
  })
  it('colors only exact real codes without appending labels, preserves values and clears after a successful zero snapshot', async () => {
    setMaterialStockContext('user-01')
    const wrapper = mount({ props: ['code'], template: '<span v-stocked-material.badge="code">物料</span>' }, { props: { code: '01020014079' }, global: { directives: { 'stocked-material': stockedMaterial } } })
    await refreshMaterialStockPlans(async () => snapshot()); await nextTick()
    expect(wrapper.classes()).toContain('pdm-stocked-material')
    expect(wrapper.text()).toBe('物料')
    expect(wrapper.find('.pdm-stocked-badge').exists()).toBe(false)
    expect(wrapper.attributes('title')).toContain('安全库存：15个')
    expect(wrapper.attributes('title')).toContain('再订货量：50个')
    await wrapper.setProps({ code: 'UKIT-000001' })
    expect(wrapper.classes()).not.toContain('pdm-stocked-material')
    expect(wrapper.text()).toBe('物料')
    await wrapper.setProps({ code: '01020014079' })
    await refreshMaterialStockPlans(async () => { throw new Error('offline') }); await nextTick()
    expect(wrapper.classes()).toContain('pdm-stocked-material')
    expect(wrapper.text()).toBe('物料')
    const zero = snapshot(); zero.items[0]!.safetyStockQuantity = 0
    await refreshMaterialStockPlans(async () => zero); await nextTick()
    expect(wrapper.classes()).not.toContain('pdm-stocked-material')
    expect(wrapper.text()).toBe('物料')
    wrapper.unmount()
  })
  it('rejects different organization records and stale requests after a context switch', async () => {
    setMaterialStockContext('org7')
    let resolve!: (value: MaterialStockPlanSnapshot) => void
    const old = refreshMaterialStockPlans(() => new Promise(done => { resolve = done }))
    setMaterialStockContext('org8')
    resolve(snapshot()); await old
    expect(materialStockPlan('01020014079')).toBeUndefined()
    const mixed = snapshot(); mixed.items[0]!.organizationCode = '8'
    await refreshMaterialStockPlans(async () => mixed)
    expect(materialStockPlan('01020014079')).toBeUndefined()
  })
})
