import { mount } from '@vue/test-utils'
import { ElMessageBox } from 'element-plus'
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

const permissions = { pending: false, canSubmit: true, canAnnotate: true, canDecide: true, desktopAvailable: false }
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
  it('在2D图纸侧栏中禁止自审，并把批注保存到当前2D图档', async () => {
    const wrapper = mount(DrawingReviewPanel, {
      props: {
        packageId: review.id,
        packages: [review],
        selectedDocumentId: 'drawing-1',
        currentUsername: 'drawing-designer',
        ...permissions,
      },
    })

    expect(wrapper.text()).toContain('当前版本由你生成，系统禁止审核自己的图。')
    expect(wrapper.findAll('.drawing-review-decision-buttons button').every(button => button.attributes('disabled') !== undefined)).toBe(true)

    await wrapper.setProps({ currentUsername: 'reviewer' })
    expect(wrapper.findAll('.drawing-review-decision-buttons button').every(button => button.attributes('disabled') === undefined)).toBe(true)
    expect(wrapper.get('.drawing-review-markup-form select').text()).toContain('必须整改')
    expect(wrapper.get('.drawing-review-markup-form select').text()).toContain('优化建议')
    await wrapper.get('.drawing-review-markup-form textarea').setValue('标题栏审核栏需确认')
    await wrapper.get('.drawing-review-markup-form > button').trigger('click')

    expect(wrapper.emitted('addMarkup')?.[0]).toEqual(['review-1', {
      itemId: 'item-1',
      target: 'Drawing2D',
      viewName: undefined,
      text: '标题栏审核栏需确认',
      severity: 'Blocking',
    }])
  })

  it('精简2D图档信息并把审核结论放在状态上方', () => {
    const reviewWithMarkups: DrawingReviewPackage = {
      ...review,
      markups: [{
        id: 'markup-1',
        packageId: review.id,
        itemId: review.items[0]!.id,
        target: 'Drawing2D',
        text: '检查孔位',
        severity: 'Blocking',
        state: 'Open',
        createdBy: 'reviewer',
        createdAt: '2026-08-24T00:00:00Z',
      }],
    }
    const wrapper = mount(DrawingReviewPanel, {
      props: {
        packageId: reviewWithMarkups.id,
        packages: [reviewWithMarkups],
        selectedDocumentId: 'drawing-1',
        currentUsername: 'reviewer',
        ...permissions,
      },
    })

    const decision = wrapper.get('.drawing-review-decision').element
    const targetSwitch = wrapper.get('.drawing-review-target-switch').element
    expect(decision.compareDocumentPosition(targetSwitch) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy()
    expect(wrapper.find('.drawing-review-current__title').exists()).toBe(false)
    expect(wrapper.find('.drawing-review-creator').exists()).toBe(false)
    expect(wrapper.text()).not.toContain('图形工具作用于当前eDrawings冻结版本')
    expect(wrapper.text()).toContain('标记已处理')
  })

  it('开发者模式允许审核本人设计的图档', () => {
    const wrapper = mount(DrawingReviewPanel, {
      props: {
        packageId: review.id,
        packages: [review],
        selectedDocumentId: 'drawing-1',
        currentUsername: 'drawing-designer',
        allowSelfReview: true,
        ...permissions,
      },
    })

    expect(wrapper.text()).not.toContain('本人设计，禁止自审')
    expect(wrapper.text()).not.toContain('当前版本由你生成，系统禁止审核自己的图。')
    expect(wrapper.findAll('.drawing-review-decision-buttons button').every(button => button.attributes('disabled') === undefined)).toBe(true)
  })

  it('没有审核单时从图档侧栏选择非标2D范围后发起审核', async () => {
    const wrapper = mount(DrawingReviewPanel, {
      props: { packageId: '', packages: [], candidates: [candidate], selectedDocumentId: 'model-1', currentUsername: 'designer', ...permissions },
    })

    await wrapper.get('.drawing-review-panel__empty button').trigger('click')
    expect(wrapper.text()).toContain('选择审核范围')
    expect(wrapper.text()).toContain('全部待审')
    expect(wrapper.get('.drawing-review-candidate').classes()).toContain('is-selected')
    await wrapper.get('.drawing-review-scope__footer button').trigger('click')
    expect(wrapper.emitted('create')).toEqual([[['model-1']]])
  })

  it('已有审核中的图纸包时仍可继续发起其余图纸', async () => {
    const wrapper = mount(DrawingReviewPanel, {
      props: {
        packageId: review.id,
        packages: [review],
        candidates: [candidate],
        selectedDocumentId: 'unreviewed-model',
        currentUsername: 'designer',
        ...permissions,
      },
    })

    const createButton = wrapper.get('.drawing-review-package-actions button:last-child')
    expect(createButton.attributes('disabled')).toBeUndefined()
    await createButton.trigger('click')
    await wrapper.get('.drawing-review-scope__footer button').trigger('click')
    expect(wrapper.emitted('create')).toEqual([[['model-1']]])
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
      props: { packageId: '', packages: [], candidates: [candidate, blocker], selectedDocumentId: 'model-1', currentUsername: 'designer', ...permissions },
    })

    await wrapper.get('.drawing-review-panel__empty button').trigger('click')

    const blockedRow = wrapper.get('.drawing-review-candidate.is-unavailable')
    expect(blockedRow.attributes('disabled')).toBeDefined()
    expect(blockedRow.text()).toContain('02040000005')
    expect(blockedRow.text()).toContain('没有来源模型')
    expect(wrapper.get('.drawing-review-scope__footer').text()).toContain('已选择 1 张')
  })

  it('默认只选待审图纸，并允许手工重新选择已审核版本', async () => {
    const candidates: DrawingReviewCandidate[] = [
      candidate,
      { ...candidate, candidateId: 'second-candidate', modelDocumentId: 'second-model', drawingDocumentId: 'second-drawing', drawingNumber: 'NS-200' },
      { ...candidate, candidateId: 'approved-candidate', modelDocumentId: 'approved-model', drawingDocumentId: 'approved-drawing', drawingNumber: 'OLD-100', state: 'ApprovedCurrent', reason: '当前版本已审核，可选择重新审核' },
    ]
    const wrapper = mount(DrawingReviewPanel, {
      props: { packageId: '', packages: [], candidates, selectedDocumentId: 'model-1', currentUsername: 'designer', ...permissions },
    })
    await wrapper.get('.drawing-review-panel__empty button').trigger('click')
    expect(wrapper.findAll('.drawing-review-candidate.is-selected')).toHaveLength(2)
    await wrapper.findAll('.drawing-review-scope__modes button')[1]!.trigger('click')
    const approved = wrapper.findAll('.drawing-review-candidate').find(button => button.text().includes('OLD-100'))!
    await approved.trigger('click')
    expect(approved.classes()).toContain('is-selected')
  })

  it('发起人可填写原因撤销审核', async () => {
    const prompt = vi.spyOn(ElMessageBox, 'prompt').mockResolvedValue({ value: '范围选择错误', action: 'confirm' } as never)
    const wrapper = mount(DrawingReviewPanel, {
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

  it('可以调节并保存审核栏透明度', async () => {
    window.localStorage.removeItem('upton-pdm-drawing-review-opacity')
    const wrapper = mount(DrawingReviewPanel, {
      props: { packageId: '', packages: [], selectedDocumentId: 'model-1', currentUsername: 'designer', ...permissions },
    })

    const opacity = wrapper.get<HTMLInputElement>('input[aria-label="调整审核栏透明度"]')
    expect(opacity.element.value).toBe('72')
    expect(opacity.attributes('min')).toBe('5')
    await opacity.setValue('5')
    await opacity.trigger('change')

    expect(wrapper.get('.drawing-review-panel').attributes('style')).toContain('--drawing-review-opacity: 5%')
    expect(window.localStorage.getItem('upton-pdm-drawing-review-opacity')).toBe('5')
  })
})
