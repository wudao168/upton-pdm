import { mount } from '@vue/test-utils'
import { describe, expect, it, vi } from 'vitest'
import OverviewTimeline from '../src/components/OverviewTimeline.vue'
import type { ProjectPlan } from '../src/types'

const task = { id: 'design', name: '设计确认', stage: 'Design', plannedStart: '2026-09-01', plannedFinish: '2026-09-12', baselineStart: '2026-09-01', baselineFinish: '2026-09-10', actualStart: '2026-09-02', actualFinish: '2026-09-11', completionPercent: 100, weight: 1, sortOrder: 1, isMilestone: true }
const plan = { tasks: [task] } as unknown as ProjectPlan

describe('overview timeline', () => {
  it('keeps overlapping phases in one translucent row without shortening their date ranges', () => {
    const overlapping = { tasks: [task, { ...task, id: 'material', stage: 'MaterialPreparation', plannedStart: '2026-09-08', plannedFinish: '2026-09-15' }, { ...task, id: 'assembly', stage: 'Assembly', plannedStart: '2026-09-16', plannedFinish: '2026-09-20' }] } as unknown as ProjectPlan
    const wrapper = mount(OverviewTimeline, { props: { plan: overlapping, baseline: null, baselineUnavailable: false } })
    const bars = wrapper.get('[aria-label="当前计划"]').findAll('.pdm-overview-timeline__segment')
    expect((bars[0]!.element as HTMLElement).style.top).toBe((bars[1]!.element as HTMLElement).style.top)
    expect(bars[1]!.attributes('style')).toContain('transparent')
    expect((bars[0]!.element as HTMLElement).style.top).toBe((bars[2]!.element as HTMLElement).style.top)
    expect(bars[0]!.attributes('title')).toContain('2026-09-01 — 2026-09-12')
    expect(bars[1]!.attributes('title')).toContain('2026-09-08 — 2026-09-15')
  })

  it.each([['success', '正常'], ['warning', '有风险'], ['danger', '已滞后']] as const)('aligns actual and planned date arrows with %s progress status', (progressTone, label) => {
    vi.useFakeTimers()
    vi.setSystemTime(new Date('2026-09-06T12:00:00'))
    try {
      const wrapper = mount(OverviewTimeline, { props: { plan, baseline: plan, baselineUnavailable: false, progressTone } })
      const arrows = wrapper.findAll('.pdm-overview-timeline__today-arrow')
      expect(arrows).toHaveLength(2)
      expect(arrows[0]!.attributes('style')).toBe(arrows[1]!.attributes('style'))
      for (const arrow of arrows) {
        expect(arrow.classes()).toContain(`is-${progressTone}`)
        expect(arrow.attributes('title')).toBe(`2026-09-06 · ${label}`)
      }
      expect(wrapper.get('[aria-label="初始基线"]').find('.pdm-overview-timeline__today-arrow').exists()).toBe(false)
      expect(wrapper.find('.pdm-overview-timeline__speeder').exists()).toBe(progressTone !== 'success')
      expect(wrapper.find('.pdm-overview-timeline__today').exists()).toBe(false)
      wrapper.unmount()
    } finally { vi.useRealTimers() }
  })

  it('compares all rows on the same scale and opens the selected stage', async () => {
    const wrapper = mount(OverviewTimeline, { props: { plan, baseline: plan, baselineUnavailable: false } })
    expect(wrapper.findAll('.pdm-overview-timeline__row').map(row => row.attributes('aria-label'))).toEqual(['实际进度', '当前计划', '初始基线'])
    expect(wrapper.find('.pdm-overview-timeline__legend').exists()).toBe(false)
    expect(wrapper.find('[aria-label="每日时间轴"]').exists()).toBe(false)
    expect(wrapper.find('.pdm-overview-timeline__day-line').exists()).toBe(false)
    expect(wrapper.get('.pdm-overview-timeline__dates').text()).toContain('共 12 天')
    const actual = wrapper.get('[aria-label="实际进度"] .pdm-overview-timeline__segment')
    expect(actual.attributes('title')).toContain('2026-09-02 — 2026-09-11')
    expect(actual.text()).toContain('100%')
    expect(wrapper.get('[aria-label="初始基线"] .pdm-overview-timeline__segment').attributes('title')).toContain('2026-09-01 — 2026-09-10')
    expect(wrapper.text()).not.toContain('设计确认')
    expect(wrapper.text()).not.toContain('今天')
    expect(wrapper.findAll('.pdm-overview-timeline__milestone')).toHaveLength(0)
    await wrapper.get('[aria-label="当前计划"] .pdm-overview-timeline__segment').trigger('click')
    expect(wrapper.emitted('openTask')).toEqual([['design']])
  })

  it.each(['success', 'warning', 'danger'] as const)('hides the actual arrow without actual dates even when progress is %s', (progressTone) => {
    vi.useFakeTimers()
    vi.setSystemTime(new Date('2026-09-06T12:00:00'))
    try {
      const wrapper = mount(OverviewTimeline, { props: { plan: { tasks: [{ ...task, actualStart: undefined, actualFinish: undefined }] } as unknown as ProjectPlan, baseline: null, baselineUnavailable: false, progressTone } })
      expect(wrapper.get('[aria-label="实际进度"]').find('.pdm-overview-timeline__today-arrow').exists()).toBe(false)
      expect(wrapper.get('[aria-label="当前计划"]').find('.pdm-overview-timeline__today-arrow').exists()).toBe(true)
      wrapper.unmount()
    } finally { vi.useRealTimers() }
  })

  it('does not invent dates for missing actuals or missing initial baseline', () => {
    const wrapper = mount(OverviewTimeline, { props: { plan: { tasks: [{ ...task, actualStart: undefined, actualFinish: undefined }] } as unknown as ProjectPlan, baseline: null, baselineUnavailable: true, progressTone: 'danger' } })
    expect(wrapper.find('.pdm-overview-timeline__speeder').exists()).toBe(false)
    expect(wrapper.get('[aria-label="实际进度"]').text()).toContain('未填报实际日期')
    expect(wrapper.get('[aria-label="初始基线"]').text()).toContain('历史缺失')
    expect(wrapper.get('[aria-label="初始基线"]').findAll('button')).toHaveLength(0)
  })
})
