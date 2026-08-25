import { mount } from '@vue/test-utils'
import { describe, expect, it } from 'vitest'
import FunctionBlockDiagram from '../src/components/FunctionBlockDiagram.vue'
import type { ProgramTemplateParameter } from '../src/types'

describe('FunctionBlockDiagram', () => {
  it('renders input, output and bidirectional pins from interface metadata', () => {
    const parameters: ProgramTemplateParameter[] = [
      { id: 'input', direction: 'Input', sortOrder: 0, name: 'Fwd', dataType: 'BOOL', defaultValue: null, unit: null, description: '正转请求' },
      { id: 'output', direction: 'Output', sortOrder: 1, name: 'FwdOut', dataType: 'BOOL', defaultValue: null, unit: null, description: '正转输出' },
      { id: 'inout', direction: 'InOut', sortOrder: 2, name: 'Axis', dataType: 'AxisRef', defaultValue: null, unit: null, description: '轴引用' },
    ]
    const wrapper = mount(FunctionBlockDiagram, {
      props: { code: 'PT-FB-0001', name: '电机正反转控制', version: 'v1.0.0', parameters },
    })

    expect(wrapper.get('svg').attributes('aria-label')).toBe('电机正反转控制功能块接口图')
    expect(wrapper.get('svg').attributes('viewBox')).toBe('0 0 640 176')
    expect(wrapper.findAll('g.is-input')).toHaveLength(1)
    expect(wrapper.findAll('g.is-output')).toHaveLength(1)
    expect(wrapper.findAll('g.is-inout')).toHaveLength(1)
    expect(wrapper.text()).toContain('Fwd')
    expect(wrapper.text()).toContain('FwdOut')
    expect(wrapper.text()).toContain('Axis')
    expect(wrapper.text()).toContain('PT-FB-0001 · v1.0.0')
  })
})
