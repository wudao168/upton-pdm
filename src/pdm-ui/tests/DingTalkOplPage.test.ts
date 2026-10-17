import { mount, flushPromises } from '@vue/test-utils'
import { afterEach, beforeEach, expect, it, vi } from 'vitest'
import DingTalkOplPage from '../src/components/DingTalkOplPage.vue'

const projectId = '11111111-1111-1111-1111-111111111111'
const fetchMock = vi.fn()
let wrapper: ReturnType<typeof mount>
const json = (value: unknown, headers = {}) => new Response(JSON.stringify(value), { status: 200, headers: { 'Content-Type': 'application/json', ...headers } })
beforeEach(() => {
  window.history.replaceState({}, '', `/?dingtalkOpl=${projectId}`)
  vi.stubGlobal('fetch', fetchMock); fetchMock.mockReset()
})
afterEach(() => { wrapper?.unmount(); vi.restoreAllMocks(); vi.unstubAllGlobals(); window.history.replaceState({}, '', '/'); document.head.querySelectorAll('script').forEach(script => script.remove()) })
it('shows a Chinese disabled message without requesting identity or project contents', async () => {
  fetchMock.mockResolvedValue(json({ enabled: false }))
  wrapper = mount(DingTalkOplPage); await flushPromises()
  expect(wrapper.get('[role="alert"]').text()).toContain('尚未启用')
  expect(wrapper.find('form').exists()).toBe(false)
  expect(fetchMock).toHaveBeenCalledTimes(1)
})
it.each(['pc','ios'])('selects the bound project on %s without any group picker or member setup', async platform => {
  const groupId='33333333-3333-3333-3333-333333333333'
  window.history.replaceState({},'',`/?dingtalkOpl=1&companyId=company&groupId=${groupId}`)
  const picker=vi.fn(()=>{throw new Error('unexpected group picker')})
  vi.stubGlobal('dd',{chooseChat:picker,biz:{chat:{chooseConversationByCorpId:picker}},env:{platform},ready:(callback:()=>void)=>callback(),runtime:{permission:{requestAuthCode:({onSuccess}:{onSuccess:(value:{code:string})=>void})=>onSuccess({code:'group-code'})}}})
  const append=document.head.appendChild.bind(document.head)
  vi.spyOn(document.head,'appendChild').mockImplementation(node=>{const result=append(node);if(node instanceof HTMLScriptElement)queueMicrotask(()=>node.dispatchEvent(new Event('load')));return result})
  fetchMock.mockResolvedValueOnce(json({enabled:true,corpId:'corp',companyId:'company'}))
    .mockResolvedValueOnce(json({guestToken:'group-ticket',displayName:'张三',groupConfirmationRequired:true}))
    .mockResolvedValueOnce(json([{id:projectId,code:'P700014',name:'TEST 5',subprojects:[]}])).mockResolvedValueOnce(json([]))
  wrapper=mount(DingTalkOplPage);await flushPromises()
  expect(JSON.parse(fetchMock.mock.calls[1]![1].body).groupId).toBe(groupId)
  expect(wrapper.get<HTMLSelectElement>('select').element.value).toBe(projectId)
  expect(wrapper.get<HTMLSelectElement>('select').element.disabled).toBe(true)
  expect(wrapper.text()).toContain('已自动关联当前群绑定的项目')
  expect(wrapper.find('form').exists()).toBe(true)
  expect(wrapper.find('[aria-label="确认项目群"]').exists()).toBe(false)
  expect(wrapper.text()).not.toContain('负责人 @ 设置')
  expect(wrapper.text()).not.toContain('初始化')
  expect(picker).not.toHaveBeenCalled()
  expect(fetchMock).toHaveBeenCalledTimes(4)
})
it.each(['ios', 'pc'])('uses a submit-only guest session on %s without requiring a DingTalk user agent', async (platform) => {
  vi.spyOn(navigator, 'userAgent', 'get').mockReturnValue('iPhone WebView')
  vi.stubGlobal('dd', { env: { platform }, ready: (callback: () => void) => callback(), runtime: { permission: { requestAuthCode: ({ onSuccess }: { onSuccess: (result: { code: string }) => void }) => onSuccess({ code: 'verified-code' }) } } })
  const append = document.head.appendChild.bind(document.head)
  vi.spyOn(document.head, 'appendChild').mockImplementation((node) => { const result = append(node); if (node instanceof HTMLScriptElement) queueMicrotask(() => node.dispatchEvent(new Event('load'))); return result })
  const view = { rootProject: { id: projectId, code: 'P700014', name: '测试项目' }, people: [], subprojects: [], rows: [] }
  fetchMock.mockResolvedValueOnce(json({ enabled: true, corpId: 'corp' }))
    .mockResolvedValueOnce(json({ guestToken: 'verified-token', displayName: '张三' }))
    .mockResolvedValueOnce(json([{ ...view.rootProject, subprojects: [] }])).mockResolvedValueOnce(json([])).mockResolvedValueOnce(json({ number: 9, submittedBy: '张三', notificationSent: false }))
  wrapper = mount(DingTalkOplPage); await flushPromises()
  expect(document.head.querySelector('script')?.src).toBe('https://g.alicdn.com/dingding/dingtalk-jsapi/3.0.25/dingtalk.open.js')
  expect(wrapper.text()).toContain('提交人：张三')
  const loginBody = JSON.parse(fetchMock.mock.calls[1]![1].body)
  expect(loginBody).toEqual({ projectId, companyId: null, groupId: null, code: 'verified-code' })
  await wrapper.get('textarea').setValue('支架松动'); await wrapper.get('form').trigger('submit'); await flushPromises()
  const [path, request] = fetchMock.mock.calls[4]!
  expect(path).toContain('/guest/projects/' + projectId + '/submit')
  expect(request.headers.get('X-DingTalk-Session')).toBe('verified-token')
  expect(request.headers.has('Authorization')).toBe(false)
  expect(JSON.parse(request.body.get('data')).description).toBe('支架松动')
  expect(wrapper.get('[role="status"]').text()).toContain('#9 已提交')
  expect(wrapper.get('[role="status"]').text()).toContain('群通知暂未送达')
  expect(fetchMock.mock.calls.filter(([url]) => url.includes('/submit'))).toHaveLength(1)
  expect(fetchMock.mock.calls.some(([url]) => url === '/api/projects/' + projectId + '/opl')).toBe(false)
  expect(wrapper.text()).not.toContain('问题清单')
  expect(wrapper.text()).not.toContain('发送群提醒')
})
