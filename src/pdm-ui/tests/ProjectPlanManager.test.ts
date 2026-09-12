import ElementPlus, { ElDatePicker, ElInputNumber, ElSelect, ElSlider, ElMessageBox } from 'element-plus'
import { mount, flushPromises } from '@vue/test-utils'
import { beforeEach, afterEach, describe, it, expect, vi } from 'vitest'
import ProjectPlanManager from '../src/components/ProjectPlanManager.vue'
import type { ProjectPlan, ProjectPlanTemplate, ProjectSummary } from '../src/types'

const api = vi.hoisted(() => ({
  generateProjectPlan: vi.fn(), deleteProjectPlan: vi.fn(), supplementProjectPlanStageSchedule: vi.fn(), submitProjectPlan: vi.fn(), submitProjectPlanChange: vi.fn(), completeProjectPlanChange: vi.fn(), abandonProjectPlanChange: vi.fn(), decideProjectPlan: vi.fn(),
  listProjectPlanTemplates: vi.fn(), listProjectPlanVersions: vi.fn(), readProjectPlan: vi.fn(), readProjectPlanPortfolio: vi.fn(),
  reuseProjectPlan: vi.fn(), saveProjectPlan: vi.fn(), saveProjectPlanTemplate: vi.fn(), setProjectPlanBaseline: vi.fn(),
  setProjectPlanStage: vi.fn(), updateProjectPlanTaskProgress: vi.fn(),
}))
vi.mock('../src/api', () => api)
const statusContent = vi.hoisted(() => ({ loadGlobalStatusContent: vi.fn() }))
vi.mock('../src/globalStatusContent', () => statusContent)

const base = { owner: 'pm', primaryProjectManager: 'pm', collaborativeProjectManagers: [], designLeads: [], designers: [], quantity: 1, serialNumbers: [], responsibleUsers: [] }
const root = { ...base, id: 'root', name: '主项目', code: 'P1' } as unknown as ProjectSummary
const child = { ...base, id: 'child', name: '设备一', code: 'P1-1', parentProjectId: 'root' } as unknown as ProjectSummary
const target = { ...child, id: 'target', name: '设备二', code: 'P1-2' }
const approvedTarget = { ...child, id: 'approved', name: '设备三', code: 'P1-3' }
const stages = [{ code: 'custom-review', name: '方案确认' }, { code: 'custom-handover', name: '交付' }]
const metadata = { createdBy: 'admin', createdAt: '2026-09-10T00:00:00Z', updatedBy: 'admin', updatedAt: '2026-09-10T00:00:00Z' }
const draft = {
  ...metadata,
  id: 'plan', projectId: 'child', templateId: 'template', templateName: '设备模板', currentStage: 'custom-review',
  approvalStatus: 'Draft', stages, baselineVersion: 0, plannedStart: '2026-09-10', plannedFinish: '2026-09-15', forecastFinish: '2026-09-15', rowVersion: 1,
  tasks: [{ id: 'task', name: '方案检查', stage: 'custom-review', plannedStart: '2026-09-10', plannedFinish: '2026-09-15', durationDays: 6, status: 'NotStarted', completionPercent: 0, weight: 1, isRequired: true, isMilestone: false, predecessorTaskIds: [], sortOrder: 10 }],
} as ProjectPlan
const template = { ...metadata, id: 'template', name: '设备模板', isActive: true, createdBy: 'admin', rowVersion: 1, stages, tasks: [{ id: 'tt', name: '方案检查', stage: 'custom-review', durationRatio: .5, predecessorSortOrders: [], weight: 1, isRequired: true, isMilestone: false, defaultAssigneeRole: 'ProjectManager', sortOrder: 10 }] } as ProjectPlanTemplate
const wrappers: ReturnType<typeof mount>[] = []
function render(project = child) {
  const wrapper = mount(ProjectPlanManager, { attachTo: document.body, props: { project, projects: [root, child, target, approvedTarget], token: 'test', currentUsername: 'pm', currentRole: 'ProjectManager', developer: false, canEdit: true }, global: { plugins: [ElementPlus] } })
  wrappers.push(wrapper)
  return wrapper
}
async function clickText(text: string) {
  const button = [...document.querySelectorAll('button')].find(item => item.textContent?.trim() === text)
  expect(button, text).toBeTruthy()
  button!.click()
  await flushPromises()
}
beforeEach(() => {
  vi.clearAllMocks()
  statusContent.loadGlobalStatusContent.mockResolvedValue({ holidayCalendars: [{ year: 2026, papers: ['https://www.gov.cn/zhengce/zhengceku/202511/content_7047091.htm'], days: [
    { name: '国庆节', date: '2026-09-20', isOffDay: false },
    { name: '中秋节', date: '2026-09-25', isOffDay: true },
    { name: '中秋节', date: '2026-09-27', isOffDay: true },
  ] }] })
  api.listProjectPlanTemplates.mockResolvedValue([structuredClone(template)])
  api.readProjectPlan.mockResolvedValue(structuredClone(draft))
  api.readProjectPlanPortfolio.mockResolvedValue({ rootProjectId: 'root', currentStage: 'Design', completionPercent: 0, laggingProjectCount: 0, riskProjectCount: 0, projects: [
    { projectId: 'root', projectCode: 'P1', projectName: '主项目', isRoot: true, hasPlan: false },
    { projectId: 'child', projectCode: 'P1-1', projectName: '设备一', hasPlan: true, plan: structuredClone(draft) },
    { projectId: 'target', projectCode: 'P1-2', projectName: '设备二', hasPlan: false },
    { projectId: 'approved', projectCode: 'P1-3', projectName: '设备三', hasPlan: true, plan: { ...structuredClone(draft), projectId: 'approved', approvalStatus: 'Approved' } },
  ] })
  api.saveProjectPlan.mockResolvedValue(structuredClone(draft))
})
afterEach(() => { wrappers.splice(0).forEach(item => item.unmount()); document.body.innerHTML = ''; vi.restoreAllMocks() })

