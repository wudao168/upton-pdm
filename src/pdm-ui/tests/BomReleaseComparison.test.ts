import { flushPromises, mount } from '@vue/test-utils'
import { describe, expect, it } from 'vitest'
import BomReleaseComparison from '../src/components/BomReleaseComparison.vue'
import type { BomComparisonSnapshot } from '../src/bomReleaseComparison'
import type { BomItem } from '../src/types'

const item = (code: string, quantity = 1): BomItem => ({ sequence: 1, drawingNumber: code, name: code, quantity, unit: '001', revision: 'A', complete: true })
const snapshots: BomComparisonSnapshot[] = [1, 2, 3, 4].map(version => ({ id: `v${version}`, label: `S-V0${version}`, packageNumber: `RP-${version}`, versionNumber: version,
  releasedAt: `2026-10-${10 + version}T00:00:00Z`, items: [item('A', version), item('B'), ...(version === 4 ? [item('C')] : [])] }))

describe('BOM发布包多选对比', () => {
  it('只展示已提交的快照，按时间左中右排列，不提供选择条或保存操作', () => {
    const wrapper = mount(BomReleaseComparison, { props: { snapshots: [snapshots[3]!, snapshots[1]!, snapshots[0]!] } })
    expect(wrapper.findAll('.bom-release-comparison__panel>header strong').map(row => row.text())).toEqual(['S-V01', 'S-V02', 'S-V04'])
    expect(wrapper.find('.bom-release-comparison__selection').exists()).toBe(false)
    expect(wrapper.findAll('button')).toHaveLength(0)
    expect(wrapper.emitted('save')).toBeUndefined()
  })

  it('差异筛选保持各容器行位一致，同步滚动可关闭', async () => {
    const wrapper = mount(BomReleaseComparison, { props: { snapshots: [snapshots[0]!, snapshots[1]!, snapshots[3]!] } })
    expect(wrapper.findAll('.bom-release-comparison__panel tbody').map(body => body.findAll('tr').length)).toEqual([3, 3, 3])
    const options = wrapper.findAll('.bom-release-comparison__toolbar input')
    await options[0]!.setValue(true)
    expect(wrapper.findAll('.bom-release-comparison__panel tbody').map(body => body.findAll('tr').length)).toEqual([2, 2, 2])
    const panels = wrapper.findAll('.bom-release-comparison__scroll')
    panels[0]!.element.scrollTop = 50
    panels[0]!.element.scrollLeft = 20
    await panels[0]!.trigger('scroll')
    expect(panels.map(panel => [panel.element.scrollTop, panel.element.scrollLeft])).toEqual([[50, 20], [50, 20], [50, 20]])
    await options[1]!.setValue(false)
    panels[0]!.element.scrollTop = 80
    await panels[0]!.trigger('scroll')
    expect(panels[1]!.element.scrollTop).toBe(50)
    await wrapper.setProps({ snapshots: [] })
    await flushPromises()
    expect(wrapper.findAll('.bom-release-comparison__panel')).toHaveLength(0)
  })
})
