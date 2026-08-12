import { computed, onMounted, ref } from 'vue'
import { checkHealth, compareDocumentVersions, listDocumentVersions, loadProjectWorkspace, login as apiLogin, PdmApiError, postDesktopMessage, readDocumentVersionFile, restoreDocumentVersion } from '../api'
import type { AuthSession } from '../api'
import type { BomItem, DocumentNode, DocumentVersionComparison, DocumentVersionSummary, PreviewMode, ProjectSummary, ReleasePackageSummary } from '../types'

const sessionKey = 'upton-pdm-session'

const emptyProject: ProjectSummary = {
  id: '',
  code: '',
  name: '',
  owner: '',
  stage: '',
  vaultName: '',
  vaultLocation: '',
  releaseLocation: '',
}

const emptyRoot: DocumentNode = {
  id: '',
  drawingNumber: '',
  name: '',
  fileName: '',
  kind: 'Assembly',
  configuration: '',
  quantity: 1,
  version: '—',
  status: 'Normal',
  children: [],
}

function findNode(root: DocumentNode, id: string): DocumentNode | undefined {
  if (root.id === id) return root
  for (const child of root.children) {
    const match = findNode(child, id)
    if (match) return match
  }
  return undefined
}

function filterNode(node: DocumentNode, query: string): DocumentNode | undefined {
  const normalized = query.trim().toLocaleLowerCase('zh-CN')
  if (!normalized) return node
  const children = node.children.map((child) => filterNode(child, query)).filter((child): child is DocumentNode => Boolean(child))
  const ownText = `${node.drawingNumber} ${node.name} ${node.fileName}`.toLocaleLowerCase('zh-CN')
  if (ownText.includes(normalized) || children.length > 0) return { ...node, children }
  return undefined
}

function countNodes(root: DocumentNode): { normal: number; warning: number } {
  let normal = root.status === 'Missing' ? 0 : 1
  let warning = root.status === 'Missing' ? 1 : 0
  for (const child of root.children) {
    const counts = countNodes(child)
    normal += counts.normal
    warning += counts.warning
  }
  return { normal, warning }
}

function messageFrom(error: unknown): string {
  return error instanceof Error ? error.message : 'PDM数据加载失败。'
}