describe('项目计划审批和配置', () => {
  it('编辑工期保留开始日期，跨月调整结束日期，阶段及里程碑只读', async () => {
    const source = { ...structuredClone(draft), tasks: [
      { ...structuredClone(draft.tasks[0]!), plannedStart: '2026-09-28', plannedFinish: '2026-09-30' },
      { ...structuredClone(draft.tasks[0]!), id: 'milestone', name: '完成节点', isMilestone: true, plannedStart: '2026-10-01', plannedFinish: '2026-10-01', durationDays: 0 },
    ] }
    api.readProjectPlan.mockResolvedValue(source)
    const prompt = vi.spyOn(ElMessageBox, 'prompt')
    const wrapper = render()
    await flushPromises()
    expect(wrapper.find('.pdm-gantt-info-head').text()).toBe('项目 / 任务责任人进度计划日期工期完成日期')
    expect(wrapper.find('.pdm-gantt-info-row.is-stage > span:nth-child(5)').text()).toBe('4天')
    expect(wrapper.find('[aria-label="编辑完成节点工期"]').exists()).toBe(false)
    expect(wrapper.find('[aria-label="编辑方案检查计划日期"]').findAll('time').map(item => item.text())).toEqual(['2026-09-28', '2026-09-30'])
    await wrapper.find('[aria-label="编辑方案检查工期"]').trigger('click')
    wrapper.findComponent(ElInputNumber).vm.$emit('update:modelValue', 5)
    await clickText('保存工期')
    expect(prompt).not.toHaveBeenCalled()
    expect(api.saveProjectPlan).toHaveBeenCalledWith('child', expect.objectContaining({ expectedRowVersion: 1, tasks: [expect.objectContaining({ plannedStart: '2026-09-28', plannedFinish: '2026-10-02' }), source.tasks[1]] }), 'test')
    expect(api.updateProjectPlanTaskProgress).not.toHaveBeenCalled()
  })

  it('拖动后直接保存且自动记录原因，不弹确认或原因框；无位移不保存', async () => {
    vi.spyOn(HTMLElement.prototype, 'clientWidth', 'get').mockReturnValue(1390)
    api.readProjectPlan.mockResolvedValue(structuredClone(draft))
    const prompt = vi.spyOn(ElMessageBox, 'prompt')
    const confirm = vi.spyOn(ElMessageBox, 'confirm')
    const wrapper = render()
    await flushPromises()
    const bar = wrapper.find('.pdm-gantt-bar.is-draggable')
    Object.assign(bar.element, { setPointerCapture: vi.fn(), releasePointerCapture: vi.fn() })
    const pointer = async (type: string, clientX: number) => {
      const event = new Event(type, { bubbles: true })
      Object.assign(event, { pointerId: 1, button: 0, clientX })
      bar.element.dispatchEvent(event)
      await flushPromises()
    }
    await pointer('pointerdown', 100)
    await pointer('pointerup', 100)
    expect(api.saveProjectPlan).not.toHaveBeenCalled()
    await pointer('pointerdown', 100)
    await pointer('pointermove', 220)
    await pointer('pointerup', 220)
    expect(prompt).not.toHaveBeenCalled()
    expect(confirm).not.toHaveBeenCalled()
    const command = api.saveProjectPlan.mock.calls[0][1]
    expect(command.expectedRowVersion).toBe(1)
    expect(command.changeReason).toContain('拖动排期')
    expect(command.tasks[0].plannedStart).not.toBe(draft.tasks[0].plannedStart)
    expect(new Date(command.tasks[0].plannedFinish).getTime() - new Date(command.tasks[0].plannedStart).getTime()).toBe(5 * 86400000)
    expect(command.tasks[0].completionPercent).toBe(0)
  })

  it('甘特条左右端可分别调整开始和结束日期，且最短保持一天', async () => {
    vi.spyOn(HTMLElement.prototype, 'clientWidth', 'get').mockReturnValue(1390)
    api.readProjectPlan.mockResolvedValue(structuredClone(draft))
    const wrapper = render()
    await flushPromises()
    expect(wrapper.findAll('.pdm-gantt-resize-handle')).toHaveLength(4)
    const dragHandle = async (selector: string, from: number, to: number) => {
      const handle = wrapper.find(selector)
      Object.assign(handle.element, { setPointerCapture: vi.fn(), releasePointerCapture: vi.fn() })
      for (const [type, clientX] of [['pointerdown', from], ['pointermove', to], ['pointerup', to]] as const) {
        const event = new Event(type, { bubbles: true })
        Object.assign(event, { pointerId: 2, button: 0, clientX })
        handle.element.dispatchEvent(event)
        await flushPromises()
      }
    }
    await dragHandle('[aria-label="拖动调整方案检查开始日期"]', 100, 1100)
    const startCommand = api.saveProjectPlan.mock.calls[0][1]
    expect(startCommand.tasks[0]).toEqual(expect.objectContaining({ plannedStart: '2026-09-15', plannedFinish: '2026-09-15' }))
    expect(startCommand.changeReason).toContain('拖动调整工期')
    expect(startCommand.changeReason).toContain('工期由 6 天调整为 1 天')
    api.saveProjectPlan.mockClear()
    await dragHandle('[aria-label="拖动调整方案检查结束日期"]', 100, 180)
    const finishCommand = api.saveProjectPlan.mock.calls[0][1]
    expect(finishCommand.tasks[0].plannedStart).toBe('2026-09-10')
    expect(finishCommand.tasks[0].plannedFinish).not.toBe('2026-09-15')
    expect(new Date(finishCommand.tasks[0].plannedFinish).getTime()).toBeGreaterThan(new Date('2026-09-15').getTime())
  })

  it('拖动阶段边界只调整当前阶段并允许与相邻阶段交叉', async () => {
    vi.spyOn(HTMLElement.prototype, 'clientWidth', 'get').mockReturnValue(1390)
    const source = { ...structuredClone(draft), plannedFinish: '2026-09-20', forecastFinish: '2026-09-20', tasks: [
      { ...structuredClone(draft.tasks[0]!), id: 'a1', name: '设计启动', plannedStart: '2026-09-10', plannedFinish: '2026-09-11', durationDays: 2, sortOrder: 10 },
      { ...structuredClone(draft.tasks[0]!), id: 'a2', name: '设计输出', plannedStart: '2026-09-12', plannedFinish: '2026-09-14', durationDays: 3, predecessorTaskIds: ['a1'], sortOrder: 20 },
      { ...structuredClone(draft.tasks[0]!), id: 'b1', name: '交付准备', stage: 'custom-handover', plannedStart: '2026-09-15', plannedFinish: '2026-09-16', durationDays: 2, predecessorTaskIds: [], sortOrder: 30 },
      { ...structuredClone(draft.tasks[0]!), id: 'b2', name: '交付完成', stage: 'custom-handover', plannedStart: '2026-09-17', plannedFinish: '2026-09-20', durationDays: 4, predecessorTaskIds: ['b1'], sortOrder: 40 },
    ] }
    api.readProjectPlan.mockResolvedValue(source)
    const wrapper = render()
    await flushPromises()
    expect(wrapper.findAll('.pdm-gantt-bar.is-stage-adjustable')).toHaveLength(2)
    expect(wrapper.findAll('.pdm-gantt-resize-handle.is-stage-handle')).toHaveLength(4)
    expect(wrapper.findAll('.pdm-gantt-bar-label.is-stage').map(item => item.text())).toEqual(['方案确认', '交付'])
    expect(wrapper.findAll('.pdm-gantt-timeline-row:not(.is-stage) .pdm-gantt-bar-label').map(item => item.text())).toEqual(expect.arrayContaining(['设计启动', '设计输出', '交付准备', '交付完成']))
    const handle = wrapper.find('[aria-label="拖动调整方案确认阶段结束边界"]')
    Object.assign(handle.element, { setPointerCapture: vi.fn(), releasePointerCapture: vi.fn() })
    for (const [type, clientX] of [['pointerdown', 100], ['pointermove', 180], ['pointerup', 180]] as const) {
      const event = new Event(type, { bubbles: true })
      Object.assign(event, { pointerId: 7, button: 0, clientX })
      handle.element.dispatchEvent(event)
      await flushPromises()
    }
    const command = api.saveProjectPlan.mock.calls[0][1]
    expect(command.expectedRowVersion).toBe(1)
    expect(command.changeReason).toContain('其他阶段不再保持首尾连续')
    expect(command.tasks).toHaveLength(4)
    const [a1, a2, b1, b2] = command.tasks
    expect(a1.plannedStart).toBe(source.plannedStart)
    expect(b2.plannedFinish).toBe(source.plannedFinish)
    expect(b1.plannedStart).toBe('2026-09-15')
    expect(new Date(b2.plannedStart).getTime() - new Date(b1.plannedFinish).getTime()).toBeGreaterThanOrEqual(86400000)
    const firstStageFinish = [a1.plannedFinish, a2.plannedFinish].sort().at(-1)
    const secondStageStart = [b1.plannedStart, b2.plannedStart].sort()[0]
    expect(new Date(firstStageFinish).getTime()).toBeGreaterThanOrEqual(new Date(secondStageStart).getTime())
  })

  it('阶段调整只联动显式前置任务，无依赖任务保持原日期', async () => {
    vi.spyOn(HTMLElement.prototype, 'clientWidth', 'get').mockReturnValue(1390)
    const source = { ...structuredClone(draft), plannedFinish: '2026-09-20', forecastFinish: '2026-09-20', tasks: [
      { ...structuredClone(draft.tasks[0]!), id: 'design', name: '设计输出', plannedStart: '2026-09-10', plannedFinish: '2026-09-14', durationDays: 5, sortOrder: 10 },
      { ...structuredClone(draft.tasks[0]!), id: 'dependent', name: '依赖任务', stage: 'custom-handover', plannedStart: '2026-09-15', plannedFinish: '2026-09-16', durationDays: 2, predecessorTaskIds: ['design'], sortOrder: 20 },
      { ...structuredClone(draft.tasks[0]!), id: 'independent', name: '并行任务', stage: 'custom-handover', plannedStart: '2026-09-15', plannedFinish: '2026-09-20', durationDays: 6, predecessorTaskIds: [], sortOrder: 30 },
    ] }
    api.readProjectPlan.mockResolvedValue(source)
    const wrapper = render()
    await flushPromises()
    const handle = wrapper.find('[aria-label="拖动调整方案确认阶段结束边界"]')
    Object.assign(handle.element, { setPointerCapture: vi.fn(), releasePointerCapture: vi.fn() })
    for (const [type, clientX] of [['pointerdown', 100], ['pointermove', 180], ['pointerup', 180]] as const) {
      const event = new Event(type, { bubbles: true })
      Object.assign(event, { pointerId: 9, button: 0, clientX })
      handle.element.dispatchEvent(event)
      await flushPromises()
    }
    const tasks = api.saveProjectPlan.mock.calls[0][1].tasks as ProjectPlan['tasks']
    const design = tasks.find(task => task.id === 'design')!
    const dependent = tasks.find(task => task.id === 'dependent')!
    const independent = tasks.find(task => task.id === 'independent')!
    expect(new Date(dependent.plannedStart).getTime() - new Date(design.plannedFinish).getTime()).toBeGreaterThanOrEqual(86400000)
    expect(independent.plannedStart).toBe('2026-09-15')
    expect(independent.plannedFinish).toBe('2026-09-20')
  })

  it('拖动阶段条会整体平移本阶段且不移动无依赖阶段', async () => {
    vi.spyOn(HTMLElement.prototype, 'clientWidth', 'get').mockReturnValue(1390)
    const source = { ...structuredClone(draft), plannedFinish: '2026-09-20', forecastFinish: '2026-09-20', tasks: [
      { ...structuredClone(draft.tasks[0]!), id: 'design', name: '设计任务', plannedStart: '2026-09-10', plannedFinish: '2026-09-14', durationDays: 5 },
      { ...structuredClone(draft.tasks[0]!), id: 'handover', name: '交付任务', stage: 'custom-handover', plannedStart: '2026-09-15', plannedFinish: '2026-09-20', durationDays: 6, sortOrder: 20 },
    ] }
    api.readProjectPlan.mockResolvedValue(source)
    const wrapper = render()
    await flushPromises()
    const bar = wrapper.find('.pdm-gantt-timeline-row.is-stage .pdm-gantt-bar')
    Object.assign(bar.element, { setPointerCapture: vi.fn(), releasePointerCapture: vi.fn() })
    for (const [type, clientX] of [['pointerdown', 100], ['pointermove', 180], ['pointerup', 180]] as const) {
      const event = new Event(type, { bubbles: true })
      Object.assign(event, { pointerId: 10, button: 0, clientX })
      bar.element.dispatchEvent(event)
      await flushPromises()
    }
    const command = api.saveProjectPlan.mock.calls[0][1]
    const design = command.tasks.find((task: ProjectPlan['tasks'][number]) => task.id === 'design')
    const handover = command.tasks.find((task: ProjectPlan['tasks'][number]) => task.id === 'handover')
    expect(command.changeReason).toContain('阶段整体平移')
    expect(design.plannedStart).not.toBe('2026-09-10')
    expect(new Date(design.plannedFinish).getTime() - new Date(design.plannedStart).getTime()).toBe(4 * 86400000)
    expect(handover.plannedStart).toBe('2026-09-15')
    expect(handover.plannedFinish).toBe('2026-09-20')
  })

  it('阶段汇总按任务真实日期显示交叉，并展示多个当前活动阶段', async () => {
    vi.spyOn(HTMLElement.prototype, 'clientWidth', 'get').mockReturnValue(1390)
    const now = new Date()
    const today = `${now.getFullYear()}-${String(now.getMonth() + 1).padStart(2, '0')}-${String(now.getDate()).padStart(2, '0')}`
    const source = { ...structuredClone(draft), approvalStatus: 'Approved' as const, plannedStart: today, plannedFinish: today, forecastFinish: today, tasks: [
      { ...structuredClone(draft.tasks[0]!), id: 'design', name: '设计任务', plannedStart: today, plannedFinish: today, durationDays: 1 },
      { ...structuredClone(draft.tasks[0]!), id: 'handover', name: '交付任务', stage: 'custom-handover', plannedStart: today, plannedFinish: today, durationDays: 1, sortOrder: 20 },
    ] }
    api.readProjectPlan.mockResolvedValue(source)
    const wrapper = render()
    await flushPromises()
    expect(wrapper.find('.pdm-plan-summary > article:first-child strong').text()).toContain('方案确认、交付')
    const bars = wrapper.findAll('.pdm-gantt-timeline-row.is-stage .pdm-gantt-bar')
    expect(bars).toHaveLength(2)
    expect(bars[0].attributes('style')).toBe(bars[1].attributes('style'))
  })

  it('已生效计划取得变更权限前不可拖动，取得后保存草稿且原计划副本保留', async () => {
    vi.spyOn(HTMLElement.prototype, 'clientWidth', 'get').mockReturnValue(1390)
    const source = { ...structuredClone(draft), approvalStatus: 'Approved' as const, approvedBy: 'approver', plannedFinish: '2026-09-20', tasks: [
      { ...structuredClone(draft.tasks[0]!), plannedStart: '2026-09-10', plannedFinish: '2026-09-14', durationDays: 5 },
      { ...structuredClone(draft.tasks[0]!), id: 'handover', name: '交付确认', stage: 'custom-handover', plannedStart: '2026-09-15', plannedFinish: '2026-09-20', durationDays: 6, sortOrder: 20 },
    ] }
    api.readProjectPlan.mockResolvedValue(source)
    const wrapper = render()
    await flushPromises()
    expect(wrapper.find('.pdm-gantt-bar.is-draggable').exists()).toBe(false)
    expect(wrapper.find('[aria-label="拖动调整方案确认阶段结束边界"]').exists()).toBe(false)
    const editable = { ...source, changeDraftSource: structuredClone(source), changeRequest: { id: 'request', tasks: [], reason: '客户调整交期', submittedBy: 'pm', submittedAt: '', approvalAssignee: 'approver', status: 'Approved' as const } }
    api.readProjectPlan.mockResolvedValue(editable)
    await wrapper.unmount()
    const draftWrapper = render()
    await flushPromises()
    const handle = draftWrapper.find('[aria-label="拖动调整方案确认阶段结束边界"]')
    expect(handle.exists()).toBe(true)
    Object.assign(handle.element, { setPointerCapture: vi.fn(), releasePointerCapture: vi.fn() })
    for (const [type, clientX] of [['pointerdown', 100], ['pointermove', 180], ['pointerup', 180]] as const) {
      const event = new Event(type, { bubbles: true })
      Object.assign(event, { pointerId: 8, button: 0, clientX })
      handle.element.dispatchEvent(event)
      await flushPromises()
    }
    expect(api.saveProjectPlan).toHaveBeenCalledWith('child', expect.objectContaining({ expectedRowVersion: 1 }), 'test')
    expect(api.submitProjectPlanChange).not.toHaveBeenCalled()
    expect(editable.changeDraftSource.tasks[0]!.plannedFinish).toBe('2026-09-14')
  })

  it('周视图第三行居中显示ISO周号并在线上显示周一起始日，其他视图仍按日期自适应', async () => {
    vi.spyOn(HTMLElement.prototype, 'clientWidth', 'get').mockReturnValue(1181)
    api.readProjectPlan.mockResolvedValue({ ...structuredClone(draft), tasks: [{ ...structuredClone(draft.tasks[0]!), plannedStart: '2026-09-10', plannedFinish: '2026-11-08' }] })
    const wrapper = render()
    await flushPromises()
    const nonWorkingDays = wrapper.find<HTMLInputElement>('input[aria-label="显示节假日和周日背景色"]')
    expect(nonWorkingDays.element.checked).toBe(false)
    expect(nonWorkingDays.element.parentElement?.textContent).toContain('休息日')
    expect(nonWorkingDays.element.parentElement?.textContent).not.toContain('休息日底色')
    expect(wrapper.find('.pdm-gantt-calendar-shade').exists()).toBe(false)
    await nonWorkingDays.setValue(true)
    expect(wrapper.find('.pdm-gantt-calendar-band.is-year').text()).toBe('2026年')
    expect(wrapper.findAll('.pdm-gantt-calendar-band.is-month').map(item => item.text())).toEqual(expect.arrayContaining(['9月', '10月', '11月']))
    const ticks = wrapper.findAll('.pdm-gantt-tick')
    expect(ticks.length).toBeGreaterThan(1)
    expect(new Date(`${ticks[0].find('time').attributes('datetime')}T00:00:00`).getDay()).toBe(1)
    const stepDays = new Date(ticks[1].find('time').attributes('datetime')!).getTime() - new Date(ticks[0].find('time').attributes('datetime')!).getTime()
    for (const [index, tick] of ticks.entries()) {
      expect(tick.find('.pdm-gantt-tick__week').exists()).toBe(true)
      const dates = tick.findAll('time')
      expect(dates).toHaveLength(1)
      const startDate = dates[0].attributes('datetime')!
      expect(tick.attributes('title')).toMatch(new RegExp(`^${startDate} ～ `))
      expect(tick.find('.pdm-gantt-tick__week').text()).toMatch(/^W\d{2}$/)
      expect(dates[0].text()).toBe(String(Number(startDate.slice(8))))
      expect(dates[0].classes()).toContain('pdm-gantt-tick__week-start')
      if (index > 0) {
        expect(new Date(startDate).getTime() - new Date(ticks[index - 1].find('time').attributes('datetime')!).getTime()).toBe(7 * 86_400_000)
        expect(Number.parseFloat((tick.element as HTMLElement).style.left) - Number.parseFloat((ticks[index - 1].element as HTMLElement).style.left)).toBeGreaterThanOrEqual(18)
      }
      const left = Number.parseFloat((tick.element as HTMLElement).style.left)
      const gridline = wrapper.find('.pdm-gantt-timeline-row').findAll('.pdm-gantt-gridline')[index]
      expect(Number.parseFloat((gridline.element as HTMLElement).style.left)).toBeCloseTo(left)
    }
    expect(stepDays).toBe(7 * 86_400_000)
    expect(ticks.every(tick => !tick.find('.pdm-gantt-tick__dates').exists())).toBe(true)
    const monthButton = wrapper.findAll('.pdm-plan-zoom button').find(button => button.text() === '月')!
    await monthButton.trigger('click')
    const monthTicks = wrapper.findAll('.pdm-gantt-tick')
    expect(monthTicks.every(tick => !tick.find('.pdm-gantt-tick__week').exists())).toBe(true)
    expect(monthTicks[0].find('time').text()).toBe(String(Number(monthTicks[0].find('time').attributes('datetime')!.slice(8))))
    const timelineWidthBefore = Number.parseFloat((wrapper.find('.pdm-gantt-timeline-head').element as HTMLElement).style.width)
    await wrapper.find('[aria-label="折叠信息列"]').trigger('click')
    const widerTicks = wrapper.findAll('.pdm-gantt-tick')
    expect(Number.parseFloat((wrapper.find('.pdm-gantt-timeline-head').element as HTMLElement).style.width)).toBeGreaterThan(timelineWidthBefore)
    expect(widerTicks.length).toBeGreaterThanOrEqual(monthTicks.length)
    for (let index = 1; index < widerTicks.length; index += 1) {
      expect(Number.parseFloat((widerTicks[index].element as HTMLElement).style.left) - Number.parseFloat((widerTicks[index - 1].element as HTMLElement).style.left)).toBeGreaterThanOrEqual(18)
    }
    expect(wrapper.find('.pdm-gantt-timeline-head [title="2026-09-20 · 国庆节调休上班"]').classes()).toContain('is-workday')
    expect(wrapper.find('.pdm-gantt-timeline-head [title="2026-09-25 · 中秋节（法定节假日）"]').classes()).toContain('is-holiday')
    expect(wrapper.find('.pdm-gantt-timeline-head [title="2026-09-13 · 周日"]').classes()).toContain('is-sunday')
    expect(wrapper.find('.pdm-gantt-timeline-row .pdm-gantt-calendar-shade.is-holiday').exists()).toBe(true)
  })

  it('无排期数据时默认时间轴也从ISO周一开始', async () => {
    vi.spyOn(HTMLElement.prototype, 'clientWidth', 'get').mockReturnValue(1390)
    const emptyPlan = { ...structuredClone(draft), projectId: 'root', tasks: [], plannedStart: '', plannedFinish: '', forecastFinish: '' }
    api.readProjectPlan.mockResolvedValue(emptyPlan)
    api.readProjectPlanPortfolio.mockResolvedValue({ rootProjectId: 'root', currentStage: 'Design', completionPercent: 0, laggingProjectCount: 0, riskProjectCount: 0, projects: [
      { projectId: 'root', projectCode: 'P1', projectName: '主项目', isRoot: true, hasPlan: true, plan: emptyPlan },
      { projectId: 'child', projectCode: 'P1-1', projectName: '设备一', hasPlan: false },
    ] })
    const wrapper = render(root)
    await flushPromises()
    const dates = wrapper.findAll('.pdm-gantt-tick time')
    expect(dates.length).toBeGreaterThan(1)
    expect(new Date(`${dates[0].attributes('datetime')}T00:00:00`).getDay()).toBe(1)
  })

  it('主项目可切换编辑，保存同步入口展示保留项差异且不删除子项目', async () => {
    api.readProjectPlan.mockResolvedValue({ ...structuredClone(draft), projectId: 'root', childSyncResults: [
      { projectId: 'target', projectCode: 'P1-2', result: '已同步（草稿）', differences: [] },
      { projectId: 'approved', projectCode: 'P1-3', result: '已生效，保留原计划', differences: ['方案检查日期与主项目不同'] },
    ] })
    const wrapper = render(root)
    await flushPromises()
    expect(wrapper.find('.pdm-plan-view-label').text()).toMatch(/^全部子项目（\d+）$/)
    expect(wrapper.findAll('.pdm-plan-toolbar__actions > button').slice(0, 3).map(item => item.text())).toEqual(['删除计划', '复制计划', '编辑主计划'])
    expect(wrapper.find('.pdm-plan-toolbar__actions').text()).toContain('删除计划复制计划编辑主计划编辑子计划刷新')
    expect(wrapper.find('.pdm-plan-toolbar__actions').text()).not.toContain('同步子项目')
    expect(wrapper.find('.pdm-plan-toolbar__actions').text()).not.toContain('同步结果与差异')
    expect(wrapper.find('.pdm-plan-toolbar__actions').text()).not.toContain('显示基线')
    await clickText('编辑主计划')
    expect(wrapper.text()).not.toContain('保存主项目计划会同步跟随且未批准的子项目计划')
    expect(wrapper.text()).not.toContain('点击责任人、计划日期或工期可直接编辑')
    expect(wrapper.find('[aria-label="编辑方案检查计划日期"]').exists()).toBe(true)
    expect(wrapper.text()).toContain('删除计划')
    await clickText('查看子项目汇总')
    await clickText('编辑子计划')
    expect(document.body.textContent).toContain('主项目同步')
    await clickText('同步跟随项目')
    expect(api.saveProjectPlan).toHaveBeenCalledWith('root', expect.objectContaining({ expectedRowVersion: 1, createMissingFollowers: true }), 'test')
    expect(document.body.textContent).toContain('方案检查日期与主项目不同')
    expect(api.deleteProjectPlan).not.toHaveBeenCalled()
    expect(api.generateProjectPlan).not.toHaveBeenCalled()
  })

  it('项目汇总行名称单元格仅分两行显示项目号和项目名称', async () => {
    const wrapper = render(root)
    await flushPromises()
    const projectRows = wrapper.findAll('.pdm-gantt-info-row.is-project')
    const childRow = projectRows.find(row => row.text().includes('P1-1'))!
    const name = childRow.find('.pdm-gantt-name')
    expect(name.find('strong').text()).toBe('P1-1')
    expect(name.find('small').text()).toBe('设备一')
    expect(name.text()).toBe('P1-1设备一')
    expect(name.text()).not.toContain('草稿')
  })

  it('无独立计划的子项目在汇总中默认显示主计划日期和甘特条', async () => {
    const rootPlan = { ...structuredClone(draft), id: 'root-plan', projectId: 'root', plannedStart: '2026-09-11', plannedFinish: '2026-11-04' }
    api.readProjectPlan.mockResolvedValue(rootPlan)
    api.readProjectPlanPortfolio.mockResolvedValue({ rootProjectId: 'root', currentStage: 'custom-review', completionPercent: 0, laggingProjectCount: 0, riskProjectCount: 0, projects: [
      { projectId: 'root', projectCode: 'P1', projectName: '主项目', isRoot: true, hasPlan: true, currentStage: 'custom-review', completionPercent: 20, plannedStart: '2026-09-11', plannedFinish: '2026-11-04', plan: rootPlan },
      { projectId: 'target', projectCode: 'P1-2', projectName: '设备二', isRoot: false, hasPlan: false, completionPercent: 0 },
    ] })
    const wrapper = render(root)
    await flushPromises()
    const inheritedRow = wrapper.findAll('.pdm-gantt-info-row.is-project').find(row => row.text().includes('P1-2'))!
    expect(inheritedRow.classes()).toContain('is-inherited-plan')
    expect(inheritedRow.attributes('title')).toBe('默认跟随主计划')
    expect(inheritedRow.findAll('time').map(item => item.text())).toEqual(['2026-09-11', '2026-11-04'])
    expect(inheritedRow.text()).toContain('20%')
    expect(inheritedRow.attributes('aria-expanded')).toBeUndefined()
    const inheritedTimeline = wrapper.findAll('.pdm-gantt-timeline-row.is-project').find(row => row.classes().includes('is-inherited-plan'))!
    expect(inheritedTimeline.find('.pdm-gantt-bar').attributes('style')).not.toContain('display: none')
  })

  it('删除主计划默认同时删除跟随计划，可选包含独立的未批准子计划', async () => {
    const rootPlan = { ...structuredClone(draft), id: 'root-plan', projectId: 'root' }
    const follower = { ...structuredClone(draft), followsParentPlan: true, parentPlanId: 'root-plan' }
    const independent = { ...structuredClone(draft), id: 'independent-plan', projectId: 'target' }
    api.readProjectPlan.mockResolvedValue(rootPlan)
    api.readProjectPlanPortfolio.mockResolvedValue({ rootProjectId: 'root', currentStage: 'Design', completionPercent: 0, laggingProjectCount: 0, riskProjectCount: 0, projects: [
      { projectId: 'root', projectCode: 'P1', projectName: '主项目', isRoot: true, hasPlan: true, plan: rootPlan },
      { projectId: 'child', projectCode: 'P1-1', projectName: '设备一', hasPlan: true, plan: follower },
      { projectId: 'target', projectCode: 'P1-2', projectName: '设备二', hasPlan: true, plan: independent },
      { projectId: 'approved', projectCode: 'P1-3', projectName: '设备三', hasPlan: true, plan: { ...structuredClone(draft), projectId: 'approved', approvalStatus: 'Approved' } },
    ] })
    render(root)
    await flushPromises()
    await clickText('编辑主计划')
    await clickText('删除计划')
    expect(document.body.textContent).toContain('同时删除 1 份跟随主计划的未批准子计划')
    expect(document.body.textContent).toContain('同时删除 1 份独立的未批准子计划')
    document.querySelector<HTMLInputElement>('.el-dialog input[type=checkbox]')!.click()
    api.readProjectPlan.mockImplementation((projectId: string) => Promise.resolve(projectId === 'child' ? structuredClone(follower) : null))
    await clickText('确认删除计划')
    expect(api.deleteProjectPlan).toHaveBeenCalledWith('root', 1, true, 'test')
  })

  it('父阶段按子任务统计而不显示分配窗口，重新生成仍沿用原始60天', async () => {
    api.readProjectPlan.mockResolvedValue({ ...structuredClone(draft), stages: [
      { code: 'custom-review', name: '设计', participatesInDelivery: true, durationRatio: .3, progressRatio: .3 },
      { code: 'custom-handover', name: '交付', participatesInDelivery: true, durationRatio: .7, progressRatio: .7 },
    ], stageSchedules: [{ stage: 'custom-review', startDate: '2026-09-10', durationDays: 18 }, { stage: 'custom-handover', startDate: '2026-09-28', durationDays: 42 }] })
    const wrapper = render()
    await flushPromises()
    expect(wrapper.find('.pdm-gantt-info-row.is-stage').text()).toContain('2026-09-10 ~ 2026-09-15')
    expect(wrapper.find('.pdm-gantt-info-row.is-stage').text()).not.toContain('2026-09-27')
    expect(wrapper.find('[aria-label="编辑方案检查计划日期"]').text()).toContain('2026-09-10 ~ 2026-09-15')
    expect(wrapper.text()).not.toContain('补充阶段排期')
    await clickText('重新生成')
    expect(document.querySelector<HTMLInputElement>('input[aria-label="交付总工期"]')!.value).toBe('60')
  })

  it('旧计划直接按子任务显示，不再提供补充阶段区间入口或修改任务', async () => {
    const old = { ...structuredClone(draft), stages: [{ code: 'custom-review', name: '设计', participatesInDelivery: true, durationRatio: 1, progressRatio: 1 }] }
    api.readProjectPlan.mockResolvedValue(old)
    api.supplementProjectPlanStageSchedule.mockResolvedValue(old)
    const wrapper = render()
    await flushPromises()
    expect(wrapper.text()).not.toContain('补充阶段排期')
    expect(wrapper.find('.pdm-gantt-info-row.is-stage').text()).toContain('2026-09-10 ~ 2026-09-15')
    expect(api.supplementProjectPlanStageSchedule).not.toHaveBeenCalled()
    expect(api.saveProjectPlan).not.toHaveBeenCalled()
    expect(api.generateProjectPlan).not.toHaveBeenCalled()
  })

  it('已生效任务行内填写完成日期即完成，阶段完成日期汇总，生效信息替代资源冲突卡片', async () => {
    const approved = { ...structuredClone(draft), approvalStatus: 'Approved', approvedBy: 'approver', approvedAt: '2026-09-10T10:00:00Z' }
    api.readProjectPlan.mockResolvedValue(approved)
    api.updateProjectPlanTaskProgress.mockResolvedValue(approved)
    const wrapper = render()
    await flushPromises()
    expect(wrapper.find('.pdm-plan-summary').text()).toContain('生效信息已生效approver 批准')
    expect(wrapper.find('.pdm-plan-summary').text()).not.toContain('资源冲突')
    expect(wrapper.find('.pdm-plan-approval-strip').exists()).toBe(false)
    expect(wrapper.find('.pdm-gantt-bar').classes()).not.toContain('is-neutral')
    // Target the explicitly labelled task cell, without opening the details drawer.
    await wrapper.find('[aria-label="编辑方案检查完成日期"]').trigger('click')
    const pickers = wrapper.find('.pdm-gantt-inline-editor.is-actual-finish').findAllComponents(ElDatePicker)
    expect(pickers).toHaveLength(2)
    pickers[1]!.vm.$emit('update:modelValue', '2026-09-10')
    await flushPromises()
    expect(document.body.textContent).not.toContain('任务详情与实际进度')
    const completed = { ...approved, tasks: approved.tasks.map(task => ({ ...task, completionPercent: 100, actualStart: '2026-09-10', actualFinish: '2026-09-10' })) }
    api.readProjectPlan.mockResolvedValue(completed)
    await clickText('保存完成日期')
    expect(api.updateProjectPlanTaskProgress).toHaveBeenCalledWith('child', 'task', { completionPercent: 100, actualStart: '2026-09-10', actualFinish: '2026-09-10', expectedRowVersion: 1 }, 'test')
    expect(api.saveProjectPlan).not.toHaveBeenCalled()
    expect(wrapper.find('.pdm-gantt-info-row.is-stage').text()).toContain('2026-09-15')
    expect(wrapper.find('.pdm-gantt-info-row.is-stage [aria-label$="完成日期"]').exists()).toBe(false)
  })

  it('完成日期仅生效计划的责任人或管理人员可填报，取消不保存', async () => {
    api.readProjectPlan.mockResolvedValue({ ...structuredClone(draft), approvalStatus: 'Approved', tasks: draft.tasks.map(task => ({...task, assignee: 'worker'})) })
    const wrapper = render()
    await wrapper.setProps({ canEdit: false, currentUsername: 'outsider', currentRole: 'Engineer' })
    await flushPromises()
    expect(wrapper.find('[aria-label="编辑方案检查完成日期"]').exists()).toBe(false)
    await wrapper.setProps({ currentUsername: 'worker' })
    await wrapper.find('[aria-label="编辑方案检查完成日期"]').trigger('click')
    await clickText('取消')
    expect(api.updateProjectPlanTaskProgress).not.toHaveBeenCalled()
    api.readProjectPlan.mockResolvedValue(structuredClone(draft))
    await clickText('刷新')
    expect(wrapper.find('[aria-label="编辑方案检查完成日期"]').exists()).toBe(false)
  })

  it('删除计划始终显示，确认前不删除，已生效或无权限时灰显', async () => {
    api.readProjectPlan.mockResolvedValue({ ...structuredClone(draft), projectId: 'root' })
    const wrapper = render(root)
    await wrapper.setProps({ projects: [root] })
    await flushPromises()
    await clickText('删除计划')
    expect(document.body.textContent).toContain('P1 · 主项目')
    expect(api.deleteProjectPlan).not.toHaveBeenCalled()
    await clickText('取消')
    await wrapper.setProps({ canEdit: false })
    expect(wrapper.find('.pdm-plan-delete-action').attributes('disabled')).toBeDefined()
    expect(wrapper.find('.pdm-plan-delete-action').attributes('title')).toBe('当前账号没有删除该计划的权限')
    api.readProjectPlan.mockResolvedValue({ ...structuredClone(draft), projectId: 'root', approvalStatus: 'Approved' })
    await wrapper.setProps({ canEdit: true })
    await clickText('刷新')
    expect(wrapper.find('.pdm-plan-delete-action').attributes('disabled')).toBeDefined()
    expect(wrapper.find('.pdm-plan-delete-action').attributes('title')).toContain('已审批生效')
  })

  it('总览提供子项目修改入口，已生效计划没有删除按钮', async () => {
    const wrapper = render(root)
    await flushPromises()
    await clickText('编辑子计划')
    const rows = document.querySelectorAll('.pdm-plan-management-list article')
    expect(rows).toHaveLength(3)
    expect(rows[0]!.textContent).toContain('删除计划')
    expect(rows[1]!.textContent).toContain('默认跟随主项目计划 · 未单独建立')
    expect(rows[1]!.textContent).toContain('单独设置计划')
    expect(rows[2]!.textContent).not.toContain('删除计划')
    await clickText('修改计划')
    expect(wrapper.emitted('switchProject')).toEqual([['child']])
    expect(api.deleteProjectPlan).not.toHaveBeenCalled()
  })

  it('子项目未建立独立计划时只读使用主计划且不显示创建入口', async () => {
    const rootPlan = { ...structuredClone(draft), id: 'root-plan', projectId: 'root', approvalStatus: 'Approved' as const }
    api.readProjectPlan.mockImplementation((projectId: string) => Promise.resolve(projectId === 'child' ? null : structuredClone(rootPlan)))
    api.readProjectPlanPortfolio.mockResolvedValue({ rootProjectId: 'root', currentStage: 'custom-review', completionPercent: 0, laggingProjectCount: 0, riskProjectCount: 0, projects: [
      { projectId: 'root', projectCode: 'P1', projectName: '主项目', isRoot: true, hasPlan: true, plan: rootPlan },
      { projectId: 'child', projectCode: 'P1-1', projectName: '设备一', hasPlan: false },
    ] })
    const wrapper = render()
    await flushPromises()
    expect(wrapper.text()).toContain('当前子项目未建立独立计划')
    expect(wrapper.text()).toContain('只读使用主项目 P1 · 主项目 的计划')
    expect(wrapper.text()).not.toContain('生成初始计划')
    expect(wrapper.find('.pdm-gantt-bar.is-draggable').exists()).toBe(false)
    expect(wrapper.find('[aria-label="编辑方案检查计划日期"]').exists()).toBe(false)
    expect(wrapper.find('[aria-label="编辑方案检查完成日期"]').exists()).toBe(false)
    await wrapper.find('.pdm-gantt-info-row.is-task').trigger('click')
    expect(document.body.textContent).not.toContain('任务详情与实际进度')
    expect(api.saveProjectPlan).not.toHaveBeenCalled()
    expect(api.updateProjectPlanTaskProgress).not.toHaveBeenCalled()
  })

  it('主项目未建立计划时子项目仅提示返回主项目且不能创建', async () => {
    api.readProjectPlan.mockResolvedValue(null)
    const wrapper = render()
    await flushPromises()
    expect(wrapper.text()).toContain('主项目尚未建立计划')
    expect(wrapper.text()).toContain('请返回主项目建立计划')
    expect(wrapper.text()).not.toContain('生成初始计划')
    expect(wrapper.find('.pdm-plan-delete-action').attributes('disabled')).toBeDefined()
    expect(wrapper.find('.pdm-plan-delete-action').attributes('title')).toBe('当前项目及其子项目没有可删除的未审批计划')
    expect(api.generateProjectPlan).not.toHaveBeenCalled()
  })

  it('主项目自身无计划但存在一份子项目草稿时可从顶部直接删除该草稿', async () => {
    const childDraft = { ...structuredClone(draft), projectId: 'child', approvalStatus: 'Draft' as const }
    api.readProjectPlan.mockImplementation((projectId: string) => Promise.resolve(projectId === 'child' ? structuredClone(childDraft) : null))
    api.readProjectPlanPortfolio.mockResolvedValue({ rootProjectId: 'root', currentStage: 'custom-review', completionPercent: 0, laggingProjectCount: 0, riskProjectCount: 0, projects: [
      { projectId: 'root', projectCode: 'P1', projectName: '主项目', isRoot: true, hasPlan: false },
      { projectId: 'child', projectCode: 'P1-1', projectName: '设备一', hasPlan: true, plan: childDraft },
      { projectId: 'target', projectCode: 'P1-2', projectName: '设备二', hasPlan: false },
    ] })
    const wrapper = render(root)
    await flushPromises()
    expect(wrapper.find('.pdm-plan-delete-action').attributes('disabled')).toBeUndefined()
    await clickText('删除计划')
    expect(document.body.textContent).toContain('确认删除 P1-1 · 设备一 的整份未批准计划')
    expect(api.deleteProjectPlan).not.toHaveBeenCalled()
    await clickText('取消')
  })

  it('主项目的子项目计划列表可为未单独排期的子项目建立独立计划', async () => {
    const rootPlan = { ...structuredClone(draft), id: 'root-plan', projectId: 'root' }
    api.listProjectPlanTemplates.mockResolvedValue([{ ...structuredClone(template), stages: [
      { code: 'custom-review', name: '方案确认', participatesInDelivery: true, durationRatio: 1, progressRatio: 1 },
      { code: 'custom-handover', name: '交付', participatesInDelivery: false, independentDurationDays: 1 },
    ] }])
    api.readProjectPlan.mockResolvedValue(rootPlan)
    api.generateProjectPlan.mockResolvedValue({ ...structuredClone(draft), projectId: 'target' })
    render(root)
    await flushPromises()
    await clickText('编辑子计划')
    const targetRow = [...document.querySelectorAll<HTMLElement>('.pdm-plan-management-list article')].find(item => item.textContent?.includes('P1-2 · 设备二'))!
    expect(targetRow.textContent).toContain('默认跟随主项目计划 · 未单独建立')
    targetRow.querySelector<HTMLButtonElement>('button')!.click()
    await flushPromises()
    expect(document.body.textContent).toContain('单独设置子项目计划')
    expect(document.body.textContent).toContain('P1-2 · 设备二 将建立独立计划')
    await clickText('生成计划')
    expect(api.generateProjectPlan).toHaveBeenCalledWith('target', expect.objectContaining({
      startDate: '2026-09-10', totalDurationDays: 6, replaceExisting: false,
    }), 'test')
  })

  it('删除整份未批准计划前读取最新版本，取消不调用接口，确认后删除并刷新', async () => {
    api.deleteProjectPlan.mockResolvedValue(undefined)
    const wrapper = render()
    await flushPromises()
    await clickText('删除计划')
    expect(document.body.textContent).toContain('P1-1 · 设备一')
    expect(api.deleteProjectPlan).not.toHaveBeenCalled()
    await clickText('取消')
    api.readProjectPlan.mockResolvedValueOnce({ ...structuredClone(draft), rowVersion: 2 })
    await clickText('删除计划')
    api.readProjectPlan.mockResolvedValue(null)
    await clickText('确认删除计划')
    expect(api.deleteProjectPlan).toHaveBeenCalledWith('child', 2, false, 'test')
    expect(wrapper.find('.pdm-plan-delete-action').attributes('disabled')).toBeDefined()
    expect(wrapper.find('.pdm-plan-delete-action').attributes('title')).toBe('当前项目及其子项目没有可删除的未审批计划')
    expect(document.body.textContent).toContain('主项目尚未建立计划')
    expect(document.body.textContent).not.toContain('生成初始计划')
  })

  it('总览修改按任务所属子项目授权，非其项目经理不能编辑安排', async () => {
    const wrapper = render(root)
    await wrapper.setProps({ projects: [root, { ...child, primaryProjectManager: 'other' }, target, approvedTarget] })
    await flushPromises()
    await clickText('编辑子计划')
    expect(document.querySelectorAll('.pdm-plan-management-list article')[0]!.textContent).toContain('查看计划')
    expect(document.querySelectorAll('.pdm-plan-management-list article')[0]!.textContent).not.toContain('删除计划')
    await clickText('关闭')
    const projectRow = wrapper.findAll('.pdm-gantt-info-row')[1]!
    await projectRow.trigger('click')
    await flushPromises()
    await wrapper.find('.pdm-gantt-info-row.is-task').trigger('click')
    await flushPromises()
    expect(wrapper.findAllComponents(ElDatePicker).find(item => item.props('type') === 'daterange')!.props('disabled')).toBe(true)
    expect(wrapper.find('.pdm-gantt-info-row.is-task .pdm-gantt-cell-edit').exists()).toBe(false)
  })

  it('系统管理员或协同项目经理也不能越权维护其他项目计划', async () => {
    const wrapper = render(root)
    await wrapper.setProps({
      canEdit: false,
      currentUsername: 'other-admin',
      currentRole: 'Administrator',
      projects: [
        { ...root, primaryProjectManager: 'root-pm', collaborativeProjectManagers: ['other-admin'] },
        { ...child, primaryProjectManager: 'child-pm', collaborativeProjectManagers: ['other-admin'] },
        target,
        approvedTarget,
      ],
    })
    await flushPromises()
    expect(wrapper.find('.pdm-plan-delete-action').attributes('disabled')).toBeDefined()
    await clickText('编辑子计划')
    expect(document.querySelectorAll('.pdm-plan-management-list article')[0]!.textContent).toContain('查看计划')
    expect(document.querySelectorAll('.pdm-plan-management-list article')[0]!.textContent).not.toContain('删除计划')
  })

  it('开发者保留跨项目维护和删除未审批计划的权限', async () => {
    const wrapper = render(root)
    await wrapper.setProps({
      canEdit: true,
      developer: true,
      currentUsername: 'developer',
      currentRole: 'Administrator',
      projects: [
        { ...root, primaryProjectManager: 'root-pm' },
        { ...child, primaryProjectManager: 'child-pm' },
        target,
        approvedTarget,
      ],
    })
    await flushPromises()
    expect(wrapper.find('.pdm-plan-delete-action').attributes('disabled')).toBeUndefined()
    await clickText('编辑子计划')
    expect(document.querySelectorAll('.pdm-plan-management-list article')[0]!.textContent).toContain('删除计划')
  })

  it('新计划交付进度与独立阶段分别显示，同责任人并行只提示不改变日期', async () => {
    const allocated = { ...structuredClone(draft), approvalStatus: 'Approved', stages: [
      { code: 'custom-review', name: '方案确认', participatesInDelivery: true, durationRatio: 1, progressRatio: 1 },
      { code: 'client', name: '客户端调试', participatesInDelivery: false, independentDurationDays: 10 },
    ], tasks: [
      { ...draft.tasks[0]!, assignee: 'pm', completionPercent: 50 },
      { ...draft.tasks[0]!, id: 'other', name: '现场调试', stage: 'client', assignee: 'pm', completionPercent: 0 },
    ] }
    api.readProjectPlan.mockResolvedValue(allocated)
    const wrapper = render()
    await flushPromises()
    expect(wrapper.find('.pdm-plan-summary').text()).toContain('交付进度50%')
    expect(wrapper.findAll('.pdm-plan-summary article')).toHaveLength(6)
    expect(wrapper.find('.pdm-plan-stage-progress').exists()).toBe(false)
    await wrapper.find('[aria-label="查看阶段进度详情"]').trigger('click')
    await flushPromises()
    expect(document.querySelector('.pdm-plan-stage-details')!.textContent).toContain('客户端调试（交付后）：0%')
    await clickText('关闭')
    expect(wrapper.find('.pdm-plan-summary').text()).toContain('生效信息已生效')
    expect(wrapper.find('.pdm-plan-summary').text()).not.toContain('资源冲突')
    expect(api.saveProjectPlan).not.toHaveBeenCalled()
  })
  it('生成新计划提交独立阶段排期，旧模板缺少配置时禁止生成', async () => {
    api.listProjectPlanTemplates.mockResolvedValue([{ ...template, stages: [
      { code: 'custom-review', name: '设计', participatesInDelivery: true, durationRatio: 1, progressRatio: 1 },
      { code: 'client', name: '客户端调试', participatesInDelivery: false, independentDurationDays: 12 },
    ] }])
    api.generateProjectPlan.mockResolvedValue(draft)
    render()
    await flushPromises()
    await clickText('重新生成')
    expect(document.body.textContent).toContain('客户端调试 · 开始日期')
    await clickText('生成计划')
    expect(api.generateProjectPlan).toHaveBeenCalledWith('child', expect.objectContaining({ independentStages: [{ stage: 'client', startDate: '2026-09-16', durationDays: 12 }] }), 'test')
  })
  it('时间轴随可用宽度适配，草稿甘特条和阶段使用中性显示', async () => {
    const original = globalThis.ResizeObserver
    let resized = () => {}
    let width = 980
    const clientWidth = vi.spyOn(HTMLElement.prototype, 'clientWidth', 'get').mockImplementation(() => width)
    vi.stubGlobal('ResizeObserver', class {
      constructor(callback: () => void) { resized = callback }
      observe() {}
      disconnect() {}
      unobserve() {}
    })
    try {
      const wrapper = render()
      await flushPromises()
      expect((wrapper.find('.pdm-gantt-timeline-head').element as HTMLElement).style.width).toBe('390px')
      width = 1400
      resized()
      await flushPromises()
      expect((wrapper.find('.pdm-gantt-timeline-head').element as HTMLElement).style.width).toBe('810px')
      expect(wrapper.find('.pdm-gantt-bar').classes()).toContain('is-neutral')
      expect(wrapper.find('.pdm-stage-badge').exists()).toBe(false)
    } finally {
      clientWidth.mockRestore()
      vi.stubGlobal('ResizeObserver', original)
    }
  })

  it('交付工期和客户端调试工期实时联动验收开始，暂不建立按连续关系联动', async () => {
    api.listProjectPlanTemplates.mockResolvedValue([{ ...template, stages: [
      { code: 'custom-review', name: '设计', participatesInDelivery: true, durationRatio: 1, progressRatio: 1 },
      { code: 'client', name: '客户端调试', participatesInDelivery: false, independentDurationDays: 12 },
      { code: 'accept', name: '验收推进', participatesInDelivery: false, independentDurationDays: 5 },
    ] }])
    api.generateProjectPlan.mockResolvedValue(draft)
    render()
    await flushPromises()
    await clickText('重新生成')
    const sections = () => [...document.querySelectorAll('.pdm-post-delivery-stage')]
    const date = (index: number) => sections()[index]!.querySelector<HTMLInputElement>('.el-date-editor input')!
    expect(date(0).value).toBe('2026-09-16')
    expect(date(1).value).toBe('2026-09-28')
    expect(date(1).disabled).toBe(true)
    const setNumber = async (label: string, value: string) => {
      const input = document.querySelector<HTMLInputElement>(`input[aria-label="${label}"]`)!
      expect(input).toBeTruthy()
      input.value = value
      input.dispatchEvent(new Event('input', { bubbles: true }))
      input.dispatchEvent(new Event('change', { bubbles: true }))
      await flushPromises()
    }
    await setNumber('交付总工期', '60')
    expect(date(0).value).toBe('2026-11-09')
    expect(date(1).value).toBe('2026-11-21')
    await setNumber('客户端调试阶段工期', '15')
    expect(date(1).value).toBe('2026-11-24')
    const defer = (index: number) => sections()[index]!.querySelector<HTMLInputElement>('input[type=checkbox]')!
    defer(0).click()
    await flushPromises()
    expect(defer(0).checked).toBe(true)
    expect(defer(1).checked).toBe(true)
    expect(date(0).disabled).toBe(true)
    defer(1).click()
    await flushPromises()
    expect(defer(0).checked).toBe(false)
    expect(defer(1).checked).toBe(false)
    defer(1).click()
    await flushPromises()
    await clickText('生成计划')
    expect(api.generateProjectPlan).toHaveBeenCalledWith('child', expect.objectContaining({
      totalDurationDays: 60, independentStages: [{ stage: 'client', startDate: '2026-11-09', durationDays: 15 }], deferredStages: ['accept'],
    }), 'test')
  })

  it('后两阶段全部暂不建立时提交空排期和明确的暂缓阶段列表', async () => {
    api.listProjectPlanTemplates.mockResolvedValue([{ ...template, stages: [
      { code: 'custom-review', name: '设计', participatesInDelivery: true, durationRatio: 1, progressRatio: 1 },
      { code: 'client', name: '客户端调试', participatesInDelivery: false, independentDurationDays: 15 },
      { code: 'accept', name: '验收推进', participatesInDelivery: false, independentDurationDays: 15 },
    ] }])
    api.generateProjectPlan.mockResolvedValue(draft)
    render()
    await flushPromises()
    await clickText('重新生成')
    document.querySelector<HTMLInputElement>('.pdm-post-delivery-stage input[type=checkbox]')!.click()
    await flushPromises()
    await clickText('生成计划')
    expect(api.generateProjectPlan).toHaveBeenCalledWith('child', expect.objectContaining({ independentStages: [], deferredStages: ['client', 'accept'] }), 'test')
  })

  it('目标子项目按复选框逐行显示，已生效计划不可覆盖', async () => {
    render(root)
    await flushPromises()
    await clickText('复制计划')
    const rows = [...document.querySelectorAll('.pdm-plan-targets__list .el-checkbox')]
    expect(rows).toHaveLength(2)
    expect(rows[0]!.textContent).toContain('P1-2 · 设备二')
    expect(rows[0]!.textContent).toContain('默认跟随主计划（复制后转独立）')
    expect(rows[0]!.querySelector('input')!.checked).toBe(true)
    expect(rows[1]!.textContent).toContain('已生效，不可覆盖')
    expect(rows[1]!.querySelector('input')!.disabled).toBe(true)
    rows[0]!.querySelector('input')!.click()
    await flushPromises()
    expect(document.querySelector('.pdm-plan-targets__header')!.textContent).toContain('已选 0')
    expect(api.reuseProjectPlan).not.toHaveBeenCalled()
  })

  it('草稿任务使用自定义阶段，保存不弹变更原因也不提交实际进度', async () => {
    const prompt = vi.spyOn(ElMessageBox, 'prompt')
    render()
    await flushPromises()
    expect(document.body.textContent).toContain('待计划生效')
    const row = document.querySelector<HTMLElement>('.pdm-gantt-info-row.is-task')!
    expect(row.textContent).not.toContain('方案确认')
    row.click()
    await flushPromises()
    expect(document.body.textContent).toContain('批准生效后才可填报实际进度')
    for (const label of ['任务名称', '阶段', '进度权重']) expect(document.querySelector<HTMLInputElement>(`input[aria-label="${label}"]`)!.readOnly).toBe(true)
    expect(document.querySelector<HTMLInputElement>('input[aria-label="阶段"]')!.value).toBe('方案确认')
    await clickText('保存')
    expect(prompt).not.toHaveBeenCalled()
    expect(api.saveProjectPlan).toHaveBeenCalledWith('child', expect.objectContaining({ changeReason: '', tasks: expect.arrayContaining([expect.objectContaining({ name: '方案检查', stage: 'custom-review', weight: 1 })]) }), 'test')
    expect(api.updateProjectPlanTaskProgress).not.toHaveBeenCalled()
  })

  it('项目页面不再提供模板编辑或复制入口，但保留模板选择', async () => {
    const wrapper = render()
    await flushPromises()
    expect(wrapper.text()).not.toContain('模板设置')
    expect(wrapper.text()).not.toContain('复制为新模板')
    expect(api.listProjectPlanTemplates).toHaveBeenCalledWith('test', false)
    await clickText('重新生成')
    expect(document.body.textContent).toContain('设备模板')
    expect(api.saveProjectPlanTemplate).not.toHaveBeenCalled()
  })

  it('主项目汇总收起阶段时同时收起项目明细，再次展开恢复阶段和任务', async () => {
    const rootPlan = { ...structuredClone(draft), id: 'root-plan', projectId: 'root' }
    api.readProjectPlan.mockResolvedValue(rootPlan)
    api.readProjectPlanPortfolio.mockResolvedValue({ rootProjectId: 'root', currentStage: 'custom-review', completionPercent: 0, laggingProjectCount: 0, riskProjectCount: 0, projects: [
      { projectId: 'root', projectCode: 'P1', projectName: '主项目', isRoot: true, hasPlan: true, plan: rootPlan },
      { projectId: 'child', projectCode: 'P1-1', projectName: '设备一', isRoot: false, hasPlan: true, plan: { ...structuredClone(draft), projectId: 'child' } },
      { projectId: 'target', projectCode: 'P1-2', projectName: '设备二', isRoot: false, hasPlan: false },
    ] })
    const wrapper = render(root)
    await flushPromises()
    await wrapper.findAll('.pdm-gantt-info-row.is-project')[0]!.trigger('click')
    expect(wrapper.find('.pdm-gantt-info-row.is-stage').exists()).toBe(true)
    expect(wrapper.find('.pdm-gantt-info-row.is-task').exists()).toBe(true)
    await wrapper.find('[aria-label="收起全部阶段"]').trigger('click')
    expect(wrapper.find('.pdm-gantt-info-row.is-stage').exists()).toBe(false)
    expect(wrapper.find('.pdm-gantt-info-row.is-task').exists()).toBe(false)
    expect(wrapper.findAll('.pdm-gantt-info-row')).toHaveLength(3)
    expect(wrapper.find('[aria-label="展开全部阶段"]').exists()).toBe(true)
    await wrapper.find('[aria-label="展开全部阶段"]').trigger('click')
    expect(wrapper.find('.pdm-gantt-info-row.is-stage').exists()).toBe(true)
    expect(wrapper.find('.pdm-gantt-info-row.is-task').exists()).toBe(true)
  })

  it('阶段可折叠且日期轴不变，提交审批紧邻刷新左侧', async () => {
    api.readProjectPlan.mockResolvedValue({ ...structuredClone(draft), tasks: [{ ...structuredClone(draft.tasks[0]!), status: 'Completed', completionPercent: 100, actualFinish: '2026-09-16' }] })
    const wrapper = render()
    await flushPromises()
    const ticks = wrapper.find('.pdm-gantt-timeline-head').text()
    expect(wrapper.find('.pdm-gantt-info-row.is-stage .pdm-gantt-name').text()).toBe('方案确认1 项任务')
    expect(wrapper.find('.pdm-gantt-info-row.is-task .pdm-gantt-name').text()).toBe('方案检查')
    expect(wrapper.find('[aria-label="编辑方案检查计划日期"]').text()).toBe('2026-09-10 ~ 2026-09-15')
    expect(wrapper.find('.pdm-gantt-info-head').text()).toContain('完成日期')
    expect(wrapper.find('.pdm-gantt-info-row.is-task > span:nth-child(6)').text()).toBe('2026-09-16')
    expect(wrapper.find('.pdm-gantt-info-row.is-task').exists()).toBe(true)
    await wrapper.find('.pdm-gantt-info-row.is-stage').trigger('click')
    expect(wrapper.find('.pdm-gantt-info-row.is-task').exists()).toBe(false)
    expect(wrapper.find('.pdm-gantt-timeline-head').text()).toBe(ticks)
    await wrapper.find('[aria-label="展开全部阶段"]').trigger('click')
    expect(wrapper.findAll('.pdm-gantt-info-row')).toHaveLength(wrapper.findAll('.pdm-gantt-timeline-row').length)
    expect(wrapper.findAll('.pdm-plan-toolbar__actions > button').slice(0, 3).map(item => item.text())).toEqual(['删除计划', '提交审批', '刷新'])
    expect(wrapper.find('.pdm-plan-summary').text()).toContain('生效信息草稿')
  })

  it('阶段列始终隐藏，左侧列可一键折叠并释放时间轴空间', async () => {
    vi.spyOn(HTMLElement.prototype, 'clientWidth', 'get').mockReturnValue(1390)
    const wrapper = render()
    await flushPromises()
    expect(wrapper.find('.pdm-gantt-info-head').text()).toBe('项目 / 任务责任人进度计划日期工期完成日期')
    expect(wrapper.find('[aria-label="收起全部阶段"]').exists()).toBe(true)
    expect(wrapper.find('[aria-label="折叠信息列"]').exists()).toBe(true)
    expect(wrapper.find('.pdm-gantt-info-head').text()).not.toContain('阶段')
    expect((wrapper.find('.pdm-gantt-timeline-head').element as HTMLElement).style.width).toBe('800px')
    await wrapper.find('[aria-label="折叠信息列"]').trigger('click')
    expect(wrapper.find('.pdm-gantt-table').classes()).toContain('is-info-collapsed')
    expect(wrapper.find('.pdm-gantt-info-head').text()).toBe('项目 / 任务计划日期工期')
    expect((wrapper.find('.pdm-gantt-timeline-head').element as HTMLElement).style.width).toBe('1040px')
    await wrapper.find('[aria-label="展开信息列"]').trigger('click')
    expect(wrapper.find('.pdm-gantt-table').classes()).not.toContain('is-info-collapsed')
    expect(wrapper.find('.pdm-gantt-info-head').text()).toBe('项目 / 任务责任人进度计划日期工期完成日期')
  })

  it('新计划默认60天，厂外调试计划统一暂不建立', async () => {
    api.readProjectPlan.mockResolvedValue(null)
    api.listProjectPlanTemplates.mockResolvedValue([{ ...template, stages: [
      { code: 'custom-review', name: '设计', participatesInDelivery: true, durationRatio: 1, progressRatio: 1 },
      { code: 'client', name: '客户端调试', participatesInDelivery: false, independentDurationDays: 15 },
      { code: 'accept', name: '验收推进', participatesInDelivery: false, independentDurationDays: 15 },
    ] }])
    render(root)
    await flushPromises()
    await clickText('编辑主计划')
    await clickText('生成初始计划')
    const today = new Date()
    const expectedToday = `${today.getFullYear()}-${String(today.getMonth() + 1).padStart(2, '0')}-${String(today.getDate()).padStart(2, '0')}`
    expect(document.querySelector<HTMLInputElement>('.pdm-plan-form__grid .el-date-editor input')!.value).toBe(expectedToday)
    expect(document.querySelector<HTMLInputElement>('input[aria-label="交付总工期"]')!.value).toBe('60')
    expect(document.querySelectorAll('.pdm-offsite-plan')).toHaveLength(1)
    expect(document.querySelector<HTMLInputElement>('.pdm-offsite-plan > header input[type=checkbox]')!.checked).toBe(true)
    expect([...document.querySelectorAll<HTMLInputElement>('.pdm-post-delivery-stage input[type=checkbox]')].every(item => item.checked)).toBe(true)
  })

  it('日期区间保存保留模板字段，实际进度提供0到100的十等分选择', async () => {
    api.readProjectPlan.mockResolvedValue({ ...draft, approvalStatus: 'Approved' })
    api.updateProjectPlanTaskProgress.mockResolvedValue(draft)
    const wrapper = render()
    await flushPromises()
    await wrapper.find('.pdm-gantt-info-row.is-task').trigger('click')
    await flushPromises()
    expect(wrapper.findAllComponents(ElDatePicker).filter(item => item.props('type') === 'daterange')).toHaveLength(1)
    const slider = wrapper.findComponent(ElSlider)
    expect(slider.props('step')).toBe(10)
    expect(slider.props('showStops')).toBe(true)
    expect(Object.keys(slider.props('marks') ?? {})).toEqual(['0','10','20','30','40','50','60','70','80','90','100'])
    slider.vm.$emit('update:modelValue', 30)
    await clickText('保存')
    expect(api.updateProjectPlanTaskProgress).toHaveBeenCalledWith('child', 'task', expect.objectContaining({ completionPercent: 30, expectedRowVersion: 1 }), 'test')
  })

  it('详情日期区间同时更新起止日期且不改模板字段', async () => {
    const wrapper = render()
    await flushPromises()
    await wrapper.find('.pdm-gantt-info-row.is-task').trigger('click')
    await flushPromises()
    const picker = wrapper.findAllComponents(ElDatePicker).find(item => item.props('type') === 'daterange')!
    picker.vm.$emit('update:modelValue', ['2026-09-15', '2026-09-20'])
    await clickText('保存')
    expect(api.saveProjectPlan).toHaveBeenCalledWith('child', expect.objectContaining({ tasks: [expect.objectContaining({ ...draft.tasks[0], plannedStart: '2026-09-15', plannedFinish: '2026-09-20' })] }), 'test')
  })

  it('责任人和日期行内编辑不打开详情，保存携带版本且只修改选中字段', async () => {
    const wrapper = render()
    await flushPromises()
    await wrapper.find('[aria-label="编辑方案检查责任人"]').trigger('click')
    expect(document.body.textContent).not.toContain('任务详情与实际进度')
    wrapper.findComponent(ElSelect).vm.$emit('update:modelValue', 'pm')
    await clickText('保存责任人')
    expect(api.saveProjectPlan).toHaveBeenLastCalledWith('child', expect.objectContaining({ expectedRowVersion: 1, tasks: [expect.objectContaining({ ...draft.tasks[0], assignee: 'pm' })] }), 'test')
    await wrapper.find('[aria-label="编辑方案检查计划日期"]').trigger('click')
    wrapper.findComponent(ElDatePicker).vm.$emit('update:modelValue', ['2026-09-12', '2026-09-18'])
    await clickText('保存日期')
    expect(api.saveProjectPlan).toHaveBeenLastCalledWith('child', expect.objectContaining({ tasks: [expect.objectContaining({ ...draft.tasks[0], plannedStart: '2026-09-12', plannedFinish: '2026-09-18' })] }), 'test')
    expect(document.body.textContent).not.toContain('任务详情与实际进度')
  })

  it('已生效计划排期只读，申请仅提交变更权限且原排期不变', async () => {
    const approved = { ...structuredClone(draft), approvalStatus: 'Approved', approvedBy: 'approver' }
    api.readProjectPlan.mockResolvedValue(approved)
    api.submitProjectPlanChange.mockResolvedValue(approved)
    const wrapper = render()
    await flushPromises()
    expect(wrapper.find('.pdm-gantt-bar.is-draggable').exists()).toBe(false)
    expect(wrapper.find('[aria-label="编辑方案检查责任人"]').exists()).toBe(false)
    expect(wrapper.find('[aria-label="编辑方案检查计划日期"]').exists()).toBe(false)
    expect(wrapper.find('[aria-label="编辑方案检查工期"]').exists()).toBe(false)
    expect(wrapper.find('[aria-label="编辑方案检查完成日期"]').exists()).toBe(true)
    await clickText('申请变更权限')
    const input = document.querySelector<HTMLTextAreaElement>('textarea[aria-label="计划变更原因"]')!
    input.value = '客户调整交期'
    input.dispatchEvent(new Event('input', { bubbles: true }))
    api.readProjectPlan.mockResolvedValue({ ...approved, changeRequest: { id: 'request', tasks: [], reason: '客户调整交期', submittedBy: 'pm', submittedAt: '', approvalAssignee: 'approver', status: 'Pending' } })
    await clickText('提交权限申请')
    expect(api.submitProjectPlanChange).toHaveBeenCalledWith('child', { tasks: [], reason: '客户调整交期', expectedRowVersion: 1 }, 'test')
    expect(wrapper.find('.pdm-gantt-info-row.is-task').text()).toContain('2026-09-10 ~ 2026-09-15')
    await clickText('权限待审批')
    expect(document.body.textContent).toContain('未预先提交任务排期修改')
    expect([...document.querySelectorAll('button')].some(item => item.textContent === '批准权限')).toBe(false)
    await wrapper.setProps({ currentUsername: 'approver' })
    expect([...document.querySelectorAll('button')].some(item => item.textContent === '批准权限')).toBe(true)
    expect(api.saveProjectPlan).not.toHaveBeenCalled()
  })

  it('获批后的变更草稿可版本对比并由申请人完成生效', async () => {
    const source = { ...structuredClone(draft), approvalStatus: 'Approved' as const, approvedBy: 'approver' }
    const editable = { ...structuredClone(source), rowVersion: 5,
      tasks: [{ ...structuredClone(source.tasks[0]!), plannedStart: '2026-09-12', plannedFinish: '2026-09-18', completionPercent: 20 }],
      changeDraftSource: source,
      changeRequest: { id: 'request', tasks: [], reason: '客户调整交期', submittedBy: 'pm', submittedAt: '', approvalAssignee: 'approver', status: 'Approved' as const },
    }
    api.readProjectPlan.mockResolvedValue(editable)
    api.listProjectPlanVersions.mockResolvedValue([])
    api.completeProjectPlanChange.mockResolvedValue({ ...editable, changeDraftSource: null, baselineVersion: 2, rowVersion: 6 })
    vi.spyOn(ElMessageBox, 'confirm').mockResolvedValue('confirm' as never)
    const wrapper = render()
    await flushPromises()
    expect(wrapper.text()).toContain('正在编辑变更草稿')
    expect(wrapper.find('.pdm-gantt-bar.is-draggable').exists()).toBe(true)
    await clickText('版本对比')
    expect(document.body.textContent).toContain('本次变更前的现行计划')
    expect(document.body.textContent).toContain('2026-09-10 ～ 2026-09-15')
    expect(document.body.textContent).toContain('2026-09-12 ～ 2026-09-18')
    await clickText('完成变更并生效')
    expect(api.completeProjectPlanChange).toHaveBeenCalledWith('child', 5, 'test')
  })

  it('获批后的变更申请人可删除现行计划和草稿并重新生成', async () => {
    const source = { ...structuredClone(draft), approvalStatus: 'Approved' as const, approvedBy: 'approver' }
    const editable = { ...structuredClone(source), rowVersion: 5, changeDraftSource: source,
      changeRequest: { id: 'request', tasks: [], reason: '重新规划', submittedBy: 'pm', submittedAt: '', approvalAssignee: 'approver', status: 'Approved' as const },
    }
    api.readProjectPlan.mockResolvedValue(editable)
    api.deleteProjectPlan.mockResolvedValue(undefined)
    const wrapper = render()
    await flushPromises()

    const deleteButton = wrapper.find('.pdm-plan-delete-action')
    expect(deleteButton.attributes('disabled')).toBeUndefined()
    expect(deleteButton.attributes('title')).toBe('删除现行计划和变更草稿')
    await clickText('删除计划')
    expect(document.body.textContent).toContain('现行计划和变更草稿')
    expect(document.body.textContent).toContain('实际进度、审批记录和历史版本将永久移除')
    api.readProjectPlan.mockResolvedValue(null)
    await clickText('确认删除计划')
    expect(api.deleteProjectPlan).toHaveBeenCalledWith('child', 5, false, 'test')
    expect(wrapper.find('.pdm-plan-delete-action').attributes('disabled')).toBeDefined()
  })

  it('审批生效计划不显示单独设置基线并可填写原因恢复正常阶段', async () => {
    const exceptionPlan = { ...structuredClone(draft), approvalStatus: 'Approved' as const, approvedBy: 'approver', baselineVersion: 1, manualStage: 'Paused' as const, manualStageReason: '客户暂停' }
    api.readProjectPlan.mockResolvedValue(exceptionPlan)
    api.setProjectPlanStage.mockResolvedValue({ ...exceptionPlan, currentStage: 'custom-review', manualStage: undefined, manualStageReason: undefined, rowVersion: 2 })
    const wrapper = render()
    await flushPromises()

    expect(wrapper.text()).not.toContain('设置基线')
    await clickText('恢复正常阶段')
    expect(document.body.textContent).toContain('当前处于“暂停”状态')
    const reason = document.querySelector<HTMLTextAreaElement>('textarea[aria-label="恢复正常阶段原因"]')!
    reason.value = '客户已确认恢复执行'
    reason.dispatchEvent(new Event('input', { bubbles: true }))
    await clickText('确认恢复')

    expect(api.setProjectPlanStage).toHaveBeenCalledWith('child', { stage: undefined, reason: '客户已确认恢复执行', expectedRowVersion: 1 }, 'test')
  })

  it('变更原因显示必填星号，未填写时在当前弹窗显示校验错误', async () => {
    api.readProjectPlan.mockResolvedValue({ ...structuredClone(draft), approvalStatus: 'Approved', approvedBy: 'approver' })
    render()
    await flushPromises()
    await clickText('申请变更权限')
    expect(document.querySelector('.pdm-plan-required')?.textContent).toBe('*')
    await clickText('提交权限申请')
    const dialog = [...document.querySelectorAll('.el-dialog')].find(item => item.textContent?.includes('申请计划变更'))!
    expect(dialog.querySelector('[role="alert"]')?.textContent).toContain('表单校验失败')
    expect(dialog.querySelector('[role="alert"]')?.textContent).toContain('请填写变更原因')
    expect(api.submitProjectPlanChange).not.toHaveBeenCalled()
  })

  it('变更申请接口报错时在当前弹窗显示详情并保留输入', async () => {
    api.readProjectPlan.mockResolvedValue({ ...structuredClone(draft), approvalStatus: 'Approved', approvedBy: 'approver' })
    api.submitProjectPlanChange.mockRejectedValueOnce(new Error('计划数据已更新，请重新确认后操作。'))
    const wrapper = render()
    await flushPromises()
    await clickText('申请变更权限')
    const input = document.querySelector<HTMLTextAreaElement>('textarea[aria-label="计划变更原因"]')!
    input.value = '客户调整交期'
    input.dispatchEvent(new Event('input', { bubbles: true }))
    await clickText('提交权限申请')
    const dialog = [...document.querySelectorAll('.el-dialog')].find(item => item.textContent?.includes('申请计划变更'))!
    expect(dialog.querySelector('[role="alert"]')?.textContent).toContain('变更申请提交失败')
    expect(dialog.querySelector('[role="alert"]')?.textContent).toContain('计划数据已更新，请重新确认后操作。')
    expect(dialog.querySelector<HTMLTextAreaElement>('textarea[aria-label="计划变更原因"]')?.value).toBe('客户调整交期')
  })

  it('审批中仍可拖动、编辑和重新生成，修改会提示自动撤回审批', async () => {
    api.readProjectPlan.mockResolvedValue({ ...draft, approvalStatus: 'Pending' })
    const wrapper = render()
    await flushPromises()
    expect(wrapper.find('.pdm-gantt-bar.is-draggable').exists()).toBe(true)
    expect(wrapper.find('[aria-label="编辑方案检查计划日期"]').exists()).toBe(true)
    expect(wrapper.find('[aria-label="编辑方案检查工期"]').exists()).toBe(true)
    expect(wrapper.text()).toContain('重新生成')
    expect(wrapper.find('.pdm-plan-summary').text()).toContain('生效信息待审批')
    expect(wrapper.text()).not.toContain('修改计划会自动撤回本次审批')
    expect(wrapper.text()).toContain('删除计划')
  })
})
