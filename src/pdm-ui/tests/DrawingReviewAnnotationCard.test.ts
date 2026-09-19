import { mount } from '@vue/test-utils'
import { ElMessageBox } from 'element-plus'
import ElementPlus from 'element-plus'
import { describe, expect, it, vi } from 'vitest'
import DrawingReviewAnnotationCard from '../src/components/DrawingReviewAnnotationCard.vue'
import type { DrawingReviewPackage } from '../src/types'

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

function mountCard(packageValue: DrawingReviewPackage | undefined, currentUsername = 'reviewer', extra: Record<string, unknown> = {}) {
  return mount(DrawingReviewAnnotationCard, {
    global: { plugins: [ElementPlus] },
    props: {
      package: packageValue,
      selectedDocumentId: 'drawing-1',
      currentUsername,
      pending: false,
      canAnnotate: true,
      canDecide: true,
      desktopAvailable: false,
      ...extra,
    },
  })
}

describe('DrawingReviewAnnotationCard', () => {
  it('禁止自审，并在指定审图人时就绪', async () => {
    const wrapper = mountCard(review, 'drawing-designer')

    expect(wrapper.text()).toContain('当前版本由你生成，系统禁止审核自己的图。')
    expect(wrapper.findAll('.drawing-review-decision-buttons button').every(button => button.attributes('disabled') !== undefined)).toBe(true)

    await wrapper.setProps({ currentUsername: 'reviewer' })
    expect(wrapper.findAll('.drawing-review-decision-buttons button').every(button => button.attributes('disabled') === undefined)).toBe(true)
    expect(wrapper.find('textarea[aria-label="审核意见"]').exists()).toBe(true)
    expect(wrapper.findAll('.drawing-review-decision-buttons button').map(button => button.text())).toEqual(['退改', '通过'])
  })

  it('只保留审核意见与通过/退改', () => {
    const wrapper = mountCard(review)

    expect(wrapper.text()).toContain('审核')
    expect(wrapper.findAll('.drawing-review-decision-buttons button').map(button => button.text())).toEqual(['退改', '通过'])
    expect(wrapper.find('.drawing-review-target-switch').exists()).toBe(false)
    expect(wrapper.find('.drawing-review-markup-tools').exists()).toBe(false)
    expect(wrapper.find('.drawing-review-markup-list').exists()).toBe(false)
  })

  it('开发者模式允许审核本人设计的图档', () => {
    const wrapper = mountCard(review, 'drawing-designer', { allowSelfReview: true })

    expect(wrapper.text()).not.toContain('当前版本由你生成，系统禁止审核自己的图。')
    expect(wrapper.findAll('.drawing-review-decision-buttons button').every(button => button.attributes('disabled') === undefined)).toBe(true)
  })

  it('已通过的图纸可撤销通过，按钮与通过同位置同尺寸', async () => {
    const confirm = vi.spyOn(ElMessageBox, 'confirm').mockResolvedValue({ action: 'confirm' } as never)
    const approved: DrawingReviewPackage = { ...review, items: [{ ...review.items[0]!, drawingState: 'Approved' }] }
    const wrapper = mountCard(approved)

    expect(wrapper.text()).toContain('该2D工程图已通过审核，等待批准')
    expect(wrapper.find('.is-approve').exists()).toBe(false)
    const revokeButton = wrapper.get('.drawing-review-decision-buttons .is-revoke')
    expect(revokeButton.text()).toBe('撤销')
    expect(revokeButton.attributes('title')).toBe('撤销审核通过')

    await revokeButton.trigger('click')
    await Promise.resolve()
    await Promise.resolve()
    expect(wrapper.emitted('decide')).toEqual([['review-1', 'item-1', 'Drawing2D', 'Revoke', '']])
    confirm.mockRestore()
  })

  it('已退改的图纸可撤销退改', async () => {
    const confirm = vi.spyOn(ElMessageBox, 'confirm').mockResolvedValue({ action: 'confirm' } as never)
    const changes: DrawingReviewPackage = { ...review, items: [{ ...review.items[0]!, drawingState: 'ChangesRequested', drawingComment: '尺寸标注需修改' }] }
    const wrapper = mountCard(changes)

    expect(wrapper.text()).toContain('该2D工程图已退回修改，其余图纸可继续审核')
    const revokeButton = wrapper.get('.drawing-review-decision-buttons .is-revoke')
    expect(revokeButton.text()).toBe('撤销')
    expect(revokeButton.attributes('title')).toBe('撤销退改结论')

    await revokeButton.trigger('click')
    await Promise.resolve()
    await Promise.resolve()
    expect(wrapper.emitted('decide')).toEqual([['review-1', 'item-1', 'Drawing2D', 'Revoke', '']])
    confirm.mockRestore()
  })

  it('只在审核进行中显示结论条', async () => {
    const wrapper = mountCard(review)
    expect(wrapper.find('.drawing-review-decision-bar').exists()).toBe(true)

    await wrapper.setProps({ package: { ...review, state: 'Withdrawn' } })
    expect(wrapper.find('.drawing-review-decision-bar').exists()).toBe(false)

    await wrapper.setProps({ package: { ...review, state: 'ChangesRequested' } })
    expect(wrapper.find('.drawing-review-decision-bar').exists()).toBe(false)

    await wrapper.setProps({ package: { ...review, state: 'PendingSupervisorApproval' } })
    expect(wrapper.find('.drawing-review-decision-bar').exists()).toBe(true)
  })

  it('按指定审核人 / 机械主管显示当前处理人并限制操作权限', async () => {
    const routed: DrawingReviewPackage = {
      ...review,
      assignedReviewers: ['reviewer'],
      assignedReviewerNames: ['指定审核员'],
      supervisor: 'manager',
      supervisorName: '机械主管',
    }
    const wrapper = mountCard(routed, 'other')

    expect(wrapper.text()).toContain('当前审核人：指定审核员')
    expect(wrapper.findAll('.drawing-review-decision-buttons button').every(button => button.attributes('disabled') !== undefined)).toBe(true)

    await wrapper.setProps({ currentUsername: 'reviewer' })
    expect(wrapper.findAll('.drawing-review-decision-buttons button').every(button => button.attributes('disabled') === undefined)).toBe(true)

    await wrapper.setProps({ package: { ...routed, state: 'PendingSupervisorApproval' }, currentUsername: 'manager' })
    expect(wrapper.text()).toContain('当前批准人：机械主管')
    expect(wrapper.findAll('.drawing-review-decision-buttons button').every(button => button.attributes('disabled') === undefined)).toBe(true)
  })

  it('未指定审核人时提示任一审核人均可处理', () => {
    const wrapper = mountCard({ ...review, assignedReviewers: [], assignedReviewerNames: [] }, 'reviewer-b')

    expect(wrapper.text()).toContain('当前审核人：具备审核权限的人员均可处理，任一通过即可')
    expect(wrapper.findAll('.drawing-review-decision-buttons button').every(button => button.attributes('disabled') === undefined)).toBe(true)
  })
})
