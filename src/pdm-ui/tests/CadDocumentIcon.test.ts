import { mount } from '@vue/test-utils'
import { describe, expect, it } from 'vitest'
import CadDocumentIcon from '../src/components/CadDocumentIcon.vue'

describe('CadDocumentIcon', () => {
  it.each(['Assembly', 'Part', 'Drawing'] as const)('renders the %s document type', (kind) => {
    const wrapper = mount(CadDocumentIcon, { props: { kind } })

    expect(wrapper.get('svg').attributes('data-cad-kind')).toBe(kind)
    expect(wrapper.get('svg').attributes('aria-label')).toBe({ Assembly: '装配体', Part: '零件', Drawing: '工程图' }[kind])
  })

  it('keeps the document type and overlays its warning state', () => {
    const wrapper = mount(CadDocumentIcon, { props: { kind: 'Part', status: 'Unarchived' } })

    expect(wrapper.get('svg').attributes('data-cad-kind')).toBe('Part')
    expect(wrapper.find('[data-cad-issue="true"]').exists()).toBe(true)
  })

  it('keeps the three confirmed silhouettes visibly distinct', () => {
    const assembly = mount(CadDocumentIcon, { props: { kind: 'Assembly' } })
    const part = mount(CadDocumentIcon, { props: { kind: 'Part' } })
    const drawing = mount(CadDocumentIcon, { props: { kind: 'Drawing' } })

    expect(assembly.find('[data-cad-assembly-link="true"]').exists()).toBe(true)
    expect(assembly.html()).toContain('#73a9d0')
    expect(assembly.html()).not.toContain('#fad75b')
    expect(part.find('[data-cad-part-body="true"]').exists()).toBe(true)
    expect(part.html()).toContain('#fad75b')
    expect(drawing.find('[data-cad-drawing-title-block="true"]').exists()).toBe(true)
    expect(new Set([assembly.html(), part.html(), drawing.html()]).size).toBe(3)
  })
})
