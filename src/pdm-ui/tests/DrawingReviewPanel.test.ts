import { mount } from '@vue/test-utils'
import { describe, expect, it } from 'vitest'
import DrawingReviewPanel from '../src/components/DrawingReviewPanel.vue'
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
    modelState: 'Pending',
    drawingState: 'Pending',
    effectiveModelVersionId: 'model-version-1',
    effectiveDrawingVersionId: 'drawing-version-1',
  }],
  markups: [],
}

const permissions = { pending: false, canSubmit: true, canAnnotate: true, canDecide: true, desktopAvailable: false }

describe('DrawingReviewPanel', () => {
  it('在图档侧栏中禁止自审，并把批注保存到切换后的2D图档', async () => {
    const wrapper = mount(DrawingReviewPanel, {
      props: {
        packageId: review.id,
        packages: [review],
        selectedDocumentId: 'model-1',
        currentUsername: 'designer',
        ...permissions,
      },
    })

    expect(wrapper.text()).toContain('本人设计，禁止自审')
    expect(wrapper.findAll('.drawing-review-decision-buttons button').every(button => button.attributes('disabled') !== undefined)).toBe(true)

    await wrapper.findAll('.drawing-review-target-switch button')[1].trigger('click')
    expect(wrapper.emitted('selectDocument')?.[0]).toEqual(['drawing-1'])
    await wrapper.setProps({ selectedDocumentId: 'drawing-1' })

    expect(wrapper.text()).not.toContain('当前版本由你生成，系统禁止审核自己的图。')
    expect(wrapper.findAll('.drawing-review-decision-buttons button').every(button => button.attributes('disabled') === undefined)).toBe(true)
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

  it('没有审核单时可直接从图档侧栏发起双审', async () => {
    const wrapper = mount(DrawingReviewPanel, {
      props: { packageId: '', packages: [], selectedDocumentId: 'model-1', currentUsername: 'designer', ...permissions },
    })

    await wrapper.get('.drawing-review-panel__empty button').trigger('click')
    expect(wrapper.emitted('create')).toEqual([[]])
  })
})
