import { mount } from '@vue/test-utils'
import { ElMessageBox } from 'element-plus'
import ElementPlus from 'element-plus'
import { describe, expect, it, vi } from 'vitest'
import DrawingReviewPanel from '../src/components/DrawingReviewPanel.vue'
import type { DrawingReviewCandidate, DrawingReviewPackage } from '../src/types'

const review: DrawingReviewPackage = {
  id: 'review-1',
  projectId: 'project-1',
  number: 'DR-20260821-00000001',
  state: 'InReview',
  createdBy: 'submitter',
  createdAt: '2026-08-21T00:00:00Z',
  items: [{
    id: 'item-1',
    packageId: 'review-1',
    bomItemId: 'bom-1',
    drawingNumber: 'A01-100',
    name: '机架组件',
    modelDocumentId: 'model-1',
    modelVersionId: 'model-version-1',
    modelRevision: 'W1',
    modelSha256: 'A'.repeat(64),
    modelCreatedBy: 'designer',
    drawingDocumentId: 'drawing-1',
    drawingVersionId: 'drawing-version-1',
    drawingRevision: 'W1',
    drawingSha256: 'B'.repeat(64),
    drawingCreatedBy: 'drawing-designer',
    modelState: 'NotRequired',
    drawingState: 'Pending',
    effectiveModelVersionId: 'model-version-1',
    effectiveDrawingVersionId: 'drawing-version-1',
  }],
  markups: [],
}

const permissions = { pending: false, canSubmit: true, canAnnotate: true, canDecide: true, desktopAvailable: false, reviewerOptions: [{ username: 'reviewer', label: '审核员（reviewer）' }] }
const candidate: DrawingReviewCandidate = {
  candidateId: 'candidate-1',
  modelDocumentId: 'model-1',
  drawingDocumentId: 'drawing-1',
  drawingNumber: 'A01-100',
  name: '机架组件',
  configuration: '默认',
  bomKinds: ['NonStandard'],
  modelRevision: 'W1',
  drawingRevision: 'W1',
  state: 'Ready',
  selectable: true,
}