export function usePdmWorkspace() {
  const project = ref<ProjectSummary>(emptyProject)
  const root = ref<DocumentNode>(emptyRoot)
  const mechanicalBom = ref<BomItem[]>([])
  const electricalBom = ref<BomItem[]>([])
  const releasePackage = ref<ReleasePackageSummary | null>(null)
  const selectedId = ref('')
  const searchQuery = ref('')
  const previewMode = ref<PreviewMode>('model')
  const serviceOnline = ref(false)
  const authenticated = ref(false)
  const currentUser = ref('')
  const currentRole = ref('')
  const loginPending = ref(false)
  const loginError = ref('')
  const loading = ref(false)
  const loadError = ref('')
  const ready = ref(false)
  const versionDrawerOpen = ref(false)
  const versions = ref<DocumentVersionSummary[]>([])
  const leftVersionId = ref('')
  const rightVersionId = ref('')
  const versionComparison = ref<DocumentVersionComparison | null>(null)
  const versionLoading = ref(false)
  const versionError = ref('')
  const approvalDialogOpen = ref(false)
  let accessToken = ''
  let pendingVersionComparison: { documentId: string; leftVersionId?: string; rightVersionId?: string } | null = null

  const selectedNode = computed(() => findNode(root.value, selectedId.value) ?? root.value)
  const filteredTree = computed(() => filterNode(root.value, searchQuery.value) ?? root.value)
  const nodeCounts = computed(() => ready.value ? countNodes(root.value) : { normal: 0, warning: 0 })
  const normalCount = computed(() => nodeCounts.value.normal)
  const warningCount = computed(() => nodeCounts.value.warning)

  function selectNode(node: DocumentNode) {
    selectedId.value = node.id
    postDesktopMessage('document-selected', { documentId: node.id, fileName: node.fileName })
  }

  async function openVersionDrawer(left?: string, right?: string) {
    versionDrawerOpen.value = true
    versionLoading.value = true
    versionError.value = ''
    versionComparison.value = null
    try {
      versions.value = await listDocumentVersions(selectedNode.value.id, accessToken)
      leftVersionId.value = left && versions.value.some(version => version.id === left) ? left : versions.value.at(-1)?.id ?? ''
      rightVersionId.value = right && versions.value.some(version => version.id === right) ? right : versions.value[0]?.id ?? ''
      await compareVersions()
    } catch (error) {
      versionError.value = messageFrom(error)
    } finally {
      versionLoading.value = false
    }
  }

  async function compareVersions() {
    if (!leftVersionId.value || !rightVersionId.value || leftVersionId.value === rightVersionId.value) {
      versionComparison.value = null
      return
    }
    versionLoading.value = true
    versionError.value = ''
    try {
      versionComparison.value = await compareDocumentVersions(selectedNode.value.id, leftVersionId.value, rightVersionId.value, accessToken)
    } catch (error) {
      versionError.value = messageFrom(error)
    } finally {
      versionLoading.value = false
    }
  }

  async function openVersionFile(versionId: string, download: boolean) {
    const blob = await readDocumentVersionFile(selectedNode.value.id, versionId, accessToken, download)
    const url = URL.createObjectURL(blob)
    if (download) {
      const anchor = document.createElement('a')
      anchor.href = url
      anchor.download = `${selectedNode.value.drawingNumber}-${versions.value.find(version => version.id === versionId)?.revision.display ?? 'history'}-${selectedNode.value.fileName}`
      anchor.click()
    } else {
      window.open(url, '_blank', 'noopener')
    }
    window.setTimeout(() => URL.revokeObjectURL(url), 60_000)
  }

  async function restoreVersion(versionId: string, changeNote: string) {
    await restoreDocumentVersion(selectedNode.value.id, versionId, changeNote, accessToken)
    await Promise.all([reload(), openVersionDrawer(undefined, undefined)])
  }

  function openDocument(node = selectedNode.value) {
    postDesktopMessage('open-document', { documentId: node.id, fileName: node.fileName })
  }

  function submitApproval() {
    if (releasePackage.value) approvalDialogOpen.value = true
  }

  function applySession(session: AuthSession) {
    accessToken = session.accessToken
    authenticated.value = true
    currentUser.value = session.displayName || session.username
    currentRole.value = session.role
    window.sessionStorage.setItem(sessionKey, JSON.stringify(session))
  }

  function clearSession() {
    accessToken = ''
    authenticated.value = false
    currentUser.value = ''
    currentRole.value = ''
    ready.value = false
    project.value = emptyProject
    root.value = emptyRoot
    mechanicalBom.value = []
    electricalBom.value = []
    releasePackage.value = null
    window.sessionStorage.removeItem(sessionKey)
  }

  async function reload() {
    if (!accessToken) return
    loading.value = true
    loadError.value = ''
    try {
      const data = await loadProjectWorkspace(accessToken)
      project.value = data.project
      root.value = data.root
      mechanicalBom.value = data.mechanicalBom
      electricalBom.value = data.electricalBom
      releasePackage.value = data.releasePackage
      selectedId.value = data.root.id
      ready.value = true
      serviceOnline.value = true
      if (pendingVersionComparison) {
        const request = pendingVersionComparison
        pendingVersionComparison = null
        selectedId.value = request.documentId
        await openVersionDrawer(request.leftVersionId, request.rightVersionId)
      }
    } catch (error) {
      ready.value = false
      if (error instanceof PdmApiError && error.status === 401) {
        clearSession()
        loginError.value = '登录已失效，请重新登录。'
      } else {
        loadError.value = messageFrom(error)
      }
    } finally {
      loading.value = false
    }
  }

  async function login(username: string, password: string) {
    loginPending.value = true
    loginError.value = ''
    try {
      const session = await apiLogin(username, password)
      applySession(session)
      await reload()
    } catch (error) {
      clearSession()
      loginError.value = error instanceof PdmApiError && error.status === 401
        ? '用户名或密码错误。'
        : messageFrom(error)
    } finally {
      loginPending.value = false
    }
  }

  function logout() {
    clearSession()
    loginError.value = ''
    loadError.value = ''
  }

  function restoreSession(): AuthSession | null {
    try {
      const value = window.sessionStorage.getItem(sessionKey)
      if (!value) return null
      const session = JSON.parse(value) as AuthSession
      if (!session.accessToken || new Date(session.expiresAt).getTime() <= Date.now()) return null
      return session
    } catch {
      return null
    }
  }

  onMounted(async () => {
    window.addEventListener('pdm-open-version-compare', async (event) => {
      const detail = (event as CustomEvent<{ documentId?: string; leftVersionId?: string; rightVersionId?: string }>).detail
      if (!detail?.documentId) return
      pendingVersionComparison = { documentId: detail.documentId, leftVersionId: detail.leftVersionId, rightVersionId: detail.rightVersionId }
      if (accessToken && ready.value) {
        const request = pendingVersionComparison
        pendingVersionComparison = null
        selectedId.value = request.documentId
        await openVersionDrawer(request.leftVersionId, request.rightVersionId)
      }
    })

    const controller = new AbortController()
    const timer = window.setTimeout(() => controller.abort(), 1600)
    serviceOnline.value = await checkHealth(controller.signal)
    window.clearTimeout(timer)

    window.chrome?.webview?.addEventListener('message', (event) => {
      const message = event.data as { type?: string; payload?: { online?: boolean } }
      if (message.type === 'service-status') serviceOnline.value = Boolean(message.payload?.online)
    })

    const session = restoreSession()
    if (session) {
      applySession(session)
      await reload()
    }
  })

  return {
    project,
    root,
    releasePackage,
    mechanicalBom,
    electricalBom,
    selectedNode,
    filteredTree,
    searchQuery,
    previewMode,
    serviceOnline,
    authenticated,
    currentUser,
    currentRole,
    loginPending,
    loginError,
    loading,
    loadError,
    ready,
    normalCount,
    warningCount,
    versionDrawerOpen,
    versions,
    leftVersionId,
    rightVersionId,
    versionComparison,
    versionLoading,
    versionError,
    approvalDialogOpen,
    selectNode,
    openDocument,
    openVersionDrawer,
    compareVersions,
    openVersionFile,
    restoreVersion,
    submitApproval,
    login,
    logout,
    reload,
  }
}
