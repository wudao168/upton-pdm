import { afterEach, describe, expect, it, vi } from 'vitest'
import { listEngineeringKits, publishEngineeringKit, saveEngineeringKit } from '../src/api'

afterEach(() => vi.unstubAllGlobals())

describe('套件接口状态转换', () => {
  it.each([
    { draft: 0, released: 1, manual: 1, auto: 0 },
    { draft: 'Draft', released: 'Released', manual: 'Manual', auto: 'Auto' },
  ])('列表兼容数字和文字状态，保留草稿、手动型号及明细：%j', async values => {
    const kit = {
      id: 'kit-1', name: 'MV-ID2023XM支架组件', model: 'R722.S2.12-00', modelMode: values.manual,
      rowVersion: 2, revisions: [{ id: 'draft', versionNumber: 1, state: values.draft,
        components: [{ materialId: 'material-1', materialCode: '01021000185', quantity: 1 }] }],
    }
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response(JSON.stringify([
      kit, { ...kit, id: 'kit-2', modelMode: values.auto, revisions: [{ state: values.released, components: [] }] },
    ]), { status: 200 })))

    const result = await listEngineeringKits('token', false)
    expect(result[0]).toMatchObject({ ...kit, modelMode: 'Manual', revisions: [{ ...kit.revisions[0], state: 'Draft' }] })
    expect(result[1]).toMatchObject({ modelMode: 'Auto', revisions: [{ state: 'Released' }] })
  })

  it('新建、保存和发布均转换响应状态，并保留发布版本和并发校验值', async () => {
    const draft = { id: 'kit-1', modelMode: 1, rowVersion: 2, revisions: [{ id: 'revision-1', state: 0, versionNumber: 1, components: [] }] }
    const released = { ...draft, rowVersion: 3, currentReleasedRevisionId: 'revision-1', revisions: [{ ...draft.revisions[0], state: 1 }] }
    const fetchMock = vi.fn()
      .mockResolvedValueOnce(new Response(JSON.stringify(draft), { status: 200 }))
      .mockResolvedValueOnce(new Response(JSON.stringify(draft), { status: 200 }))
      .mockResolvedValueOnce(new Response(JSON.stringify(released), { status: 200 }))
    vi.stubGlobal('fetch', fetchMock)
    const input = { name: '组件', brand: 'UPTON', modelMode: 'Manual' as const, model: 'R722.S2.12-00', components: [] }
    expect(await saveEngineeringKit(input, 'token')).toMatchObject({ modelMode: 'Manual', revisions: [{ state: 'Draft' }] })
    expect(await saveEngineeringKit({ ...input, expectedRowVersion: 2 }, 'token', 'kit-1')).toMatchObject({ modelMode: 'Manual', revisions: [{ state: 'Draft' }] })
    expect(await publishEngineeringKit('kit-1', 2, 'token')).toMatchObject({ modelMode: 'Manual', rowVersion: 3, currentReleasedRevisionId: 'revision-1', revisions: [{ state: 'Released' }] })
    expect(fetchMock.mock.calls.map(call => [new URL(call[0], 'http://localhost').pathname, call[1].method])).toEqual([
      ['/api/engineering-kits', 'POST'], ['/api/engineering-kits/kit-1', 'PUT'], ['/api/engineering-kits/kit-1/publish', 'POST'],
    ])
    expect(JSON.parse(fetchMock.mock.calls[1][1].body)).toEqual({ ...input, expectedRowVersion: 2 })
    expect(JSON.parse(fetchMock.mock.calls[2][1].body)).toEqual({ expectedRowVersion: 2 })
  })
})