describe('DrawingReviewPanel', () => {
  it('没有审核单时从图档侧栏选择非标2D范围后发起审核', async () => {
    const wrapper = mount(DrawingReviewPanel, {
      global: { plugins: [ElementPlus] },
      props: { packageId: '', packages: [], candidates: [candidate], selectedDocumentId: 'model-1', currentUsername: 'designer', ...permissions },
    })

    await wrapper.get('.drawing-review-panel__empty button').trigger('click')
    expect(wrapper.text()).toContain('选择审核范围')
    expect(wrapper.findAll('.drawing-review-scope__modes')).toHaveLength(0)
    expect(wrapper.findAll('.drawing-review-scope__hint')).toHaveLength(0)
    const candidateRow = wrapper.get('.drawing-review-candidate')
    expect(candidateRow.classes()).toContain('is-selected')
    expect(candidateRow.text()).toBe('✓A01-100待提交')
    expect(candidateRow.text()).not.toContain('机架组件')
    expect(candidateRow.text()).not.toContain('2D W1')
    expect(candidateRow.attributes('title')).toBe('机架组件')
    expect(wrapper.findAll('.drawing-review-scope__footer button')).toHaveLength(0)
    expect(wrapper.get('.drawing-review-panel__header .drawing-review-submit').text()).toContain('发起审核')
    await wrapper.get('.drawing-review-panel__header .drawing-review-submit').trigger('click')
    expect(wrapper.emitted('create')).toEqual([[['model-1'], ['reviewer']]])
  })

  it('审核栏可折叠为窄条并可再次展开', async () => {
    const wrapper = mount(DrawingReviewPanel, {
      global: { plugins: [ElementPlus] },
      props: { packageId: review.id, packages: [review], candidates: [candidate], selectedDocumentId: 'drawing-1', currentUsername: 'reviewer', ...permissions },
    })

    expect(wrapper.classes()).not.toContain('is-collapsed')
    expect(wrapper.get('.drawing-review-panel__header .drawing-review-collapse').attributes('aria-label')).toBe('折叠图纸审核栏')

    await wrapper.get('.drawing-review-panel__header .drawing-review-collapse').trigger('click')
    expect(wrapper.classes()).toContain('is-collapsed')
    expect(wrapper.find('.drawing-review-panel__toolbar').exists()).toBe(false)

    await wrapper.get('.drawing-review-panel__header .drawing-review-collapse').trigger('click')
    expect(wrapper.classes()).not.toContain('is-collapsed')
    expect(wrapper.find('.drawing-review-panel__toolbar').exists()).toBe(true)
  })

  it('浮层窗口内同样可以折叠和展开审核栏', async () => {
    const wrapper = mount(DrawingReviewPanel, {
      global: { plugins: [ElementPlus] },
      props: { packageId: review.id, packages: [review], candidates: [candidate], selectedDocumentId: 'drawing-1', currentUsername: 'reviewer', overlayHosted: true, ...permissions },
    })

    expect(wrapper.find('.drawing-review-close').exists()).toBe(false)
    expect(wrapper.classes()).not.toContain('is-collapsed')

    await wrapper.get('.drawing-review-panel__header .drawing-review-collapse').trigger('click')
    expect(wrapper.classes()).toContain('is-collapsed')
    expect(wrapper.emitted('update:collapsed')).toEqual([[true]])
  })

  it('发起审核按钮位于刷新右侧并打开审核范围', async () => {
    const wrapper = mount(DrawingReviewPanel, {
      global: { plugins: [ElementPlus] },
      props: { packageId: review.id, packages: [review], candidates: [candidate], selectedDocumentId: 'model-1', currentUsername: 'submitter', ...permissions },
    })

    const toolbarButtons = wrapper.findAll('.drawing-review-panel__toolbar button')
    expect(toolbarButtons.map(button => button.text())).toEqual(['刷新', '发起审核'])
    expect(wrapper.findAll('.drawing-review-package-actions button').map(button => button.text())).toEqual(['撤销审核'])

    await toolbarButtons[1]!.trigger('click')
    expect(wrapper.text()).toContain('选择审核范围')
  })

  it('明细分为待操作/已操作两页并可定位图纸', async () => {
    const candidates: DrawingReviewCandidate[] = [
      candidate,
      { ...candidate, candidateId: 'in-review', modelDocumentId: 'model-2', drawingDocumentId: 'drawing-2', drawingNumber: 'A01-200', state: 'InReview', selectable: false },
      { ...candidate, candidateId: 'approved', modelDocumentId: 'model-3', drawingDocumentId: 'drawing-3', drawingNumber: 'A01-300', state: 'ApprovedCurrent' },
      { ...candidate, candidateId: 'unavailable', modelDocumentId: 'model-4', drawingDocumentId: 'drawing-4', drawingNumber: 'A01-400', state: 'Unavailable', selectable: false },
    ]
    const reviewWithSecondDrawing: DrawingReviewPackage = {
      ...review,
      items: [{ ...review.items[0]!, modelDocumentId: 'model-2', drawingDocumentId: 'drawing-2', drawingNumber: 'A01-200' }],
    }
    const wrapper = mount(DrawingReviewPanel, {
      global: { plugins: [ElementPlus] },
      props: { packageId: review.id, packages: [reviewWithSecondDrawing], candidates, selectedDocumentId: 'model-1', currentUsername: 'reviewer', ...permissions },
    })

    // 默认「待操作」：待提交、待审核、不可发起（已批准属于已操作页）。
    expect(wrapper.get('[role="tab"][aria-selected="true"]').text()).toBe('待操作（3）')
    const todoRows = wrapper.findAll('.drawing-review-overview__row')
    expect(todoRows).toHaveLength(3)
    expect(wrapper.get('[aria-label="图纸审核状态表"]').text()).toContain('A01-100待提交')
    expect(wrapper.get('[aria-label="图纸审核状态表"]').text()).toContain('A01-200待审核')
    expect(wrapper.get('[aria-label="图纸审核状态表"]').text()).toContain('A01-400不可发起')
    expect(wrapper.get('[aria-label="图纸审核状态表"]').text()).not.toContain('A01-300')
    expect(todoRows[1]!.classes()).toContain('is-warning')
    expect(todoRows[2]!.classes()).toContain('is-neutral')

    await todoRows[1]!.trigger('click')
    expect(wrapper.emitted('update:packageId')).toContainEqual(['review-1'])
    expect(wrapper.emitted('selectDocument')).toContainEqual(['drawing-2'])

    // 切到「已操作」：只剩已批准的图纸。
    const doneTab = wrapper.findAll('[role="tab"]').find(tab => tab.text().startsWith('已操作'))!
    await doneTab.trigger('click')
    const doneRows = wrapper.findAll('.drawing-review-overview__row')
    expect(doneRows).toHaveLength(1)
    expect(doneRows[0]!.text()).toContain('A01-300')
    expect(doneRows[0]!.text()).toContain('已批准')
    expect(doneRows[0]!.classes()).toContain('is-success')

    // 状态下拉与选项卡叠加生效：只看待审核时，已操作页为空。
    await wrapper.get('select[aria-label="筛选图纸审核状态"]').setValue('InReview')
    expect(wrapper.findAll('[role="tab"]').map(tab => tab.text())).toEqual(['待操作（1）', '已操作（0）'])
  })

  it('状态表只显示状态，审核人或批准人放在悬停提示', async () => {
    const candidates: DrawingReviewCandidate[] = [
      { ...candidate, candidateId: 'ready', drawingNumber: 'A01-100', state: 'Ready' },
      { ...candidate, candidateId: 'in-review', modelDocumentId: 'model-2', drawingDocumentId: 'drawing-2', drawingNumber: 'A01-200', state: 'InReview', selectable: false },
      { ...candidate, candidateId: 'pending-supervisor', modelDocumentId: 'model-3', drawingDocumentId: 'drawing-3', drawingNumber: 'A01-300', state: 'InReview', selectable: false },
    ]
    const item = (modelDocumentId: string, drawingDocumentId: string, drawingNumber: string) =>
      ({ ...review.items[0]!, modelDocumentId, drawingDocumentId, drawingNumber })
    const reviewerPackage: DrawingReviewPackage = {
      ...review,
      assignedReviewer: 'wangguifeng',
      assignedReviewerName: '王贵锋',
      items: [item('model-2', 'drawing-2', 'A01-200')],
    }
    const supervisorPackage: DrawingReviewPackage = {
      ...review,
      id: 'review-2',
      state: 'PendingSupervisorApproval',
      assignedReviewer: 'wangguifeng',
      assignedReviewerName: '王贵锋',
      supervisor: 'lizhuguan',
      supervisorName: '李主管',
      items: [item('model-3', 'drawing-3', 'A01-300')],
    }
    const wrapper = mount(DrawingReviewPanel, {
      global: { plugins: [ElementPlus] },
      props: {
        packageId: reviewerPackage.id,
        packages: [reviewerPackage, supervisorPackage],
        candidates,
        selectedDocumentId: 'drawing-1',
        currentUsername: 'reviewer',
        ...permissions,
      },
    })

    const rows = wrapper.findAll('.drawing-review-overview__row')
    expect(rows).toHaveLength(3)
    expect(rows[0]!.find('em').text()).toBe('待提交')
    expect(rows[0]!.find('.drawing-review-overview__reviewer').exists()).toBe(false)
    // 状态表只显示状态，审核人/批准人放在悬停提示里。
    expect(rows.every(row => row.find('.drawing-review-overview__reviewer').exists())).toBe(false)
    expect(rows[1]!.find('em').text()).toBe('待审核')
    expect(rows[1]!.find('em').attributes('title')).toBe('王贵锋')
    expect(rows[2]!.find('em').text()).toBe('待审核')
    expect(rows[2]!.find('em').attributes('title')).toBe('李主管')
  })

  it('状态表按审核项结论显示，通过后不再停留在审核中', async () => {
    const candidates: DrawingReviewCandidate[] = [
      { ...candidate, candidateId: 'approved', modelDocumentId: 'model-2', drawingDocumentId: 'drawing-2', drawingNumber: 'A01-200', state: 'InReview', selectable: false },
      { ...candidate, candidateId: 'rejected', modelDocumentId: 'model-3', drawingDocumentId: 'drawing-3', drawingNumber: 'A01-300', state: 'InReview', selectable: false },
    ]
    const passedPackage: DrawingReviewPackage = {
      ...review,
      items: [
        { ...review.items[0]!, modelDocumentId: 'model-2', drawingDocumentId: 'drawing-2', drawingNumber: 'A01-200', drawingState: 'Approved' },
        { ...review.items[0]!, id: 'item-3', modelDocumentId: 'model-3', drawingDocumentId: 'drawing-3', drawingNumber: 'A01-300', drawingState: 'ChangesRequested' },
      ],
    }
    const wrapper = mount(DrawingReviewPanel, {
      global: { plugins: [ElementPlus] },
      props: { packageId: passedPackage.id, packages: [passedPackage], candidates, selectedDocumentId: 'drawing-1', currentUsername: 'reviewer', ...permissions },
    })

    // 已通过（待批准）与已退回都归入「已操作」页，默认待操作页为空并给出提示。
    expect(wrapper.findAll('.drawing-review-overview__row')).toHaveLength(0)
    expect(wrapper.get('[aria-label="图纸审核状态表"]').text()).toContain('当前没有需要操作的图纸')
    await wrapper.findAll('[role="tab"]').find(tab => tab.text().startsWith('已操作'))!.trigger('click')
    const rows = wrapper.findAll('.drawing-review-overview__row')
    expect(rows).toHaveLength(2)
    expect(rows[0]!.find('em').text()).toContain('待批准')
    expect(rows[0]!.classes()).toContain('is-pending')
    expect(rows[1]!.find('em').text()).toContain('已退回（待修改）')
    expect(rows[1]!.classes()).toContain('is-danger')
    expect(wrapper.text()).toContain('1/2项完成')
  })

  it('没有有效非标BOM候选时显示可操作的门禁说明', async () => {
    const wrapper = mount(DrawingReviewPanel, {
      global: { plugins: [ElementPlus] },
      props: { packageId: '', packages: [], candidates: [], selectedDocumentId: 'drawing-1', currentUsername: 'designer', ...permissions },
    })

    await wrapper.get('.drawing-review-panel__empty button').trigger('click')

    expect(wrapper.get('.drawing-review-no-candidate').text()).toContain('当前没有有效非标件BOM候选')
    expect(wrapper.get('.drawing-review-no-candidate').text()).toContain('唯一关联的2D工程图')
    expect(wrapper.get('.drawing-review-panel__header .drawing-review-submit').attributes('disabled')).toBeDefined()
  })

  it('已有审核中的图纸包时仍可继续发起其余图纸', async () => {
    const wrapper = mount(DrawingReviewPanel, {
      global: { plugins: [ElementPlus] },
      props: {
        packageId: review.id,
        packages: [review],
        candidates: [candidate],
        selectedDocumentId: 'unreviewed-model',
        currentUsername: 'designer',
        ...permissions,
      },
    })

    const createButton = wrapper.get('.drawing-review-toolbar__create')
    expect(createButton.attributes('disabled')).toBeUndefined()
    await createButton.trigger('click')
    await wrapper.get('.drawing-review-panel__header .drawing-review-submit').trigger('click')
    expect(wrapper.emitted('create')).toEqual([[['model-1'], ['reviewer']]])
  })

  it('shows source-less non-standard BOM rows as disabled blockers', async () => {
    const blocker: DrawingReviewCandidate = {
      candidateId: 'bom-blocker',
      bomItemId: 'bom-blocker',
      modelDocumentId: null,
      drawingDocumentId: null,
      drawingNumber: '02040000005',
      name: '夹紧胶2',
      bomKinds: ['NonStandard'],
      modelRevision: '—',
      state: 'Unavailable',
      reason: '非标BOM没有来源模型，无法定位唯一2D工程图',
      selectable: false,
    }
    const wrapper = mount(DrawingReviewPanel, {
      global: { plugins: [ElementPlus] },
      props: { packageId: '', packages: [], candidates: [candidate, blocker], selectedDocumentId: 'model-1', currentUsername: 'designer', ...permissions },
    })

    await wrapper.get('.drawing-review-panel__empty button').trigger('click')

    const blockedRow = wrapper.get('.drawing-review-candidate.is-unavailable')
    expect(blockedRow.attributes('disabled')).toBeDefined()
    expect(blockedRow.text()).toContain('02040000005')
    expect(blockedRow.text()).toContain('不可发起')
    expect(blockedRow.text()).not.toContain('没有来源模型')
    expect(blockedRow.attributes('title')).toContain('没有来源模型')
    expect(wrapper.get('.drawing-review-scope__footer').text()).toContain('已选择 1 张')
  })

  it('显示指定审核人到机械主管的两级节点并限制当前处理人', async () => {
    const routedReview: DrawingReviewPackage = {
      ...review,
      assignedReviewers: ['reviewer'],
      assignedReviewerNames: ['指定审核员'],
      supervisor: 'manager',
      supervisorName: '机械主管',
    }
    const wrapper = mount(DrawingReviewPanel, {
      global: { plugins: [ElementPlus] },
      props: { packageId: routedReview.id, packages: [routedReview], candidates: [candidate], selectedDocumentId: 'drawing-1', currentUsername: 'other', ...permissions },
    })

    expect(wrapper.text()).toContain('审核：指定审核员 → 批准：机械主管')
    expect(wrapper.find('.drawing-review-decision-bar').exists()).toBe(false)
  })

  it('未选择审核人时发起按钮置灰，选择多人后按并行提交', async () => {
    const openReview: DrawingReviewPackage = { ...review, assignedReviewers: [], assignedReviewerNames: [] }
    const twoReviewers = {
      ...permissions,
      reviewerOptions: [
        { username: 'reviewer', label: '审核员（reviewer）' },
        { username: 'reviewer-b', label: '审图员（reviewer-b）' },
      ],
    }
    const wrapper = mount(DrawingReviewPanel, {
      global: { plugins: [ElementPlus] },
      props: { packageId: openReview.id, packages: [openReview], candidates: [candidate], selectedDocumentId: 'drawing-1', currentUsername: 'reviewer-b', ...twoReviewers },
    })

    await wrapper.get('.drawing-review-toolbar__create').trigger('click')
    expect(wrapper.get('.drawing-review-panel__header .drawing-review-submit').attributes('disabled')).toBeDefined()
    await wrapper.get('.drawing-review-panel__header .drawing-review-submit').trigger('click')
    expect(wrapper.emitted('create')).toBeUndefined()

    await wrapper.findComponent({ name: 'ElSelect' }).vm.$emit('update:modelValue', ['reviewer-b'])
    await wrapper.vm.$nextTick()
    expect(wrapper.get('.drawing-review-panel__header .drawing-review-submit').attributes('disabled')).toBeUndefined()

    await wrapper.get('.drawing-review-panel__header .drawing-review-submit').trigger('click')
    expect(wrapper.emitted('create')).toEqual([[['model-1'], ['reviewer-b']]])
  })

  it('默认全选待审图纸，并可手工增选或取消已审核版本', async () => {
    const candidates: DrawingReviewCandidate[] = [
      candidate,
      { ...candidate, candidateId: 'second-candidate', modelDocumentId: 'second-model', drawingDocumentId: 'second-drawing', drawingNumber: 'NS-200' },
      { ...candidate, candidateId: 'approved-candidate', modelDocumentId: 'approved-model', drawingDocumentId: 'approved-drawing', drawingNumber: 'OLD-100', state: 'ApprovedCurrent', reason: '当前版本已审核，可选择重新审核' },
    ]
    const wrapper = mount(DrawingReviewPanel, {
      global: { plugins: [ElementPlus] },
      props: { packageId: '', packages: [], candidates, selectedDocumentId: 'model-1', currentUsername: 'designer', ...permissions },
    })
    await wrapper.get('.drawing-review-panel__empty button').trigger('click')
    expect(wrapper.findAll('.drawing-review-scope__modes')).toHaveLength(0)
    expect(wrapper.findAll('.drawing-review-candidate.is-selected')).toHaveLength(2)
    const approved = wrapper.findAll('.drawing-review-candidate').find(button => button.text().includes('OLD-100'))!
    await approved.trigger('click')
    expect(approved.classes()).toContain('is-selected')
    await approved.trigger('click')
    expect(approved.classes()).not.toContain('is-selected')
  })

  it('发起人可填写原因撤销审核', async () => {
    const prompt = vi.spyOn(ElMessageBox, 'prompt').mockResolvedValue({ value: '范围选择错误', action: 'confirm' } as never)
    const wrapper = mount(DrawingReviewPanel, {
      global: { plugins: [ElementPlus] },
      props: { packageId: review.id, packages: [review], candidates: [candidate], selectedDocumentId: 'model-1', currentUsername: 'submitter', ...permissions },
    })

    await wrapper.get('.drawing-review-package-actions .is-danger').trigger('click')
    await Promise.resolve()
    expect(wrapper.emitted('withdraw')).toEqual([['review-1', '范围选择错误']])
    prompt.mockRestore()
  })

  it('历史无工程图审核项不再进入2D审核面板', () => {
    const modelOnlyReview: DrawingReviewPackage = {
      ...review,
      items: [{
        ...review.items[0]!,
        drawingDocumentId: null,
        drawingVersionId: null,
        drawingRevision: null,
        drawingSha256: null,
        drawingCreatedBy: null,
        drawingState: 'NotRequired',
        effectiveDrawingVersionId: null,
      }],
    }
    const wrapper = mount(DrawingReviewPanel, {
      global: { plugins: [ElementPlus] },
      props: {
        packageId: modelOnlyReview.id,
        packages: [modelOnlyReview],
        selectedDocumentId: 'model-1',
        currentUsername: 'reviewer',
        ...permissions,
      },
    })

    expect(wrapper.find('.drawing-review-target-switch').exists()).toBe(false)
    expect(wrapper.text()).toContain('当前图档未纳入此审核单')
  })

  it('审核栏不再提供透明度调节，默认完全不透明', () => {
    const wrapper = mount(DrawingReviewPanel, {
      global: { plugins: [ElementPlus] },
      props: { packageId: '', packages: [], selectedDocumentId: 'model-1', currentUsername: 'designer', ...permissions },
    })

    expect(wrapper.find('input[aria-label="调整审核栏透明度"]').exists()).toBe(false)
    expect(wrapper.get('.drawing-review-panel').attributes('style') ?? '').not.toContain('--drawing-review-opacity')
  })

  it('明细固定为待操作/已操作两页，且提示文案一致', async () => {
    const wrapper = mount(DrawingReviewPanel, {
      global: { plugins: [ElementPlus] },
      props: { packageId: '', packages: [], candidates: [candidate], selectedDocumentId: 'model-1', currentUsername: 'designer', ...permissions },
    })

    const tabs = wrapper.findAll('[role="tab"]').map(tab => tab.text())
    expect(tabs).toEqual(['待操作（1）', '已操作（0）'])
    // 原状态下拉保留，选项卡只是加在列表上方。
    expect(wrapper.find('select[aria-label="筛选图纸审核状态"]').exists()).toBe(true)
    expect(wrapper.find('.drawing-review-overview > .drawing-review-panel__tabs').exists()).toBe(true)

    await wrapper.findAll('[role="tab"]')[1]!.trigger('click')
    expect(wrapper.get('[aria-label="图纸审核状态表"]').text()).toContain('还没有已处理（通过或退回）的图纸。')
  })

  it('审核明细栏内不再渲染审核结论条（结论条在预览工具条里）', () => {
    const wrapper = mount(DrawingReviewPanel, {
      global: { plugins: [ElementPlus] },
      props: { packageId: review.id, packages: [review], candidates: [candidate], selectedDocumentId: 'drawing-1', currentUsername: 'reviewer', ...permissions },
    })

    expect(wrapper.find('.drawing-review-decision-bar').exists()).toBe(false)
    expect(wrapper.text()).not.toContain('非标 BOM · 2D')
  })
})
