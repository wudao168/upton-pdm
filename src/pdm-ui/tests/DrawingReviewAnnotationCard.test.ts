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
  it('审核单已完成或正在写标记时不再显示等待批准', () => {
    const approvedItem = { ...review.items[0]!, drawingState: 'Approved' as const }
    const writing = mountCard({ ...review, state: 'WritingProperties', items: [approvedItem] })
    expect(writing.text()).toContain('该2D工程图已通过审核，正在写入审核标记')
    expect(writing.text()).not.toContain('等待批准')
    writing.unmount()

    const finished = mountCard({ ...review, state: 'Approved', items: [approvedItem] })
    expect(finished.text()).toContain('该2D工程图已通过审核，审核单已完成')
    expect(finished.text()).not.toContain('等待批准')
    finished.unmount()

    const supervised = mountCard({ ...review, state: 'PendingSupervisorApproval', items: [approvedItem] })
    expect(supervised.text()).toContain('该2D工程图已通过审核，等待批准')
    supervised.unmount()
  })

  it('禁止自审，并在指定审图人时就绪', async () => {
    const wrapper = mountCard(review, 'drawing-designer')

    // 不再提示自审拦截文案，只体现为不可操作（输入框与按钮置灰）。
    expect(wrapper.text()).not.toContain('系统禁止审核自己的图')
    expect(wrapper.text()).toContain('当前审核人')
    expect(wrapper.get('textarea[aria-label="审核意见"]').attributes('disabled')).toBeDefined()
    expect(wrapper.findAll('.drawing-review-decision-buttons button').every(button => button.attributes('disabled') !== undefined)).toBe(true)

    await wrapper.setProps({ currentUsername: 'reviewer' })
    expect(wrapper.findAll('.drawing-review-decision-buttons button').every(button => button.attributes('disabled') === undefined)).toBe(true)
    expect(wrapper.find('textarea[aria-label="审核意见"]').exists()).toBe(true)
    expect(wrapper.findAll('.drawing-review-decision-buttons button').map(button => button.text())).toEqual(['驳回', '通过'])
  })

  it('只保留审核意见与通过/驳回', () => {
    const wrapper = mountCard(review)

    expect(wrapper.text()).toContain('审核')
    expect(wrapper.findAll('.drawing-review-decision-buttons button').map(button => button.text())).toEqual(['驳回', '通过'])
    expect(wrapper.find('.drawing-review-target-switch').exists()).toBe(false)
    expect(wrapper.find('.drawing-review-markup-tools').exists()).toBe(false)
    expect(wrapper.find('.drawing-review-markup-list').exists()).toBe(false)
  })

  it('开发者模式允许审核本人设计的图档', () => {
    const wrapper = mountCard(review, 'drawing-designer', { allowSelfReview: true })

    expect(wrapper.get('textarea[aria-label="审核意见"]').attributes('disabled')).toBeUndefined()
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

  it('通过后就地变成撤销按钮，驳回不可再点', async () => {
    const wrapper = mountCard(review)

    expect(wrapper.get('.is-approve').text()).toContain('通过')
    expect(wrapper.find('.is-revoke').exists()).toBe(false)

    await wrapper.setProps({ package: { ...review, items: [{ ...review.items[0]!, drawingState: 'Approved' }] } })

    expect(wrapper.find('.is-approve').exists()).toBe(false)
    expect(wrapper.get('.is-revoke').text()).toBe('撤销')
    expect(wrapper.get('.is-reject').attributes('disabled')).toBeDefined()
    expect(wrapper.get('textarea[aria-label="审核意见"]').attributes('disabled')).toBeDefined()
  })

  it('点击通过不需要二次确认，驳回仍要确认', async () => {
    const confirm = vi.spyOn(ElMessageBox, 'confirm').mockResolvedValue({ action: 'confirm' } as never)
    const wrapper = mountCard(review)

    await wrapper.get('.is-approve').trigger('click')
    await Promise.resolve()
    expect(confirm).not.toHaveBeenCalled()
    expect(wrapper.emitted('decide')).toEqual([['review-1', 'item-1', 'Drawing2D', 'Approve', '']])

    await wrapper.get('textarea[aria-label="审核意见"]').setValue('尺寸需确认')
    await wrapper.get('.is-reject').trigger('click')
    await Promise.resolve()
    await Promise.resolve()
    expect(confirm).toHaveBeenCalled()
    expect(wrapper.emitted('decide')![1]).toEqual(['review-1', 'item-1', 'Drawing2D', 'RequestChanges', '尺寸需确认'])
    confirm.mockRestore()
  })

  it('待批准阶段：主管只能去明细勾选后批量批准，审图人看到撤销按钮', () => {
    const pending: DrawingReviewPackage = {
      ...review,
      state: 'PendingSupervisorApproval',
      supervisor: 'manager',
      supervisorName: '机械主管',
      items: [{ ...review.items[0]!, drawingState: 'Approved' }],
    }

    const supervisor = mountCard(pending, 'manager')
    expect(supervisor.get('.drawing-review-decision-bar__hint').text()).toContain('请在下方明细中勾选图纸后批量批准')
    expect(supervisor.find('.is-approve').exists()).toBe(false)
    expect(supervisor.find('.is-revoke').exists()).toBe(false)
    expect(supervisor.find('.is-reject').exists()).toBe(false)

    const reviewer = mountCard(pending, 'reviewer')
    expect(reviewer.find('.is-approve').exists()).toBe(false)
    expect(reviewer.get('.is-revoke').text()).toBe('撤销')
  })

  it('已驳回的图纸可撤销驳回', async () => {
    const confirm = vi.spyOn(ElMessageBox, 'confirm').mockResolvedValue({ action: 'confirm' } as never)
    const changes: DrawingReviewPackage = { ...review, items: [{ ...review.items[0]!, drawingState: 'ChangesRequested', drawingComment: '尺寸标注需修改' }] }
    const wrapper = mountCard(changes)

    expect(wrapper.text()).toContain('该2D工程图已驳回（待修改），其余图纸可继续审核')
    const revokeButton = wrapper.get('.drawing-review-decision-buttons .is-revoke')
    expect(revokeButton.text()).toBe('撤销')
    expect(revokeButton.attributes('title')).toBe('撤销驳回结论')

    await revokeButton.trigger('click')
    await Promise.resolve()
    await Promise.resolve()
    expect(wrapper.emitted('decide')).toEqual([['review-1', 'item-1', 'Drawing2D', 'Revoke', '']])
    confirm.mockRestore()
  })

  it('驳回图档的设计者可按最新存档版本重新提交审核并显示驳回说明', async () => {
    const confirm = vi.spyOn(ElMessageBox, 'confirm').mockResolvedValue({ action: 'confirm' } as never)
    const changes: DrawingReviewPackage = { ...review, items: [{ ...review.items[0]!, drawingState: 'ChangesRequested', drawingComment: '尺寸标注需修改' }] }
    const wrapper = mountCard(changes, 'drawing-designer', { canResubmit: true })

    expect(wrapper.text()).toContain('驳回说明：尺寸标注需修改')
    expect(wrapper.text()).toContain('然后点“重新提交”')
    expect(wrapper.findAll('.drawing-review-decision-buttons button').map(button => button.text())).toEqual(['重新提交'])
    expect(wrapper.find('.is-reject').exists()).toBe(false)
    expect(wrapper.find('.is-revoke').exists()).toBe(false)

    await wrapper.get('.is-approve').trigger('click')
    await Promise.resolve()
    await Promise.resolve()
    expect(confirm).toHaveBeenCalled()
    expect(wrapper.emitted('resubmit')).toEqual([['review-1', 'item-1']])
    confirm.mockRestore()
  })

  it('结论条常驻显示：审核进行中可操作，其它状态只读', async () => {
    const wrapper = mountCard(review)
    expect(wrapper.find('.drawing-review-decision-bar').exists()).toBe(true)
    expect(wrapper.get('textarea[aria-label="审核意见"]').attributes('disabled')).toBeUndefined()
    expect(wrapper.get('.is-approve').attributes('disabled')).toBeUndefined()

    await wrapper.setProps({ package: { ...review, state: 'Withdrawn' } })
    expect(wrapper.find('.drawing-review-decision-bar').exists()).toBe(true)
    expect(wrapper.get('textarea[aria-label="审核意见"]').attributes('disabled')).toBeDefined()
    expect(wrapper.get('.is-approve').attributes('disabled')).toBeDefined()
    expect(wrapper.get('.is-reject').attributes('disabled')).toBeDefined()

    await wrapper.setProps({ package: { ...review, state: 'PendingSupervisorApproval' } })
    expect(wrapper.get('textarea[aria-label="审核意见"]').attributes('disabled')).toBeDefined()
    // 当前用户不是该单主管时只读：按钮仍在但不可点（主管本人会看到“请勾选后批量批准”提示）。
    expect(wrapper.get('.is-approve').attributes('disabled')).toBeDefined()
    expect(wrapper.find('.drawing-review-decision-bar__hint').exists()).toBe(false)
  })

  it('没有审核单时结论条常驻且全部不可操作', () => {
    const wrapper = mountCard(undefined)

    expect(wrapper.find('.drawing-review-decision-bar').exists()).toBe(true)
    expect(wrapper.text()).toContain('当前图档未纳入审核单')
    expect(wrapper.get('textarea[aria-label="审核意见"]').attributes('disabled')).toBeDefined()
    expect(wrapper.findAll('.drawing-review-decision-buttons button').every(button => button.attributes('disabled') !== undefined)).toBe(true)
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

  it('机械主管节点不在结论条上直接批准，避免一次点击批准整单', async () => {
    const supervisorNode: DrawingReviewPackage = {
      ...review,
      state: 'PendingSupervisorApproval',
      supervisor: 'manager',
      supervisorName: '机械主管',
      items: [{ ...review.items[0]!, drawingState: 'Approved' }],
    }
    const wrapper = mountCard(supervisorNode, 'manager')

    // 结论条不再提供整单批准按钮，批准只能由明细勾选后批量完成。
    expect(wrapper.find('.drawing-review-decision-buttons .is-approve').exists()).toBe(false)
    expect(wrapper.emitted('decideSupervisor')).toBeUndefined()
    expect(wrapper.get('.drawing-review-decision-bar__hint').text()).toContain('批量批准')
  })

  it('整单驳回的历史审核单给出可执行的下一步说明', () => {
    const wholeReturned: DrawingReviewPackage = { ...review, state: 'ChangesRequested', items: [{ ...review.items[0]!, drawingState: 'Approved' }] }
    const wrapper = mountCard(wholeReturned, 'reviewer')

    expect(wrapper.text()).toContain('本审核单已整单驳回（待修改）')
    expect(wrapper.text()).toContain('可重新发起审核')
    expect(wrapper.findAll('.drawing-review-decision-buttons button').every(button => button.attributes('disabled') !== undefined)).toBe(true)
  })
})
