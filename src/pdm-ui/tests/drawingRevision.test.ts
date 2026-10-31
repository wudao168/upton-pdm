import { describe, expect, it } from 'vitest'
import { documentVersionSummary, bomRevisionLabel, drawingRevisionHint, drawingRevisionLabel, packageRevisionLabel, packageRevisionHint } from '../src/drawingRevision'
import type { BomItem, ReleasePackageSummary } from '../src/types'

const item: BomItem = { sequence: 1, drawingNumber: 'PART', name: '底板', quantity: 1, unit: '个', revision: 'W1', complete: true, sourceDocumentId: 'model' }
const release = (raw = 'W1', target = 'A'): ReleasePackageSummary => ({ id: 'rp', number: 'RP-1', state: '已发布', scope: 'NonStandardWithDrawing',
  standardBomSnapshot: [], nonStandardBomSnapshot: [{ ...item, revision: raw }], electricalBomSnapshot: [],
  versionBindings: [{ documentId: 'model', versionId: 'source', revision: raw, sha256: 'hash', publish: true, targetRevision: target }],
  steps: [], workflowVersion: 1, selectedBomItemIds: [], createsManufacturingBaseline: true, locksDocuments: true })

describe('统一图纸版次', () => {
  it('标准件没有独立版本时采用该发布包冻结的BOM版本，保留已有物料版本', () => {
    const standard = { ...item, kind: 'Standard' as const }
    const packageA = { ...release(), scope: 'StandardSupplement' as const, standardBomRevision: 'S-P700016-2-B03', versionBindings: [] }
    expect(packageRevisionLabel(standard, packageA)).toBe('S-P700016-2-V03')
    expect(packageRevisionLabel(standard, { ...packageA, standardBomRevision: 'S-P700016-2-V04' })).toBe('S-P700016-2-V04')
    expect(packageRevisionLabel({ ...standard, revision: 'A' }, packageA)).toBe('A')
    expect(packageRevisionHint(standard, packageA)).toBe('标准件BOM发布版本：S-P700016-2-V03；取自本次发布包冻结记录')
    expect(standard.revision).toBe('W1')
  })
  it('旧标准件记录缺少BOM版本时提示核对，不臆造版本', () => {
    expect(packageRevisionLabel({ ...item, kind: 'Standard' }, { ...release(), scope: 'StandardFormal', versionBindings: [] })).toBe('BOM发布版本待核对')
  })
  it('图纸序号不受发布字母影响，缺失映射时不猜测', () => {
    expect(documentVersionSummary({ drawingVersionNumber: 4, publishedVersion: 'A', version: 'A-W1' })).toBe('图纸版本 V4 · 发布版本 A')
    expect(documentVersionSummary({ version: 'A-W1' })).toBe('图纸版本 待核对 · 发布版本 未发布')
  })
  it('发布中和驳回均保留上次成功发布版本', () => {
    const current = release('A-W2', 'B')
    current.versionBindings![0]!.previousReleasedRevision = 'A'
    for (const state of ['发布中', 'Publishing', '审批中', '已驳回'])
      expect(packageRevisionLabel(item, { ...current, state })).toBe('A')
  })
  it.each([['W1', 'A-D'], ['W99', 'A-D'], ['A', 'A'], ['A-W1', 'B-D'],
    ['B-W12', 'C-D'], ['Z-W1', '版次已达 Z（待处理）'], ['Rev-12', 'Rev-12']])('%s 显示 %s', (raw, expected) => {
    expect(drawingRevisionLabel(raw)).toBe(expected)
  })
  it('当前BOM采用最近成功发布版本，保持原始存档标识用于保存', () => {
    expect(bomRevisionLabel(item, [release('A-W1', 'B'), release()])).toBe('B')
    expect(bomRevisionLabel({ ...item, revision: 'A-W2' }, [release()])).toBe('A')
    expect(item.revision).toBe('W1')
  })
  it('发布前显示拟发布，发布后使用同一冻结绑定的正式版次', () => {
    expect(packageRevisionLabel(item, { ...release(), state: '审批中' })).toBe('未发布')
    expect(packageRevisionLabel(item, release())).toBe('A')
    expect(packageRevisionLabel({ ...item, revision: 'A-W1' }, release('A-W1', 'B'))).toBe('B')
  })
  it('历史缺失绑定时标记待核对，不能直接把W1替换为A', () => {
    expect(packageRevisionLabel(item, { ...release(), versionBindings: null })).toBe('历史版次待核对')
  })
  it('未存储目标版次的旧发布绑定仍显示正式字母，不保留草稿后缀', () => {
    const value = release('A-W2', '')
    expect(packageRevisionLabel({ ...item, revision: 'A-W2' }, value)).toBe('B')
  })
  it('中文悬浮提示区分草稿、修订和已发布源存档', () => {
    expect(drawingRevisionHint('W7')).toBe('设计中；尚未正式发布')
    expect(drawingRevisionHint('A-W2')).toBe('修订中；当前生产使用 A 版')
    expect(drawingRevisionHint('W1', 'A')).toBe('已发布')
  })
})
